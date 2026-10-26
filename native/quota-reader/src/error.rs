// SPDX-License-Identifier: Apache-2.0
use serde::Serialize;
use std::sync::{
    atomic::{AtomicU64, Ordering},
    Arc,
};

/// Closed diagnostics only. Never attach tokens, paths, URLs, bodies or library errors.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
pub enum ReaderError {
    MissingCredentials,
    UnsupportedAuth,
    InvalidCredentials,
    CredentialReadFailed,
    CredentialKeyMissing,
    EphemeralCredentials,
    UnsupportedConfiguration,
    ExpiredCredentials,
    Unauthorized,
    Forbidden,
    RateLimited,
    RedirectBlocked,
    DnsFailure,
    ConnectFailure,
    ProxyConfiguration,
    ProxyConnectFailure,
    ProxyAuthentication,
    TlsFailure,
    CertificateConfiguration,
    NetworkConfiguration,
    NetworkFailure,
    Timeout,
    ResponseTooLarge,
    InvalidResponse,
    AccountChanged,
    InvalidArguments,
    InternalError,
}

pub type ErrorKind = ReaderError;

// Every diagnostic value is a closed label. No Display/source text is serializable.
macro_rules! diagnostic_enum {
    ($name:ident { $first:ident $(, $variant:ident)* $(,)? }) => {
        #[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
        #[repr(u8)]
        pub enum $name { $first, $($variant),* }
        impl $name {
            fn from_tag(tag: u64) -> Self {
                const VALUES: &[$name] = &[$name::$first, $($name::$variant),*];
                VALUES.get(tag as usize).copied().unwrap_or(Self::$first)
            }
        }
    };
}
diagnostic_enum!(NetworkRoute {
    Unknown,
    Direct,
    EnvironmentProxy,
    WindowsProxy,
    OtherSystemProxy,
    WindowsAutoProxy
});
diagnostic_enum!(ProxyProtocol {
    None,
    Http,
    Https,
    Socks4,
    Socks4a,
    Socks5,
    Socks5h,
    Other
});
diagnostic_enum!(TlsBackend {
    Unknown,
    NativeTls,
    Rustls
});
diagnostic_enum!(DotenvStatus {
    NotChecked,
    Absent,
    Loaded,
    Failed
});
diagnostic_enum!(ErrorLayer {
    None,
    Reqwest,
    HyperConnect,
    Io,
    NativeTls,
    Rustls,
    HttpStatus,
    Timeout,
    Unknown,
    SystemProxy
});
diagnostic_enum!(IoKind {
    None,
    ConnectionRefused,
    ConnectionReset,
    ConnectionAborted,
    NotConnected,
    AddrInUse,
    AddrNotAvailable,
    NetworkUnreachable,
    HostUnreachable,
    PermissionDenied,
    NotFound,
    TimedOut,
    WouldBlock,
    UnexpectedEof,
    InvalidData,
    Other
});

diagnostic_enum!(SystemProxyConfig {
    NotChecked,
    Absent,
    StaticOnly,
    AutoConfig,
    AutoDetect,
    AutoConfigAndDetect,
    ReadFailed
});
diagnostic_enum!(SystemProxyResolution {
    NotChecked,
    NotNeeded,
    Resolving,
    ResolvedProxy,
    ResolvedDirect,
    Failed,
    TimedOut,
    Unsupported,
    Unavailable
});

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct NetworkDiagnostics {
    pub route: NetworkRoute,
    pub proxy_protocol: ProxyProtocol,
    pub no_proxy_bypass: bool,
    pub tls_backend: TlsBackend,
    pub ca_override: bool,
    pub dotenv: DotenvStatus,
    pub error_layer: ErrorLayer,
    pub io_kind: IoKind,
    pub os_code: Option<i32>,
    pub chain_truncated: bool,
    pub system_proxy_config: SystemProxyConfig,
    pub system_proxy_resolution: SystemProxyResolution,
    pub system_proxy_static_present: bool,
}
impl Serialize for NetworkDiagnostics {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        use serde::ser::SerializeStruct;
        let mut state = serializer.serialize_struct("NetworkDiagnostics", 15)?;
        state.serialize_field("route", &self.route)?;
        state.serialize_field("proxyProtocol", &self.proxy_protocol)?;
        state.serialize_field("noProxyBypass", &self.no_proxy_bypass)?;
        state.serialize_field("tlsBackend", &self.tls_backend)?;
        state.serialize_field("caOverride", &self.ca_override)?;
        state.serialize_field("dotenv", &self.dotenv)?;
        state.serialize_field("errorLayer", &self.error_layer)?;
        state.serialize_field("ioKind", &self.io_kind)?;
        state.serialize_field("osCode", &self.os_code)?;
        state.serialize_field("chainTruncated", &self.chain_truncated)?;
        state.serialize_field("systemProxyConfig", &self.system_proxy_config)?;
        state.serialize_field("systemProxyResolution", &self.system_proxy_resolution)?;
        state.serialize_field(
            "systemProxyStaticPresent",
            &self.system_proxy_static_present,
        )?;
        state.serialize_field(
            "systemProxyAutoDetect",
            &matches!(
                self.system_proxy_config,
                SystemProxyConfig::AutoDetect | SystemProxyConfig::AutoConfigAndDetect
            ),
        )?;
        state.serialize_field(
            "systemProxyPacPresent",
            &matches!(
                self.system_proxy_config,
                SystemProxyConfig::AutoConfig | SystemProxyConfig::AutoConfigAndDetect
            ),
        )?;
        state.end()
    }
}
impl Default for NetworkDiagnostics {
    fn default() -> Self {
        Self {
            route: NetworkRoute::Unknown,
            proxy_protocol: ProxyProtocol::None,
            no_proxy_bypass: false,
            tls_backend: TlsBackend::Unknown,
            ca_override: false,
            dotenv: DotenvStatus::NotChecked,
            error_layer: ErrorLayer::None,
            io_kind: IoKind::None,
            os_code: None,
            chain_truncated: false,
            system_proxy_config: SystemProxyConfig::NotChecked,
            system_proxy_resolution: SystemProxyResolution::NotChecked,
            system_proxy_static_present: false,
        }
    }
}
impl NetworkDiagnostics {
    pub fn clear_error(mut self) -> Self {
        self.error_layer = ErrorLayer::None;
        self.io_kind = IoKind::None;
        self.os_code = None;
        self.chain_truncated = false;
        self
    }
    pub fn with_error(mut self, layer: ErrorLayer) -> Self {
        self.error_layer = layer;
        self
    }
    // One atomic word provides a coherent snapshot to the shared-deadline thread.
    // It never waits for a network worker, and cannot hold strings or identifiers.
    fn encode(self) -> u64 {
        self.route as u64
            | (self.proxy_protocol as u64) << 3
            | (self.tls_backend as u64) << 7
            | (self.ca_override as u64) << 9
            | (self.dotenv as u64) << 10
            | (self.error_layer as u64) << 12
            | (self.io_kind as u64) << 16
            | (self.os_code.is_some() as u64) << 21
            | (self.os_code.unwrap_or_default() as u32 as u64) << 22
            | (self.chain_truncated as u64) << 54
            | (self.no_proxy_bypass as u64) << 55
            | (self.system_proxy_config as u64) << 56
            | (self.system_proxy_resolution as u64) << 59
            | (self.system_proxy_static_present as u64) << 63
    }
    fn decode(bits: u64) -> Self {
        Self {
            route: NetworkRoute::from_tag(bits & 7),
            proxy_protocol: ProxyProtocol::from_tag((bits >> 3) & 15),
            tls_backend: TlsBackend::from_tag((bits >> 7) & 3),
            ca_override: bits & (1 << 9) != 0,
            dotenv: DotenvStatus::from_tag((bits >> 10) & 3),
            error_layer: ErrorLayer::from_tag((bits >> 12) & 15),
            io_kind: IoKind::from_tag((bits >> 16) & 31),
            os_code: (bits & (1 << 21) != 0).then_some((bits >> 22) as u32 as i32),
            chain_truncated: bits & (1 << 54) != 0,
            no_proxy_bypass: bits & (1 << 55) != 0,
            system_proxy_config: SystemProxyConfig::from_tag((bits >> 56) & 7),
            system_proxy_resolution: SystemProxyResolution::from_tag((bits >> 59) & 15),
            system_proxy_static_present: bits & (1 << 63) != 0,
        }
    }
}

#[derive(Clone, Default)]
pub struct NetworkSnapshot(Arc<AtomicU64>);
impl NetworkSnapshot {
    pub fn load(&self) -> NetworkDiagnostics {
        NetworkDiagnostics::decode(self.0.load(Ordering::Acquire))
    }
    pub fn store(&self, diagnostics: NetworkDiagnostics) {
        self.0.store(diagnostics.encode(), Ordering::Release);
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Failure {
    pub kind: ReaderError,
    pub code: Option<u16>,
    pub network: Option<NetworkDiagnostics>,
}

impl Failure {
    pub fn new(kind: ReaderError) -> Self {
        Self {
            kind,
            code: None,
            network: None,
        }
    }
    pub fn with_network(mut self, network: NetworkDiagnostics) -> Self {
        self.network = Some(network);
        self
    }
    pub fn http(code: u16) -> Self {
        let kind = match code {
            401 => ReaderError::Unauthorized,
            403 => ReaderError::Forbidden,
            429 => ReaderError::RateLimited,
            300..=399 => ReaderError::RedirectBlocked,
            _ => ReaderError::NetworkFailure,
        };
        Self {
            kind,
            code: Some(code),
            network: None,
        }
    }
}
impl From<ReaderError> for Failure {
    fn from(kind: ReaderError) -> Self {
        Self::new(kind)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn atomic_snapshot_round_trips_closed_values_and_signed_os_codes() {
        let snapshot = NetworkSnapshot::default();
        assert_eq!(snapshot.load(), NetworkDiagnostics::default());
        for os_code in [
            None,
            Some(0),
            Some(10061),
            Some(-1),
            Some(i32::MIN),
            Some(i32::MAX),
        ] {
            let value = NetworkDiagnostics {
                route: NetworkRoute::OtherSystemProxy,
                proxy_protocol: ProxyProtocol::Other,
                no_proxy_bypass: true,
                tls_backend: TlsBackend::Rustls,
                ca_override: true,
                dotenv: DotenvStatus::Failed,
                error_layer: ErrorLayer::Unknown,
                io_kind: IoKind::Other,
                os_code,
                chain_truncated: true,
                system_proxy_config: SystemProxyConfig::AutoConfigAndDetect,
                system_proxy_resolution: SystemProxyResolution::Unavailable,
                system_proxy_static_present: true,
            };
            snapshot.store(value);
            assert_eq!(snapshot.load(), value);
        }
    }
    #[test]
    fn deadline_observes_a_coherent_snapshot_without_locks() {
        let snapshot = NetworkSnapshot::default();
        let first = NetworkDiagnostics::default();
        let second = NetworkDiagnostics {
            route: NetworkRoute::EnvironmentProxy,
            proxy_protocol: ProxyProtocol::Https,
            tls_backend: TlsBackend::Rustls,
            ca_override: true,
            dotenv: DotenvStatus::Loaded,
            error_layer: ErrorLayer::Io,
            io_kind: IoKind::ConnectionRefused,
            os_code: Some(10061),
            ..first
        };
        let writer = snapshot.clone();
        let worker = std::thread::spawn(move || {
            for _ in 0..10000 {
                writer.store(second);
                writer.store(first);
            }
        });
        for _ in 0..10000 {
            let read = snapshot.load();
            assert!(read == first || read == second);
        }
        worker.join().unwrap();
    }
}
