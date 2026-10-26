// SPDX-License-Identifier: Apache-2.0
use codex_quota_reader::{
    auth::ExistingAuthSource,
    error::{DotenvStatus, ErrorLayer, NetworkSnapshot, ReaderError},
    http::{observed_network, OfficialTransport},
    network_config::NetworkEnvironment,
    Envelope, Stage,
};
use std::io::{self, Write};
use std::time::{Duration, Instant};

fn prepare(
    diagnostics: &NetworkSnapshot,
) -> Result<(ExistingAuthSource, NetworkEnvironment), Envelope> {
    let mut args = std::env::args_os().skip(1);
    if args.next().as_deref() != Some(std::ffi::OsStr::new("--read")) || args.next().is_some() {
        return Err(Envelope::failed(
            Stage::AuthLoad,
            ReaderError::InvalidArguments,
        ));
    }
    let source = ExistingAuthSource::from_environment()
        .map_err(|kind| Envelope::failed(Stage::AuthLoad, kind))?;
    let network = match NetworkEnvironment::read(&source) {
        Ok(network) => network,
        Err(kind) => {
            let mut profile = diagnostics.load();
            profile.dotenv = DotenvStatus::Failed;
            diagnostics.store(profile);
            return Err(Envelope::failed(Stage::UsageRead, kind).with_network(profile));
        }
    };
    let mut profile = diagnostics.load();
    profile.dotenv = network.dotenv_status();
    diagnostics.store(profile);
    Ok((source, network))
}

fn read(source: ExistingAuthSource, diagnostics: NetworkSnapshot) -> Envelope {
    let transport = match OfficialTransport::new(diagnostics) {
        Ok(value) => value,
        Err(kind) => return Envelope::failed(Stage::UsageRead, kind),
    };
    codex_quota_reader::read_quota(&source, &transport)
}

/// The worker is joined before returning its result. On timeout the caller exits
/// the process, discarding it; unfinished preflight data can never be applied.
fn run_worker<T: Send + 'static>(
    deadline: Instant,
    stage: Stage,
    diagnostics: &NetworkSnapshot,
    work: impl FnOnce() -> T + Send + 'static,
) -> Result<T, Envelope> {
    let remaining = deadline.saturating_duration_since(Instant::now());
    if remaining.is_zero() {
        return Err(worker_failure(stage, ReaderError::Timeout, diagnostics));
    }
    let (sender, receiver) = std::sync::mpsc::sync_channel(1);
    let worker = std::thread::spawn(move || {
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(work));
        let _ = sender.send(result);
    });
    let result = receiver.recv_timeout(remaining).map_err(|error| {
        worker_failure(
            stage,
            match error {
                std::sync::mpsc::RecvTimeoutError::Timeout => ReaderError::Timeout,
                std::sync::mpsc::RecvTimeoutError::Disconnected => ReaderError::InternalError,
            },
            diagnostics,
        )
    })?;
    worker
        .join()
        .map_err(|_| worker_failure(stage, ReaderError::InternalError, diagnostics))?;
    if Instant::now() >= deadline {
        return Err(worker_failure(stage, ReaderError::Timeout, diagnostics));
    }
    result.map_err(|_| worker_failure(stage, ReaderError::InternalError, diagnostics))
}

fn worker_failure(stage: Stage, kind: ReaderError, diagnostics: &NetworkSnapshot) -> Envelope {
    let mut network = observed_network(diagnostics.load());
    network.error_layer = if kind == ReaderError::Timeout
        && network.system_proxy_resolution
            == codex_quota_reader::error::SystemProxyResolution::Resolving
    {
        network.system_proxy_resolution =
            codex_quota_reader::error::SystemProxyResolution::TimedOut;
        ErrorLayer::SystemProxy
    } else if kind == ReaderError::Timeout {
        ErrorLayer::Timeout
    } else {
        ErrorLayer::Unknown
    };
    Envelope::failed(stage, kind).with_network(network)
}

fn run_bounded() -> Envelope {
    // Includes home selection, dotenv parsing, credential/KDF work and OS DNS.
    let deadline = Instant::now() + Duration::from_secs(30);
    let diagnostics = NetworkSnapshot::default();
    let preflight = diagnostics.clone();
    let (source, network) = match run_worker(deadline, Stage::UsageRead, &diagnostics, move || {
        prepare(&preflight)
    }) {
        Ok(Ok(prepared)) => prepared,
        Ok(Err(failure)) | Err(failure) => {
            return if failure.network.is_some() {
                failure
            } else {
                failure.with_network(diagnostics.load())
            }
        }
    };
    // The read-only preflight worker has been joined. No client/runtime or other
    // worker exists while this sole thread applies the fully validated allowlist.
    if std::panic::catch_unwind(|| unsafe { network.apply() }).is_err() {
        return Envelope::failed(Stage::UsageRead, ReaderError::NetworkConfiguration)
            .with_network(diagnostics.load());
    }
    let request = diagnostics.clone();
    match run_worker(deadline, Stage::RateLimitsRead, &diagnostics, move || {
        read(source, request)
    }) {
        Ok(envelope) | Err(envelope) => envelope,
    }
}

fn main() {
    // A third-party panic must never print credentials, a path or a response to stderr.
    std::panic::set_hook(Box::new(|_| {}));
    // Returning from main terminates a worker that exceeded the shared deadline.
    let envelope = std::panic::catch_unwind(run_bounded).unwrap_or_else(|_| {
        Envelope::failed(Stage::AuthLoad, ReaderError::InternalError)
            .with_network(Default::default())
    });
    let mut bytes = envelope.to_bounded_json();
    bytes.push(b'\n');
    // A broken pipe is simply a finished consumer. Do not emit a second document.
    let _ = io::stdout().lock().write_all(&bytes);
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{
        atomic::{AtomicBool, Ordering},
        Arc,
    };

    #[test]
    fn preflight_timeout_cannot_deliver_partial_configuration() {
        let result = run_worker(
            Instant::now() + Duration::from_millis(5),
            Stage::UsageRead,
            &NetworkSnapshot::default(),
            || {
                std::thread::sleep(Duration::from_millis(50));
                "must never apply"
            },
        );
        let failure = result.unwrap_err();
        assert_eq!(failure.error_kind, Some(ReaderError::Timeout));
        assert_eq!(failure.stage, Stage::UsageRead);
    }

    #[test]
    fn worker_has_exited_before_result_can_be_applied() {
        let exited = Arc::new(AtomicBool::new(false));
        struct OnDrop(Arc<AtomicBool>);
        impl Drop for OnDrop {
            fn drop(&mut self) {
                self.0.store(true, Ordering::SeqCst);
            }
        }
        let flag = exited.clone();
        run_worker(
            Instant::now() + Duration::from_secs(1),
            Stage::UsageRead,
            &NetworkSnapshot::default(),
            move || {
                let _guard = OnDrop(flag);
                "validated"
            },
        )
        .unwrap();
        assert!(exited.load(Ordering::SeqCst));
    }

    #[test]
    fn quota_stage_cannot_reset_the_shared_deadline() {
        let ran = Arc::new(AtomicBool::new(false));
        let flag = ran.clone();
        let result = run_worker(
            Instant::now() - Duration::from_millis(1),
            Stage::RateLimitsRead,
            &NetworkSnapshot::default(),
            move || {
                flag.store(true, Ordering::SeqCst);
            },
        );
        assert_eq!(result.unwrap_err().error_kind, Some(ReaderError::Timeout));
        assert!(!ran.load(Ordering::SeqCst));
    }

    #[test]
    fn preflight_panic_returns_a_closed_failure() {
        let result = run_worker(
            Instant::now() + Duration::from_secs(1),
            Stage::UsageRead,
            &NetworkSnapshot::default(),
            || panic!("synthetic sensitive fixture, must not reach protocol"),
        );
        let failure = result.unwrap_err();
        assert_eq!(failure.error_kind, Some(ReaderError::InternalError));
        assert!(!String::from_utf8(failure.to_bounded_json())
            .unwrap()
            .contains("synthetic"));
    }
    #[test]
    fn shared_deadline_preserves_known_profile_and_sets_timeout_layer() {
        let diagnostics = NetworkSnapshot::default();
        let profile = codex_quota_reader::error::NetworkDiagnostics {
            tls_backend: codex_quota_reader::error::TlsBackend::Rustls,
            ca_override: true,
            dotenv: DotenvStatus::Loaded,
            ..Default::default()
        };
        diagnostics.store(profile);
        let start = Instant::now();
        let failure = run_worker(
            Instant::now() + Duration::from_millis(5),
            Stage::RateLimitsRead,
            &diagnostics,
            || std::thread::sleep(Duration::from_millis(50)),
        )
        .unwrap_err();
        assert!(start.elapsed() < Duration::from_millis(45));
        let network = failure.network.unwrap();
        assert_eq!(network.tls_backend, profile.tls_backend);
        assert!(network.ca_override);
        assert_eq!(network.dotenv, DotenvStatus::Loaded);
        assert_eq!(network.error_layer, ErrorLayer::Timeout);
        assert_eq!(network.os_code, None);
    }
}
