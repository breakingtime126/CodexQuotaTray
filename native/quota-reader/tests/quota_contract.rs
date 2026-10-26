use codex_quota_reader::auth::{CredentialSource, Credentials};
use codex_quota_reader::error::{
    DotenvStatus, ErrorLayer, Failure, IoKind, NetworkDiagnostics, NetworkRoute, ProxyProtocol,
    ReaderError, TlsBackend,
};
use codex_quota_reader::http::{Endpoint, Transport, MAX_RESPONSE_BYTES};
use codex_quota_reader::quota::{parse_reset_credits, parse_usage};
use codex_quota_reader::{read_quota, Envelope, Stage, Status};
use serde_json::{json, Value};
use std::cell::RefCell;
use std::collections::VecDeque;

fn credential(token: &str, account: &str) -> Credentials {
    Credentials {
        access_token: token.into(),
        account_id: account.into(),
    }
}
struct Source {
    values: RefCell<VecDeque<Result<Credentials, ReaderError>>>,
    calls: RefCell<usize>,
}
impl Source {
    fn new(values: Vec<Result<Credentials, ReaderError>>) -> Self {
        Self {
            values: RefCell::new(values.into()),
            calls: RefCell::new(0),
        }
    }
}
impl CredentialSource for Source {
    fn load(&self) -> Result<Credentials, ReaderError> {
        *self.calls.borrow_mut() += 1;
        self.values
            .borrow_mut()
            .pop_front()
            .expect("unexpected auth reload")
    }
}
struct FakeTransport {
    responses: RefCell<VecDeque<Result<Vec<u8>, Failure>>>,
    requests: RefCell<Vec<(Endpoint, String, String)>>,
}
impl FakeTransport {
    fn new(responses: Vec<Result<Vec<u8>, Failure>>) -> Self {
        Self {
            responses: RefCell::new(responses.into()),
            requests: RefCell::new(vec![]),
        }
    }
}
impl Transport for FakeTransport {
    fn get(&self, endpoint: Endpoint, credentials: &Credentials) -> Result<Vec<u8>, Failure> {
        self.requests.borrow_mut().push((
            endpoint,
            credentials.access_token.clone(),
            credentials.account_id.clone(),
        ));
        self.responses
            .borrow_mut()
            .pop_front()
            .expect("unexpected network call")
    }
}
fn usage() -> Result<Vec<u8>, Failure> {
    Ok(serde_json::to_vec(&json!({
    "plan_type":"prolite","account_id":"account-a",
    "rate_limit":{"primary_window":{"used_percent":42.25,"limit_window_seconds":18000,"reset_at":1791770703},
        "secondary_window":{"used_percent":84,"limit_window_seconds":604800,"reset_at":1791980767}},
    "rate_limit_reset_credits":{"available_count":3},
    "user_id":"must-never-leave-helper","token":"must-never-leave-helper"
})).unwrap())
}
fn details() -> Result<Vec<u8>, Failure> {
    Ok(serde_json::to_vec(&json!({"available_count":3,"credits":[
    {"id":"card-1","reset_type":"codex_rate_limits","status":"available","granted_at":"2026-10-01T00:00:01Z","expires_at":"2026-11-01T12:34:56+02:00","title":"untrusted-title-never-emitted"},
    {"id":"card-2","reset_type":"codex_rate_limits","status":"available","granted_at":"2026-10-01T00:00:01Z","expires_at":null}
]})).unwrap())
}
fn basic_source() -> Source {
    Source::new(vec![Ok(credential("old-token", "account-a"))])
}

#[test]
fn maps_real_plan_windows_and_second_precision() {
    let result = parse_usage(&usage().unwrap(), "account-a").unwrap();
    let bucket = &result.rate_limits;
    assert_eq!(bucket.plan_type, Some("prolite"));
    assert_eq!(bucket.primary.as_ref().unwrap().used_percent, 42.25);
    assert_eq!(
        bucket.primary.as_ref().unwrap().window_duration_mins,
        Some(300)
    );
    assert_eq!(
        bucket.secondary.as_ref().unwrap().window_duration_mins,
        Some(10080)
    );
    assert_eq!(
        bucket.secondary.as_ref().unwrap().resets_at,
        Some(1791980767)
    );
    assert_eq!(result.rate_limit_reset_credits.unwrap().available_count, 3);
}
#[test]
fn optional_values_stay_unknown_and_duration_rounding_matches_upstream() {
    let result = parse_usage(br#"{"plan_type":"future-plan","rate_limit":{"primary_window":{"used_percent":10},"secondary_window":{"used_percent":2,"limit_window_seconds":61}}}"#, "account-a").unwrap();
    assert_eq!(result.rate_limits.plan_type, Some("unknown"));
    assert!(result.rate_limit_reset_credits.is_none());
    let primary = result.rate_limits.primary.unwrap();
    assert!(primary.resets_at.is_none());
    assert!(primary.window_duration_mins.is_none());
    assert_eq!(
        result.rate_limits.secondary.unwrap().window_duration_mins,
        Some(2)
    );
    let missing = parse_usage(
        br#"{"rate_limit_reset_credits":{"available_count":0}}"#,
        "account-a",
    )
    .unwrap();
    assert!(missing.rate_limits.plan_type.is_none());
    assert!(missing.rate_limits.primary.is_none());
}
#[test]
fn plan_mapping_preserves_skus_and_official_education_alias() {
    for (input, expected) in [
        ("plus", "plus"),
        ("pro", "pro"),
        ("prolite", "prolite"),
        ("promax", "promax"),
        ("education", "edu"),
        ("guest", "unknown"),
        ("self_serve_business_prolite", "self_serve_business_prolite"),
        ("ent26", "ent26"),
    ] {
        let bytes = serde_json::to_vec(&json!({"plan_type":input})).unwrap();
        assert_eq!(
            parse_usage(&bytes, "account-a")
                .unwrap()
                .rate_limits
                .plan_type,
            Some(expected)
        );
    }
}
#[test]
fn rejects_malformed_wrong_account_duplicate_bucket_and_oversize() {
    for body in [
        b"not json".as_slice(),
        br#"{}"#,
        br#"[]"#,
        br#"{"plan_type":"pro","rate_limit":{"primary_window":{"used_percent":"NaN"}}}"#,
        br#"{"plan_type":"pro","rate_limit_reset_credits":{"available_count":-1}}"#,
        br#"{"plan_type":"pro","additional_rate_limits":[{"metered_feature":"codex"}]}"#,
    ] {
        assert!(matches!(
            parse_usage(body, "account-a"),
            Err(ReaderError::InvalidResponse)
        ));
    }
    assert!(matches!(
        parse_usage(&usage().unwrap(), "different-account"),
        Err(ReaderError::AccountChanged)
    ));
    assert!(matches!(
        parse_usage(&vec![b' '; MAX_RESPONSE_BYTES + 1], "account-a"),
        Err(ReaderError::ResponseTooLarge)
    ));
}
#[test]
fn detailed_cards_map_types_and_times_without_server_display_text() {
    let result = parse_reset_credits(&details().unwrap()).unwrap();
    assert_eq!(result.available_count, 3);
    let cards = result.credits.unwrap();
    assert_eq!(cards.len(), 2);
    assert_eq!(cards[0].reset_type, "codexRateLimits");
    assert_eq!(cards[0].expires_at, Some(1793529296));
    assert_eq!(cards[1].expires_at, None);
    let serialized = serde_json::to_string(&cards).unwrap();
    assert!(!serialized.contains("untrusted-title"));
}
#[test]
fn missing_or_invalid_expiry_is_not_invented_as_never() {
    for row in [
        json!({"id":"a","reset_type":"codex_rate_limits","status":"available","granted_at":"2026-10-01T00:00:00Z"}),
        json!({"id":"a","reset_type":"codex_rate_limits","status":"available","granted_at":"2026-10-01T00:00:00Z","expires_at":"bad timestamp"}),
    ] {
        let bytes = serde_json::to_vec(&json!({"available_count":4,"credits":[row]})).unwrap();
        assert!(matches!(
            parse_reset_credits(&bytes),
            Err(ReaderError::InvalidResponse)
        ));
    }
}
#[test]
fn success_emits_one_bounded_typed_object_and_no_identity_or_tokens() {
    let source = basic_source();
    let transport = FakeTransport::new(vec![usage(), details()]);
    let envelope = read_quota(&source, &transport);
    assert_eq!(envelope.status, Status::Ready);
    assert_eq!(envelope.stage, Stage::RateLimitsRead);
    let bytes = envelope.to_bounded_json();
    let value: Value = serde_json::from_slice(&bytes).unwrap();
    assert_eq!(value["protocolVersion"], 1);
    assert_eq!(value["helperVersion"], "0.2.16");
    assert_eq!(
        value["result"]["rateLimitResetCredits"]["availableCount"],
        3
    );
    let output = String::from_utf8(bytes).unwrap();
    for secret in [
        "old-token",
        "account-a",
        "must-never-leave-helper",
        "untrusted-title",
    ] {
        assert!(!output.contains(secret));
    }
    assert_eq!(*source.calls.borrow(), 1);
    assert_eq!(transport.requests.borrow().len(), 2);
}
#[test]
fn usage_401_reloads_changed_token_same_account_once() {
    let source = Source::new(vec![
        Ok(credential("old", "account-a")),
        Ok(credential("new", "account-a")),
    ]);
    let transport = FakeTransport::new(vec![Err(Failure::http(401)), usage(), details()]);
    assert_eq!(read_quota(&source, &transport).status, Status::Ready);
    assert_eq!(*source.calls.borrow(), 2);
    assert_eq!(
        transport
            .requests
            .borrow()
            .iter()
            .map(|r| r.1.as_str())
            .collect::<Vec<_>>(),
        vec!["old", "new", "new"]
    );
}
#[test]
fn reset_401_can_use_only_remaining_global_retry_budget() {
    let source = Source::new(vec![
        Ok(credential("old", "account-a")),
        Ok(credential("new", "account-a")),
    ]);
    let transport = FakeTransport::new(vec![usage(), Err(Failure::http(401)), details()]);
    let out = read_quota(&source, &transport);
    assert_eq!(out.status, Status::Ready);
    assert!(out.error_kind.is_none());
    assert_eq!(*source.calls.borrow(), 2);
    assert_eq!(transport.requests.borrow().len(), 3);
    let source = Source::new(vec![
        Ok(credential("old", "account-a")),
        Ok(credential("new", "account-a")),
    ]);
    let transport = FakeTransport::new(vec![
        Err(Failure::http(401)),
        usage(),
        Err(Failure::http(401)),
    ]);
    let out = read_quota(&source, &transport);
    assert_eq!(out.stage, Stage::ResetCardsRead);
    assert_eq!(out.code, Some(401));
    assert_eq!(*source.calls.borrow(), 2);
    assert_eq!(transport.requests.borrow().len(), 3);
}
#[test]
fn unchanged_token_and_repeat_401_never_refresh_or_retry_again() {
    for changed in [false, true] {
        let source = Source::new(vec![
            Ok(credential("old", "account-a")),
            Ok(credential(if changed { "new" } else { "old" }, "account-a")),
        ]);
        let transport = FakeTransport::new(vec![Err(Failure::http(401)), Err(Failure::http(401))]);
        let out = read_quota(&source, &transport);
        assert_eq!(out.status, Status::NeedsLogin);
        assert!(out.result.is_none());
        assert_eq!(*source.calls.borrow(), 2);
        assert_eq!(
            transport.requests.borrow().len(),
            if changed { 2 } else { 1 }
        );
    }
}
#[test]
fn changed_account_on_reload_never_retries_or_shows_old_account() {
    for at_reset in [false, true] {
        let source = Source::new(vec![
            Ok(credential("old", "account-a")),
            Ok(credential("new", "account-b")),
        ]);
        let transport = FakeTransport::new(if at_reset {
            vec![usage(), Err(Failure::http(401))]
        } else {
            vec![Err(Failure::http(401))]
        });
        let out = read_quota(&source, &transport);
        assert_eq!(out.error_kind, Some(ReaderError::AccountChanged));
        assert!(out.result.is_none());
        assert_eq!(
            transport.requests.borrow().len(),
            if at_reset { 2 } else { 1 }
        );
    }
}
#[test]
fn non_401_failures_do_not_reload_or_retry_and_details_preserve_count() {
    for failure in [
        Failure::http(403),
        Failure::http(429),
        Failure::http(500),
        Failure::http(302),
        ReaderError::Timeout.into(),
        ReaderError::NetworkFailure.into(),
    ] {
        let source = basic_source();
        let transport = FakeTransport::new(vec![Err(failure)]);
        let out = read_quota(&source, &transport);
        assert!(out.result.is_none());
        assert_eq!(*source.calls.borrow(), 1);
        let source = basic_source();
        let transport = FakeTransport::new(vec![usage(), Err(failure)]);
        let out = read_quota(&source, &transport);
        assert_eq!(out.status, Status::Ready);
        assert_eq!(out.stage, Stage::ResetCardsRead);
        let summary = out.result.unwrap().rate_limit_reset_credits.unwrap();
        assert_eq!(summary.available_count, 3);
        assert!(summary.credits.is_none());
        assert_eq!(*source.calls.borrow(), 1);
    }
}
#[test]
fn malformed_details_fall_back_to_actual_usage_count_not_zero() {
    let out = read_quota(
        &basic_source(),
        &FakeTransport::new(vec![usage(), Ok(b"server secret malformed".to_vec())]),
    );
    assert_eq!(out.error_kind, Some(ReaderError::InvalidResponse));
    assert_eq!(
        out.result
            .unwrap()
            .rate_limit_reset_credits
            .unwrap()
            .available_count,
        3
    );
}
#[test]
fn missing_credentials_causes_zero_requests() {
    let source = Source::new(vec![Err(ReaderError::MissingCredentials)]);
    let transport = FakeTransport::new(vec![]);
    let out = read_quota(&source, &transport);
    assert_eq!(out.status, Status::NeedsLogin);
    assert_eq!(out.stage, Stage::AuthLoad);
    assert!(transport.requests.borrow().is_empty());
}
#[test]
fn all_errors_are_closed_safe_envelopes() {
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
        ReaderError::NetworkFailure,
        ReaderError::NetworkConfiguration,
        ReaderError::DnsFailure,
        ReaderError::ConnectFailure,
        ReaderError::ProxyConfiguration,
        ReaderError::ProxyConnectFailure,
        ReaderError::ProxyAuthentication,
        ReaderError::TlsFailure,
        ReaderError::CertificateConfiguration,
        ReaderError::Timeout,
        ReaderError::ResponseTooLarge,
        ReaderError::InvalidResponse,
        ReaderError::AccountChanged,
        ReaderError::InvalidArguments,
        ReaderError::InternalError,
    ] {
        let value: Value =
            serde_json::from_slice(&Envelope::failed(Stage::AuthLoad, kind).to_bounded_json())
                .unwrap();
        assert_eq!(value.as_object().unwrap().len(), 7);
        assert!(value["result"].is_null());
        assert!(value["errorKind"].is_string());
    }
}
#[test]
fn cli_rejects_endpoint_path_and_auth_arguments_without_loading_auth() {
    for args in [
        vec![],
        vec!["--read", "--endpoint=https://attacker.invalid"],
        vec!["--login"],
        vec!["--read", "/sensitive/auth.json"],
    ] {
        let output = std::process::Command::new(env!("CARGO_BIN_EXE_codex-quota-reader"))
            .args(args)
            .output()
            .unwrap();
        assert!(output.status.success());
        assert!(output.stderr.is_empty());
        let value: Value = serde_json::from_slice(&output.stdout).unwrap();
        assert_eq!(value["errorKind"], "InvalidArguments");
        assert!(output.stdout.len() < 768);
        assert_eq!(output.stdout.iter().filter(|b| **b == b'\n').count(), 1);
    }
}

#[test]
fn failed_auth_reload_during_details_invalidates_old_usage() {
    for kind in [
        ReaderError::MissingCredentials,
        ReaderError::InvalidCredentials,
        ReaderError::CredentialReadFailed,
        ReaderError::CredentialKeyMissing,
        ReaderError::ExpiredCredentials,
        ReaderError::EphemeralCredentials,
        ReaderError::UnsupportedAuth,
        ReaderError::UnsupportedConfiguration,
    ] {
        let source = Source::new(vec![Ok(credential("old", "account-a")), Err(kind)]);
        let transport = FakeTransport::new(vec![usage(), Err(Failure::http(401))]);
        let out = read_quota(&source, &transport);
        assert!(out.result.is_none());
        assert_eq!(out.error_kind, Some(kind));
        assert_eq!(transport.requests.borrow().len(), 2);
    }
}

struct ProfileTransport {
    inner: FakeTransport,
    profiles: RefCell<VecDeque<NetworkDiagnostics>>,
    latest: RefCell<Option<NetworkDiagnostics>>,
}
impl ProfileTransport {
    fn new(responses: Vec<Result<Vec<u8>, Failure>>, profiles: Vec<NetworkDiagnostics>) -> Self {
        Self {
            inner: FakeTransport::new(responses),
            profiles: RefCell::new(profiles.into()),
            latest: RefCell::new(None),
        }
    }
}
impl Transport for ProfileTransport {
    fn get(&self, endpoint: Endpoint, credentials: &Credentials) -> Result<Vec<u8>, Failure> {
        let profile = self
            .profiles
            .borrow_mut()
            .pop_front()
            .expect("one profile per request");
        *self.latest.borrow_mut() = Some(profile);
        self.inner
            .get(endpoint, credentials)
            .map_err(|failure| failure.with_network(profile))
    }
    fn network_diagnostics(&self) -> Option<NetworkDiagnostics> {
        *self.latest.borrow()
    }
}
fn network_profile() -> NetworkDiagnostics {
    NetworkDiagnostics {
        route: NetworkRoute::EnvironmentProxy,
        proxy_protocol: ProxyProtocol::Http,
        tls_backend: TlsBackend::Rustls,
        ca_override: true,
        dotenv: DotenvStatus::Loaded,
        ..Default::default()
    }
}
#[test]
fn successful_snapshot_carries_exact_closed_network_profile() {
    let profile = network_profile();
    let transport = ProfileTransport::new(vec![usage(), details()], vec![profile, profile]);
    let out = read_quota(&basic_source(), &transport);
    assert_eq!(out.network, Some(profile));
    assert_eq!(transport.inner.requests.borrow().len(), 2);
    let value: Value = serde_json::from_slice(&out.to_bounded_json()).unwrap();
    assert_eq!(
        value["network"],
        json!({
            "route":"EnvironmentProxy", "proxyProtocol":"Http", "noProxyBypass":false,
            "tlsBackend":"Rustls", "caOverride":true, "dotenv":"Loaded",
            "errorLayer":"None", "ioKind":"None", "osCode":null, "chainTruncated":false,
            "systemProxyConfig":"NotChecked", "systemProxyResolution":"NotChecked",
            "systemProxyAutoDetect":false, "systemProxyPacPresent":false, "systemProxyStaticPresent":false
        })
    );
}
#[test]
fn final_warning_keeps_its_own_profile_after_401_and_success() {
    let initial = network_profile().with_error(ErrorLayer::HttpStatus);
    let success = NetworkDiagnostics {
        route: NetworkRoute::Direct,
        proxy_protocol: ProxyProtocol::None,
        ..network_profile()
    };
    let warning = NetworkDiagnostics {
        error_layer: ErrorLayer::Io,
        io_kind: IoKind::ConnectionRefused,
        os_code: Some(10061),
        ..success
    };
    let source = Source::new(vec![
        Ok(credential("old", "account-a")),
        Ok(credential("new", "account-a")),
    ]);
    let transport = ProfileTransport::new(
        vec![
            Err(Failure::http(401)),
            usage(),
            Err(ReaderError::ConnectFailure.into()),
        ],
        vec![initial, success, warning],
    );
    let out = read_quota(&source, &transport);
    assert_eq!(out.status, Status::Ready);
    assert_eq!(out.stage, Stage::ResetCardsRead);
    assert_eq!(out.network, Some(warning));
    assert_eq!(out.code, None);
    assert_eq!(transport.inner.requests.borrow().len(), 3);
    assert_eq!(*source.calls.borrow(), 2);
}
#[test]
fn successful_retry_and_parse_warning_do_not_reuse_prior_http_error() {
    for body in [details(), Ok(b"private malformed detail body".to_vec())] {
        let initial = network_profile().with_error(ErrorLayer::HttpStatus);
        let success = network_profile();
        let source = Source::new(vec![
            Ok(credential("old", "account-a")),
            Ok(credential("new", "account-a")),
        ]);
        let transport = ProfileTransport::new(
            vec![Err(Failure::http(401)), usage(), body],
            vec![initial, success, success],
        );
        let out = read_quota(&source, &transport);
        assert_eq!(out.status, Status::Ready);
        assert_eq!(out.network, Some(success));
        assert_eq!(out.code, None);
        assert_eq!(transport.inner.requests.borrow().len(), 3);
    }
}
#[test]
#[cfg(windows)]
fn cli_early_config_failures_report_dotenv_without_paths_or_settings() {
    for (dotenv, expected) in [
        (None, "Absent"),
        (Some("# valid empty fixture\n"), "Loaded"),
        (Some("HTTPS_PROXY='private-proxy-credential"), "Failed"),
    ] {
        let home = tempfile::tempdir().unwrap();
        if let Some(text) = dotenv {
            std::fs::write(home.path().join(".env"), text).unwrap();
        }
        let mut command = std::process::Command::new(env!("CARGO_BIN_EXE_codex-quota-reader"));
        command.arg("--read").env("CODEX_HOME", home.path());
        for name in [
            "CODEX_AUTH_STORAGE",
            "CODEX_CA_CERTIFICATE",
            "SSL_CERT_FILE",
            "SSL_CERT_DIR",
        ] {
            command.env_remove(name);
        }
        let output = command.output().unwrap();
        assert!(output.status.success());
        assert!(output.stderr.is_empty());
        let value: Value = serde_json::from_slice(&output.stdout).unwrap();
        assert_eq!(value["network"]["dotenv"], expected);
        assert_eq!(value["network"]["route"], "Unknown");
        if expected == "Failed" {
            assert_eq!(value["errorKind"], "NetworkConfiguration");
            assert_eq!(value["network"]["tlsBackend"], "Unknown");
        }
        let text = String::from_utf8(output.stdout).unwrap();
        assert!(!text.contains("private-proxy-credential"));
        assert!(!text.contains(home.path().to_str().unwrap()));
    }
}
