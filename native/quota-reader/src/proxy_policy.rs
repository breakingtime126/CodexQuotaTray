// SPDX-License-Identifier: Apache-2.0
//! System-first route policy matching the official Codex outbound-proxy resolver.
//! No registry, network, credentials or system-setting writes occur in this module.
use crate::error::{
    ErrorLayer, Failure, NetworkDiagnostics, NetworkRoute, ProxyProtocol, ReaderError,
    SystemProxyResolution,
};
use crate::system_proxy::{ProxyScheme, Route};
use std::time::{Duration, Instant};

/// Intentionally not Debug or Serialize: values can contain private proxy details.
pub(crate) enum EnvironmentRoute {
    Direct,
    Proxy {
        url: String,
        no_proxy: Option<String>,
    },
}

/// Used only when system proxy resolution is unavailable. A system DIRECT or
/// proxy decision is authoritative and must never consult these environment values.
/// Adapted from Codex rust-v0.159.0 http-client/src/outbound_proxy.rs:
/// resolve_env_proxy_route and proxy_env_value.
pub(crate) fn environment_route(
    url: &str,
    env: impl Fn(&str) -> Option<String>,
) -> Result<EnvironmentRoute, ReaderError> {
    let url = reqwest::Url::parse(url).map_err(|_| ReaderError::InvalidArguments)?;
    let value = |upper: &str| {
        env(upper)
            .or_else(|| env(&upper.to_ascii_lowercase()))
            .filter(|value| !value.is_empty())
    };
    // An existing empty uppercase value shadows lowercase, then is filtered;
    // the next proxy family can still be used. CGI REQUEST_METHOD is irrelevant.
    let proxy = match url.scheme() {
        "https" => value("HTTPS_PROXY").or_else(|| value("ALL_PROXY")),
        "wss" => value("HTTPS_PROXY")
            .or_else(|| value("HTTP_PROXY"))
            .or_else(|| value("ALL_PROXY")),
        "http" | "ws" => value("HTTP_PROXY").or_else(|| value("ALL_PROXY")),
        _ => value("ALL_PROXY"),
    };
    Ok(match proxy {
        Some(url) => EnvironmentRoute::Proxy {
            url,
            no_proxy: value("NO_PROXY"),
        },
        None => EnvironmentRoute::Direct,
    })
}

pub(crate) fn resolution_failure(
    profile: NetworkDiagnostics,
    timed_out: bool,
    os_code: Option<u32>,
) -> Failure {
    let mut detail = profile.clear_error();
    detail.error_layer = ErrorLayer::SystemProxy;
    detail.os_code = os_code.map(|code| code as i32);
    detail.system_proxy_resolution = if timed_out {
        SystemProxyResolution::TimedOut
    } else {
        SystemProxyResolution::Failed
    };
    Failure::new(if timed_out {
        ReaderError::Timeout
    } else {
        ReaderError::ProxyConfiguration
    })
    .with_network(detail)
}

/// Execute only the first route selected before networking. No request failure
/// permits retrying another proxy or DIRECT, including DNS/refusal/timeouts.
pub(crate) fn execute_routes<T>(
    routes: &[Route],
    deadline: Instant,
    request_timeout: Duration,
    profile: NetworkDiagnostics,
    mut publish: impl FnMut(NetworkDiagnostics),
    mut attempt: impl FnMut(&Route, Duration, NetworkDiagnostics) -> Result<T, Failure>,
) -> Result<T, Failure> {
    let Some(route) = routes.first() else {
        return Err(resolution_failure(profile, false, None));
    };
    let remaining = deadline.saturating_duration_since(Instant::now());
    if remaining.is_zero() {
        let mut detail = profile.clear_error();
        detail.error_layer = ErrorLayer::Timeout;
        return Err(Failure::new(ReaderError::Timeout).with_network(detail));
    }
    let mut detail = profile.clear_error();
    detail.no_proxy_bypass = false;
    match route {
        Route::Direct => {
            detail.route = NetworkRoute::Direct;
            detail.proxy_protocol = ProxyProtocol::None;
            detail.system_proxy_resolution = SystemProxyResolution::ResolvedDirect;
        }
        Route::Proxy { scheme, .. } => {
            detail.route = if profile.route == NetworkRoute::WindowsProxy {
                NetworkRoute::WindowsProxy
            } else {
                NetworkRoute::WindowsAutoProxy
            };
            detail.proxy_protocol = match scheme {
                ProxyScheme::Http => ProxyProtocol::Http,
                ProxyScheme::Https => ProxyProtocol::Https,
            };
            detail.system_proxy_resolution = SystemProxyResolution::ResolvedProxy;
        }
        Route::UnsupportedScheme => {
            detail.route = NetworkRoute::Unknown;
            detail.proxy_protocol = ProxyProtocol::Other;
            detail.error_layer = ErrorLayer::SystemProxy;
            detail.system_proxy_resolution = SystemProxyResolution::Unsupported;
            publish(detail);
            return Err(Failure::new(ReaderError::ProxyConfiguration).with_network(detail));
        }
    }
    publish(detail);
    attempt(route, remaining.min(request_timeout), detail).map_err(|failure| {
        let failure = if failure.network.is_some() {
            failure
        } else {
            failure.with_network(detail)
        };
        publish(failure.network.unwrap());
        failure
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::error::{IoKind, SystemProxyConfig, TlsBackend};
    use std::cell::RefCell;

    const HTTPS_URL: &str = "https://chatgpt.com/backend-api/wham/usage";
    const HTTPS_PROXY: &str = "http://https.fixture.invalid:8080";
    const ALL_PROXY: &str = "http://all.fixture.invalid:8080";

    fn environment(values: &[(&str, &str)]) -> EnvironmentRoute {
        environment_for(HTTPS_URL, values)
    }
    fn environment_for(url: &str, values: &[(&str, &str)]) -> EnvironmentRoute {
        environment_route(url, |name| {
            values
                .iter()
                .find(|(key, _)| *key == name)
                .map(|(_, value)| value.to_string())
        })
        .unwrap()
    }
    fn assert_proxy(route: EnvironmentRoute, expected_url: &str, expected_no_proxy: Option<&str>) {
        let EnvironmentRoute::Proxy { url, no_proxy } = route else {
            panic!("expected selected environment proxy");
        };
        assert_eq!(url, expected_url);
        assert_eq!(no_proxy.as_deref(), expected_no_proxy);
    }
    fn proxy() -> Route {
        Route::Proxy {
            scheme: ProxyScheme::Http,
            host: "synthetic.invalid".into(),
            port: 8080,
        }
    }

    #[test]
    fn environment_https_precedence_matches_official_source() {
        for values in [
            vec![("HTTPS_PROXY", HTTPS_PROXY)],
            vec![("https_proxy", HTTPS_PROXY)],
            vec![("HTTPS_PROXY", HTTPS_PROXY), ("ALL_PROXY", ALL_PROXY)],
            vec![("https_proxy", HTTPS_PROXY), ("ALL_PROXY", ALL_PROXY)],
            vec![("HTTPS_PROXY", HTTPS_PROXY), ("https_proxy", ALL_PROXY)],
            vec![("HTTPS_PROXY", HTTPS_PROXY), ("REQUEST_METHOD", "GET")],
        ] {
            assert_proxy(environment(&values), HTTPS_PROXY, None);
        }
        for values in [
            vec![("ALL_PROXY", ALL_PROXY)],
            vec![("all_proxy", ALL_PROXY)],
            vec![("ALL_PROXY", ALL_PROXY), ("all_proxy", HTTPS_PROXY)],
            vec![("HTTP_PROXY", HTTPS_PROXY), ("ALL_PROXY", ALL_PROXY)],
        ] {
            assert_proxy(environment(&values), ALL_PROXY, None);
        }
        for values in [
            vec![],
            vec![("HTTP_PROXY", HTTPS_PROXY)],
            vec![("http_proxy", HTTPS_PROXY)],
            vec![("REQUEST_METHOD", "GET")],
            vec![("NO_PROXY", "*")],
        ] {
            assert!(matches!(environment(&values), EnvironmentRoute::Direct));
        }
    }

    #[test]
    fn empty_uppercase_shadows_lowercase_before_falling_back() {
        assert_proxy(
            environment(&[
                ("HTTPS_PROXY", ""),
                ("https_proxy", HTTPS_PROXY),
                ("ALL_PROXY", ALL_PROXY),
            ]),
            ALL_PROXY,
            None,
        );
        for values in [
            vec![("HTTPS_PROXY", ""), ("https_proxy", HTTPS_PROXY)],
            vec![("ALL_PROXY", ""), ("all_proxy", ALL_PROXY)],
            vec![
                ("HTTPS_PROXY", ""),
                ("https_proxy", HTTPS_PROXY),
                ("ALL_PROXY", ""),
                ("all_proxy", ALL_PROXY),
            ],
        ] {
            assert!(matches!(environment(&values), EnvironmentRoute::Direct));
        }
    }

    #[test]
    fn selected_proxy_and_no_proxy_values_are_preserved_for_transport() {
        for value in ["invalid-value", " ", "socks5://synthetic.invalid:1080"] {
            assert_proxy(
                environment(&[("HTTPS_PROXY", value), ("ALL_PROXY", ALL_PROXY)]),
                value,
                None,
            );
        }
        for no_proxy in [
            "*",
            "chatgpt.com",
            ".chatgpt.com",
            "other.invalid,chatgpt.com",
            " ",
        ] {
            assert_proxy(
                environment(&[("HTTPS_PROXY", HTTPS_PROXY), ("NO_PROXY", no_proxy)]),
                HTTPS_PROXY,
                Some(no_proxy),
            );
        }
        assert_proxy(
            environment(&[("HTTPS_PROXY", HTTPS_PROXY), ("no_proxy", "*")]),
            HTTPS_PROXY,
            Some("*"),
        );
        assert_proxy(
            environment(&[
                ("HTTPS_PROXY", HTTPS_PROXY),
                ("NO_PROXY", "other.invalid"),
                ("no_proxy", "*"),
            ]),
            HTTPS_PROXY,
            Some("other.invalid"),
        );
        assert_proxy(
            environment(&[
                ("HTTPS_PROXY", HTTPS_PROXY),
                ("NO_PROXY", ""),
                ("no_proxy", "*"),
            ]),
            HTTPS_PROXY,
            None,
        );
    }

    #[test]
    fn no_proxy_is_read_only_after_an_environment_proxy_is_selected() {
        let reads = RefCell::new(Vec::new());
        let route = environment_route(HTTPS_URL, |key| {
            reads.borrow_mut().push(key.to_string());
            None
        })
        .unwrap();
        assert!(matches!(route, EnvironmentRoute::Direct));
        assert_eq!(
            *reads.borrow(),
            ["HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy"]
        );
    }

    #[test]
    fn official_scheme_families_use_their_correct_environment_variables() {
        let values = [
            ("HTTPS_PROXY", HTTPS_PROXY),
            ("HTTP_PROXY", "http://http.fixture.invalid:8080"),
            ("ALL_PROXY", ALL_PROXY),
        ];
        for scheme in ["https", "wss"] {
            assert_proxy(
                environment_for(&format!("{scheme}://fixture.invalid"), &values),
                HTTPS_PROXY,
                None,
            );
        }
        for scheme in ["http", "ws"] {
            assert_proxy(
                environment_for(&format!("{scheme}://fixture.invalid"), &values),
                values[1].1,
                None,
            );
        }
        assert_proxy(
            environment_for("ftp://fixture.invalid", &values),
            ALL_PROXY,
            None,
        );
        assert_proxy(
            environment_for("wss://fixture.invalid", &values[1..]),
            values[1].1,
            None,
        );
        assert!(matches!(
            environment_route("not a URL", |_| panic!("must reject before reading env")),
            Err(ReaderError::InvalidArguments)
        ));
    }

    #[test]
    fn every_failure_preserves_the_first_route_result_without_any_failover() {
        for kind in [
            ReaderError::MissingCredentials,
            ReaderError::UnsupportedAuth,
            ReaderError::InvalidCredentials,
            ReaderError::CredentialReadFailed,
            ReaderError::CredentialKeyMissing,
            ReaderError::EphemeralCredentials,
            ReaderError::UnsupportedConfiguration,
            ReaderError::ExpiredCredentials,
            ReaderError::Unauthorized,
            ReaderError::Forbidden,
            ReaderError::RateLimited,
            ReaderError::RedirectBlocked,
            ReaderError::DnsFailure,
            ReaderError::ConnectFailure,
            ReaderError::ProxyConfiguration,
            ReaderError::ProxyConnectFailure,
            ReaderError::ProxyAuthentication,
            ReaderError::TlsFailure,
            ReaderError::CertificateConfiguration,
            ReaderError::NetworkConfiguration,
            ReaderError::NetworkFailure,
            ReaderError::Timeout,
            ReaderError::ResponseTooLarge,
            ReaderError::InvalidResponse,
            ReaderError::AccountChanged,
            ReaderError::InvalidArguments,
            ReaderError::InternalError,
        ] {
            for io_kind in [
                IoKind::None,
                IoKind::ConnectionRefused,
                IoKind::ConnectionReset,
                IoKind::ConnectionAborted,
                IoKind::NotConnected,
                IoKind::AddrInUse,
                IoKind::AddrNotAvailable,
                IoKind::NetworkUnreachable,
                IoKind::HostUnreachable,
                IoKind::PermissionDenied,
                IoKind::NotFound,
                IoKind::TimedOut,
                IoKind::WouldBlock,
                IoKind::UnexpectedEof,
                IoKind::InvalidData,
                IoKind::Other,
            ] {
                let expected = Failure {
                    kind,
                    code: match kind {
                        ReaderError::Forbidden => Some(403),
                        ReaderError::ProxyAuthentication => Some(407),
                        _ => None,
                    },
                    network: Some(NetworkDiagnostics {
                        route: NetworkRoute::WindowsAutoProxy,
                        proxy_protocol: ProxyProtocol::Http,
                        error_layer: ErrorLayer::Io,
                        io_kind,
                        os_code: Some(10061),
                        chain_truncated: true,
                        ..Default::default()
                    }),
                };
                let mut calls = 0;
                let mut published = Vec::new();
                let failure = execute_routes::<()>(
                    &[proxy(), proxy(), Route::Direct],
                    Instant::now() + Duration::from_secs(1),
                    Duration::from_secs(1),
                    Default::default(),
                    |detail| published.push(detail),
                    |route, _, _| {
                        calls += 1;
                        assert!(matches!(route, Route::Proxy { .. }));
                        Err(expected)
                    },
                )
                .unwrap_err();
                assert_eq!(calls, 1);
                assert_eq!(failure, expected);
                assert_eq!(published.len(), 2);
                assert_eq!(published.last().copied(), expected.network);
            }
        }
    }

    #[test]
    fn failure_without_diagnostics_keeps_error_and_http_code() {
        let mut expected_detail = None;
        let failure = execute_routes::<()>(
            &[proxy(), Route::Direct],
            Instant::now() + Duration::from_secs(1),
            Duration::from_secs(1),
            Default::default(),
            |_| {},
            |_, _, detail| {
                expected_detail = Some(detail);
                Err(Failure::http(403))
            },
        )
        .unwrap_err();
        assert_eq!(failure.kind, ReaderError::Forbidden);
        assert_eq!(failure.code, Some(403));
        assert_eq!(failure.network, expected_detail);
    }

    #[test]
    fn direct_is_used_only_when_first_and_never_retries_a_proxy() {
        let mut calls = 0;
        let result = execute_routes(
            &[Route::Direct, proxy()],
            Instant::now() + Duration::from_secs(1),
            Duration::from_secs(1),
            Default::default(),
            |_| {},
            |route, _, detail| {
                calls += 1;
                assert!(matches!(route, Route::Direct));
                assert_eq!(detail.route, NetworkRoute::Direct);
                assert_eq!(detail.proxy_protocol, ProxyProtocol::None);
                assert_eq!(
                    detail.system_proxy_resolution,
                    SystemProxyResolution::ResolvedDirect
                );
                Ok(7)
            },
        )
        .unwrap();
        assert_eq!(result, 7);
        assert_eq!(calls, 1);
        let failure = execute_routes::<()>(
            &[Route::Direct, proxy()],
            Instant::now() + Duration::from_secs(1),
            Duration::from_secs(1),
            Default::default(),
            |_| {},
            |route, _, _| {
                assert!(matches!(route, Route::Direct));
                Err(Failure::new(ReaderError::ConnectFailure))
            },
        )
        .unwrap_err();
        assert_eq!(failure.kind, ReaderError::ConnectFailure);
        assert_eq!(failure.network.unwrap().route, NetworkRoute::Direct);
    }

    #[test]
    fn selected_proxy_diagnostics_preserve_static_source_and_clear_stale_errors() {
        for route in [
            NetworkRoute::WindowsProxy,
            NetworkRoute::WindowsAutoProxy,
            NetworkRoute::Unknown,
        ] {
            for (scheme, protocol) in [
                (ProxyScheme::Http, ProxyProtocol::Http),
                (ProxyScheme::Https, ProxyProtocol::Https),
            ] {
                let profile = NetworkDiagnostics {
                    route,
                    tls_backend: TlsBackend::NativeTls,
                    no_proxy_bypass: true,
                    error_layer: ErrorLayer::Io,
                    io_kind: IoKind::ConnectionRefused,
                    os_code: Some(10061),
                    chain_truncated: true,
                    system_proxy_config: SystemProxyConfig::AutoConfig,
                    ..Default::default()
                };
                execute_routes(
                    &[Route::Proxy {
                        scheme,
                        host: "fixture.invalid".into(),
                        port: 8080,
                    }],
                    Instant::now() + Duration::from_secs(10),
                    Duration::from_millis(123),
                    profile,
                    |_| {},
                    |_, timeout, detail| {
                        assert_eq!(timeout, Duration::from_millis(123));
                        assert_eq!(
                            detail.route,
                            if route == NetworkRoute::WindowsProxy {
                                NetworkRoute::WindowsProxy
                            } else {
                                NetworkRoute::WindowsAutoProxy
                            }
                        );
                        assert_eq!(detail.proxy_protocol, protocol);
                        assert_eq!(
                            detail.system_proxy_resolution,
                            SystemProxyResolution::ResolvedProxy
                        );
                        assert_eq!(detail.system_proxy_config, profile.system_proxy_config);
                        assert_eq!(detail.tls_backend, profile.tls_backend);
                        assert!(!detail.no_proxy_bypass);
                        assert_eq!(detail.error_layer, ErrorLayer::None);
                        assert_eq!(detail.io_kind, IoKind::None);
                        assert_eq!(detail.os_code, None);
                        assert!(!detail.chain_truncated);
                        Ok(())
                    },
                )
                .unwrap();
            }
        }
    }

    #[test]
    fn first_unsupported_route_fails_and_later_routes_are_never_inspected() {
        let failure = execute_routes::<()>(
            &[Route::UnsupportedScheme, proxy(), Route::Direct],
            Instant::now() + Duration::from_secs(1),
            Duration::from_secs(1),
            Default::default(),
            |_| {},
            |_, _, _| panic!("must not connect"),
        )
        .unwrap_err();
        assert_eq!(failure.kind, ReaderError::ProxyConfiguration);
        assert_eq!(
            failure.network.unwrap().system_proxy_resolution,
            SystemProxyResolution::Unsupported
        );
        let failure = execute_routes::<()>(
            &[proxy(), Route::UnsupportedScheme, Route::Direct],
            Instant::now() + Duration::from_secs(1),
            Duration::from_secs(1),
            Default::default(),
            |_| {},
            |_, _, _| Err(Failure::new(ReaderError::ConnectFailure)),
        )
        .unwrap_err();
        assert_eq!(failure.kind, ReaderError::ConnectFailure);
        assert_eq!(
            failure.network.unwrap().system_proxy_resolution,
            SystemProxyResolution::ResolvedProxy
        );
        assert_eq!(
            execute_routes(
                &[proxy(), Route::UnsupportedScheme, Route::Direct],
                Instant::now() + Duration::from_secs(1),
                Duration::from_secs(1),
                Default::default(),
                |_| {},
                |_, _, _| Ok(9),
            )
            .unwrap(),
            9
        );
    }

    #[test]
    fn exhausted_deadline_starts_no_connection_and_empty_list_is_failure() {
        let failure = execute_routes::<()>(
            &[Route::Direct],
            Instant::now(),
            Duration::from_secs(1),
            Default::default(),
            |_| {},
            |_, _, _| panic!("must not connect"),
        )
        .unwrap_err();
        assert_eq!(failure.kind, ReaderError::Timeout);
        let failure = execute_routes::<()>(
            &[],
            Instant::now() + Duration::from_secs(1),
            Duration::from_secs(1),
            Default::default(),
            |_| {},
            |_, _, _| panic!("must not connect"),
        )
        .unwrap_err();
        assert_eq!(failure.kind, ReaderError::ProxyConfiguration);
    }
}
