// Copyright 2025 OpenAI
// SPDX-License-Identifier: Apache-2.0
// Adapted from OpenAI Codex rust-v0.159.0, http-client/src/outbound_proxy/windows.rs.
// CHANGED: cancellable WinHTTP API with bounded time/data and private diagnostics.
// Restore upstream configured PAC -> enabled auto-detect -> static/bypass resolution.
// Missing settings never enable WPAD; automatic domain authentication stays disabled.
// Select one route before networking, never retry later candidates on connection errors.
// Also adapted from http-client/src/outbound_proxy.rs (proxy-list and bypass parsing).
// https://github.com/openai/codex/blob/rust-v0.159.0/codex-rs/http-client/src/outbound_proxy/windows.rs

use std::time::{Duration, Instant};

const MAX_RESOLUTION_TIME: Duration = Duration::from_secs(10);

/// Safe to expose in diagnostics; contains no address, PAC URL or bypass string.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ConfigState {
    Absent,
    StaticOnly,
    Pac,
    AutoDetect,
    PacAndAutoDetect,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ProxyScheme {
    Http,
    Https,
}

/// Intentionally not Debug or Serialize. Proxy addresses can be private.
/// Only the selected route is returned. Addresses never enter diagnostics.
pub enum Route {
    Direct,
    Proxy {
        scheme: ProxyScheme,
        host: String,
        port: u16,
    },
    UnsupportedScheme,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ResolveErrorKind {
    UnsupportedPlatform,
    NotConfigured,
    ConfigurationRead,
    InvalidConfiguration,
    InvalidStaticRoute,
    Initialization,
    TimedOut,
    AuthenticationRequired,
    PacDownload,
    PacScript,
    AutoDetection,
    NativeFailure,
    InvalidResult,
    InvalidUrl,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ResolveError {
    pub kind: ResolveErrorKind,
    pub os_code: Option<u32>,
}
impl ResolveError {
    fn new(kind: ResolveErrorKind) -> Self {
        Self {
            kind,
            os_code: None,
        }
    }
    #[cfg(windows)]
    fn os(kind: ResolveErrorKind, code: u32) -> Self {
        Self {
            kind,
            os_code: Some(code),
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ResolutionSource {
    Automatic,
    Static,
    Direct,
}

/// No Debug/Serialize: routes may contain private proxy addresses.
pub struct ResolvedRoutes {
    pub routes: Vec<Route>,
    pub source: ResolutionSource,
}

/// Read-only snapshot of this Windows user's active Internet settings.
/// No Debug/Serialize: PAC, static proxy and bypass values stay private.
pub struct Configuration {
    auto_detect: bool,
    pac_url: Option<String>,
    static_proxy: Option<String>,
    static_bypass: Option<String>,
}
impl Configuration {
    pub fn read_current_user() -> Result<Self, ResolveError> {
        Self::read_with(&Platform)
    }

    pub fn has_pac(&self) -> bool {
        self.pac_url.is_some()
    }
    pub fn auto_detect(&self) -> bool {
        self.auto_detect
    }
    pub fn has_static(&self) -> bool {
        self.static_proxy.is_some()
    }

    pub fn state(&self) -> ConfigState {
        match (self.has_pac(), self.auto_detect(), self.has_static()) {
            (true, true, _) => ConfigState::PacAndAutoDetect,
            (true, false, _) => ConfigState::Pac,
            (false, true, _) => ConfigState::AutoDetect,
            (false, false, true) => ConfigState::StaticOnly,
            (false, false, false) => ConfigState::Absent,
        }
    }

    /// Resolve before networking: configured PAC, explicitly enabled WPAD, then
    /// static/bypass. Err means upstream Unavailable, eligible for env/direct
    /// selection by the caller. It must not replay a failed network request.
    pub fn resolve(&self, url: &str, timeout: Duration) -> Result<ResolvedRoutes, ResolveError> {
        self.resolve_with(&Platform, url, timeout)
    }

    fn read_with(api: &impl NativeApi) -> Result<Self, ResolveError> {
        api.read_configuration()
    }

    fn resolve_with(
        &self,
        api: &impl NativeApi,
        url: &str,
        timeout: Duration,
    ) -> Result<ResolvedRoutes, ResolveError> {
        if !url.starts_with("https://") || url.contains('\0') || url.len() > 16 * 1024 {
            return Err(ResolveError::new(ResolveErrorKind::InvalidUrl));
        }
        let origin = RequestOrigin::parse(url)
            .ok_or_else(|| ResolveError::new(ResolveErrorKind::InvalidUrl))?;
        let deadline = Instant::now() + timeout.min(MAX_RESOLUTION_TIME);
        let mut last_error = None;
        if let Some(pac_url) = &self.pac_url {
            let result = remaining(deadline)
                .and_then(|budget| api.resolve_attempt(url, Some(pac_url), budget))
                .and_then(first_route);
            match result {
                Ok(route) => return Ok(resolved(route, ResolutionSource::Automatic)),
                Err(error) => last_error = Some(error),
            }
        }
        if self.auto_detect {
            // No implicit WPAD when a PAC fails or current-user settings are absent.
            let result = remaining(deadline)
                .and_then(|budget| api.resolve_attempt(url, None, budget))
                .and_then(first_route);
            match result {
                Ok(route) => return Ok(resolved(route, ResolutionSource::Automatic)),
                Err(error) => last_error = Some(error),
            }
        }
        // Static selection performs no network I/O and is valid even after the
        // automatic resolver's deadline expired. This is the upstream ordering.
        if let Some(proxy) = self.static_proxy.as_deref() {
            let route = if self
                .static_bypass
                .as_deref()
                .is_some_and(|bypass| proxy_bypass_matches_origin(bypass, &origin))
            {
                Route::Direct
            } else {
                static_route(proxy, &origin.scheme)?
            };
            return Ok(resolved(route, ResolutionSource::Static));
        }
        match last_error {
            Some(error) => Err(error),
            None => Ok(resolved(Route::Direct, ResolutionSource::Direct)),
        }
    }
}
fn resolved(route: Route, source: ResolutionSource) -> ResolvedRoutes {
    ResolvedRoutes {
        routes: vec![route],
        source,
    }
}
fn remaining(deadline: Instant) -> Result<Duration, ResolveError> {
    deadline
        .checked_duration_since(Instant::now())
        .filter(|d| !d.is_zero())
        .ok_or_else(|| ResolveError::new(ResolveErrorKind::TimedOut))
}
fn first_route(routes: Vec<Route>) -> Result<Route, ResolveError> {
    routes
        .into_iter()
        .find(|route| !matches!(route, Route::UnsupportedScheme))
        .ok_or_else(|| ResolveError::new(ResolveErrorKind::InvalidResult))
}

struct RequestOrigin {
    scheme: String,
    host: String,
    port: u16,
}
impl RequestOrigin {
    fn parse(url: &str) -> Option<Self> {
        let url = reqwest::Url::parse(url).ok()?;
        Some(Self {
            scheme: url.scheme().to_ascii_lowercase(),
            host: url
                .host_str()?
                .trim_matches(['[', ']'])
                .to_ascii_lowercase(),
            port: url.port_or_known_default()?,
        })
    }
}

fn static_route(proxy: &str, scheme: &str) -> Result<Route, ResolveError> {
    let invalid = || ResolveError::new(ResolveErrorKind::InvalidStaticRoute);
    match parse_proxy_list(proxy, scheme) {
        ParsedProxyListDecision::Direct => Ok(Route::Direct),
        ParsedProxyListDecision::Proxy(value) => {
            let url = reqwest::Url::parse(&value).map_err(|_| invalid())?;
            let scheme = match url.scheme() {
                "http" => ProxyScheme::Http,
                "https" => ProxyScheme::Https,
                _ => return Err(invalid()),
            };
            // The existing transport only supports host/port routes. Do not discard
            // explicit credentials or reinterpret proxy paths as connection hosts.
            if !url.username().is_empty()
                || url.password().is_some()
                || !matches!(url.path(), "" | "/")
                || url.query().is_some()
                || url.fragment().is_some()
            {
                return Err(invalid());
            }
            Ok(Route::Proxy {
                scheme,
                host: url
                    .host_str()
                    .ok_or_else(invalid)?
                    .trim_matches(['[', ']'])
                    .into(),
                port: url
                    .port_or_known_default()
                    .filter(|p| *p != 0)
                    .ok_or_else(invalid)?,
            })
        }
        ParsedProxyListDecision::UnsupportedScheme | ParsedProxyListDecision::Unavailable => {
            Err(ResolveError::new(ResolveErrorKind::InvalidResult))
        }
    }
}

// Proxy-list and bypass selection below follow the upstream v0.159.0 functions.
fn no_proxy_matches_origin(no_proxy: &str, origin: &RequestOrigin) -> bool {
    no_proxy
        .split(',')
        .map(str::trim)
        .filter(|entry| !entry.is_empty())
        .any(|entry| no_proxy_entry_matches_origin(entry, origin))
}

fn no_proxy_entry_matches_origin(entry: &str, origin: &RequestOrigin) -> bool {
    if entry == "*" {
        return true;
    }

    let mut entry = entry
        .strip_prefix("http://")
        .or_else(|| entry.strip_prefix("https://"))
        .unwrap_or(entry)
        .trim_matches(['[', ']'])
        .to_ascii_lowercase();
    let mut port = None;
    let parsed_host_port = entry.rsplit_once(':').and_then(|(host, candidate_port)| {
        if host.contains(':') {
            return None;
        }
        candidate_port
            .parse::<u16>()
            .ok()
            .map(|parsed_port| (host.to_string(), parsed_port))
    });
    if let Some((host, parsed_port)) = parsed_host_port {
        entry = host;
        port = Some(parsed_port);
    }
    if port.is_some_and(|port| port != origin.port) {
        return false;
    }

    if let Some(suffix) = entry.strip_prefix('.') {
        return origin.host == suffix || origin.host.ends_with(&format!(".{suffix}"));
    }

    if entry.contains('*') {
        return wildcard_host_match(&entry, &origin.host);
    }

    origin.host == entry
}

fn wildcard_host_match(pattern: &str, host: &str) -> bool {
    let mut remaining = host;
    let mut first = true;
    for part in pattern.split('*') {
        if part.is_empty() {
            continue;
        }
        if first && !pattern.starts_with('*') {
            let Some(stripped) = remaining.strip_prefix(part) else {
                return false;
            };
            remaining = stripped;
        } else {
            let Some(index) = remaining.find(part) else {
                return false;
            };
            remaining = &remaining[index + part.len()..];
        }
        first = false;
    }
    pattern.ends_with('*') || remaining.is_empty()
}

enum ParsedProxyListDecision {
    Direct,
    Proxy(String),
    UnsupportedScheme,
    Unavailable,
}

fn parse_proxy_list(input: &str, target_scheme: &str) -> ParsedProxyListDecision {
    let mut saw_unsupported = false;

    {
        let mut process_token = |token: &str| {
            let decision = parse_proxy_token(token, target_scheme);
            match decision {
                ParsedProxyListDecision::Direct => Some(ParsedProxyListDecision::Direct),
                ParsedProxyListDecision::Proxy(url) => Some(ParsedProxyListDecision::Proxy(url)),
                ParsedProxyListDecision::UnsupportedScheme => {
                    saw_unsupported = true;
                    None
                }
                ParsedProxyListDecision::Unavailable => None,
            }
        };

        for segment in input
            .split(';')
            .map(str::trim)
            .filter(|segment| !segment.is_empty())
        {
            let mut parts = segment.split_whitespace();
            let directive = parts.next();
            let hostport = parts.next();
            let extra = parts.next();
            let is_proxy_directive = matches!(
                directive.map(str::to_ascii_lowercase).as_deref(),
                Some("proxy" | "http" | "https" | "socks" | "socks4" | "socks5")
            ) && hostport.is_some()
                && extra.is_none();

            if is_proxy_directive {
                if let Some(decision) = process_token(segment) {
                    return decision;
                }
            } else {
                for token in segment.split_whitespace() {
                    if let Some(decision) = process_token(token) {
                        return decision;
                    }
                }
            }
        }
    }

    if saw_unsupported {
        ParsedProxyListDecision::UnsupportedScheme
    } else {
        ParsedProxyListDecision::Unavailable
    }
}

fn parse_proxy_token(token: &str, target_scheme: &str) -> ParsedProxyListDecision {
    if token.eq_ignore_ascii_case("DIRECT") {
        return ParsedProxyListDecision::Direct;
    }

    if let Some(decision) = parse_proxy_key_token(token, target_scheme) {
        return decision;
    }
    if token.contains('=') {
        return ParsedProxyListDecision::Unavailable;
    }

    let mut parts = token.split_whitespace();
    let directive = parts.next();
    let hostport = parts.next();
    if let (Some(directive), Some(hostport), None) = (directive, hostport, parts.next()) {
        return match directive.to_ascii_lowercase().as_str() {
            "proxy" | "http" => proxy_url_from_hostport("http", hostport),
            "https" => proxy_url_from_hostport("https", hostport),
            "socks" | "socks4" | "socks5" => ParsedProxyListDecision::UnsupportedScheme,
            _ => ParsedProxyListDecision::Unavailable,
        };
    }

    proxy_url_from_hostport("http", token)
}

fn parse_proxy_key_token(token: &str, target_scheme: &str) -> Option<ParsedProxyListDecision> {
    let (key, value) = token.split_once('=')?;
    if key.trim().eq_ignore_ascii_case(target_scheme) {
        Some(proxy_url_from_hostport("http", value.trim()))
    } else {
        Some(ParsedProxyListDecision::Unavailable)
    }
}

fn proxy_url_from_hostport(proxy_scheme: &str, hostport: &str) -> ParsedProxyListDecision {
    if hostport.is_empty() {
        return ParsedProxyListDecision::Unavailable;
    }
    if hostport.contains("://") {
        return ParsedProxyListDecision::Proxy(hostport.to_string());
    }
    ParsedProxyListDecision::Proxy(format!("{proxy_scheme}://{hostport}"))
}

fn proxy_bypass_matches_origin(proxy_bypass: &str, origin: &RequestOrigin) -> bool {
    proxy_bypass
        .split(|ch: char| ch == ';' || ch == ',' || ch.is_whitespace())
        .map(str::trim)
        .filter(|entry| !entry.is_empty())
        .any(|entry| {
            if entry.eq_ignore_ascii_case("<local>") {
                !origin.host.contains('.')
            } else {
                no_proxy_matches_origin(entry, origin)
            }
        })
}

/// Injection boundary for portable policy tests; fakes do not claim to run WinHTTP.
trait NativeApi {
    fn read_configuration(&self) -> Result<Configuration, ResolveError>;
    /// Some(PAC) requests that URL only; None requests explicitly enabled WPAD only.
    fn resolve_attempt(
        &self,
        url: &str,
        pac: Option<&str>,
        timeout: Duration,
    ) -> Result<Vec<Route>, ResolveError>;
}
struct Platform;
#[cfg(not(windows))]
impl NativeApi for Platform {
    fn read_configuration(&self) -> Result<Configuration, ResolveError> {
        Err(ResolveError::new(ResolveErrorKind::UnsupportedPlatform))
    }
    fn resolve_attempt(
        &self,
        _: &str,
        _: Option<&str>,
        _: Duration,
    ) -> Result<Vec<Route>, ResolveError> {
        Err(ResolveError::new(ResolveErrorKind::UnsupportedPlatform))
    }
}

#[cfg(windows)]
mod windows {
    use super::*;
    use std::collections::HashMap;
    use std::ffi::c_void;
    use std::ptr;
    use std::sync::{
        atomic::{AtomicUsize, Ordering},
        Arc, Condvar, Mutex, OnceLock,
    };
    use windows_sys::Win32::Foundation::{
        GetLastError, GlobalFree, ERROR_FILE_NOT_FOUND, ERROR_IO_PENDING,
    };
    use windows_sys::Win32::Networking::WinHttp::*;

    const MAX_WIDE_UNITS: usize = 32 * 1024;
    const MAX_ROUTES: usize = 256;
    // windows-sys exposes the component status bits, rather than this C macro.
    const CALLBACK_HANDLES: u32 =
        WINHTTP_CALLBACK_STATUS_HANDLE_CREATED | WINHTTP_CALLBACK_STATUS_HANDLE_CLOSING;

    impl NativeApi for Platform {
        fn read_configuration(&self) -> Result<Configuration, ResolveError> {
            let mut raw = WINHTTP_CURRENT_USER_IE_PROXY_CONFIG::default();
            let ok = unsafe { WinHttpGetIEProxyConfigForCurrentUser(&mut raw) };
            let code = if ok == 0 {
                unsafe { GetLastError() }
            } else {
                0
            };
            // Own every returned allocation immediately, including on error paths.
            let pac = GlobalString(raw.lpszAutoConfigUrl);
            let proxy = GlobalString(raw.lpszProxy);
            let bypass = GlobalString(raw.lpszProxyBypass);
            if ok == 0 {
                if code == ERROR_FILE_NOT_FOUND {
                    // Deliberate difference from upstream: absence never enables WPAD.
                    return Ok(Configuration {
                        auto_detect: false,
                        pac_url: None,
                        static_proxy: None,
                        static_bypass: None,
                    });
                }
                return Err(ResolveError::os(ResolveErrorKind::ConfigurationRead, code));
            }
            let pac_url = unsafe { read_wide(pac.0) }
                .map_err(|_| ResolveError::new(ResolveErrorKind::InvalidConfiguration))?
                .filter(|v| !v.is_empty());
            let static_proxy = unsafe { read_wide(proxy.0) }
                .map_err(|_| ResolveError::new(ResolveErrorKind::InvalidConfiguration))?
                .filter(|v| !v.is_empty());
            let static_bypass = unsafe { read_wide(bypass.0) }
                .map_err(|_| ResolveError::new(ResolveErrorKind::InvalidConfiguration))?
                .filter(|v| !v.is_empty());
            Ok(Configuration {
                auto_detect: raw.fAutoDetect != 0,
                pac_url,
                static_proxy,
                static_bypass,
            })
        }

        fn resolve_attempt(
            &self,
            url: &str,
            pac: Option<&str>,
            timeout: Duration,
        ) -> Result<Vec<Route>, ResolveError> {
            let deadline = Instant::now() + timeout;
            let agent = wide("CodexQuotaTray");
            let session = Handle(unsafe {
                WinHttpOpen(
                    agent.as_ptr(),
                    WINHTTP_ACCESS_TYPE_NO_PROXY,
                    ptr::null(),
                    ptr::null(),
                    WINHTTP_FLAG_ASYNC,
                )
            });
            if session.0.is_null() {
                return Err(last(ResolveErrorKind::Initialization));
            }
            let millis = timeout.as_millis().clamp(1, i32::MAX as u128) as i32;
            if unsafe { WinHttpSetTimeouts(session.0, millis, millis, millis, millis) } == 0 {
                return Err(last(ResolveErrorKind::Initialization));
            }
            // Register on the session before CreateProxyResolver as required by WinHTTP.
            let previous = unsafe {
                WinHttpSetStatusCallback(
                    session.0,
                    Some(callback),
                    WINHTTP_CALLBACK_FLAG_REQUEST_ERROR
                        | WINHTTP_CALLBACK_FLAG_GETPROXYFORURL_COMPLETE
                        | CALLBACK_HANDLES,
                    0,
                )
            };
            if previous.map(|f| f as usize) == Some(usize::MAX) {
                return Err(last(ResolveErrorKind::Initialization));
            }
            let mut raw = ptr::null_mut();
            let code = unsafe { WinHttpCreateProxyResolver(session.0, &mut raw) };
            if code != 0 {
                return Err(ResolveError::os(ResolveErrorKind::Initialization, code));
            }
            let resolver = Handle(raw);
            let request = Arc::new(Request::new(url, pac));
            let id = next_context()?;
            registry()
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .insert(id, Arc::clone(&request));
            // Nonzero context ensures HANDLE_CLOSING is delivered even on start failure.
            if unsafe {
                WinHttpSetOption(
                    resolver.0,
                    WINHTTP_OPTION_CONTEXT_VALUE,
                    (&id as *const usize).cast(),
                    std::mem::size_of::<usize>() as u32,
                )
            } == 0
            {
                let error = last(ResolveErrorKind::Initialization);
                registry()
                    .lock()
                    .unwrap_or_else(|e| e.into_inner())
                    .remove(&id);
                return Err(error);
            }
            remaining(deadline)?;
            let code = unsafe {
                WinHttpGetProxyForUrlEx(resolver.0, request.url.as_ptr(), &request.options.0, id)
            };
            if code != ERROR_IO_PENDING {
                return Err(classify(code));
            }
            let mut outcome = request.outcome.lock().unwrap_or_else(|e| e.into_inner());
            loop {
                if let Some(code) = *outcome {
                    if code != 0 {
                        return Err(classify(code));
                    }
                    break;
                }
                let budget = remaining(deadline)?;
                let (next, _) = request
                    .ready
                    .wait_timeout(outcome, budget)
                    .unwrap_or_else(|e| e.into_inner());
                outcome = next;
            }
            drop(outcome);
            let mut result = ProxyResult(WINHTTP_PROXY_RESULT::default());
            let code = unsafe { WinHttpGetProxyResult(resolver.0, &mut result.0) };
            if code != 0 {
                return Err(classify(code));
            }
            if result.0.cEntries == 0
                || result.0.cEntries as usize > MAX_ROUTES
                || result.0.pEntries.is_null()
            {
                return Err(ResolveError::new(ResolveErrorKind::InvalidResult));
            }
            let entries = unsafe {
                std::slice::from_raw_parts(result.0.pEntries, result.0.cEntries as usize)
            };
            let mut routes = Vec::with_capacity(entries.len());
            for entry in entries {
                if entry.fProxy == 0 {
                    // This includes a native bypass decision.
                    return Ok(vec![Route::Direct]);
                }
                let scheme = match entry.ProxyScheme {
                    WINHTTP_INTERNET_SCHEME_HTTP => ProxyScheme::Http,
                    WINHTTP_INTERNET_SCHEME_HTTPS => ProxyScheme::Https,
                    _ => {
                        routes.push(Route::UnsupportedScheme);
                        continue;
                    }
                };
                let host = unsafe { read_wide(entry.pwszProxy) }?
                    .filter(|s| !s.is_empty())
                    .ok_or_else(|| ResolveError::new(ResolveErrorKind::InvalidResult))?;
                if entry.ProxyPort == 0 {
                    return Err(ResolveError::new(ResolveErrorKind::InvalidResult));
                }
                return Ok(vec![Route::Proxy {
                    scheme,
                    host,
                    port: entry.ProxyPort,
                }]);
            }
            // Native result allocation is freed before resolver/session handles close.
            Ok(routes)
        }
    }

    // No callback receives a Rust pointer. It looks up a never-reused integer key and
    // clones an Arc under the registry lock. Concurrent callbacks/cancellation cannot
    // dereference freed memory. Inputs stay alive until the final HANDLE_CLOSING
    // notification; a timeout closes the resolver without waiting for PAC completion.
    static NEXT_CONTEXT: AtomicUsize = AtomicUsize::new(1);
    fn next_context() -> Result<usize, ResolveError> {
        let mut current = NEXT_CONTEXT.load(Ordering::Relaxed);
        loop {
            let next = current
                .checked_add(1)
                .ok_or_else(|| ResolveError::new(ResolveErrorKind::Initialization))?;
            match NEXT_CONTEXT.compare_exchange_weak(
                current,
                next,
                Ordering::Relaxed,
                Ordering::Relaxed,
            ) {
                Ok(_) => return Ok(current),
                Err(observed) => current = observed,
            }
        }
    }
    static REQUESTS: OnceLock<Mutex<HashMap<usize, Arc<Request>>>> = OnceLock::new();
    fn registry() -> &'static Mutex<HashMap<usize, Arc<Request>>> {
        REQUESTS.get_or_init(|| Mutex::new(HashMap::new()))
    }
    struct Request {
        url: Vec<u16>,
        _pac: Option<Vec<u16>>,
        options: SharedOptions,
        outcome: Mutex<Option<u32>>,
        ready: Condvar,
    }
    struct SharedOptions(WINHTTP_AUTOPROXY_OPTIONS);
    // The only non-null pointer is into Request::_pac, a stable heap allocation.
    // Options and input buffers are immutable and owned for all native callbacks.
    unsafe impl Send for SharedOptions {}
    unsafe impl Sync for SharedOptions {}
    impl Request {
        fn new(url: &str, pac: Option<&str>) -> Self {
            let pac = pac.map(wide);
            let options = WINHTTP_AUTOPROXY_OPTIONS {
                dwFlags: if pac.is_some() {
                    WINHTTP_AUTOPROXY_CONFIG_URL
                } else {
                    WINHTTP_AUTOPROXY_AUTO_DETECT
                },
                dwAutoDetectFlags: if pac.is_some() {
                    0
                } else {
                    WINHTTP_AUTO_DETECT_TYPE_DHCP | WINHTTP_AUTO_DETECT_TYPE_DNS_A
                },
                lpszAutoConfigUrl: pac.as_ref().map_or(ptr::null(), |v| v.as_ptr()),
                lpvReserved: ptr::null_mut(),
                dwReserved: 0,
                fAutoLogonIfChallenged: 0,
            };
            Self {
                url: wide(url),
                _pac: pac,
                options: SharedOptions(options),
                outcome: Mutex::new(None),
                ready: Condvar::new(),
            }
        }
    }
    unsafe extern "system" fn callback(
        _: *mut c_void,
        context: usize,
        status: u32,
        information: *mut c_void,
        length: u32,
    ) {
        if context == 0 {
            return;
        }
        let request = {
            let mut requests = registry().lock().unwrap_or_else(|e| e.into_inner());
            if status == WINHTTP_CALLBACK_STATUS_HANDLE_CLOSING {
                requests.remove(&context)
            } else {
                requests.get(&context).cloned()
            }
        };
        let Some(request) = request else {
            return;
        };
        let code = match status {
            WINHTTP_CALLBACK_STATUS_GETPROXYFORURL_COMPLETE => Some(0),
            WINHTTP_CALLBACK_STATUS_REQUEST_ERROR => {
                if information.is_null()
                    || length < std::mem::size_of::<WINHTTP_ASYNC_RESULT>() as u32
                {
                    Some(ERROR_WINHTTP_INTERNAL_ERROR)
                } else {
                    Some(
                        unsafe { ptr::read_unaligned(information.cast::<WINHTTP_ASYNC_RESULT>()) }
                            .dwError,
                    )
                }
            }
            WINHTTP_CALLBACK_STATUS_HANDLE_CLOSING => Some(ERROR_WINHTTP_OPERATION_CANCELLED),
            _ => None,
        };
        if let Some(code) = code {
            let mut outcome = request.outcome.lock().unwrap_or_else(|e| e.into_inner());
            if outcome.is_none() {
                *outcome = Some(code);
                request.ready.notify_all();
            }
        }
    }
    struct Handle(*mut c_void);
    impl Drop for Handle {
        fn drop(&mut self) {
            if !self.0.is_null() {
                unsafe {
                    WinHttpCloseHandle(self.0);
                }
            }
        }
    }
    struct ProxyResult(WINHTTP_PROXY_RESULT);
    impl Drop for ProxyResult {
        fn drop(&mut self) {
            unsafe {
                WinHttpFreeProxyResult(&mut self.0);
            }
        }
    }
    struct GlobalString(*mut u16);
    impl Drop for GlobalString {
        fn drop(&mut self) {
            if !self.0.is_null() {
                unsafe {
                    GlobalFree(self.0.cast());
                }
            }
        }
    }
    fn wide(value: &str) -> Vec<u16> {
        value.encode_utf16().chain(Some(0)).collect()
    }
    unsafe fn read_wide(value: *const u16) -> Result<Option<String>, ResolveError> {
        if value.is_null() {
            return Ok(None);
        }
        for len in 0..MAX_WIDE_UNITS {
            if unsafe { *value.add(len) } == 0 {
                let units = unsafe { std::slice::from_raw_parts(value, len) };
                return String::from_utf16(units)
                    .map(Some)
                    .map_err(|_| ResolveError::new(ResolveErrorKind::InvalidResult));
            }
        }
        Err(ResolveError::new(ResolveErrorKind::InvalidResult))
    }
    fn last(kind: ResolveErrorKind) -> ResolveError {
        ResolveError::os(kind, unsafe { GetLastError() })
    }
    fn classify(code: u32) -> ResolveError {
        let kind = match code {
            ERROR_WINHTTP_TIMEOUT => ResolveErrorKind::TimedOut,
            ERROR_WINHTTP_LOGIN_FAILURE => ResolveErrorKind::AuthenticationRequired,
            ERROR_WINHTTP_UNABLE_TO_DOWNLOAD_SCRIPT => ResolveErrorKind::PacDownload,
            ERROR_WINHTTP_BAD_AUTO_PROXY_SCRIPT
            | ERROR_WINHTTP_SCRIPT_EXECUTION_ERROR
            | ERROR_WINHTTP_UNHANDLED_SCRIPT_TYPE => ResolveErrorKind::PacScript,
            ERROR_WINHTTP_AUTODETECTION_FAILED => ResolveErrorKind::AutoDetection,
            ERROR_WINHTTP_INVALID_URL | ERROR_WINHTTP_UNRECOGNIZED_SCHEME => {
                ResolveErrorKind::InvalidConfiguration
            }
            _ => ResolveErrorKind::NativeFailure,
        };
        ResolveError::os(kind, code)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::RefCell;
    use std::collections::VecDeque;
    const URL: &str = "https://chatgpt.com/backend-api/wham/usage";
    struct Fake {
        config: RefCell<Option<Result<Configuration, ResolveError>>>,
        results: RefCell<VecDeque<Result<Vec<Route>, ResolveError>>>,
        calls: RefCell<Vec<(bool, Duration)>>,
        delay: Duration,
    }
    impl Fake {
        fn new(results: Vec<Result<Vec<Route>, ResolveError>>) -> Self {
            Self {
                config: RefCell::new(None),
                results: RefCell::new(results.into()),
                calls: RefCell::new(vec![]),
                delay: Duration::ZERO,
            }
        }
    }
    impl NativeApi for Fake {
        fn read_configuration(&self) -> Result<Configuration, ResolveError> {
            self.config.borrow_mut().take().expect("fake config")
        }
        fn resolve_attempt(
            &self,
            _: &str,
            pac: Option<&str>,
            timeout: Duration,
        ) -> Result<Vec<Route>, ResolveError> {
            self.calls.borrow_mut().push((pac.is_some(), timeout));
            std::thread::sleep(self.delay);
            self.results
                .borrow_mut()
                .pop_front()
                .expect("unexpected native call")
        }
    }
    fn config(pac: bool, auto_detect: bool, static_present: bool) -> Configuration {
        Configuration {
            pac_url: pac.then(|| "https://private.example/config.pac".into()),
            auto_detect,
            static_proxy: static_present.then(|| "https=static.example:8080".into()),
            static_bypass: None,
        }
    }
    fn failure(kind: ResolveErrorKind) -> Result<Vec<Route>, ResolveError> {
        Err(ResolveError::new(kind))
    }
    fn assert_proxy(routes: &ResolvedRoutes, source: ResolutionSource, expected: &str) {
        assert_eq!(routes.source, source);
        assert!(
            matches!(routes.routes.as_slice(), [Route::Proxy { host, port: 8080, .. }] if host == expected)
        );
    }
    #[test]
    fn configuration_exposes_only_closed_state_and_presence() {
        for (pac, auto, fixed, expected) in [
            (false, false, false, ConfigState::Absent),
            (false, false, true, ConfigState::StaticOnly),
            (true, false, false, ConfigState::Pac),
            (false, true, false, ConfigState::AutoDetect),
            (true, true, true, ConfigState::PacAndAutoDetect),
        ] {
            let fake = Fake::new(vec![]);
            *fake.config.borrow_mut() = Some(Ok(config(pac, auto, fixed)));
            let cfg = Configuration::read_with(&fake).unwrap();
            assert_eq!(cfg.state(), expected);
            assert_eq!(
                (cfg.has_pac(), cfg.auto_detect(), cfg.has_static()),
                (pac, auto, fixed)
            );
            let diagnostic = format!("{:?}", cfg.state());
            assert!(!diagnostic.contains("private.example"));
            assert!(!diagnostic.contains("static.example"));
            assert!(fake.calls.borrow().is_empty());
        }
    }
    #[test]
    fn autodetect_12180_uses_static_instead_of_fatal_error() {
        let fake = Fake::new(vec![Err(ResolveError {
            kind: ResolveErrorKind::AutoDetection,
            os_code: Some(12180),
        })]);
        let result = config(false, true, true)
            .resolve_with(&fake, URL, Duration::from_secs(1))
            .unwrap();
        assert_proxy(&result, ResolutionSource::Static, "static.example");
        assert_eq!(fake.calls.borrow().len(), 1);
    }
    #[test]
    fn autodetect_12180_static_route_reaches_production_connect_without_replay() {
        use std::io::{Read, Write};
        use std::net::TcpListener;
        // Both destination and proxy are loopback, with no credentials, TLS
        // handshake, DNS lookup, actual WinHTTP, or external traffic.
        for status in [403, 407] {
            let listener = TcpListener::bind("127.0.0.1:0").unwrap();
            listener.set_nonblocking(true).unwrap();
            let address = listener.local_addr().unwrap();
            let server = std::thread::spawn(move || {
                let deadline = Instant::now() + Duration::from_secs(3);
                let mut requests = Vec::new();
                while Instant::now() < deadline {
                    match listener.accept() {
                        Ok((mut stream, _)) => {
                            stream
                                .set_read_timeout(Some(Duration::from_secs(1)))
                                .unwrap();
                            let mut request = Vec::new();
                            let mut byte = [0; 1];
                            while request.len() < 16 * 1024 {
                                if stream.read(&mut byte).unwrap_or(0) == 0 {
                                    break;
                                }
                                request.push(byte[0]);
                                if request.ends_with(b"\r\n\r\n") {
                                    break;
                                }
                            }
                            requests.push(String::from_utf8(request).unwrap());
                            write!(stream, "HTTP/1.1 {status} Rejected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").unwrap();
                            break;
                        }
                        Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                            std::thread::sleep(Duration::from_millis(5));
                        }
                        Err(error) => panic!("loopback accept failed: {error}"),
                    }
                }
                std::thread::sleep(Duration::from_millis(100));
                assert!(
                    matches!(listener.accept(), Err(error) if error.kind() == std::io::ErrorKind::WouldBlock),
                    "CONNECT must not be replayed"
                );
                requests
            });
            let fake = Fake::new(vec![Err(ResolveError {
                kind: ResolveErrorKind::AutoDetection,
                os_code: Some(12180),
            })]);
            let mut cfg = config(false, true, false);
            cfg.static_proxy = Some(format!("PROXY {address}; DIRECT"));
            let target = "https://127.0.0.1:9/no-external-network";
            let resolution = cfg
                .resolve_with(&fake, target, Duration::from_secs(1))
                .unwrap();
            assert_eq!(resolution.source, ResolutionSource::Static);
            assert_eq!(resolution.routes.len(), 1);
            let client = crate::http::explicit_client(&resolution.routes[0], None).unwrap();
            assert!(client
                .get(target)
                .timeout(Duration::from_secs(1))
                .send()
                .is_err());
            let requests = server.join().unwrap();
            assert_eq!(
                requests.len(),
                1,
                "old 12180-fatal path reaches zero CONNECTs"
            );
            assert!(requests[0].starts_with("CONNECT 127.0.0.1:9 HTTP/1.1\r\n"));
            assert!(!requests[0].to_ascii_lowercase().contains("authorization:"));
        }
    }
    #[test]
    fn every_auto_error_class_can_fall_back_to_static() {
        for kind in [
            ResolveErrorKind::TimedOut,
            ResolveErrorKind::AuthenticationRequired,
            ResolveErrorKind::PacDownload,
            ResolveErrorKind::PacScript,
            ResolveErrorKind::AutoDetection,
            ResolveErrorKind::NativeFailure,
            ResolveErrorKind::Initialization,
            ResolveErrorKind::InvalidResult,
        ] {
            let fake = Fake::new(vec![failure(kind)]);
            let result = config(false, true, true)
                .resolve_with(&fake, URL, Duration::from_secs(1))
                .unwrap();
            assert_proxy(&result, ResolutionSource::Static, "static.example");
        }
    }
    #[test]
    fn pac_and_autodetect_failures_use_static() {
        let fake = Fake::new(vec![
            failure(ResolveErrorKind::PacDownload),
            failure(ResolveErrorKind::AutoDetection),
        ]);
        let result = config(true, true, true)
            .resolve_with(&fake, URL, Duration::from_secs(1))
            .unwrap();
        assert_proxy(&result, ResolutionSource::Static, "static.example");
        assert_eq!(
            fake.calls.borrow().iter().map(|v| v.0).collect::<Vec<_>>(),
            vec![true, false]
        );
    }
    #[test]
    fn static_bypass_is_direct_after_auto_failure() {
        let fake = Fake::new(vec![failure(ResolveErrorKind::AutoDetection)]);
        let mut cfg = config(false, true, true);
        cfg.static_bypass = Some("*.chatgpt.com;chatgpt.com".into());
        let result = cfg
            .resolve_with(&fake, URL, Duration::from_secs(1))
            .unwrap();
        assert_eq!(result.source, ResolutionSource::Static);
        assert!(matches!(result.routes.as_slice(), [Route::Direct]));
    }
    #[test]
    fn autodetect_failure_without_static_is_unavailable() {
        let fake = Fake::new(vec![Err(ResolveError {
            kind: ResolveErrorKind::AutoDetection,
            os_code: Some(12180),
        })]);
        assert!(matches!(
            config(false, true, false).resolve_with(&fake, URL, Duration::from_secs(1)),
            Err(ResolveError {
                kind: ResolveErrorKind::AutoDetection,
                os_code: Some(12180)
            })
        ));
    }
    #[test]
    fn absent_and_static_settings_never_start_discovery() {
        let fake = Fake::new(vec![]);
        let result = config(false, false, false)
            .resolve_with(&fake, URL, Duration::ZERO)
            .unwrap();
        assert_eq!(result.source, ResolutionSource::Direct);
        assert!(matches!(result.routes.as_slice(), [Route::Direct]));
        let result = config(false, false, true)
            .resolve_with(&fake, URL, Duration::ZERO)
            .unwrap();
        assert_proxy(&result, ResolutionSource::Static, "static.example");
        assert!(fake.calls.borrow().is_empty());
    }
    #[test]
    fn pac_failure_does_not_enable_wpad() {
        let fake = Fake::new(vec![failure(ResolveErrorKind::PacDownload)]);
        let result = config(true, false, true)
            .resolve_with(&fake, URL, Duration::from_secs(1))
            .unwrap();
        assert_proxy(&result, ResolutionSource::Static, "static.example");
        assert_eq!(
            fake.calls.borrow().iter().map(|v| v.0).collect::<Vec<_>>(),
            vec![true]
        );
    }
    #[test]
    fn pac_direct_takes_priority_over_static_and_autodetect() {
        let fake = Fake::new(vec![Ok(vec![Route::Direct])]);
        let result = config(true, true, true)
            .resolve_with(&fake, URL, Duration::from_secs(1))
            .unwrap();
        assert_eq!(result.source, ResolutionSource::Automatic);
        assert!(matches!(result.routes.as_slice(), [Route::Direct]));
        assert_eq!(fake.calls.borrow().len(), 1);
    }
    #[test]
    fn autodetect_requires_setting_and_shares_deadline() {
        let mut fake = Fake::new(vec![
            failure(ResolveErrorKind::PacDownload),
            Ok(vec![Route::Direct]),
        ]);
        fake.delay = Duration::from_millis(5);
        let result = config(true, true, false)
            .resolve_with(&fake, URL, Duration::from_secs(1))
            .unwrap();
        assert!(matches!(result.routes.as_slice(), [Route::Direct]));
        let calls = fake.calls.borrow();
        assert!(calls[0].0 && !calls[1].0);
        assert!(calls[1].1 < calls[0].1);
    }
    #[test]
    fn exhausted_budget_skips_second_native_attempt_but_preserves_static() {
        for fixed in [false, true] {
            let mut fake = Fake::new(vec![failure(ResolveErrorKind::PacDownload)]);
            fake.delay = Duration::from_millis(5);
            let result =
                config(true, true, fixed).resolve_with(&fake, URL, Duration::from_millis(1));
            if fixed {
                assert_proxy(&result.unwrap(), ResolutionSource::Static, "static.example");
            } else {
                assert!(matches!(
                    result,
                    Err(ResolveError {
                        kind: ResolveErrorKind::TimedOut,
                        ..
                    })
                ));
            }
            assert_eq!(fake.calls.borrow().len(), 1);
        }
    }
    #[test]
    fn only_first_usable_native_candidate_is_exposed_to_transport() {
        let fake = Fake::new(vec![Ok(vec![
            Route::UnsupportedScheme,
            Route::Proxy {
                scheme: ProxyScheme::Http,
                host: "first.example".into(),
                port: 8080,
            },
            Route::Direct,
        ])]);
        let result = config(true, true, true)
            .resolve_with(&fake, URL, Duration::from_secs(1))
            .unwrap();
        assert_proxy(&result, ResolutionSource::Automatic, "first.example");
        assert_eq!(fake.calls.borrow().len(), 1);
    }
    #[test]
    fn unusable_native_result_is_unavailable_and_uses_static() {
        for routes in [vec![], vec![Route::UnsupportedScheme]] {
            let fake = Fake::new(vec![Ok(routes)]);
            let result = config(true, false, true)
                .resolve_with(&fake, URL, Duration::from_secs(1))
                .unwrap();
            assert_proxy(&result, ResolutionSource::Static, "static.example");
        }
    }
    #[test]
    fn static_parser_preserves_official_protocol_mapping_and_first_candidate() {
        for (input, scheme, host, port) in [
            (
                "http=plain.example:80;https=secure.example:8080",
                ProxyScheme::Http,
                "secure.example",
                8080,
            ),
            (
                "http=plain.example:80 https=secure.example:8080",
                ProxyScheme::Http,
                "secure.example",
                8080,
            ),
            ("bare.example:8080", ProxyScheme::Http, "bare.example", 8080),
            (
                "PROXY first.example:8080; DIRECT",
                ProxyScheme::Http,
                "first.example",
                8080,
            ),
            (
                "HTTP first.example:8080",
                ProxyScheme::Http,
                "first.example",
                8080,
            ),
            (
                "HTTPS tls.example:8080",
                ProxyScheme::Https,
                "tls.example",
                8080,
            ),
            (
                "https=https://tls.example:8080",
                ProxyScheme::Https,
                "tls.example",
                8080,
            ),
            (
                "SOCKS ignored.example:1080; PROXY first.example:8080",
                ProxyScheme::Http,
                "first.example",
                8080,
            ),
            ("proxy.example", ProxyScheme::Http, "proxy.example", 80),
            ("[::1]:8080", ProxyScheme::Http, "::1", 8080),
        ] {
            assert!(
                matches!(static_route(input, "https").unwrap(), Route::Proxy { scheme: actual_scheme, host: actual_host, port: actual_port } if actual_scheme == scheme && actual_host == host && actual_port == port)
            );
        }
        assert!(matches!(
            static_route("DIRECT; PROXY ignored.example:8080", "https").unwrap(),
            Route::Direct
        ));
        assert!(matches!(
            static_route("SOCKS ignored.example:1080; DIRECT", "https").unwrap(),
            Route::Direct
        ));
        assert!(static_route("http=ignored.example:80", "https").is_err());
        assert!(static_route("SOCKS ignored.example:1080", "https").is_err());
    }
    #[test]
    fn bypass_matches_official_local_domain_wildcard_ip_and_port_rules() {
        for (url, bypass, expected) in [
            ("https://intranet/", "<local>", true),
            ("https://chatgpt.com/", "<local>", false),
            ("https://chatgpt.com/", ".chatgpt.com", true),
            ("https://api.chatgpt.com/", ".chatgpt.com", true),
            ("https://evilchatgpt.com/", ".chatgpt.com", false),
            ("https://api.chatgpt.com/", "*.chatgpt.com", true),
            (
                "https://chatgpt.com/",
                "other.example;chatgpt.com:443",
                true,
            ),
            ("https://chatgpt.com/", "chatgpt.com:80", false),
            ("https://127.0.0.1/", "127.0.0.1", true),
            ("https://[::1]/", "[::1]", true),
            ("https://chatgpt.com/", "*", true),
        ] {
            assert_eq!(
                proxy_bypass_matches_origin(bypass, &RequestOrigin::parse(url).unwrap()),
                expected
            );
        }
    }
    #[test]
    fn invalid_private_configuration_returns_only_closed_errors() {
        for proxy in [
            "http://secret:password@private.example:8080",
            "http://private.example/path",
            "private.example:0",
        ] {
            let error = match static_route(proxy, "https") {
                Err(error) => error,
                Ok(_) => panic!("invalid route accepted"),
            };
            assert_eq!(
                format!("{error:?}"),
                "ResolveError { kind: InvalidStaticRoute, os_code: None }"
            );
        }
    }
    #[test]
    fn automatic_configuration_errors_are_distinct_from_selected_static_errors() {
        let fake = Fake::new(vec![failure(ResolveErrorKind::InvalidConfiguration)]);
        let error = config(true, false, false)
            .resolve_with(&fake, URL, Duration::from_secs(1))
            .err()
            .unwrap();
        assert_eq!(error.kind, ResolveErrorKind::InvalidConfiguration);
        assert_ne!(error.kind, ResolveErrorKind::InvalidStaticRoute);
        let unavailable = static_route("http=private.example:8080", "https")
            .err()
            .unwrap();
        assert_eq!(unavailable.kind, ResolveErrorKind::InvalidResult);
    }
    #[test]
    fn zero_budget_or_bad_url_never_reaches_native_api() {
        let fake = Fake::new(vec![]);
        let cfg = config(true, false, false);
        assert!(matches!(
            cfg.resolve_with(&fake, URL, Duration::ZERO),
            Err(ResolveError {
                kind: ResolveErrorKind::TimedOut,
                ..
            })
        ));
        assert!(matches!(
            cfg.resolve_with(&fake, "https://example.test/\0bad", Duration::from_secs(1)),
            Err(ResolveError {
                kind: ResolveErrorKind::InvalidUrl,
                ..
            })
        ));
        assert!(fake.calls.borrow().is_empty());
    }
    #[test]
    fn resolution_budget_is_capped() {
        let fake = Fake::new(vec![Ok(vec![Route::Direct])]);
        config(false, true, false)
            .resolve_with(&fake, URL, Duration::from_secs(300))
            .unwrap();
        assert!(fake.calls.borrow()[0].1 <= MAX_RESOLUTION_TIME);
    }
    #[cfg(not(windows))]
    #[test]
    fn actual_non_windows_platform_is_explicitly_unsupported() {
        assert!(matches!(
            Configuration::read_current_user(),
            Err(ResolveError {
                kind: ResolveErrorKind::UnsupportedPlatform,
                ..
            })
        ));
    }
}
