use std::io::{Read, Write};
use std::net::TcpListener;
use std::sync::{
    atomic::{AtomicBool, Ordering},
    Arc,
};
use std::time::{Duration, Instant};

#[test]
fn existing_dotenv_proxy_reaches_production_client() {
    let home = tempfile::tempdir().unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    std::fs::write(
        home.path().join(".env"),
        format!("HTTPS_PROXY=http://{}\n", listener.local_addr().unwrap()),
    )
    .unwrap();
    listener.set_nonblocking(true).unwrap();
    let proxy = std::thread::spawn(move || {
        let deadline = Instant::now() + Duration::from_secs(3);
        loop {
            match listener.accept() {
                Ok((mut stream, _)) => {
                    stream
                        .set_read_timeout(Some(Duration::from_secs(2)))
                        .unwrap();
                    let mut request = Vec::new();
                    while request.len() < 4096 && !request.ends_with(b"\r\n\r\n") {
                        let mut byte = [0];
                        if stream.read(&mut byte).unwrap_or(0) == 0 {
                            break;
                        }
                        request.push(byte[0]);
                    }
                    stream.write_all(b"HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\n\r\n").unwrap();
                    return Some(request);
                }
                Err(error)
                    if error.kind() == std::io::ErrorKind::WouldBlock
                        && Instant::now() < deadline =>
                {
                    std::thread::sleep(Duration::from_millis(10))
                }
                Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => return None,
                Err(_) => panic!("loopback fixture failed"),
            }
        }
    });
    let mut child = std::process::Command::new(std::env::current_exe().unwrap());
    child.args([
        "--exact",
        "network_config::tests::dotenv_proxy_child",
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
        "SSL_CERT_FILE",
        "SSL_CERT_DIR",
        "CODEX_CA_CERTIFICATE",
    ] {
        child.env_remove(key);
    }
    let result = child.env("CODEX_HOME", home.path()).output().unwrap();
    let request = proxy.join().unwrap();
    assert!(
        result.status.success(),
        "isolated proxy regression failed: {}",
        String::from_utf8_lossy(&result.stdout)
    );
    let request = request.expect("production builder must observe the existing dotenv proxy");
    assert!(request.starts_with(b"CONNECT fixture.invalid:443 HTTP/1.1\r\n"));
    assert!(!request
        .windows(13)
        .any(|window| window.eq_ignore_ascii_case(b"authorization")));
}

struct RejectDirectDns(Arc<AtomicBool>);
impl reqwest::dns::Resolve for RejectDirectDns {
    fn resolve(&self, _: reqwest::dns::Name) -> reqwest::dns::Resolving {
        self.0.store(true, Ordering::SeqCst);
        Box::pin(async { Err(std::io::Error::other("synthetic DNS failure").into()) })
    }
}

#[test]
#[ignore = "isolated subprocess fixture, never reads actual credentials or uses external network"]
fn dotenv_proxy_child() {
    // Only this isolated fixture changes its synthetic process environment.
    let home = std::path::PathBuf::from(std::env::var_os("CODEX_HOME").unwrap());
    unsafe {
        super::NetworkEnvironment::read_home(&home).unwrap().apply();
    }
    let direct = Arc::new(AtomicBool::new(false));
    let client = crate::http::client_builder()
        .dns_resolver(Arc::new(RejectDirectDns(direct.clone())))
        .build()
        .unwrap();
    assert!(client
        .get("https://fixture.invalid/")
        .timeout(Duration::from_secs(2))
        .send()
        .is_err());
    assert!(
        !direct.load(Ordering::SeqCst),
        "dotenv HTTPS proxy was ignored and the client tried direct DNS"
    );
}

fn parsed(text: &str, windows: bool, inherited: &[(&str, &str)]) -> super::NetworkEnvironment {
    super::NetworkEnvironment::parse(text.as_bytes(), windows, &|key| {
        inherited
            .iter()
            .find(|(name, _)| {
                if windows {
                    name.eq_ignore_ascii_case(key)
                } else {
                    *name == key
                }
            })
            .map(|(_, value)| (*value).to_owned())
    })
    .unwrap()
}

fn values(config: &super::NetworkEnvironment) -> std::collections::BTreeMap<String, String> {
    config
        .assignments
        .iter()
        .map(|(key, value)| (key.clone(), value.to_string()))
        .collect()
}

#[test]
fn aliases_override_inherited_sequentially_without_exporting_non_network_keys() {
    let config = parsed("BASE=http://first\nHTTPS_PROXY=${BASE}\nBASE=http://second\nHTTPS_PROXY=${BASE}\nOPENAI_API_KEY=private-dotenv\nSSL_CERT_FILE=${OPENAI_API_KEY}/ca.pem\nNO_PROXY=\n", false, &[("BASE", "http://inherited"), ("HTTPS_PROXY", "http://old"), ("NO_PROXY", "old.invalid")]);
    assert_eq!(
        values(&config),
        std::collections::BTreeMap::from([
            ("HTTPS_PROXY".to_owned(), "http://second".to_owned()),
            (
                "SSL_CERT_FILE".to_owned(),
                "private-dotenv/ca.pem".to_owned()
            ),
            ("NO_PROXY".to_owned(), String::new()),
        ])
    );
}

#[test]
fn windows_environment_overlay_is_case_insensitive_but_parsed_symbols_are_not() {
    let config = parsed("base=http://lower\nHTTPS_PROXY=${BASE}\nHttp_Proxy=http://mixed\nhttp_proxy=http://last\nssl_cert_file=ca.pem\n", true, &[("Base", "http://inherited")]);
    assert_eq!(config.assignments["HTTPS_PROXY"].as_str(), "http://lower");
    assert_eq!(config.assignments["HTTP_PROXY"].as_str(), "http://last");
    assert_eq!(config.assignments["SSL_CERT_FILE"].as_str(), "ca.pem");
    let protected = parsed("codex_alias=lower\nHTTPS_PROXY=${CODEX_ALIAS}\n", true, &[]);
    assert_eq!(protected.assignments["HTTPS_PROXY"].as_str(), "");
    let inherited_protected = parsed(
        "CoDeX_Alias=filtered\nHTTPS_PROXY=${CODEX_ALIAS}\n",
        true,
        &[("codex_alias", "inherited-protected")],
    );
    assert_eq!(
        inherited_protected.assignments["HTTPS_PROXY"].as_str(),
        "inherited-protected"
    );
}

#[test]
fn unix_keeps_uppercase_and_lowercase_proxy_variables_distinct() {
    let config = parsed("HTTP_PROXY=http://upper\nhttp_proxy=http://lower\nHttp_Proxy=http://mixed\nssl_cert_file=ignored\n", false, &[]);
    assert_eq!(
        values(&config),
        std::collections::BTreeMap::from([
            ("HTTP_PROXY".to_owned(), "http://upper".to_owned()),
            ("http_proxy".to_owned(), "http://lower".to_owned()),
        ])
    );
}

#[test]
fn codex_prefix_is_excluded_in_every_case_while_official_symbol_fallback_is_preserved() {
    for windows in [false, true] {
        let config = parsed("CODEX_HOME=ignored-home\nCODEX_CA_CERTIFICATE=ignored-ca\nCoDeX_Token=ignored-token\nHTTPS_PROXY=${CODEX_HOME}\nSSL_CERT_FILE=${CODEX_CA_CERTIFICATE}\nCODEX_ALIAS=private-only\nNO_PROXY=${CODEX_ALIAS}\n", windows, &[("CODEX_HOME", "inherited-home"), ("CODEX_CA_CERTIFICATE", "inherited-ca")]);
        assert_eq!(config.assignments["HTTPS_PROXY"].as_str(), "inherited-home");
        assert_eq!(config.assignments["SSL_CERT_FILE"].as_str(), "inherited-ca");
        assert_eq!(config.assignments["NO_PROXY"].as_str(), "private-only");
        assert!(config
            .assignments
            .keys()
            .all(|key| !key.starts_with("CODEX_")));
    }
}

#[test]
fn exact_allowlist_includes_certificate_directory_and_all_proxy_forms() {
    for key in [
        "HTTP_PROXY",
        "http_proxy",
        "HTTPS_PROXY",
        "https_proxy",
        "ALL_PROXY",
        "all_proxy",
        "NO_PROXY",
        "no_proxy",
        "SSL_CERT_FILE",
        "SSL_CERT_DIR",
    ] {
        assert!(super::is_network_key(key, false));
        assert!(super::is_network_key(key, true));
    }
    for key in [
        "CODEX_HOME",
        "codex_ca_certificate",
        "OPENAI_API_KEY",
        "CHATGPT_ACCOUNT_ID",
        "CODEX_ACCESS_TOKEN",
        "OPENAI_BASE_URL",
        "PATH",
        "HOME",
        "USERPROFILE",
        "REQUEST_METHOD",
        "SSL_CERT_FILE_EXTRA",
        "HTTPS_PROXY_URL",
    ] {
        assert!(!super::is_network_key(key, false));
        assert!(!super::is_network_key(key, true));
    }
}

#[test]
fn missing_file_is_empty_and_existing_invalid_file_fails_closed() {
    let home = tempfile::tempdir().unwrap();
    assert!(super::NetworkEnvironment::read_home(home.path())
        .unwrap()
        .assignments
        .is_empty());
    let file = home.path().join(".env");
    std::fs::create_dir(&file).unwrap();
    assert!(matches!(
        super::NetworkEnvironment::read_home(home.path()),
        Err(super::Error::NetworkConfiguration)
    ));
    std::fs::remove_dir(&file).unwrap();
    for content in [
        b"HTTPS_PROXY=http://valid\nMALFORMED\n".as_slice(),
        b"HTTPS_PROXY=\xff\n",
        b"HTTPS_PROXY=\0\n",
        b"HTTPS_PROXY=\"unclosed\n",
    ] {
        std::fs::write(&file, content).unwrap();
        assert!(matches!(
            super::NetworkEnvironment::read_home(home.path()),
            Err(super::Error::NetworkConfiguration)
        ));
    }
    std::fs::write(&file, vec![b'#'; super::MAX_DOTENV_BYTES + 1]).unwrap();
    assert!(matches!(
        super::NetworkEnvironment::read_home(home.path()),
        Err(super::Error::NetworkConfiguration)
    ));
}

#[cfg(unix)]
#[test]
fn unreadable_file_returns_only_a_closed_error() {
    use std::os::unix::fs::PermissionsExt;
    let home = tempfile::tempdir().unwrap();
    let file = home.path().join(".env");
    std::fs::write(&file, "HTTPS_PROXY=http://private\n").unwrap();
    std::fs::set_permissions(&file, std::fs::Permissions::from_mode(0)).unwrap();
    assert!(matches!(
        super::NetworkEnvironment::read_home(home.path()),
        Err(super::Error::NetworkConfiguration)
    ));
    std::fs::set_permissions(&file, std::fs::Permissions::from_mode(0o600)).unwrap();
}

#[test]
fn input_record_and_expansion_limits_are_enforced_before_growth() {
    let parse = |bytes: &[u8]| super::NetworkEnvironment::parse(bytes, false, &|_| None);
    assert!(parse(&vec![b'#'; super::MAX_DOTENV_BYTES + 1]).is_err());
    assert!(parse("A=x\n".repeat(super::MAX_RECORDS + 1).as_bytes()).is_err());
    let large = "a".repeat(super::MAX_EXPANDED_VALUE_BYTES + 1);
    assert!(parse(format!("HTTPS_PROXY={large}\n").as_bytes()).is_err());
    let mut exponential = "A=x\n".to_owned();
    for _ in 0..20 {
        exponential.push_str("A=${A}${A}\n");
    }
    assert!(parse(exponential.as_bytes()).is_err());
    let initial = "x".repeat(16 * 1024);
    let repeated = format!("A={initial}\n{}", "A=${A}\n".repeat(64));
    assert!(parse(repeated.as_bytes()).is_err());
    assert!(
        super::NetworkEnvironment::parse(b"HTTPS_PROXY=${HUGE}\n", false, &|_| Some(large.clone()))
            .is_err()
    );
}

#[test]
fn adapted_parser_matches_pinned_dotenvy_grammar() {
    let cases = [
        "",
        "# comment\n",
        "\n  # comment\r\n",
        "A=one\n",
        "export A=one\n",
        "export=one\n",
        "A=\n",
        "A= # comment\n",
        "A=one # comment\n",
        "A=one#literal\n",
        "A='strong $A'\n",
        "A=\"weak ${A}\"\n",
        "A=one\nB=$A\n",
        "A=one\nB=${A}\n",
        "A=one\nA=${A}two\n",
        "A=one\nB=$A$A\n",
        "A=one\nB=${A}${A}\n",
        "A=one\nB=${UNSET}\n",
        "A=one\nB=$UNSET\n",
        "A=\"two\nlines\"\n",
        "A='two\r\nlines'\n",
        "A=one\\ two\n",
        "A=\"a\\nb\"\n",
        "A=\\$literal\n",
        "A=\"\\\"quote\\\"\"\n",
        "A=x\r\nB=y\r\n",
        "A.B=one\nC=${A.B}\n",
        "A_B=one\nC=$A_B\n",
        "A_B=one\nC=${A_B}\n",
        "A= x \t\n",
        "A=x\nA=\nC=$A\n",
        "BAD\n",
        "A=x y\n",
        "A=\"unclosed\n",
        "A='unclosed\n",
        "A=\\q\n",
        "A=${unclosed\n",
        "1A=x\n",
        "\u{feff}A=x\n",
        "A=x\nB=\"${A:-default}\"\n",
        "A=x\nB='${A:-default}'\n",
        "A=\"hello\\\nworld\"\n",
        "A=\"\\'\\\\\\$\"\n",
    ];
    for text in cases {
        let expected: Vec<_> = dotenvy::from_read_iter(text.as_bytes())
            .map(|item| item.map_err(|_| ()))
            .collect();
        let mut parser = super::iter::Iter::new(text.as_bytes());
        let mut actual = Vec::new();
        while let Some(item) = parser.next(&|key| std::env::var(key).ok()) {
            actual.push(item.map_err(|_| ()));
        }
        assert_eq!(actual, expected, "grammar fixture {text:?}");
    }
}

#[test]
fn process_environment_and_source_files_remain_protected() {
    let output = std::process::Command::new(std::env::current_exe().unwrap())
        .args([
            "--exact",
            "network_config::tests::protected_environment_child",
            "--ignored",
        ])
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "protected environment fixture failed: {}",
        String::from_utf8_lossy(&output.stdout)
    );
}

#[test]
#[ignore = "isolated synthetic environment fixture"]
fn protected_environment_child() {
    let home = tempfile::tempdir().unwrap();
    let keys = [
        "CODEX_HOME",
        "CODEX_CA_CERTIFICATE",
        "OPENAI_API_KEY",
        "CODEX_ACCESS_TOKEN",
        "OPENAI_BASE_URL",
        "HOME",
        "USERPROFILE",
        "PATH",
    ];
    for key in keys {
        std::env::set_var(key, "inherited-synthetic");
    }
    std::env::set_var("CODEX_HOME", home.path());
    std::env::set_var("HTTPS_PROXY", "http://inherited.invalid");
    let before: Vec<_> = keys
        .iter()
        .map(|key| (*key, std::env::var_os(key)))
        .collect();
    let dotenv = b"CODEX_HOME=other-home\nCoDeX_Home=another-home\nCODEX_CA_CERTIFICATE=other-ca\nOPENAI_API_KEY=other-token\nCODEX_ACCESS_TOKEN=other-token\nOPENAI_BASE_URL=https://other.invalid\nHOME=other-home\nUSERPROFILE=other-home\nPATH=other-path\nHTTPS_PROXY=http://loopback.invalid\nSSL_CERT_DIR=certs\n";
    let auth = b"synthetic-auth-file-must-not-be-read-or-written";
    std::fs::write(home.path().join(".env"), dotenv).unwrap();
    std::fs::write(home.path().join("auth.json"), auth).unwrap();
    unsafe {
        super::NetworkEnvironment::read_home(home.path())
            .unwrap()
            .apply();
    }
    assert_eq!(
        std::env::var("HTTPS_PROXY").unwrap(),
        "http://loopback.invalid"
    );
    assert_eq!(std::env::var("SSL_CERT_DIR").unwrap(), "certs");
    for (key, value) in &before {
        assert_eq!(&std::env::var_os(key), value);
    }
    assert_eq!(std::fs::read(home.path().join(".env")).unwrap(), dotenv);
    assert_eq!(std::fs::read(home.path().join("auth.json")).unwrap(), auth);
    // A later parse error cannot leak earlier successful assignments into the process.
    std::fs::write(
        home.path().join(".env"),
        b"HTTPS_PROXY=http://partial.invalid\nBROKEN\n",
    )
    .unwrap();
    assert!(super::NetworkEnvironment::read_home(home.path()).is_err());
    assert_eq!(
        std::env::var("HTTPS_PROXY").unwrap(),
        "http://loopback.invalid"
    );
    for (key, value) in before {
        assert_eq!(std::env::var_os(key), value);
    }
}

#[test]
fn private_overlay_matches_official_sequential_export_for_network_results() {
    let output = std::process::Command::new(std::env::current_exe().unwrap())
        .args([
            "--exact",
            "network_config::tests::official_comparison_child",
            "--ignored",
        ])
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "official comparison failed: {}",
        String::from_utf8_lossy(&output.stdout)
    );
}

#[test]
#[ignore = "isolated upstream differential fixture using only synthetic variables"]
fn official_comparison_child() {
    std::env::set_var("CQT_ALIAS", "inherited-value");
    std::env::set_var("CODEX_CQT_ALIAS", "protected-value");
    std::env::remove_var("CODEX_CQT_MISSING");
    let text = "CQT_ALIAS=http://first.invalid\nHTTPS_PROXY=${CQT_ALIAS}\nCQT_ALIAS=http://last.invalid\nHTTPS_PROXY=${CQT_ALIAS}\nCODEX_CQT_ALIAS=filtered-value\nNO_PROXY=${CODEX_CQT_ALIAS}\nCODEX_CQT_MISSING=private-symbol\nSSL_CERT_FILE=${CODEX_CQT_MISSING}\nSSL_CERT_DIR=\"two words\"\nhttp_proxy=http://lower.invalid\nALL_PROXY=\n";
    let actual = super::NetworkEnvironment::parse(text.as_bytes(), cfg!(windows), &|key| {
        std::env::var(key).ok()
    })
    .unwrap();
    let mut expected = std::collections::BTreeMap::new();
    // This is the exact official set_filtered loop, confined to this disposable
    // synthetic fixture. It must never be used by the production helper.
    for (key, value) in dotenvy::from_read_iter(text.as_bytes()).flatten() {
        if !key.to_ascii_uppercase().starts_with("CODEX_") {
            std::env::set_var(&key, &value);
            if super::is_network_key(&key, cfg!(windows)) {
                expected.insert(super::environment_key(&key, cfg!(windows)), value);
            }
        }
    }
    assert_eq!(values(&actual), expected);
}

#[cfg(unix)]
#[test]
fn present_dangling_dotenv_symlink_fails_closed() {
    let home = tempfile::tempdir().unwrap();
    let file = home.path().join(".env");
    let target = home.path().join("network.env");
    std::os::unix::fs::symlink(&target, &file).unwrap();
    assert!(matches!(
        super::NetworkEnvironment::read_home(home.path()),
        Err(super::Error::NetworkConfiguration)
    ));
    std::fs::write(&target, b"HTTPS_PROXY=http://synthetic.invalid\n").unwrap();
    assert_eq!(
        super::NetworkEnvironment::read_home(home.path())
            .unwrap()
            .assignments["HTTPS_PROXY"]
            .as_str(),
        "http://synthetic.invalid"
    );
}
