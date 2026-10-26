// Copyright 2025 OpenAI
// SPDX-License-Identifier: Apache-2.0
// Adapted request shape and custom-CA semantics from OpenAI Codex rust-v0.159.0.
// CHANGED: standalone fixed GET transport, bounded IO, closed errors, no login or mutation.
// See ../THIRD_PARTY_NOTICES.md for upstream sources and the reduced scope.
use crate::auth::Credentials;
#[cfg(any(windows, test))]
use crate::error::TlsBackend;
use crate::error::{
    ErrorLayer, Failure, IoKind, NetworkDiagnostics, NetworkRoute, NetworkSnapshot, ProxyProtocol,
    ReaderError, SystemProxyConfig, SystemProxyResolution,
};
use std::error::Error;
use std::io::Read;
use std::time::Duration;

pub const MAX_RESPONSE_BYTES: usize = 256 * 1024;
pub const USAGE_URL: &str = "https://chatgpt.com/backend-api/wham/usage";
pub const RESET_CREDITS_URL: &str = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";
const MAX_ERROR_DEPTH: usize = 32;
#[cfg(any(windows, test))]
const MAX_CA_BYTES: usize = 1024 * 1024;

/// The caller can select only two fixed, read-only official endpoints.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Endpoint {
    Usage,
    ResetCredits,
}
impl Endpoint {
    pub fn url(self) -> &'static str {
        match self {
            Self::Usage => USAGE_URL,
            Self::ResetCredits => RESET_CREDITS_URL,
        }
    }
    fn timeout(self) -> Duration {
        Duration::from_secs(match self {
            Self::Usage => 10,
            Self::ResetCredits => 5,
        })
    }
}

pub trait Transport {
    fn get(&self, endpoint: Endpoint, credentials: &Credentials) -> Result<Vec<u8>, Failure>;
    fn network_diagnostics(&self) -> Option<NetworkDiagnostics> {
        None
    }
}

pub struct OfficialTransport {
    #[cfg(not(windows))]
    client: reqwest::blocking::Client,
    diagnostics: NetworkSnapshot,
    #[cfg(windows)]
    ca_path: Option<std::path::PathBuf>,
}
impl OfficialTransport {
    #[cfg(windows)]
    pub fn new(diagnostics: NetworkSnapshot) -> Result<Self, Failure> {
        let ca = configured_ca_path(|key| std::env::var(key).ok());
        let mut profile = diagnostics.load().clear_error();
        profile.ca_override = ca.is_some();
        diagnostics.store(profile);
        let builder = configure_ca(client_builder(), ca.as_deref())
            .map_err(|kind| Failure::new(kind).with_network(profile))?;
        profile.tls_backend = if ca.is_some() {
            TlsBackend::Rustls
        } else {
            TlsBackend::NativeTls
        };
        diagnostics.store(profile);
        let _client = builder.build().map_err(|error| {
            let detail = inspect_error(&error, profile);
            diagnostics.store(detail);
            Failure::new(if ca.is_some() {
                ReaderError::CertificateConfiguration
            } else {
                classify_transport(&error)
            })
            .with_network(detail)
        })?;
        Ok(Self {
            diagnostics,
            ca_path: ca,
        })
    }
    #[cfg(not(windows))]
    pub fn new(diagnostics: NetworkSnapshot) -> Result<Self, Failure> {
        // This deliverable targets Windows. Tests inject clients and synthetic credentials.
        Err(Failure::new(ReaderError::UnsupportedConfiguration).with_network(diagnostics.load()))
    }
}

/// Read only the actual system matcher decision, never reconstruct proxy precedence.
/// The helper has one client and one fixed HTTPS origin; pooled requests retain that decision.
pub fn observed_network(mut profile: NetworkDiagnostics) -> NetworkDiagnostics {
    // Explicit system-resolved clients use no_proxy(), so the legacy matcher
    // snapshot belongs to a different route and must not overwrite this request.
    if !matches!(
        profile.system_proxy_resolution,
        SystemProxyResolution::NotChecked | SystemProxyResolution::NotNeeded
    ) || profile.no_proxy_bypass
    {
        return profile;
    }
    use hyper_util::client::proxy::matcher::{
        last_system_route, ProxyProtocol as ObservedProtocol, SystemRouteSource,
    };
    if let Some(observed) = last_system_route() {
        profile.route = match observed.source {
            SystemRouteSource::Direct => NetworkRoute::Direct,
            SystemRouteSource::Environment => NetworkRoute::EnvironmentProxy,
            SystemRouteSource::WindowsSystem => NetworkRoute::WindowsProxy,
            SystemRouteSource::MacosSystem => NetworkRoute::OtherSystemProxy,
            SystemRouteSource::Unknown => NetworkRoute::Unknown,
        };
        profile.proxy_protocol = match observed.protocol {
            ObservedProtocol::None => ProxyProtocol::None,
            ObservedProtocol::Http => ProxyProtocol::Http,
            ObservedProtocol::Https => ProxyProtocol::Https,
            ObservedProtocol::Socks4 => ProxyProtocol::Socks4,
            ObservedProtocol::Socks4a => ProxyProtocol::Socks4a,
            ObservedProtocol::Socks5 => ProxyProtocol::Socks5,
            ObservedProtocol::Socks5h => ProxyProtocol::Socks5h,
            ObservedProtocol::Unknown => ProxyProtocol::Other,
        };
        profile.no_proxy_bypass = observed.no_proxy_bypass;
    }
    profile
}

#[cfg(any(windows, test))]
pub(crate) fn client_builder() -> reqwest::blocking::ClientBuilder {
    reqwest::blocking::Client::builder()
        // Keep reqwest's default environment/system proxy routing and native TLS.
        // Automatic system routes are resolved separately and explicitly applied.
        // No TLS-backend retry or certificate-verification bypass is used.
        .redirect(reqwest::redirect::Policy::none())
        .https_only(true)
        .min_tls_version(reqwest::tls::Version::TLS_1_2)
        // These are the helper's bounded-request policy, not upstream defaults.
        .connect_timeout(Duration::from_secs(5))
        .timeout(Duration::from_secs(10))
        .user_agent(concat!("CodexQuotaTray/", env!("CARGO_PKG_VERSION")))
}

#[cfg(any(windows, test))]
fn configured_ca_path(env: impl Fn(&str) -> Option<String>) -> Option<std::path::PathBuf> {
    ["CODEX_CA_CERTIFICATE", "SSL_CERT_FILE"]
        .into_iter()
        .find_map(|key| env(key).filter(|value| !value.is_empty()))
        .map(std::path::PathBuf::from)
}

#[cfg(any(windows, test))]
fn configure_ca(
    mut builder: reqwest::blocking::ClientBuilder,
    path: Option<&std::path::Path>,
) -> Result<reqwest::blocking::ClientBuilder, ReaderError> {
    let Some(path) = path else { return Ok(builder) };
    let mut bytes = Vec::new();
    std::fs::File::open(path)
        .and_then(|file| file.take((MAX_CA_BYTES + 1) as u64).read_to_end(&mut bytes))
        .map_err(|_| ReaderError::CertificateConfiguration)?;
    if bytes.len() > MAX_CA_BYTES {
        return Err(ReaderError::CertificateConfiguration);
    }
    // Upstream selects rustls for custom roots, while retaining native root certificates.
    builder = builder.use_rustls_tls();
    for certificate in parse_ca_certificates(&bytes)? {
        builder = builder.add_root_certificate(certificate);
    }
    Ok(builder)
}

#[cfg(any(windows, test))]
fn parse_ca_certificates(bytes: &[u8]) -> Result<Vec<reqwest::Certificate>, ReaderError> {
    use rustls::pki_types::pem::{PemObject, SectionKind};
    // Match Codex's OpenSSL TRUSTED CERTIFICATE / X509_AUX normalization and CRL
    // handling. The upstream source is http-client/src/custom_ca.rs at rust-v0.159.0.
    let text = String::from_utf8_lossy(bytes);
    let trusted = text.contains("TRUSTED CERTIFICATE");
    let normalized = text
        .replace("BEGIN TRUSTED CERTIFICATE", "BEGIN CERTIFICATE")
        .replace("END TRUSTED CERTIFICATE", "END CERTIFICATE");
    let mut certificates = Vec::new();
    for section in <(SectionKind, Vec<u8>)>::pem_slice_iter(normalized.as_bytes()) {
        let (kind, der) = section.map_err(|_| ReaderError::CertificateConfiguration)?;
        if kind == SectionKind::Certificate {
            let der = if trusted {
                first_der_item(&der).ok_or(ReaderError::CertificateConfiguration)?
            } else {
                &der
            };
            certificates.push(
                reqwest::Certificate::from_der(der)
                    .map_err(|_| ReaderError::CertificateConfiguration)?,
            );
        }
    }
    if certificates.is_empty() {
        return Err(ReaderError::CertificateConfiguration);
    }
    Ok(certificates)
}

#[cfg(any(windows, test))]
fn first_der_item(der: &[u8]) -> Option<&[u8]> {
    let length = *der.get(1)?;
    let end = if length & 0x80 == 0 {
        2usize.checked_add(usize::from(length))?
    } else {
        let count = usize::from(length & 0x7f);
        if count == 0 {
            return None;
        }
        let header = 2usize.checked_add(count)?;
        let mut length = 0usize;
        for byte in der.get(2..header)? {
            length = length.checked_mul(256)?.checked_add(usize::from(*byte))?;
        }
        header.checked_add(length)?
    };
    der.get(..end)
}

impl Transport for OfficialTransport {
    fn get(&self, endpoint: Endpoint, credentials: &Credentials) -> Result<Vec<u8>, Failure> {
        let mut profile = self.diagnostics.load().clear_error();
        profile.route = NetworkRoute::Unknown;
        profile.proxy_protocol = ProxyProtocol::None;
        profile.no_proxy_bypass = false;
        profile.system_proxy_config = SystemProxyConfig::NotChecked;
        profile.system_proxy_static_present = false;
        profile.system_proxy_resolution = SystemProxyResolution::NotChecked;
        self.diagnostics.store(profile);
        #[cfg(windows)]
        let result = self.get_windows(endpoint, credentials, profile);
        #[cfg(not(windows))]
        let result = execute_profile(
            &self.client,
            endpoint.url(),
            endpoint.timeout(),
            credentials,
            profile,
        );
        let network = observed_network(match &result {
            Err(failure) => failure.network.unwrap_or(self.diagnostics.load()),
            Ok(_) => self.diagnostics.load(),
        });
        self.diagnostics.store(network);
        result.map_err(|failure| failure.with_network(network))
    }
    fn network_diagnostics(&self) -> Option<NetworkDiagnostics> {
        Some(self.diagnostics.load())
    }
}

#[cfg(windows)]
impl OfficialTransport {
    fn get_windows(
        &self,
        endpoint: Endpoint,
        credentials: &Credentials,
        mut profile: NetworkDiagnostics,
    ) -> Result<Vec<u8>, Failure> {
        use crate::proxy_policy::execute_routes;
        use crate::system_proxy::{ConfigState, Configuration, ResolutionSource, ResolveErrorKind};
        let deadline = std::time::Instant::now() + endpoint.timeout() + Duration::from_secs(5);
        // RespectSystemProxy: the system decision precedes environment variables.
        let configuration = match Configuration::read_current_user() {
            Ok(configuration) => configuration,
            Err(_) => {
                profile.system_proxy_config = SystemProxyConfig::ReadFailed;
                return self.get_environment_fallback(endpoint, credentials, profile);
            }
        };
        profile.system_proxy_config = match configuration.state() {
            ConfigState::Absent => SystemProxyConfig::Absent,
            ConfigState::StaticOnly => SystemProxyConfig::StaticOnly,
            ConfigState::Pac => SystemProxyConfig::AutoConfig,
            ConfigState::AutoDetect => SystemProxyConfig::AutoDetect,
            ConfigState::PacAndAutoDetect => SystemProxyConfig::AutoConfigAndDetect,
        };
        profile.system_proxy_static_present = configuration.has_static();
        profile.system_proxy_resolution = SystemProxyResolution::Resolving;
        self.diagnostics.store(profile);
        let resolved = match configuration.resolve(endpoint.url(), Duration::from_secs(5)) {
            Ok(resolved) => resolved,
            // An explicitly selected but unsupported static route is not Unavailable.
            Err(error) if error.kind == ResolveErrorKind::InvalidStaticRoute => {
                return Err(crate::proxy_policy::resolution_failure(
                    profile,
                    false,
                    error.os_code,
                ));
            }
            // Resolution unavailability is not a connection or policy rejection.
            Err(_) => return self.get_environment_fallback(endpoint, credentials, profile),
        };
        if resolved.source == ResolutionSource::Static {
            profile.route = NetworkRoute::WindowsProxy;
        }
        execute_routes(
            &resolved.routes,
            deadline,
            endpoint.timeout(),
            profile,
            |network| self.diagnostics.store(network),
            |route, timeout, network| {
                let client = explicit_client(route, self.ca_path.as_deref())
                    .map_err(|kind| Failure::new(kind).with_network(network))?;
                execute_profile(&client, endpoint.url(), timeout, credentials, network)
            },
        )
    }

    fn get_environment_fallback(
        &self,
        endpoint: Endpoint,
        credentials: &Credentials,
        mut profile: NetworkDiagnostics,
    ) -> Result<Vec<u8>, Failure> {
        profile.system_proxy_resolution = SystemProxyResolution::Unavailable;
        let route =
            crate::proxy_policy::environment_route(endpoint.url(), |key| std::env::var(key).ok())
                .map_err(|kind| Failure::new(kind).with_network(profile))?;
        let (client, profile) =
            environment_client(&route, endpoint.url(), self.ca_path.as_deref(), profile)
                .map_err(|kind| Failure::new(kind).with_network(profile))?;
        self.diagnostics.store(profile);
        execute_profile(
            &client,
            endpoint.url(),
            endpoint.timeout(),
            credentials,
            profile,
        )
    }
}

#[cfg(any(windows, test))]
fn environment_client(
    route: &crate::proxy_policy::EnvironmentRoute,
    url: &str,
    ca: Option<&std::path::Path>,
    mut profile: NetworkDiagnostics,
) -> Result<(reqwest::blocking::Client, NetworkDiagnostics), ReaderError> {
    use crate::proxy_policy::EnvironmentRoute;
    use hyper_util::client::proxy::matcher::Matcher;
    let mut builder = configure_ca(client_builder().no_proxy(), ca)?;
    profile.route = NetworkRoute::Direct;
    profile.proxy_protocol = ProxyProtocol::None;
    profile.no_proxy_bypass = false;
    if let EnvironmentRoute::Proxy {
        url: proxy_url,
        no_proxy,
    } = route
    {
        let proxy = reqwest::Proxy::all(proxy_url)
            .map_err(|_| ReaderError::ProxyConfiguration)?
            .no_proxy(no_proxy.as_deref().and_then(reqwest::NoProxy::from_string));
        builder = builder.proxy(proxy);
        // The same locked matcher used by reqwest provides safe route diagnostics.
        let mut matcher = Matcher::builder().all(proxy_url);
        if let Some(no_proxy) = no_proxy {
            matcher = matcher.no(no_proxy);
        }
        let uri = url.parse().map_err(|_| ReaderError::InvalidArguments)?;
        if matcher.build().intercept(&uri).is_none() {
            profile.no_proxy_bypass = true;
        } else {
            profile.route = NetworkRoute::EnvironmentProxy;
            let parsed = reqwest::Url::parse(proxy_url)
                .or_else(|_| reqwest::Url::parse(&format!("http://{proxy_url}")))
                .map_err(|_| ReaderError::ProxyConfiguration)?;
            profile.proxy_protocol = match parsed.scheme() {
                "http" => ProxyProtocol::Http,
                "https" => ProxyProtocol::Https,
                "socks4" => ProxyProtocol::Socks4,
                "socks4a" => ProxyProtocol::Socks4a,
                "socks5" => ProxyProtocol::Socks5,
                "socks5h" => ProxyProtocol::Socks5h,
                _ => ProxyProtocol::Other,
            };
        }
    }
    Ok((
        builder
            .build()
            .map_err(|_| ReaderError::ProxyConfiguration)?,
        profile,
    ))
}

#[cfg(any(windows, test))]
pub(crate) fn explicit_client(
    route: &crate::system_proxy::Route,
    ca: Option<&std::path::Path>,
) -> Result<reqwest::blocking::Client, ReaderError> {
    use crate::system_proxy::Route;
    let builder = configure_ca(client_builder().no_proxy(), ca)?;
    let builder = match route {
        Route::Direct => builder,
        Route::Proxy { scheme, host, port } => builder.proxy(
            reqwest::Proxy::all(proxy_url(*scheme, host, *port)?)
                .map_err(|_| ReaderError::ProxyConfiguration)?,
        ),
        Route::UnsupportedScheme => return Err(ReaderError::ProxyConfiguration),
    };
    builder.build().map_err(|_| ReaderError::ProxyConfiguration)
}

#[cfg(any(windows, test))]
fn proxy_url(
    scheme: crate::system_proxy::ProxyScheme,
    host: &str,
    port: u16,
) -> Result<reqwest::Url, ReaderError> {
    use crate::system_proxy::ProxyScheme;
    // WinHTTP returns a host and port, not a credential-bearing arbitrary URL.
    if host.is_empty()
        || host.len() > 2048
        || port == 0
        || host
            .chars()
            .any(|c| c.is_whitespace() || matches!(c, '/' | '\\' | '@' | '?' | '#' | '%'))
    {
        return Err(ReaderError::ProxyConfiguration);
    }
    let scheme = match scheme {
        ProxyScheme::Http => "http",
        ProxyScheme::Https => "https",
    };
    let host = if host.contains(':') && !host.starts_with('[') {
        format!("[{host}]")
    } else {
        host.to_owned()
    };
    let value = reqwest::Url::parse(&format!("{scheme}://{host}:{port}/"))
        .map_err(|_| ReaderError::ProxyConfiguration)?;
    if value.host_str().is_none()
        || !value.username().is_empty()
        || value.password().is_some()
        || value.path() != "/"
        || value.query().is_some()
        || value.fragment().is_some()
    {
        return Err(ReaderError::ProxyConfiguration);
    }
    Ok(value)
}

#[cfg(test)]
fn execute(
    client: &reqwest::blocking::Client,
    url: &str,
    timeout: Duration,
    credentials: &Credentials,
) -> Result<Vec<u8>, Failure> {
    execute_profile(
        client,
        url,
        timeout,
        credentials,
        NetworkDiagnostics::default(),
    )
}

fn execute_profile(
    client: &reqwest::blocking::Client,
    url: &str,
    timeout: Duration,
    credentials: &Credentials,
    profile: NetworkDiagnostics,
) -> Result<Vec<u8>, Failure> {
    if !header_value(&credentials.access_token, 32768)
        || !header_value(&credentials.account_id, 1024)
    {
        return Err(ReaderError::InvalidCredentials.into());
    }
    let response = client
        .get(url)
        .timeout(timeout)
        .header(
            "Authorization",
            format!("Bearer {}", credentials.access_token),
        )
        .header("ChatGPT-Account-ID", &credentials.account_id)
        .header("Accept", "application/json")
        .header("Cache-Control", "no-store")
        .send()
        .map_err(|error| {
            Failure::new(classify_transport(&error)).with_network(inspect_error(&error, profile))
        })?;
    if !response.status().is_success() {
        return Err(Failure::http(response.status().as_u16())
            .with_network(profile.with_error(ErrorLayer::HttpStatus)));
    }
    // Never consume an error body. Successful bodies are bounded regardless of advertised size.
    if response
        .content_length()
        .is_some_and(|length| length > MAX_RESPONSE_BYTES as u64)
    {
        return Err(ReaderError::ResponseTooLarge.into());
    }
    read_bounded_with_profile(response, profile)
}

fn chain_source<'a>(error: &'a (dyn Error + 'static)) -> Option<&'a (dyn Error + 'static)> {
    // io::Error::source() skips its boxed error; visit get_ref() instead.
    error
        .downcast_ref::<std::io::Error>()
        .and_then(|io| io.get_ref().map(|inner| inner as &(dyn Error + 'static)))
        .or_else(|| error.source())
}

fn inspected_chain<'a>(error: &'a (dyn Error + 'static)) -> (Vec<&'a (dyn Error + 'static)>, bool) {
    let mut chain: Vec<&(dyn Error + 'static)> = Vec::new();
    let mut next = Some(error);
    while let Some(source) = next {
        if chain.len() == MAX_ERROR_DEPTH || chain.iter().any(|seen| std::ptr::eq(*seen, source)) {
            return (chain, true);
        }
        chain.push(source);
        next = chain_source(source);
    }
    (chain, false)
}

fn error_chain<'a>(
    error: &'a (dyn Error + 'static),
) -> impl Iterator<Item = &'a (dyn Error + 'static)> {
    inspected_chain(error).0.into_iter()
}

fn is_timeout(error: &(dyn Error + 'static)) -> bool {
    let (chain, truncated) = inspected_chain(error);
    // reqwest's helper walks sources without a bound: use only proven finite chains.
    chain.into_iter().any(|error| {
        error.downcast_ref::<std::io::Error>().is_some_and(|io| {
            matches!(
                io.kind(),
                std::io::ErrorKind::TimedOut | std::io::ErrorKind::WouldBlock
            )
        }) || !truncated
            && error
                .downcast_ref::<reqwest::Error>()
                .is_some_and(reqwest::Error::is_timeout)
    })
}

fn safe_io_kind(kind: std::io::ErrorKind) -> IoKind {
    use std::io::ErrorKind as K;
    match kind {
        K::ConnectionRefused => IoKind::ConnectionRefused,
        K::ConnectionReset => IoKind::ConnectionReset,
        K::ConnectionAborted => IoKind::ConnectionAborted,
        K::NotConnected => IoKind::NotConnected,
        K::AddrInUse => IoKind::AddrInUse,
        K::AddrNotAvailable => IoKind::AddrNotAvailable,
        K::NetworkUnreachable => IoKind::NetworkUnreachable,
        K::HostUnreachable => IoKind::HostUnreachable,
        K::PermissionDenied => IoKind::PermissionDenied,
        K::NotFound => IoKind::NotFound,
        K::TimedOut => IoKind::TimedOut,
        K::WouldBlock => IoKind::WouldBlock,
        K::UnexpectedEof => IoKind::UnexpectedEof,
        K::InvalidData => IoKind::InvalidData,
        _ => IoKind::Other,
    }
}

fn inspect_error(error: &(dyn Error + 'static), profile: NetworkDiagnostics) -> NetworkDiagnostics {
    let (chain, truncated) = inspected_chain(error);
    let mut detail = profile.clear_error().with_error(ErrorLayer::Unknown);
    detail.chain_truncated = truncated;
    let mut tls_layer = None;
    for source in chain {
        // Deeper recognized sources replace wrapper labels. IO metadata and TLS
        // backend are independent, so an IO-wrapped TLS cause retains both facts.
        if let Some(io) = source.downcast_ref::<std::io::Error>() {
            detail.error_layer = ErrorLayer::Io;
            detail.io_kind = safe_io_kind(io.kind());
            // Keep the deepest available OS number even if a later IO wrapper has none.
            if let Some(code) = io.raw_os_error() {
                detail.os_code = Some(code);
            }
        } else if source.is::<native_tls::Error>() {
            tls_layer = Some(ErrorLayer::NativeTls);
        } else if source.is::<rustls::Error>() {
            tls_layer = Some(ErrorLayer::Rustls);
        } else if let Some(hyper) = source.downcast_ref::<hyper_util::client::legacy::Error>() {
            if hyper.is_connect() {
                detail.error_layer = ErrorLayer::HyperConnect;
            }
        } else if let Some(reqwest) = source.downcast_ref::<reqwest::Error>() {
            detail.error_layer = if reqwest.status().is_some() {
                ErrorLayer::HttpStatus
            } else {
                ErrorLayer::Reqwest
            };
        }
    }
    if let Some(layer) = tls_layer {
        detail.error_layer = layer;
    }
    if is_timeout(error) {
        detail.error_layer = ErrorLayer::Timeout;
    }
    detail
}

fn classify_transport(error: &reqwest::Error) -> ReaderError {
    if is_timeout(error) {
        return ReaderError::Timeout;
    }
    if error_chain(error)
        .any(|source| source.is::<native_tls::Error>() || source.is::<rustls::Error>())
    {
        return ReaderError::TlsFailure;
    }
    let mut connecting = false;
    for source in error_chain(error) {
        // Only inspect the immediate transport-owned cause of a typed Hyper connection
        // error, never arbitrary leaf/OS messages or HTTP response content.
        if let Some(connect) = source.downcast_ref::<hyper_util::client::legacy::Error>() {
            if connect.is_connect() {
                connecting = true;
                if let Some(kind) = connect
                    .source()
                    .and_then(|cause| fixed_connect_marker(&cause.to_string()))
                {
                    return kind;
                }
            }
        }
    }
    if connecting {
        ReaderError::ConnectFailure
    } else {
        ReaderError::NetworkFailure
    }
}

fn fixed_connect_marker(message: &str) -> Option<ReaderError> {
    // Exact, closed INTERNAL labels, not substring guesses. Verified against locked
    // hyper-util 0.1.20: src/client/legacy/connect/http.rs ConnectError::Display,
    // and connect/proxy/tunnel.rs TunnelError::Display. Those Display impls emit only
    // fixed literals; no URL, server text, headers, credentials, or nested source.
    // The private error types are not exported. Re-audit these on dependency upgrades.
    match message {
        "dns error" => Some(ReaderError::DnsFailure),
        "tunnel error: proxy authorization required" => Some(ReaderError::ProxyAuthentication),
        "tunnel error: missing destination host" => Some(ReaderError::ProxyConfiguration),
        "tunnel error: proxy response headers too long"
        | "tunnel error: unexpected end of file"
        | "tunnel error: unsuccessful"
        | "tunnel error: failed to create underlying connection"
        | "tunnel error: io error establishing tunnel" => Some(ReaderError::ProxyConnectFailure),
        _ => None,
    }
}

fn header_value(value: &str, maximum: usize) -> bool {
    !value.is_empty()
        && value.len() <= maximum
        && value.bytes().all(|byte| (0x21..=0x7e).contains(&byte))
}

#[cfg(test)]
fn read_bounded(reader: impl Read) -> Result<Vec<u8>, Failure> {
    read_bounded_with_profile(reader, NetworkDiagnostics::default())
}

fn read_bounded_with_profile(
    reader: impl Read,
    profile: NetworkDiagnostics,
) -> Result<Vec<u8>, Failure> {
    let mut body = Vec::new();
    reader
        .take((MAX_RESPONSE_BYTES + 1) as u64)
        .read_to_end(&mut body)
        .map_err(|error| {
            Failure::new(if is_timeout(&error) {
                ReaderError::Timeout
            } else {
                ReaderError::NetworkFailure
            })
            .with_network(inspect_error(&error, profile))
        })?;
    if body.len() > MAX_RESPONSE_BYTES {
        return Err(ReaderError::ResponseTooLarge.into());
    }
    Ok(body)
}

#[cfg(test)]
mod tests {
    #[test]
    fn environment_fallback_builder_applies_only_selected_env_no_proxy() {
        use crate::proxy_policy::EnvironmentRoute;
        let profile = NetworkDiagnostics {
            system_proxy_resolution: SystemProxyResolution::Unavailable,
            ..Default::default()
        };
        for (no_proxy, bypass) in [
            (None, false),
            (Some("chatgpt.com".to_owned()), true),
            (Some("other.invalid".to_owned()), false),
        ] {
            let route = EnvironmentRoute::Proxy {
                url: "http://127.0.0.1:9".to_owned(),
                no_proxy,
            };
            let (_, actual) = environment_client(&route, USAGE_URL, None, profile).unwrap();
            assert_eq!(actual.no_proxy_bypass, bypass);
            assert_eq!(
                actual.route,
                if bypass {
                    NetworkRoute::Direct
                } else {
                    NetworkRoute::EnvironmentProxy
                }
            );
        }
        let (_, direct) =
            environment_client(&EnvironmentRoute::Direct, USAGE_URL, None, profile).unwrap();
        assert_eq!(direct.route, NetworkRoute::Direct);
        let invalid = EnvironmentRoute::Proxy {
            url: "http://[invalid".to_owned(),
            no_proxy: None,
        };
        assert!(matches!(
            environment_client(&invalid, USAGE_URL, None, profile),
            Err(ReaderError::ProxyConfiguration)
        ));
    }

    use super::*;
    use std::io::Write;
    use std::net::TcpListener;
    use std::sync::Arc;

    #[test]
    fn system_route_proxy_uses_production_builder_and_stops_on_http_rejection() {
        for (status_line, expected) in [
            (
                "HTTP/1.1 407 Proxy Authentication Required",
                ReaderError::ProxyAuthentication,
            ),
            (
                "HTTP/1.0 407 Proxy Authentication Required",
                ReaderError::ProxyConnectFailure,
            ),
            ("HTTP/1.1 403 Forbidden", ReaderError::ProxyConnectFailure),
        ] {
            use crate::system_proxy::{ProxyScheme, Route};
            let listener = TcpListener::bind("127.0.0.1:0").unwrap();
            let port = listener.local_addr().unwrap().port();
            let server = std::thread::spawn(move || {
                let (mut stream, _) = accept_fixture(&listener);
                let request = read_headers(&mut stream);
                assert!(request.starts_with("connect fixture.invalid:443 http/1.1\r\n"));
                assert!(!request.contains("synthetic-token"));
                assert!(!request.contains("synthetic-account"));
                assert!(!request.contains("proxy-authorization"));
                write!(
                    stream,
                    "{status_line}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                )
                .unwrap();
            });
            let routes = [
                Route::Proxy {
                    scheme: ProxyScheme::Http,
                    host: "127.0.0.1".into(),
                    port,
                },
                Route::Direct,
            ];
            let mut attempts = 0;
            let profile = NetworkDiagnostics {
                system_proxy_config: SystemProxyConfig::AutoConfig,
                tls_backend: TlsBackend::NativeTls,
                ..Default::default()
            };
            let failure = crate::proxy_policy::execute_routes(
                &routes,
                std::time::Instant::now() + Duration::from_secs(3),
                Duration::from_secs(2),
                profile,
                |_| {},
                |route, timeout, network| {
                    attempts += 1;
                    let client = explicit_client(route, None).unwrap();
                    execute_profile(
                        &client,
                        "https://fixture.invalid/private-path",
                        timeout,
                        &fixture_credentials(),
                        network,
                    )
                },
            )
            .unwrap_err();
            server.join().unwrap();
            assert_eq!(attempts, 1);
            assert_eq!(failure.kind, expected);
            let network = failure.network.unwrap();
            assert_eq!(network.route, NetworkRoute::WindowsAutoProxy);
            assert_eq!(
                network.system_proxy_resolution,
                SystemProxyResolution::ResolvedProxy
            );
            let serialized = String::from_utf8(
                crate::Envelope::failed(crate::Stage::UsageRead, failure).to_bounded_json(),
            )
            .unwrap();
            for forbidden in [
                "127.0.0.1",
                "fixture.invalid",
                "synthetic-token",
                "synthetic-account",
                "private-path",
            ] {
                assert!(!serialized.contains(forbidden));
            }
        }
    }
    #[test]
    fn system_proxy_host_cannot_inject_credentials_paths_or_headers() {
        use crate::system_proxy::ProxyScheme;
        for host in [
            "",
            "user:password@host",
            "host/path",
            "host?query",
            "host#fragment",
            "host\\path",
            "host\r\nheader:value",
            "host%20",
        ] {
            assert!(proxy_url(ProxyScheme::Http, host, 8080).is_err());
        }
        assert!(proxy_url(ProxyScheme::Http, "localhost", 0).is_err());
        assert_eq!(
            proxy_url(ProxyScheme::Http, "::1", 8080).unwrap().as_str(),
            "http://[::1]:8080/"
        );
        assert_eq!(
            proxy_url(ProxyScheme::Https, "proxy.example", 443)
                .unwrap()
                .as_str(),
            "https://proxy.example/"
        );
    }
    #[test]
    fn default_matcher_snapshot_never_overwrites_explicit_system_route() {
        for resolution in [
            SystemProxyResolution::Resolving,
            SystemProxyResolution::ResolvedProxy,
            SystemProxyResolution::ResolvedDirect,
            SystemProxyResolution::Failed,
            SystemProxyResolution::TimedOut,
            SystemProxyResolution::Unsupported,
        ] {
            let profile = NetworkDiagnostics {
                route: NetworkRoute::WindowsAutoProxy,
                proxy_protocol: ProxyProtocol::Https,
                system_proxy_config: SystemProxyConfig::AutoConfig,
                system_proxy_resolution: resolution,
                ..Default::default()
            };
            assert_eq!(observed_network(profile), profile);
        }
    }

    fn fixture_credentials() -> Credentials {
        Credentials {
            access_token: "synthetic-token".into(),
            account_id: "synthetic-account".into(),
        }
    }
    fn test_client_builder() -> reqwest::blocking::ClientBuilder {
        // Test-only local HTTP and isolated proxy environment; production has neither override.
        client_builder().https_only(false).no_proxy()
    }
    fn assert_closed_failure(failure: Failure, kind: &str, code: Option<u16>) {
        let envelope = crate::Envelope::failed(crate::Stage::UsageRead, failure);
        let bytes = envelope.to_bounded_json();
        let text = String::from_utf8(bytes.clone()).unwrap();
        for forbidden in [
            "synthetic-token",
            "synthetic-account",
            "private resolver",
            "private-path",
            "private-query",
            "private-proxy-body",
            "fixture.invalid",
        ] {
            assert!(!text.contains(forbidden));
        }
        let mut json: serde_json::Value = serde_json::from_slice(&bytes).unwrap();
        let network = json.as_object_mut().unwrap().remove("network").unwrap();
        assert_eq!(network.as_object().unwrap().len(), 15);
        assert_eq!(network["route"], "Unknown");
        assert_eq!(network["proxyProtocol"], "None");
        assert!(network["osCode"].is_null() || network["osCode"].is_i64());
        assert_eq!(
            json,
            serde_json::json!({
                "protocolVersion": 1, "helperVersion": env!("CARGO_PKG_VERSION"),
                "status": "Unavailable", "stage": "UsageRead", "code": code,
                "errorKind": kind, "result": null
            })
        );
    }
    struct FailingResolver {
        kind: std::io::ErrorKind,
        nested: bool,
    }
    impl reqwest::dns::Resolve for FailingResolver {
        fn resolve(&self, _: reqwest::dns::Name) -> reqwest::dns::Resolving {
            let error = std::io::Error::new(self.kind, "private resolver synthetic-token");
            let error = if self.nested {
                std::io::Error::other(error)
            } else {
                error
            };
            Box::pin(std::future::ready(Err(
                Box::new(error) as Box<dyn Error + Send + Sync>
            )))
        }
    }
    #[test]
    fn execute_classifies_dns_without_exposing_error_or_url() {
        let client = test_client_builder()
            .dns_resolver(Arc::new(FailingResolver {
                kind: std::io::ErrorKind::Other,
                nested: false,
            }))
            .build()
            .unwrap();
        let failure = execute(
            &client,
            "https://fixture.invalid/private-path?private-query",
            Duration::from_secs(2),
            &fixture_credentials(),
        )
        .unwrap_err();
        assert_closed_failure(failure, "DnsFailure", None);
    }
    #[test]
    fn execute_classifies_refused_loopback_connection() {
        let client = test_client_builder().build().unwrap();
        let failure = execute(
            &client,
            "https://127.0.0.1:0/",
            Duration::from_secs(2),
            &fixture_credentials(),
        )
        .unwrap_err();
        assert_eq!(failure.network.unwrap().io_kind, IoKind::ConnectionRefused);
        assert!(failure.network.unwrap().os_code.is_some());
        let encoded: serde_json::Value = serde_json::from_slice(
            &crate::Envelope::failed(crate::Stage::UsageRead, failure).to_bounded_json(),
        )
        .unwrap();
        assert_eq!(encoded["network"]["ioKind"], "ConnectionRefused");
        assert_eq!(
            encoded["network"]["osCode"].as_i64(),
            failure.network.unwrap().os_code.map(i64::from)
        );
        assert!(encoded["code"].is_null());
        assert_closed_failure(failure, "ConnectFailure", None);
    }
    #[test]
    fn execute_detects_nested_timeout_before_dns_classification() {
        for kind in [std::io::ErrorKind::TimedOut, std::io::ErrorKind::WouldBlock] {
            let client = test_client_builder()
                .dns_resolver(Arc::new(FailingResolver { kind, nested: true }))
                .build()
                .unwrap();
            let failure = execute(
                &client,
                "https://fixture.invalid/",
                Duration::from_secs(2),
                &fixture_credentials(),
            )
            .unwrap_err();
            assert_closed_failure(failure, "Timeout", None);
        }
    }
    fn accept_fixture(listener: &TcpListener) -> (std::net::TcpStream, std::net::SocketAddr) {
        listener.set_nonblocking(true).unwrap();
        let deadline = std::time::Instant::now() + Duration::from_secs(3);
        loop {
            match listener.accept() {
                Ok(connection) => return connection,
                Err(error)
                    if error.kind() == std::io::ErrorKind::WouldBlock
                        && std::time::Instant::now() < deadline =>
                {
                    std::thread::sleep(Duration::from_millis(5))
                }
                Err(error) => panic!("loopback accept failed: {error}"),
            }
        }
    }
    fn read_headers(stream: &mut std::net::TcpStream) -> String {
        stream
            .set_read_timeout(Some(Duration::from_secs(2)))
            .unwrap();
        let mut request = Vec::new();
        while !request.ends_with(b"\r\n\r\n") {
            let mut byte = [0];
            stream.read_exact(&mut byte).unwrap();
            request.push(byte[0]);
            assert!(request.len() < 8192);
        }
        String::from_utf8(request).unwrap().to_ascii_lowercase()
    }
    fn proxy_failure(status: u16) -> Failure {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let proxy =
            reqwest::Proxy::all(format!("http://{}", listener.local_addr().unwrap())).unwrap();
        let server = std::thread::spawn(move || {
            let (mut stream, _) = accept_fixture(&listener);
            let request = read_headers(&mut stream);
            assert!(request.starts_with("connect fixture.invalid:443 http/1.1\r\n"));
            assert!(!request.contains("synthetic-token"));
            assert!(!request.contains("synthetic-account"));
            // A client may close immediately after the rejection status. Send one
            // buffer and let the assertion below verify receipt/classification.
            let response = format!("HTTP/1.1 {status} Fixture\r\nContent-Length: 18\r\nConnection: close\r\n\r\nprivate-proxy-body");
            let _ = stream.write_all(response.as_bytes());
        });
        let client = test_client_builder().proxy(proxy).build().unwrap();
        let failure = execute(
            &client,
            "https://fixture.invalid/private-path?private-query",
            Duration::from_secs(2),
            &fixture_credentials(),
        )
        .unwrap_err();
        server.join().unwrap();
        failure
    }
    #[test]
    fn execute_classifies_proxy_authentication() {
        // HTTP proxy authentication is 407. A 401 from the tunnel is a generic rejection.
        assert_closed_failure(proxy_failure(407), "ProxyAuthentication", None);
    }
    #[test]
    fn execute_classifies_proxy_tunnel_rejection() {
        for status in [401, 502] {
            assert_closed_failure(proxy_failure(status), "ProxyConnectFailure", None);
        }
    }
    #[test]
    fn execute_classifies_native_and_rustls_handshake_failures() {
        for rustls in [false, true] {
            let listener = TcpListener::bind("127.0.0.1:0").unwrap();
            let url = format!("https://{}/private-path", listener.local_addr().unwrap());
            let server = std::thread::spawn(move || {
                let (mut stream, _) = accept_fixture(&listener);
                stream
                    .set_read_timeout(Some(Duration::from_secs(2)))
                    .unwrap();
                let mut hello = [0; 8192];
                let read = stream.read(&mut hello).unwrap();
                assert!(read > 0);
                assert!(!String::from_utf8_lossy(&hello[..read]).contains("synthetic-token"));
                stream.write_all(b"HTTP/1.1 200 Not-TLS\r\n\r\n").unwrap();
            });
            let builder = test_client_builder();
            let client = if rustls {
                builder.use_rustls_tls()
            } else {
                builder.use_native_tls()
            }
            .build()
            .unwrap();
            let failure = execute(
                &client,
                &url,
                Duration::from_secs(2),
                &fixture_credentials(),
            )
            .unwrap_err();
            server.join().unwrap();
            assert_eq!(
                failure.network.unwrap().error_layer,
                if rustls {
                    ErrorLayer::Rustls
                } else {
                    ErrorLayer::NativeTls
                }
            );
            assert_closed_failure(failure, "TlsFailure", None);
        }
    }
    #[test]
    fn execute_preserves_http_status_without_error_body() {
        for status in [401, 403, 429, 500] {
            let listener = TcpListener::bind("127.0.0.1:0").unwrap();
            let url = format!("http://{}/", listener.local_addr().unwrap());
            let server = std::thread::spawn(move || {
                let (mut stream, _) = accept_fixture(&listener);
                read_headers(&mut stream);
                // No body is supplied; execute must not wait for its advertised content.
                write!(
                    stream,
                    "HTTP/1.1 {status} Fixture\r\nContent-Length: 999999\r\n\r\n"
                )
                .unwrap();
            });
            let failure = execute(
                &test_client_builder().build().unwrap(),
                &url,
                Duration::from_secs(2),
                &fixture_credentials(),
            )
            .unwrap_err();
            server.join().unwrap();
            assert_eq!(
                failure,
                Failure::http(status)
                    .with_network(NetworkDiagnostics::default().with_error(ErrorLayer::HttpStatus))
            );
            assert_eq!(failure.network.unwrap().os_code, None);
        }
    }
    #[test]
    fn fixed_library_markers_require_complete_exact_match() {
        for marker in [
            "dns error",
            "tunnel error: proxy authorization required",
            "tunnel error: unsuccessful",
            "tunnel error: missing destination host",
            "tunnel error: proxy response headers too long",
            "tunnel error: unexpected end of file",
            "tunnel error: failed to create underlying connection",
            "tunnel error: io error establishing tunnel",
        ] {
            assert!(fixed_connect_marker(marker).is_some());
            assert_eq!(fixed_connect_marker(&format!("{marker}x")), None);
            assert_eq!(fixed_connect_marker(&format!("x{marker}")), None);
        }
    }
    #[test]
    fn recursive_error_walk_is_bounded() {
        #[derive(Debug)]
        struct Cycle;
        impl std::fmt::Display for Cycle {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.write_str("cycle")
            }
        }
        impl Error for Cycle {
            fn source(&self) -> Option<&(dyn Error + 'static)> {
                Some(self)
            }
        }
        assert_eq!(error_chain(&Cycle).count(), 1);
        assert!(inspect_error(&Cycle, NetworkDiagnostics::default()).chain_truncated);
        assert!(!is_timeout(&Cycle));
    }
    #[test]
    fn custom_ca_precedence_empty_and_invalid_files_fail_closed() {
        assert_eq!(
            configured_ca_path(|key| Some(
                if key == "CODEX_CA_CERTIFICATE" {
                    "codex.pem"
                } else {
                    "fallback.pem"
                }
                .into()
            )),
            Some("codex.pem".into())
        );
        assert_eq!(
            configured_ca_path(|key| Some(
                if key == "CODEX_CA_CERTIFICATE" {
                    ""
                } else {
                    "fallback.pem"
                }
                .into()
            )),
            Some("fallback.pem".into())
        );
        assert_eq!(configured_ca_path(|_| Some(String::new())), None);
        let dir = tempfile::tempdir().unwrap();
        let file = dir.path().join("ca.pem");
        for bytes in [
            b"invalid PEM".to_vec(),
            b"-----BEGIN CERTIFICATE-----\nMAEA\n-----END CERTIFICATE-----\n".to_vec(),
            vec![b'x'; MAX_CA_BYTES + 1],
        ] {
            std::fs::write(&file, bytes).unwrap();
            assert!(matches!(
                configure_ca(test_client_builder(), Some(&file)),
                Err(ReaderError::CertificateConfiguration)
            ));
        }
        assert!(matches!(
            configure_ca(test_client_builder(), Some(&dir.path().join("missing.pem"))),
            Err(ReaderError::CertificateConfiguration)
        ));
    }

    #[test]
    fn valid_standard_trusted_and_crl_ca_bundles_build() {
        // Public, synthetic test CA generated for this fixture; no private key is embedded.
        const CERT: &str = r#"-----BEGIN CERTIFICATE-----
MIIDMzCCAhugAwIBAgIUOmrwaa4Qg+Mx3lQCAo0KBaXA/1wwDQYJKoZIhvcNAQEL
BQAwKTEnMCUGA1UEAwweUXVvdGEgUmVhZGVyIFN5bnRoZXRpYyBUZXN0IENBMB4X
DTI2MTAxMjExMjM1N1oXDTM2MTAwOTExMjM1N1owKTEnMCUGA1UEAwweUXVvdGEg
UmVhZGVyIFN5bnRoZXRpYyBUZXN0IENBMIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8A
MIIBCgKCAQEA0WPN++OdkzTwmx9EHRE7SQeNF55bLhJztxpffgrZwF9/NrnD05ME
pBpUfyFxxE9McfF0R0/vncdO7thgHCvAE1faLOHBex5RsJBVFu2rfW/QWSwzKBir
Er7gEmhBFdLQMdab8WNwRpOI92Fzr7p1SdwKv2oEV8SUlc2hmM9eY/K9dTjElfX1
5eSV8dFocF1WdbqO2JB/d10Frz9ch/9Kce08T0n12TNPG6E1ZiRZZ1cAoloJbri4
P6FKCgAv+usiCa0RE3fOTQri9rN4j3t+N/CXhX9y5fW3zKuVj83bXAUdqvwlc++I
+ObNp8FjzMoexbZSHKl0M9wcuZwr8DaJzwIDAQABo1MwUTAdBgNVHQ4EFgQUhA63
ppSpiJDikMIZ7eVj0tJ/YvEwHwYDVR0jBBgwFoAUhA63ppSpiJDikMIZ7eVj0tJ/
YvEwDwYDVR0TAQH/BAUwAwEB/zANBgkqhkiG9w0BAQsFAAOCAQEAvWMtfWAN1fZd
amLzLvauHtDQsyrGvoE7ZFYbPMvAaEsj/o/dhfhyNdX8nFNDUwt+wEShHtMJWb8r
EvKUK3/twYT6iZvqR3tbmsh4DKfC1ywDNP+97CvbqC0adEd7jKOfEa2ElTOARD6V
QGJiS+PU4nh33nmLJmKJ8TStqo2NioYfPH0Zc4nfyvsD310ZApzEQrqTgMaxsrwi
iZfPu7xg7Eb7dJ6OqX/oO0ay/BXcTs7TuUOD9dO0G7BU4dh2j3OoShBGN3QH/t6B
IhCA06GTd27tW0zfcd+ToOsvis8gg9kINndZcVEvnqH8rX27C9JJRp1ecCihm1Ih
csPA0T0c6g==
-----END CERTIFICATE-----
"#;
        for bundle in [
            format!("{CERT}{CERT}"),
            CERT.replace("CERTIFICATE", "TRUSTED CERTIFICATE"),
            format!("{CERT}-----BEGIN X509 CRL-----\nMAEA\n-----END X509 CRL-----\n"),
        ] {
            let file = tempfile::NamedTempFile::new().unwrap();
            std::fs::write(file.path(), bundle).unwrap();
            configure_ca(test_client_builder(), Some(file.path()))
                .unwrap()
                .build()
                .unwrap();
        }
        assert_eq!(
            first_der_item(&[0x30, 1, 0, 0xff, 0xff]),
            Some([0x30, 1, 0].as_slice())
        );
        assert_eq!(first_der_item(&[0x30, 0x80]), None);
        assert_eq!(first_der_item(&[0x30, 0x82, 0xff, 0xff]), None);
    }

    #[test]
    fn environment_https_proxy_precedence_and_no_proxy_are_preserved() {
        for bypass in [false, true] {
            let listener = TcpListener::bind("127.0.0.1:0").unwrap();
            let proxy_url = format!("http://{}", listener.local_addr().unwrap());
            let server = if bypass {
                None
            } else {
                let listener = listener.try_clone().unwrap();
                Some(std::thread::spawn(move || {
                    let (mut stream, _) = accept_fixture(&listener);
                    let request = read_headers(&mut stream);
                    assert!(request.starts_with("connect fixture.invalid:443 http/1.1"));
                    assert!(!request.contains("synthetic-token"));
                    stream.write_all(b"HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\n\r\n").unwrap();
                }))
            };
            // Isolated subprocess avoids mutating proxy configuration shared with other tests.
            let mut child = std::process::Command::new(std::env::current_exe().unwrap());
            child.args([
                "--exact",
                "http::tests::proxy_environment_child",
                "--ignored",
            ]);
            for key in [
                "HTTP_PROXY",
                "http_proxy",
                "HTTPS_PROXY",
                "https_proxy",
                "ALL_PROXY",
                "all_proxy",
                "NO_PROXY",
                "no_proxy",
                "REQUEST_METHOD",
            ] {
                child.env_remove(key);
            }
            let output = child
                .env("HTTPS_PROXY", proxy_url)
                .env("ALL_PROXY", "http://127.0.0.1:0")
                .env("HTTP_PROXY", "http://127.0.0.1:0")
                .env("NO_PROXY", if bypass { "fixture.invalid" } else { "" })
                .env("CQT_PROXY_BYPASS_FIXTURE", if bypass { "1" } else { "0" })
                .output()
                .unwrap();
            if let Some(server) = server {
                server.join().unwrap();
            }
            assert!(
                output.status.success(),
                "proxy child failed: {}",
                String::from_utf8_lossy(&output.stdout)
            );
            if bypass {
                listener.set_nonblocking(true).unwrap();
                assert_eq!(
                    listener.accept().unwrap_err().kind(),
                    std::io::ErrorKind::WouldBlock
                );
            }
        }
    }

    #[test]
    #[ignore = "invoked only by the isolated proxy environment fixture"]
    fn proxy_environment_child() {
        let bypass = std::env::var("CQT_PROXY_BYPASS_FIXTURE").unwrap() == "1";
        assert_eq!(
            observed_network(NetworkDiagnostics::default()).route,
            NetworkRoute::Unknown
        );
        let client = client_builder()
            .dns_resolver(Arc::new(FailingResolver {
                kind: std::io::ErrorKind::Other,
                nested: false,
            }))
            .build()
            .unwrap();
        let failure = execute(
            &client,
            "https://fixture.invalid/",
            Duration::from_secs(2),
            &fixture_credentials(),
        )
        .unwrap_err();
        let network = observed_network(failure.network.unwrap());
        assert_eq!(
            network.route,
            if bypass {
                NetworkRoute::Direct
            } else {
                NetworkRoute::EnvironmentProxy
            }
        );
        assert_eq!(
            network.proxy_protocol,
            if bypass {
                ProxyProtocol::None
            } else {
                ProxyProtocol::Http
            }
        );
        assert_eq!(network.no_proxy_bypass, bypass);
        let envelope =
            crate::Envelope::failed(crate::Stage::UsageRead, failure.with_network(network));
        let json: serde_json::Value = serde_json::from_slice(&envelope.to_bounded_json()).unwrap();
        assert_eq!(
            json["network"]["route"],
            if bypass { "Direct" } else { "EnvironmentProxy" }
        );
        assert_eq!(json["network"]["noProxyBypass"], bypass);
        assert_closed_failure(
            failure,
            if bypass {
                "DnsFailure"
            } else {
                "ProxyAuthentication"
            },
            None,
        );
    }

    #[test]
    fn endpoints_are_fixed_https_read_paths() {
        assert_eq!(
            Endpoint::Usage.url(),
            "https://chatgpt.com/backend-api/wham/usage"
        );
        assert_eq!(
            Endpoint::ResetCredits.url(),
            "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits"
        );
        assert!(!Endpoint::ResetCredits.url().contains("consume"));
    }
    #[test]
    fn body_size_is_enforced_without_content_length() {
        assert_eq!(
            read_bounded(vec![b'x'; MAX_RESPONSE_BYTES].as_slice())
                .unwrap()
                .len(),
            MAX_RESPONSE_BYTES
        );
        assert_eq!(
            read_bounded(vec![b'x'; MAX_RESPONSE_BYTES + 1].as_slice()),
            Err(ReaderError::ResponseTooLarge.into())
        );
    }
    #[test]
    fn headers_are_coherent_and_redirect_is_not_followed() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let target = TcpListener::bind("127.0.0.1:0").unwrap();
        target.set_nonblocking(true).unwrap();
        let url = format!("http://{}/fixture", listener.local_addr().unwrap());
        let location = format!(
            "http://{}/must-not-receive-credentials",
            target.local_addr().unwrap()
        );
        let server = std::thread::spawn(move || {
            let (mut stream, _) = accept_fixture(&listener);
            stream
                .set_read_timeout(Some(Duration::from_secs(2)))
                .unwrap();
            let mut request = Vec::new();
            loop {
                let mut byte = [0];
                stream.read_exact(&mut byte).unwrap();
                request.push(byte[0]);
                if request.ends_with(b"\r\n\r\n") {
                    break;
                }
            }
            let request = String::from_utf8(request).unwrap().to_ascii_lowercase();
            assert!(request.starts_with("get /fixture http/1.1\r\n"));
            assert!(request.contains("authorization: bearer synthetic-token\r\n"));
            assert!(request.contains("chatgpt-account-id: synthetic-account\r\n"));
            write!(stream, "HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 16\r\nConnection: close\r\n\r\nsecret-error-body").unwrap();
        });
        let result = execute(
            &test_client_builder().build().unwrap(),
            &url,
            Duration::from_secs(2),
            &Credentials {
                access_token: "synthetic-token".into(),
                account_id: "synthetic-account".into(),
            },
        );
        server.join().unwrap();
        assert_eq!(
            result,
            Err(Failure {
                kind: ReaderError::RedirectBlocked,
                code: Some(302),
                network: Some(NetworkDiagnostics::default().with_error(ErrorLayer::HttpStatus)),
            })
        );
        assert_eq!(
            target.accept().unwrap_err().kind(),
            std::io::ErrorKind::WouldBlock
        );
    }
    #[test]
    fn no_header_injection_is_possible() {
        for bad in ["", "a\r\nx-leak: yes", "a b", "😈"] {
            assert!(!header_value(bad, 100));
        }
        assert!(!header_value(&"x".repeat(101), 100));
    }
    #[test]
    fn diagnostic_chain_uses_deepest_io_without_formatting_secrets() {
        #[derive(Debug)]
        struct PrivateWrapper(std::io::Error);
        impl std::fmt::Display for PrivateWrapper {
            fn fmt(&self, _: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                panic!(
                    "must never format bearer secret-token proxy-user:proxy-password /private/path"
                );
            }
        }
        impl Error for PrivateWrapper {
            fn source(&self) -> Option<&(dyn Error + 'static)> {
                Some(&self.0)
            }
        }
        let wrapped = PrivateWrapper(std::io::Error::other(std::io::Error::from_raw_os_error(
            10061,
        )));
        let profile = NetworkDiagnostics {
            tls_backend: TlsBackend::Rustls,
            ca_override: true,
            ..Default::default()
        };
        let network = inspect_error(&wrapped, profile);
        assert_eq!(network.os_code, Some(10061));
        assert_eq!(network.error_layer, ErrorLayer::Io);
        assert_eq!(network.tls_backend, TlsBackend::Rustls);
        assert!(!network.chain_truncated);
        let envelope = crate::Envelope::failed(
            crate::Stage::UsageRead,
            Failure::new(ReaderError::ConnectFailure).with_network(network),
        );
        let value: serde_json::Value = serde_json::from_slice(&envelope.to_bounded_json()).unwrap();
        assert_eq!(value["network"]["osCode"], 10061);
        assert!(value["code"].is_null());
        assert_eq!(value["network"].as_object().unwrap().len(), 15);
        for private in [
            "secret-token",
            "proxy-user",
            "proxy-password",
            "/private/path",
        ] {
            assert!(!String::from_utf8(envelope.to_bounded_json())
                .unwrap()
                .contains(private));
        }
    }

    #[test]
    fn exact_chain_budget_does_not_mark_a_terminated_chain_truncated() {
        fn nested(count: usize) -> std::io::Error {
            let mut error = std::io::Error::from_raw_os_error(10061);
            for _ in 1..count {
                error = std::io::Error::other(error);
            }
            error
        }
        let detail = inspect_error(&nested(MAX_ERROR_DEPTH), NetworkDiagnostics::default());
        assert_eq!(detail.os_code, Some(10061));
        assert!(!detail.chain_truncated);
        let detail = inspect_error(&nested(MAX_ERROR_DEPTH + 1), NetworkDiagnostics::default());
        assert!(detail.chain_truncated);
        assert_eq!(detail.os_code, None);
    }

    #[test]
    fn body_io_failure_preserves_profile_and_closed_source_metadata() {
        struct FailedBody;
        impl Read for FailedBody {
            fn read(&mut self, _: &mut [u8]) -> std::io::Result<usize> {
                Err(std::io::Error::new(
                    std::io::ErrorKind::UnexpectedEof,
                    "private-token body /private/path",
                ))
            }
        }
        let profile = NetworkDiagnostics {
            tls_backend: TlsBackend::NativeTls,
            ..Default::default()
        };
        let failure = read_bounded_with_profile(FailedBody, profile).unwrap_err();
        let network = failure.network.unwrap();
        assert_eq!(network.io_kind, IoKind::UnexpectedEof);
        assert_eq!(network.error_layer, ErrorLayer::Io);
        assert_eq!(network.tls_backend, TlsBackend::NativeTls);
        let json = String::from_utf8(
            crate::Envelope::failed(crate::Stage::UsageRead, failure).to_bounded_json(),
        )
        .unwrap();
        assert!(!json.contains("private-token"));
        assert!(!json.contains("/private/path"));
    }
    /// Explicitly authorized cloud-only manual reachability check. This is never
    /// run by the ordinary suite or called by the shipped helper.
    #[test]
    #[ignore = "manual one-request cloud reachability check; requires explicit authorization"]
    fn manual_cloud_official_reachability() {
        assert_eq!(std::env::var("CQT_MANUAL_CLOUD_PROBE").as_deref(), Ok("1"));
        let ca = configured_ca_path(|key| std::env::var(key).ok());
        let mut network = NetworkDiagnostics {
            ca_override: ca.is_some(),
            ..Default::default()
        };
        let builder = match configure_ca(client_builder(), ca.as_deref()) {
            Ok(builder) => builder,
            Err(kind) => {
                println!(
                    "{}",
                    serde_json::json!({"httpStatus":null, "errorKind":kind, "network":network})
                );
                return;
            }
        };
        network.tls_backend = if ca.is_some() {
            TlsBackend::Rustls
        } else {
            TlsBackend::NativeTls
        };
        let client = match builder.build() {
            Ok(client) => client,
            Err(error) => {
                println!(
                    "{}",
                    serde_json::json!({"httpStatus":null, "errorKind":classify_transport(&error), "network":inspect_error(&error, network)})
                );
                return;
            }
        };
        // Deliberately no credentials, cookie store, credential source or body read.
        match client
            .get(USAGE_URL)
            .header("Accept", "application/json")
            .header("Cache-Control", "no-store")
            .send()
        {
            Ok(response) => {
                network = observed_network(network);
                if !response.status().is_success() {
                    network.error_layer = ErrorLayer::HttpStatus;
                }
                println!(
                    "{}",
                    serde_json::json!({"httpStatus":response.status().as_u16(), "errorKind":null, "network":network})
                );
            }
            Err(error) => {
                network = observed_network(inspect_error(&error, network));
                println!(
                    "{}",
                    serde_json::json!({"httpStatus":null, "errorKind":classify_transport(&error), "network":network})
                );
            }
        }
    }
}
