// CodexQuotaTray addition to hyper-util 0.1.20. SPDX-License-Identifier: MIT
//! Passive metadata for the existing system-matcher decision. No addresses,
//! credentials, headers, or environment values leave this module.

#[cfg(any(windows, test))]
use super::Builder;
use super::Intercept;
use std::sync::atomic::{AtomicU32, Ordering};

const OBSERVED: u32 = 1 << 16;
static LAST_SYSTEM_ROUTE: AtomicU32 = AtomicU32::new(0);

/// Provenance of the proxy selected by a system matcher, or a direct decision.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
#[repr(u8)]
pub enum SystemRouteSource {
    /// The matcher selected no proxy, including a NO_PROXY bypass.
    Direct = 1,
    /// The selected proxy came from the process environment.
    Environment = 2,
    /// The selected proxy came from Windows Internet Settings.
    WindowsSystem = 3,
    /// The selected proxy came from macOS system configuration.
    MacosSystem = 4,
    /// The source is not one of the supported, closed categories.
    #[default]
    Unknown = 5,
}

/// Closed protocol label for a selected proxy. Contains no proxy address.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(u8)]
pub enum ProxyProtocol {
    /// No proxy was selected.
    None = 0,
    /// An HTTP proxy.
    Http = 1,
    /// An HTTPS proxy.
    Https = 2,
    /// A SOCKS4 proxy.
    Socks4 = 3,
    /// A SOCKS4a proxy.
    Socks4a = 4,
    /// A SOCKS5 proxy.
    Socks5 = 5,
    /// A SOCKS5 proxy with remote name resolution.
    Socks5h = 6,
    /// A protocol outside the supported, closed categories.
    Unknown = 7,
}

/// A single atomic snapshot of an actual system-matcher invocation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct SystemRouteDiagnostic {
    /// Origin of the selected proxy, or a direct decision.
    pub source: SystemRouteSource,
    /// Protocol of the selected proxy, or `None` for a direct decision.
    pub protocol: ProxyProtocol,
    /// Whether the existing NO_PROXY filter suppressed a configured proxy for this destination.
    pub no_proxy_bypass: bool,
}

/// Read the last actual invocation of a matcher constructed by `from_system()`.
///
/// `None` means no such invocation has been observed in this process. This does
/// not evaluate proxy rules, inspect the environment, or initiate any IO.
/// The snapshot is process-wide and is retained when a connection is reused.
///
/// This is a matcher observation, not a general per-request transport API.
/// CodexQuotaTray relies on its single default system matcher, one HTTP client,
/// fixed HTTPS origin, and disabled redirects: reqwest's HTTP-only proxy-header
/// hooks are skipped, so these observations come from its connection dispatcher.
/// Additional clients, HTTP destinations, or custom proxies require a new audit.
pub fn last_system_route() -> Option<SystemRouteDiagnostic> {
    let packed = LAST_SYSTEM_ROUTE.load(Ordering::Relaxed);
    if packed & OBSERVED == 0 {
        return None;
    }
    Some(SystemRouteDiagnostic {
        source: match packed & 0xff {
            1 => SystemRouteSource::Direct,
            2 => SystemRouteSource::Environment,
            3 => SystemRouteSource::WindowsSystem,
            4 => SystemRouteSource::MacosSystem,
            _ => SystemRouteSource::Unknown,
        },
        protocol: match (packed >> 8) & 0x7f {
            0 => ProxyProtocol::None,
            1 => ProxyProtocol::Http,
            2 => ProxyProtocol::Https,
            3 => ProxyProtocol::Socks4,
            4 => ProxyProtocol::Socks4a,
            5 => ProxyProtocol::Socks5,
            6 => ProxyProtocol::Socks5h,
            _ => ProxyProtocol::Unknown,
        },
        no_proxy_bypass: packed & (1 << 15) != 0,
    })
}

pub(super) fn stamp(mut proxy: Intercept, source: SystemRouteSource) -> Intercept {
    proxy.source = source;
    proxy
}

pub(super) fn record(proxy: Option<&Intercept>, no_proxy_bypass: bool) {
    let (source, protocol) = match proxy {
        None => (SystemRouteSource::Direct, ProxyProtocol::None),
        Some(proxy) => (
            proxy.source,
            match proxy.uri.scheme_str() {
                Some("http") => ProxyProtocol::Http,
                Some("https") => ProxyProtocol::Https,
                Some("socks4") => ProxyProtocol::Socks4,
                Some("socks4a") => ProxyProtocol::Socks4a,
                Some("socks5") => ProxyProtocol::Socks5,
                Some("socks5h") => ProxyProtocol::Socks5h,
                _ => ProxyProtocol::Unknown,
            },
        ),
    };
    // All fields are packed in one atomic: readers cannot see a torn snapshot.
    // No other data is synchronized, so relaxed ordering is sufficient.
    LAST_SYSTEM_ROUTE.store(
        OBSERVED | source as u32 | ((protocol as u32) << 8) | ((no_proxy_bypass as u32) << 15),
        Ordering::Relaxed,
    );
}

// The upstream Windows fill behavior, with source metadata added alongside each
// actual assignment. Keeping this seam platform-independent permits synthetic
// tests without reading or writing Windows Internet Settings.
#[cfg(any(windows, test))]
pub(super) fn fill_windows_proxy_server(builder: &mut Builder, val: String) {
    if builder.http.is_empty() {
        builder.http = val.clone();
        builder.http_source = SystemRouteSource::WindowsSystem;
    }
    if builder.https.is_empty() {
        builder.https = val;
        builder.https_source = SystemRouteSource::WindowsSystem;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::client::proxy::matcher::Matcher;

    fn environment_builder(https: &str, all: &str, no: &str) -> Builder {
        Builder {
            https: https.into(),
            all: all.into(),
            no: no.into(),
            observe_system: true,
            all_source: SystemRouteSource::Environment,
            http_source: SystemRouteSource::Environment,
            https_source: SystemRouteSource::Environment,
            ..Builder::default()
        }
    }

    fn unobserved_twin(b: &Builder) -> Matcher {
        Builder {
            is_cgi: b.is_cgi,
            all: b.all.clone(),
            http: b.http.clone(),
            https: b.https.clone(),
            no: b.no.clone(),
            observe_system: false,
            all_source: b.all_source,
            http_source: b.http_source,
            https_source: b.https_source,
        }
        .build()
    }

    fn check(
        b: Builder,
        source: SystemRouteSource,
        protocol: ProxyProtocol,
        bypass: bool,
    ) -> Option<Intercept> {
        let dst = "https://target.invalid/path".parse().unwrap();
        let unobserved = unobserved_twin(&b).intercept(&dst);
        let before = last_system_route();
        let _ = unobserved_twin(&b).intercept(&dst);
        assert_eq!(last_system_route(), before);
        let observed = b.build().intercept(&dst);
        assert_eq!(observed.is_some(), unobserved.is_some());
        if let (Some(a), Some(b)) = (&observed, &unobserved) {
            assert_eq!(a.uri(), b.uri());
            assert_eq!(a.basic_auth(), b.basic_auth());
            assert_eq!(a.raw_auth(), b.raw_auth());
        }
        assert_eq!(
            last_system_route(),
            Some(SystemRouteDiagnostic {
                source,
                protocol,
                no_proxy_bypass: bypass,
            })
        );
        observed
    }

    // One test owns the global diagnostic snapshot; no process environment,
    // registry, credentials, network, or callback is involved.
    #[test]
    fn observation_preserves_selection_auth_and_bypass() {
        use ProxyProtocol as P;
        use SystemRouteSource as S;
        LAST_SYSTEM_ROUTE.store(0, Ordering::Relaxed);
        assert_eq!(last_system_route(), None);
        let _ = Matcher::builder()
            .all("http://explicit.invalid")
            .build()
            .intercept(&"https://target.invalid".parse().unwrap());
        assert_eq!(last_system_route(), None);

        check(environment_builder("", "", ""), S::Direct, P::None, false);
        check(environment_builder("", "", "*"), S::Direct, P::None, false);
        let auth = check(
            environment_builder("http://test%20user:fake%40pass@env.invalid:8080", "", ""),
            S::Environment,
            P::Http,
            false,
        )
        .unwrap();
        assert!(auth.basic_auth().is_some());
        assert!(auth.raw_auth().is_none());
        assert!(auth.uri().authority().unwrap().as_str().find('@').is_none());

        for (scheme, protocol) in [
            ("https", P::Https),
            ("socks4", P::Socks4),
            ("socks4a", P::Socks4a),
            ("socks5", P::Socks5),
            ("socks5h", P::Socks5h),
        ] {
            let value = format!("{scheme}://test%20user:fake%40pass@env.invalid:8080");
            let auth = check(
                environment_builder("", &value, ""),
                S::Environment,
                protocol,
                false,
            )
            .unwrap();
            if scheme != "https" {
                assert_eq!(auth.raw_auth(), Some(("test user", "fake@pass")));
                assert!(auth.basic_auth().is_none());
            }
        }
        check(
            environment_builder("http://env.invalid", "", "target.invalid"),
            S::Direct,
            P::None,
            true,
        );
        check(
            environment_builder("http://env.invalid", "", "*"),
            S::Direct,
            P::None,
            true,
        );

        // Windows-specific proxy wins over an environment ALL_PROXY when HTTPS
        // is empty. Looking only for any environment proxy would mislabel it.
        let mut b = environment_builder("", "http://all.invalid", "");
        fill_windows_proxy_server(&mut b, "https://windows.invalid:8443".into());
        check(b, S::WindowsSystem, P::Https, false);

        let mut b = environment_builder("http://env.invalid", "http://all.invalid", "");
        fill_windows_proxy_server(&mut b, "https://windows.invalid".into());
        check(b, S::Environment, P::Http, false);

        // A nonempty but invalid HTTPS value prevents Windows filling, then the
        // existing parser falls back to ALL_PROXY, carrying its true source.
        let mut b = environment_builder("unsupported://invalid", "http://all.invalid", "");
        fill_windows_proxy_server(&mut b, "https://windows.invalid".into());
        check(b, S::Environment, P::Http, false);

        let mut b = environment_builder("", "http://all.invalid", "");
        fill_windows_proxy_server(&mut b, "unsupported://invalid".into());
        check(b, S::Environment, P::Http, false);

        let mut b = environment_builder("http://env.invalid", "http://all.invalid", "*");
        b.is_cgi = true;
        check(b, S::Direct, P::None, false);
    }
}
