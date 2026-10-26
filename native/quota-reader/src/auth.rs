// Copyright 2025 OpenAI
// SPDX-License-Identifier: Apache-2.0
//
// Read-only, deliberately reduced adaptation of OpenAI Codex rust-v0.159.0:
// login/src/auth/storage.rs (store names, key derivation, store selection),
// login/src/token_data.rs (JWT claim layout), secrets/src/{lib,local}.rs
// (namespace, envelope and passphrase format), core/src/config/auth_keyring.rs.
// Sources: https://github.com/openai/codex/tree/rust-v0.159.0/codex-rs
// MODIFIED for CodexQuotaTray: all login, refresh, mutation, key creation,
// migration, network, logging and logout paths removed. Reads and KDF work are
// bounded; unsupported policy/format and ambiguous source changes fail closed.
// This is not the official AuthManager and intentionally has no write API.

use crate::error::ReaderError;
use age::secrecy::SecretString;
use base64::Engine as _;
use serde::Deserialize;
use sha2::{Digest, Sha256};
use std::collections::BTreeMap;
use std::fs::File;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};
use zeroize::{Zeroize, Zeroizing};

const MAX_AUTH_BYTES: usize = 256 * 1024;
const MAX_CONFIG_BYTES: usize = 256 * 1024;
const MAX_CIPHERTEXT_BYTES: usize = 512 * 1024;
const MAX_TOKEN_BYTES: usize = 32 * 1024;
#[cfg(any(windows, test))]
const MAX_KEYRING_BYTES: usize = 256 * 1024;
// scrypt r=8: at most approximately 256 MiB temporary KDF memory. Stronger
// files are rejected rather than executing an attacker-selected work factor.
const MAX_SCRYPT_LOG_N: u8 = 18;

// Deliberately neither Debug nor Serialize: tokens and account IDs must never
// become diagnostic strings or travel over the helper's stdout protocol.
#[derive(Clone, PartialEq, Eq)]
pub struct Credentials {
    pub access_token: String,
    pub account_id: String,
}

pub trait CredentialSource {
    fn load(&self) -> Result<Credentials, ReaderError>;
}

/// The entire platform capability passed to the loader. There is no save,
/// delete, create-directory, credential-generation or network operation here.
trait ReadOnlyStore: Send + Sync {
    fn read_file(&self, path: &Path, limit: usize) -> Result<Option<Vec<u8>>, ReaderError>;
    fn exists(&self, path: &Path) -> Result<bool, ReaderError>;
    fn canonical_home(&self, home: &Path) -> Result<PathBuf, ReaderError>;
    fn read_keyring(&self, service: &str, account: &str) -> Result<Option<String>, ReaderError>;
}

struct NativeReadOnlyStore;

impl ReadOnlyStore for NativeReadOnlyStore {
    fn read_file(&self, path: &Path, limit: usize) -> Result<Option<Vec<u8>>, ReaderError> {
        let metadata = match std::fs::metadata(path) {
            Ok(value) => value,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
            Err(_) => return Err(ReaderError::CredentialReadFailed),
        };
        if !metadata.is_file() || metadata.len() > limit as u64 {
            return Err(ReaderError::CredentialReadFailed);
        }
        let file = File::open(path).map_err(|_| ReaderError::CredentialReadFailed)?;
        let metadata = file
            .metadata()
            .map_err(|_| ReaderError::CredentialReadFailed)?;
        if !metadata.is_file() || metadata.len() > limit as u64 {
            return Err(ReaderError::CredentialReadFailed);
        }
        let mut result = Vec::new();
        file.take(limit as u64 + 1)
            .read_to_end(&mut result)
            .map_err(|_| ReaderError::CredentialReadFailed)?;
        if result.len() > limit {
            return Err(ReaderError::CredentialReadFailed);
        }
        Ok(Some(result))
    }

    fn exists(&self, path: &Path) -> Result<bool, ReaderError> {
        path.try_exists()
            .map_err(|_| ReaderError::UnsupportedConfiguration)
    }

    fn canonical_home(&self, home: &Path) -> Result<PathBuf, ReaderError> {
        // Use native Rust canonicalization, including Windows verbatim path
        // prefix and original case. Do not strip/lowercase/Unix-normalize it.
        // Unlike upstream's fallback, failure here cannot select another key.
        let canonical = home
            .canonicalize()
            .map_err(|_| ReaderError::CredentialReadFailed)?;
        if !canonical.is_dir() || canonical.to_str().is_none() {
            return Err(ReaderError::CredentialReadFailed);
        }
        Ok(canonical)
    }

    fn read_keyring(&self, service: &str, account: &str) -> Result<Option<String>, ReaderError> {
        native_read_keyring(service, account)
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum StoreMode {
    File,
    Keyring,
    Auto,
    Ephemeral,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum KeyringBackend {
    Direct,
    Secrets,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Origin {
    File,
    Direct,
    Secrets,
}

#[derive(Clone, PartialEq, Eq)]
struct AuthConfig {
    mode: StoreMode,
    backend: KeyringBackend,
    workspaces: Option<Vec<String>>,
}

struct Selection {
    config: AuthConfig,
    origin: Origin,
}

pub struct ExistingAuthSource {
    home: PathBuf,
    policy_paths: Vec<PathBuf>,
    store: Arc<dyn ReadOnlyStore>,
    // Pin the original store for a helper request's 401 reload. In particular,
    // a disappearing keyring value may not reveal a stale fallback account.
    selected: Mutex<Option<Selection>>,
}

impl ExistingAuthSource {
    pub(crate) fn codex_home(&self) -> &Path {
        &self.home
    }

    pub fn from_environment() -> Result<Self, ReaderError> {
        reject_environment_overrides(
            std::env::vars_os()
                .map(|(key, value)| (key.to_string_lossy().into_owned(), !value.is_empty())),
        )?;
        let home = match std::env::var_os("CODEX_HOME") {
            Some(value) if !value.is_empty() => {
                let path = PathBuf::from(value);
                if !path.is_absolute() || !path.is_dir() {
                    return Err(ReaderError::UnsupportedConfiguration);
                }
                path.canonicalize()
                    .map_err(|_| ReaderError::CredentialReadFailed)?
            }
            _ => dirs::home_dir()
                .ok_or(ReaderError::MissingCredentials)?
                .join(".codex"),
        };
        // These are actual Windows KnownFolder paths, not an untrusted
        // ProgramData environment override. This narrow reader does not claim
        // to implement managed/cloud config merge or network-policy semantics.
        let system_dir = system_config_dir()?;
        Ok(Self {
            policy_paths: vec![
                system_dir.join("config.toml"),
                system_dir.join("requirements.toml"),
            ],
            home,
            store: Arc::new(NativeReadOnlyStore),
            selected: Mutex::new(None),
        })
    }

    fn read_config(&self) -> Result<AuthConfig, ReaderError> {
        for path in &self.policy_paths {
            if self.store.exists(path)? {
                return Err(ReaderError::UnsupportedConfiguration);
            }
        }
        let bytes = self
            .store
            .read_file(&self.home.join("config.toml"), MAX_CONFIG_BYTES)?;
        parse_config(bytes.as_deref().unwrap_or_default())
    }

    fn file_auth(&self) -> Result<Option<Vec<u8>>, ReaderError> {
        self.store
            .read_file(&self.home.join("auth.json"), MAX_AUTH_BYTES)
    }

    fn keyring_auth(&self, backend: KeyringBackend) -> Result<Option<Vec<u8>>, ReaderError> {
        // Source naming follows exactly the upstream SHA256(canonical path
        // UTF-8), first 16 lowercase hex digits, with no path transformations.
        let canonical = self.store.canonical_home(&self.home)?;
        let suffix = home_hash(&canonical)?;
        match backend {
            KeyringBackend::Direct => {
                let value = self
                    .store
                    .read_keyring("Codex Auth", &format!("cli|{suffix}"))?;
                match value {
                    Some(value) if value.len() <= MAX_AUTH_BYTES => Ok(Some(value.into_bytes())),
                    Some(_) => Err(ReaderError::InvalidCredentials),
                    None => Ok(None),
                }
            }
            KeyringBackend::Secrets => {
                let Some(ciphertext) = self.store.read_file(
                    &self.home.join("secrets").join("codex_auth.age"),
                    MAX_CIPHERTEXT_BYTES,
                )?
                else {
                    return Ok(None);
                };
                let passphrase = self
                    .store
                    .read_keyring("codex", &format!("secrets|{suffix}"))?
                    .ok_or(ReaderError::CredentialKeyMissing)?;
                let passphrase = Zeroizing::new(passphrase);
                if passphrase.is_empty() || passphrase.len() > 1024 {
                    return Err(ReaderError::InvalidCredentials);
                }
                let plaintext = decrypt_existing(&ciphertext, &passphrase)?;
                let envelope: SecretsEnvelope = serde_json::from_slice(&plaintext)
                    .map_err(|_| ReaderError::InvalidCredentials)?;
                // Upstream reads v0 as v1 without requiring a migration. We do
                // the same in memory only; there is never a write-back.
                if envelope.version > 1 {
                    return Err(ReaderError::UnsupportedAuth);
                }
                let auth = envelope
                    .secrets
                    .get("global/CODEX_AUTH")
                    .ok_or(ReaderError::MissingCredentials)?;
                if auth.len() > MAX_AUTH_BYTES {
                    return Err(ReaderError::InvalidCredentials);
                }
                Ok(Some(auth.as_bytes().to_vec()))
            }
        }
    }
}

impl CredentialSource for ExistingAuthSource {
    fn load(&self) -> Result<Credentials, ReaderError> {
        let config = self.read_config()?;
        if config.mode == StoreMode::Ephemeral {
            return Err(ReaderError::EphemeralCredentials);
        }
        let mut selected = self
            .selected
            .lock()
            .map_err(|_| ReaderError::CredentialReadFailed)?;
        if selected.as_ref().is_some_and(|old| old.config != config) {
            return Err(ReaderError::UnsupportedConfiguration);
        }
        let keyring_origin = match config.backend {
            KeyringBackend::Direct => Origin::Direct,
            KeyringBackend::Secrets => Origin::Secrets,
        };
        let (bytes, origin) = if let Some(old) = selected.as_ref() {
            let bytes = match old.origin {
                Origin::File => self.file_auth()?,
                Origin::Direct => self.keyring_auth(KeyringBackend::Direct)?,
                Origin::Secrets => self.keyring_auth(KeyringBackend::Secrets)?,
            };
            (bytes, old.origin)
        } else {
            match config.mode {
                StoreMode::File => (self.file_auth()?, Origin::File),
                StoreMode::Keyring => (self.keyring_auth(config.backend)?, keyring_origin),
                StoreMode::Auto => match self.keyring_auth(config.backend)? {
                    Some(bytes) => (Some(bytes), keyring_origin),
                    None => (self.file_auth()?, Origin::File),
                },
                StoreMode::Ephemeral => return Err(ReaderError::EphemeralCredentials),
            }
        };
        let bytes = Zeroizing::new(bytes.ok_or(ReaderError::MissingCredentials)?);
        let credentials = parse_auth(&bytes, &config, now_unix()?)?;
        *selected = Some(Selection { config, origin });
        Ok(credentials)
    }
}

fn now_unix() -> Result<u64, ReaderError> {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|value| value.as_secs())
        .map_err(|_| ReaderError::CredentialReadFailed)
}

fn home_hash(home: &Path) -> Result<String, ReaderError> {
    let path = home.to_str().ok_or(ReaderError::CredentialReadFailed)?;
    let hash = format!("{:x}", Sha256::digest(path.as_bytes()));
    Ok(hash[..16].to_string())
}

fn reject_environment_overrides(
    values: impl Iterator<Item = (String, bool)>,
) -> Result<(), ReaderError> {
    // Check only presence, never capture or log any environment credential.
    for (key, nonempty) in values {
        if !nonempty {
            continue;
        }
        let key = key.to_ascii_uppercase();
        if matches!(
            key.as_str(),
            "CODEX_API_KEY" | "CODEX_ACCESS_TOKEN" | "OPENAI_API_KEY"
        ) {
            return Err(ReaderError::UnsupportedAuth);
        }
        if key.starts_with("CODEX_")
            && (key.contains("AUTH")
                || key.contains("TOKEN")
                || key.contains("CONFIG")
                || key.contains("PROFILE")
                || key.contains("BASE_URL")
                || key.contains("WORKSPACE")
                || key.contains("ACCOUNT")
                || key.contains("LOGIN")
                || key.contains("PROXY")
                || key.contains("MANAGED"))
            || matches!(key.as_str(), "OPENAI_BASE_URL" | "CHATGPT_BASE_URL")
        {
            return Err(ReaderError::UnsupportedConfiguration);
        }
    }
    Ok(())
}

fn parse_config(bytes: &[u8]) -> Result<AuthConfig, ReaderError> {
    if bytes.len() > MAX_CONFIG_BYTES {
        return Err(ReaderError::UnsupportedConfiguration);
    }
    let raw = std::str::from_utf8(bytes).map_err(|_| ReaderError::UnsupportedConfiguration)?;
    let mut value: toml::Table =
        toml::from_str(raw).map_err(|_| ReaderError::UnsupportedConfiguration)?;
    // Unselected profiles cannot affect this reader. A selected profile's
    // documented provider, base URL and feature settings do affect auth and
    // must be considered; ordinary model/UI settings remain irrelevant.
    if let Some(profile_name) = optional_str(&value, "profile")? {
        let profile = value
            .get("profiles")
            .and_then(toml::Value::as_table)
            .and_then(|profiles| profiles.get(profile_name))
            .and_then(toml::Value::as_table)
            .ok_or(ReaderError::UnsupportedConfiguration)?
            .clone();
        for (key, setting) in profile {
            if key == "features" {
                let features = setting
                    .as_table()
                    .ok_or(ReaderError::UnsupportedConfiguration)?;
                let merged = value
                    .entry("features".to_string())
                    .or_insert_with(|| toml::Value::Table(toml::Table::new()))
                    .as_table_mut()
                    .ok_or(ReaderError::UnsupportedConfiguration)?;
                for (key, setting) in features {
                    merged.insert(key.clone(), setting.clone());
                }
            } else if matches!(key.as_str(), "model_provider" | "chatgpt_base_url") {
                value.insert(key, setting);
            } else if is_auth_adjacent(&key) {
                return Err(ReaderError::UnsupportedConfiguration);
            }
        }
    }
    // Ordinary unrelated model/UI settings are ignored. Auth-adjacent unknown
    // fields, providers, profiles and routing are not silently reinterpreted.
    for key in value.keys() {
        if matches!(
            key.as_str(),
            "cli_auth_credentials_store"
                | "forced_login_method"
                | "forced_chatgpt_workspace_id"
                | "chatgpt_base_url"
                | "features"
                | "profile"
                | "profiles"
                | "model_provider"
                | "model_providers"
        ) {
            continue;
        }
        if is_auth_adjacent(key) {
            return Err(ReaderError::UnsupportedConfiguration);
        }
    }
    if optional_str(&value, "model_provider")?.is_some_and(|provider| provider != "openai") {
        return Err(ReaderError::UnsupportedConfiguration);
    }
    if let Some(providers) = value.get("model_providers") {
        // Custom providers which are not selected are irrelevant. Upstream
        // disallows overriding the reserved built-in provider.
        let providers = providers
            .as_table()
            .ok_or(ReaderError::UnsupportedConfiguration)?;
        if providers.contains_key("openai") {
            return Err(ReaderError::UnsupportedConfiguration);
        }
    }
    let mode = match optional_str(&value, "cli_auth_credentials_store")? {
        None | Some("file") => StoreMode::File,
        Some("keyring") => StoreMode::Keyring,
        Some("auto") => StoreMode::Auto,
        Some("ephemeral") => StoreMode::Ephemeral,
        _ => return Err(ReaderError::UnsupportedConfiguration),
    };
    let mut backend = KeyringBackend::Secrets; // This product targets Windows.
    if let Some(features) = value.get("features") {
        let features = features
            .as_table()
            .ok_or(ReaderError::UnsupportedConfiguration)?;
        for (key, setting) in features {
            if key == "secret_auth_storage" {
                backend = match setting.as_bool() {
                    Some(true) => KeyringBackend::Secrets,
                    Some(false) => KeyringBackend::Direct,
                    None => return Err(ReaderError::UnsupportedConfiguration),
                };
            } else if key.contains("auth")
                || key.contains("proxy")
                || key.contains("routing")
                || key.contains("residency")
                || key.contains("managed")
            {
                return Err(ReaderError::UnsupportedConfiguration);
            }
        }
    }
    match optional_str(&value, "forced_login_method")? {
        None | Some("chatgpt") => (),
        Some("api") => return Err(ReaderError::UnsupportedAuth),
        _ => return Err(ReaderError::UnsupportedConfiguration),
    }
    if let Some(url) = optional_str(&value, "chatgpt_base_url")? {
        if !matches!(
            url,
            "https://chatgpt.com/backend-api" | "https://chatgpt.com/backend-api/"
        ) {
            return Err(ReaderError::UnsupportedConfiguration);
        }
    }
    let workspaces = match value.get("forced_chatgpt_workspace_id") {
        None => None,
        Some(toml::Value::String(id)) => Some(vec![id.clone()]),
        Some(toml::Value::Array(ids)) if ids.len() <= 64 => {
            let mut result = Vec::new();
            for id in ids {
                result.push(
                    id.as_str()
                        .ok_or(ReaderError::UnsupportedConfiguration)?
                        .to_string(),
                );
            }
            Some(result)
        }
        _ => return Err(ReaderError::UnsupportedConfiguration),
    };
    if let Some(ids) = &workspaces {
        if ids.is_empty()
            || ids
                .iter()
                .any(|id| !valid_account_id(id) || id.contains(','))
        {
            return Err(ReaderError::UnsupportedConfiguration);
        }
    }
    Ok(AuthConfig {
        mode,
        backend,
        workspaces,
    })
}

fn is_auth_adjacent(key: &str) -> bool {
    // "workspace" in a command sandbox is not a ChatGPT restriction. These
    // documented runtime/tool sections do not select the primary credentials.
    if key.starts_with("mcp_")
        || matches!(
            key,
            "sandbox_workspace_write"
                | "sandbox_mode"
                | "approval_policy"
                | "projects"
                | "project_root_markers"
                | "permissions"
                | "default_permissions"
                | "windows"
                | "tui"
                | "agents"
                | "shell_environment_policy"
                | "history"
                | "tools"
                | "apps"
                | "skills"
                | "cloud"
                | "notice"
                | "analytics"
                | "feedback"
                | "otel"
                | "hooks"
        )
    {
        return false;
    }
    key.contains("auth")
        || key.contains("credential")
        || key.contains("account")
        || key.contains("login")
        || key.contains("workspace")
        || key.contains("proxy")
        || key.contains("routing")
        || key.contains("residency")
        || key.contains("managed")
        || matches!(
            key,
            "profile_v2"
                | "profiles_v2"
                | "openai_base_url"
                | "include"
                | "config_file"
                | "config_path"
                | "requirements"
        )
}

fn optional_str<'a>(table: &'a toml::Table, key: &str) -> Result<Option<&'a str>, ReaderError> {
    table
        .get(key)
        .map(|value| value.as_str().ok_or(ReaderError::UnsupportedConfiguration))
        .transpose()
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct AuthDocument {
    auth_mode: Option<String>,
    #[serde(rename = "OPENAI_API_KEY")]
    openai_api_key: Option<serde::de::IgnoredAny>,
    tokens: Option<TokenDocument>,
    #[allow(dead_code)]
    last_refresh: Option<serde::de::IgnoredAny>,
    // A managed ChatGPT snapshot can additionally carry an agent_identity;
    // it is ignored unless it is the selected auth mode. Never bootstrap it.
    #[allow(dead_code)]
    agent_identity: Option<serde::de::IgnoredAny>,
    personal_access_token: Option<serde::de::IgnoredAny>,
    bedrock_api_key: Option<serde::de::IgnoredAny>,
    bedrock_access_keys: Option<serde::de::IgnoredAny>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct TokenDocument {
    id_token: String,
    access_token: String,
    #[allow(dead_code)]
    refresh_token: String,
    account_id: Option<String>,
}

impl Drop for TokenDocument {
    fn drop(&mut self) {
        self.id_token.zeroize();
        self.access_token.zeroize();
        self.refresh_token.zeroize();
    }
}

#[derive(Deserialize)]
struct JwtClaims {
    exp: Option<u64>,
    #[serde(rename = "https://api.openai.com/auth")]
    auth: Option<AccountClaims>,
}

#[derive(Deserialize)]
struct AccountClaims {
    chatgpt_account_id: Option<String>,
    #[serde(default)]
    chatgpt_account_is_fedramp: bool,
}

fn jwt_claims(jwt: &str) -> Result<JwtClaims, ReaderError> {
    if jwt.len() > MAX_TOKEN_BYTES || jwt.bytes().any(|b| !b.is_ascii_graphic()) {
        return Err(ReaderError::InvalidCredentials);
    }
    let parts: Vec<_> = jwt.split('.').collect();
    if parts.len() != 3 || parts.iter().any(|part| part.is_empty()) {
        return Err(ReaderError::InvalidCredentials);
    }
    let payload = Zeroizing::new(
        base64::engine::general_purpose::URL_SAFE_NO_PAD
            .decode(parts[1])
            .map_err(|_| ReaderError::InvalidCredentials)?,
    );
    serde_json::from_slice(&payload).map_err(|_| ReaderError::InvalidCredentials)
}

fn valid_account_id(id: &str) -> bool {
    !id.is_empty()
        && id.len() <= 256
        && id
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b"-_".contains(&b))
}

fn parse_auth(bytes: &[u8], config: &AuthConfig, now: u64) -> Result<Credentials, ReaderError> {
    if bytes.len() > MAX_AUTH_BYTES {
        return Err(ReaderError::InvalidCredentials);
    }
    let auth: AuthDocument =
        serde_json::from_slice(bytes).map_err(|_| ReaderError::InvalidCredentials)?;
    match auth.auth_mode.as_deref() {
        Some("chatgptAuthTokens") => return Err(ReaderError::EphemeralCredentials),
        Some("chatgpt") | None => (),
        _ => return Err(ReaderError::UnsupportedAuth),
    }
    if auth.openai_api_key.is_some()
        || auth.personal_access_token.is_some()
        || auth.bedrock_api_key.is_some()
        || auth.bedrock_access_keys.is_some()
    {
        return Err(ReaderError::UnsupportedAuth);
    }
    let mut tokens = auth.tokens.ok_or(ReaderError::MissingCredentials)?;
    let id_claims = jwt_claims(&tokens.id_token)?;
    let access_claims = jwt_claims(&tokens.access_token)?;
    if access_claims.exp.is_some_and(|expiry| expiry <= now) {
        return Err(ReaderError::ExpiredCredentials);
    }
    // Explicit selected account takes priority. Missing account IDs can only
    // come from this snapshot's ID token, never from a different credential.
    let account_id = tokens
        .account_id
        .take()
        .or_else(|| {
            id_claims
                .auth
                .as_ref()
                .and_then(|claims| claims.chatgpt_account_id.clone())
        })
        .ok_or(ReaderError::InvalidCredentials)?;
    if !valid_account_id(&account_id) {
        return Err(ReaderError::InvalidCredentials);
    }
    for claims in [id_claims.auth.as_ref(), access_claims.auth.as_ref()]
        .into_iter()
        .flatten()
    {
        if claims.chatgpt_account_is_fedramp {
            return Err(ReaderError::UnsupportedConfiguration);
        }
        if claims
            .chatgpt_account_id
            .as_deref()
            .is_some_and(|id| id != account_id)
        {
            return Err(ReaderError::InvalidCredentials);
        }
    }
    if config
        .workspaces
        .as_ref()
        .is_some_and(|ids| !ids.contains(&account_id))
    {
        // Do not call upstream enforcement: its mismatch path can log out.
        return Err(ReaderError::UnsupportedConfiguration);
    }
    Ok(Credentials {
        access_token: std::mem::take(&mut tokens.access_token),
        account_id,
    })
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct SecretsEnvelope {
    version: u8,
    #[serde(deserialize_with = "unique_secret_map")]
    secrets: BTreeMap<String, String>,
}

fn unique_secret_map<'de, D: serde::Deserializer<'de>>(
    deserializer: D,
) -> Result<BTreeMap<String, String>, D::Error> {
    struct Visitor;
    impl<'de> serde::de::Visitor<'de> for Visitor {
        type Value = BTreeMap<String, String>;
        fn expecting(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            formatter.write_str("a unique-key secret map")
        }
        fn visit_map<M: serde::de::MapAccess<'de>>(
            self,
            mut map: M,
        ) -> Result<Self::Value, M::Error> {
            let mut result = BTreeMap::new();
            while let Some((key, mut value)) = map.next_entry::<String, String>()? {
                if result.contains_key(&key) {
                    value.zeroize();
                    for value in result.values_mut() {
                        String::zeroize(value);
                    }
                    return Err(serde::de::Error::custom("duplicate secret key"));
                }
                result.insert(key, value);
            }
            Ok(result)
        }
    }
    deserializer.deserialize_map(Visitor)
}

impl Drop for SecretsEnvelope {
    fn drop(&mut self) {
        for value in self.secrets.values_mut() {
            value.zeroize();
        }
    }
}

fn decrypt_existing(
    ciphertext: &[u8],
    passphrase: &str,
) -> Result<Zeroizing<Vec<u8>>, ReaderError> {
    if ciphertext.len() > MAX_CIPHERTEXT_BYTES {
        return Err(ReaderError::InvalidCredentials);
    }
    let mut identity = age::scrypt::Identity::new(SecretString::from(passphrase.to_string()));
    identity.set_max_work_factor(MAX_SCRYPT_LOG_N);
    let decryptor = age::Decryptor::new(ciphertext).map_err(|_| ReaderError::InvalidCredentials)?;
    let reader = decryptor
        .decrypt(std::iter::once(&identity as &dyn age::Identity))
        .map_err(|error| match error {
            age::DecryptError::ExcessiveWork { .. } => ReaderError::UnsupportedConfiguration,
            _ => ReaderError::InvalidCredentials,
        })?;
    let mut plaintext = Zeroizing::new(Vec::new());
    reader
        .take(MAX_AUTH_BYTES as u64 + 1)
        .read_to_end(&mut plaintext)
        .map_err(|_| ReaderError::InvalidCredentials)?;
    if plaintext.len() > MAX_AUTH_BYTES {
        return Err(ReaderError::InvalidCredentials);
    }
    Ok(plaintext)
}

#[cfg(windows)]
fn native_read_keyring(service: &str, account: &str) -> Result<Option<String>, ReaderError> {
    use windows_sys::Win32::Foundation::{GetLastError, ERROR_NOT_FOUND};
    use windows_sys::Win32::Security::Credentials::{
        CredFree, CredReadW, CREDENTIALW, CRED_TYPE_GENERIC,
    };
    // keyring-rs 3.6.3 uses `user.service`, storing passwords as UTF-16LE.
    let target: Vec<u16> = format!("{account}.{service}")
        .encode_utf16()
        .chain(Some(0))
        .collect();
    let mut ptr: *mut CREDENTIALW = std::ptr::null_mut();
    // SAFETY: target is NUL-terminated; CredReadW owns/initializes ptr on success.
    if unsafe { CredReadW(target.as_ptr(), CRED_TYPE_GENERIC, 0, &mut ptr) } == 0 {
        return if unsafe { GetLastError() } == ERROR_NOT_FOUND {
            Ok(None)
        } else {
            Err(ReaderError::CredentialReadFailed)
        };
    }
    if ptr.is_null() {
        return Err(ReaderError::CredentialReadFailed);
    }
    struct FreeCredential(*mut CREDENTIALW);
    impl Drop for FreeCredential {
        fn drop(&mut self) {
            unsafe {
                CredFree(self.0.cast());
            }
        }
    }
    let guard = FreeCredential(ptr);
    // SAFETY: successful CredReadW supplies a valid CREDENTIALW until guard drops.
    let credential = unsafe { &*guard.0 };
    let size = credential.CredentialBlobSize as usize;
    if size == 0 || size > MAX_KEYRING_BYTES || size % 2 != 0 || credential.CredentialBlob.is_null()
    {
        return Err(ReaderError::InvalidCredentials);
    }
    let bytes = unsafe { std::slice::from_raw_parts(credential.CredentialBlob, size) };
    decode_keyring_password(bytes).map(Some)
}

#[cfg(not(windows))]
fn native_read_keyring(_service: &str, _account: &str) -> Result<Option<String>, ReaderError> {
    Err(ReaderError::UnsupportedConfiguration)
}

#[cfg(any(windows, test))]
fn decode_keyring_password(bytes: &[u8]) -> Result<String, ReaderError> {
    if bytes.is_empty() || bytes.len() > MAX_KEYRING_BYTES || bytes.len() % 2 != 0 {
        return Err(ReaderError::InvalidCredentials);
    }
    let wide = Zeroizing::new(
        bytes
            .chunks_exact(2)
            .map(|part| u16::from_le_bytes([part[0], part[1]]))
            .collect::<Vec<_>>(),
    );
    let result = String::from_utf16(&wide).map_err(|_| ReaderError::InvalidCredentials)?;
    if result.contains('\0') {
        return Err(ReaderError::InvalidCredentials);
    }
    Ok(result)
}

#[cfg(windows)]
fn system_config_dir() -> Result<PathBuf, ReaderError> {
    use std::os::windows::ffi::OsStringExt;
    use windows_sys::Win32::System::Com::CoTaskMemFree;
    use windows_sys::Win32::UI::Shell::{FOLDERID_ProgramData, SHGetKnownFolderPath};
    let mut ptr = std::ptr::null_mut();
    let hr =
        unsafe { SHGetKnownFolderPath(&FOLDERID_ProgramData, 0, std::ptr::null_mut(), &mut ptr) };
    if hr != 0 || ptr.is_null() {
        return Err(ReaderError::UnsupportedConfiguration);
    }
    struct FreePath(*mut u16);
    impl Drop for FreePath {
        fn drop(&mut self) {
            unsafe {
                CoTaskMemFree(self.0.cast());
            }
        }
    }
    let guard = FreePath(ptr);
    // Windows paths are bounded by the NT maximum path length.
    let mut len = 0;
    while len < 32768 && unsafe { *guard.0.add(len) } != 0 {
        len += 1;
    }
    if len == 32768 {
        return Err(ReaderError::UnsupportedConfiguration);
    }
    let wide = unsafe { std::slice::from_raw_parts(guard.0, len) };
    Ok(PathBuf::from(std::ffi::OsString::from_wide(wide))
        .join("OpenAI")
        .join("Codex"))
}

#[cfg(not(windows))]
fn system_config_dir() -> Result<PathBuf, ReaderError> {
    // Production is Windows-only. Tests construct a fake source explicitly;
    // they never inspect this machine's default credential or config paths.
    Err(ReaderError::UnsupportedConfiguration)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    const HOME: &str = "synthetic-codex-home";
    const CANONICAL: &str = r"\\?\C:\Users\Synthetic\.codex";
    const ACCOUNT: &str = "synthetic-account-001";

    #[derive(Default)]
    struct FakeStore {
        files: Mutex<BTreeMap<PathBuf, Vec<u8>>>,
        keyring: Mutex<BTreeMap<(String, String), String>>,
        reads: Mutex<Vec<(String, String)>>,
        fail_keyring: bool,
        file_reads: AtomicUsize,
        // No mutation method exists in the production capability. Snapshots
        // below additionally assert that every synthetic backing value stays
        // byte-for-byte unchanged across success and failure paths.
    }

    impl ReadOnlyStore for FakeStore {
        fn read_file(&self, path: &Path, limit: usize) -> Result<Option<Vec<u8>>, ReaderError> {
            self.file_reads.fetch_add(1, Ordering::Relaxed);
            let value = self.files.lock().unwrap().get(path).cloned();
            if value.as_ref().is_some_and(|bytes| bytes.len() > limit) {
                return Err(ReaderError::CredentialReadFailed);
            }
            Ok(value)
        }
        fn exists(&self, path: &Path) -> Result<bool, ReaderError> {
            Ok(self.files.lock().unwrap().contains_key(path))
        }
        fn canonical_home(&self, _home: &Path) -> Result<PathBuf, ReaderError> {
            Ok(PathBuf::from(CANONICAL))
        }
        fn read_keyring(
            &self,
            service: &str,
            account: &str,
        ) -> Result<Option<String>, ReaderError> {
            self.reads
                .lock()
                .unwrap()
                .push((service.to_string(), account.to_string()));
            if self.fail_keyring {
                return Err(ReaderError::CredentialReadFailed);
            }
            Ok(self
                .keyring
                .lock()
                .unwrap()
                .get(&(service.to_string(), account.to_string()))
                .cloned())
        }
    }

    fn source(store: Arc<FakeStore>) -> ExistingAuthSource {
        ExistingAuthSource {
            home: PathBuf::from(HOME),
            policy_paths: vec![PathBuf::from("synthetic-system/requirements.toml")],
            store,
            selected: Mutex::new(None),
        }
    }
    fn put_file(store: &FakeStore, file: &str, bytes: Vec<u8>) {
        store
            .files
            .lock()
            .unwrap()
            .insert(PathBuf::from(HOME).join(file), bytes);
    }
    fn put_key(store: &FakeStore, service: &str, prefix: &str, value: String) {
        let key = format!("{prefix}|{}", home_hash(Path::new(CANONICAL)).unwrap());
        store
            .keyring
            .lock()
            .unwrap()
            .insert((service.to_string(), key), value);
    }
    fn jwt(account: &str, exp: u64) -> String {
        let claims = serde_json::json!({"exp":exp,"https://api.openai.com/auth":{
            "chatgpt_account_id":account,"chatgpt_account_is_fedramp":false
        }});
        format!(
            "synthetic.{}.signature",
            base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(claims.to_string())
        )
    }
    fn auth(account: &str) -> Vec<u8> {
        serde_json::to_vec(&serde_json::json!({
            "auth_mode":"chatgpt","OPENAI_API_KEY":null,
            "tokens":{"id_token":jwt(account,4102444800),"access_token":jwt(account,4102444800),
                "refresh_token":"synthetic-never-read-for-refresh","account_id":account},
            "last_refresh":"2026-01-01T00:00:00Z"
        }))
        .unwrap()
    }
    fn encrypted(plaintext: &[u8], passphrase: &str) -> Vec<u8> {
        let mut recipient = age::scrypt::Recipient::new(SecretString::from(passphrase.to_string()));
        recipient.set_work_factor(10); // Synthetic fixtures keep tests cheap.
        age::encrypt(&recipient, plaintext).unwrap()
    }
    fn secret_file(account: &str, version: u8) -> Vec<u8> {
        let plaintext = serde_json::to_vec(&serde_json::json!({"version":version,"secrets":{
            "global/CODEX_AUTH":String::from_utf8(auth(account)).unwrap()
        }}))
        .unwrap();
        encrypted(&plaintext, "synthetic-passphrase")
    }
    fn assert_error(result: Result<Credentials, ReaderError>, expected: ReaderError) {
        match result {
            Err(error) => assert_eq!(error, expected),
            Ok(_) => panic!("expected fixed error"),
        }
    }

    #[test]
    fn file_auth_and_config_are_read_only_and_credentials_stay_associated() {
        let store = Arc::new(FakeStore::default());
        put_file(&store, "auth.json", auth(ACCOUNT));
        let before = store.files.lock().unwrap().clone();
        let result = source(store.clone()).load().unwrap();
        assert_eq!(result.account_id, ACCOUNT);
        assert_eq!(result.access_token, jwt(ACCOUNT, 4102444800));
        assert_eq!(*store.files.lock().unwrap(), before);
        assert!(store.reads.lock().unwrap().is_empty());
    }

    #[test]
    fn direct_keyring_uses_official_service_account_and_never_writes() {
        let store = Arc::new(FakeStore::default());
        put_file(
            &store,
            "config.toml",
            b"cli_auth_credentials_store='keyring'\n[features]\nsecret_auth_storage=false".to_vec(),
        );
        put_key(
            &store,
            "Codex Auth",
            "cli",
            String::from_utf8(auth(ACCOUNT)).unwrap(),
        );
        let before = store.keyring.lock().unwrap().clone();
        assert_eq!(source(store.clone()).load().unwrap().account_id, ACCOUNT);
        assert_eq!(*store.keyring.lock().unwrap(), before);
        assert_eq!(store.reads.lock().unwrap()[0].0, "Codex Auth");
        assert!(store.reads.lock().unwrap()[0].1.starts_with("cli|"));
    }

    #[test]
    fn secrets_default_windows_backend_reads_existing_key_only() {
        let store = Arc::new(FakeStore::default());
        put_file(
            &store,
            "config.toml",
            b"cli_auth_credentials_store='keyring'".to_vec(),
        );
        put_file(&store, "secrets/codex_auth.age", secret_file(ACCOUNT, 1));
        put_key(
            &store,
            "codex",
            "secrets",
            "synthetic-passphrase".to_string(),
        );
        let files = store.files.lock().unwrap().clone();
        let keys = store.keyring.lock().unwrap().clone();
        assert_eq!(source(store.clone()).load().unwrap().account_id, ACCOUNT);
        assert_eq!(*store.files.lock().unwrap(), files);
        assert_eq!(*store.keyring.lock().unwrap(), keys);
        assert_eq!(store.reads.lock().unwrap()[0].0, "codex");
    }

    #[test]
    fn missing_secrets_key_cannot_generate_key_or_fall_back_to_other_account() {
        let store = Arc::new(FakeStore::default());
        put_file(
            &store,
            "config.toml",
            b"cli_auth_credentials_store='auto'".to_vec(),
        );
        put_file(&store, "secrets/codex_auth.age", secret_file(ACCOUNT, 1));
        put_file(&store, "auth.json", auth("stale-other-account"));
        let files = store.files.lock().unwrap().clone();
        assert_error(
            source(store.clone()).load(),
            ReaderError::CredentialKeyMissing,
        );
        assert!(store.keyring.lock().unwrap().is_empty());
        assert_eq!(*store.files.lock().unwrap(), files);
    }

    #[test]
    fn corrupt_secrets_and_newer_versions_fail_without_file_fallback() {
        for bytes in [b"not-age".to_vec(), secret_file(ACCOUNT, 2)] {
            let store = Arc::new(FakeStore::default());
            put_file(
                &store,
                "config.toml",
                b"cli_auth_credentials_store='auto'".to_vec(),
            );
            put_file(&store, "secrets/codex_auth.age", bytes);
            put_key(
                &store,
                "codex",
                "secrets",
                "synthetic-passphrase".to_string(),
            );
            put_file(&store, "auth.json", auth("stale-other-account"));
            assert!(source(store).load().is_err());
        }
    }

    #[test]
    fn absent_secrets_file_does_not_read_or_create_a_key() {
        let store = Arc::new(FakeStore::default());
        put_file(
            &store,
            "config.toml",
            b"cli_auth_credentials_store='keyring'".to_vec(),
        );
        assert_error(
            source(store.clone()).load(),
            ReaderError::MissingCredentials,
        );
        assert!(store.reads.lock().unwrap().is_empty());
        assert!(store.keyring.lock().unwrap().is_empty());
        assert_eq!(store.files.lock().unwrap().len(), 1);
    }

    #[test]
    fn auto_uses_file_only_when_configured_keyring_store_is_absent() {
        let store = Arc::new(FakeStore::default());
        put_file(
            &store,
            "config.toml",
            b"cli_auth_credentials_store='auto'\n[features]\nsecret_auth_storage=false".to_vec(),
        );
        put_file(&store, "auth.json", auth(ACCOUNT));
        assert_eq!(source(store).load().unwrap().account_id, ACCOUNT);
        let store = Arc::new(FakeStore {
            fail_keyring: true,
            ..Default::default()
        });
        put_file(
            &store,
            "config.toml",
            b"cli_auth_credentials_store='auto'\n[features]\nsecret_auth_storage=false".to_vec(),
        );
        put_file(&store, "auth.json", auth("stale-other-account"));
        assert_error(source(store).load(), ReaderError::CredentialReadFailed);
    }

    #[test]
    fn reload_pins_source_and_does_not_reveal_stale_fallback() {
        let store = Arc::new(FakeStore::default());
        put_file(
            &store,
            "config.toml",
            b"cli_auth_credentials_store='auto'\n[features]\nsecret_auth_storage=false".to_vec(),
        );
        put_file(&store, "auth.json", auth("stale-other-account"));
        put_key(
            &store,
            "Codex Auth",
            "cli",
            String::from_utf8(auth(ACCOUNT)).unwrap(),
        );
        let reader = source(store.clone());
        assert_eq!(reader.load().unwrap().account_id, ACCOUNT);
        store.keyring.lock().unwrap().clear(); // Simulate a separate official client.
        assert_error(reader.load(), ReaderError::MissingCredentials);
    }

    #[test]
    fn reload_reads_fresh_snapshot_and_rejects_changed_config() {
        let store = Arc::new(FakeStore::default());
        put_file(&store, "auth.json", auth(ACCOUNT));
        let reader = source(store.clone());
        let first = reader.load().unwrap();
        put_file(&store, "auth.json", auth("synthetic-account-002"));
        let second = reader.load().unwrap();
        assert_ne!(first.account_id, second.account_id); // HTTP layer must reject retry across accounts.
        put_file(
            &store,
            "config.toml",
            b"cli_auth_credentials_store='keyring'".to_vec(),
        );
        assert_error(reader.load(), ReaderError::UnsupportedConfiguration);
    }

    #[test]
    fn config_modes_restrictions_and_unrelated_profiles_are_handled_explicitly() {
        let config = parse_config(b"model='synthetic-model'\nprofile='work'\nmcp_oauth_credentials_store='auto'\n[profiles.work]\nmodel='other'\n[profiles.unselected]\nchatgpt_base_url='https://irrelevant.invalid'").unwrap();
        assert!(config.mode == StoreMode::File);
        for bytes in [
            b"cli_auth_credentials_store='other'".as_slice(),
            b"forced_login_method='mystery'",
            b"features.secret_auth_storage='false'",
            b"profile='missing'",
            b"chatgpt_base_url='https://other.invalid'",
            b"forced_chatgpt_workspace_id=[]",
            b"auth_file='other-account.json'",
            b"profile='work'\n[profiles.work]\nmodel_provider='other'",
        ] {
            assert!(parse_config(bytes).is_err());
        }
        let config = parse_config(b"forced_chatgpt_workspace_id=['different-workspace']").unwrap();
        assert_error(
            parse_auth(&auth(ACCOUNT), &config, 1),
            ReaderError::UnsupportedConfiguration,
        );
        let config =
            parse_config(format!("forced_chatgpt_workspace_id='{ACCOUNT}'").as_bytes()).unwrap();
        assert!(parse_auth(&auth(ACCOUNT), &config, 1).is_ok());
    }

    #[test]
    fn ephemeral_and_host_only_credentials_are_not_persisted_fallbacks() {
        let store = Arc::new(FakeStore::default());
        put_file(
            &store,
            "config.toml",
            b"cli_auth_credentials_store='ephemeral'".to_vec(),
        );
        put_file(&store, "auth.json", auth(ACCOUNT));
        assert_error(
            source(store.clone()).load(),
            ReaderError::EphemeralCredentials,
        );
        assert!(store.reads.lock().unwrap().is_empty());
        let mut value: serde_json::Value = serde_json::from_slice(&auth(ACCOUNT)).unwrap();
        value["auth_mode"] = "chatgptAuthTokens".into();
        assert_error(
            parse_auth(
                &serde_json::to_vec(&value).unwrap(),
                &parse_config(b"").unwrap(),
                1,
            ),
            ReaderError::EphemeralCredentials,
        );
    }

    #[test]
    fn unsupported_modes_malformed_tokens_and_conflicting_accounts_are_rejected() {
        let config = parse_config(b"").unwrap();
        for mode in [
            "apikey",
            "headers",
            "agentIdentity",
            "personalAccessToken",
            "future",
        ] {
            let mut value: serde_json::Value = serde_json::from_slice(&auth(ACCOUNT)).unwrap();
            value["auth_mode"] = mode.into();
            assert_error(
                parse_auth(&serde_json::to_vec(&value).unwrap(), &config, 1),
                ReaderError::UnsupportedAuth,
            );
        }
        for field in ["id_token", "access_token"] {
            let mut value: serde_json::Value = serde_json::from_slice(&auth(ACCOUNT)).unwrap();
            value["tokens"][field] = "malformed-token".into();
            assert_error(
                parse_auth(&serde_json::to_vec(&value).unwrap(), &config, 1),
                ReaderError::InvalidCredentials,
            );
        }
        let mut value: serde_json::Value = serde_json::from_slice(&auth(ACCOUNT)).unwrap();
        value["tokens"]["account_id"] = "different-account".into();
        assert_error(
            parse_auth(&serde_json::to_vec(&value).unwrap(), &config, 1),
            ReaderError::InvalidCredentials,
        );
    }

    #[test]
    fn expired_token_is_reported_without_refresh_and_unknown_schema_fails_closed() {
        let config = parse_config(b"").unwrap();
        assert_error(
            parse_auth(&auth(ACCOUNT), &config, 4102444800),
            ReaderError::ExpiredCredentials,
        );
        let mut value: serde_json::Value = serde_json::from_slice(&auth(ACCOUNT)).unwrap();
        value["accounts"] = serde_json::json!([]);
        assert_error(
            parse_auth(&serde_json::to_vec(&value).unwrap(), &config, 1),
            ReaderError::InvalidCredentials,
        );
        assert_error(
            parse_auth(&vec![b' '; MAX_AUTH_BYTES + 1], &config, 1),
            ReaderError::InvalidCredentials,
        );
    }

    #[test]
    fn policy_presence_and_environment_overrides_fail_closed_without_deleting_auth() {
        let store = Arc::new(FakeStore::default());
        put_file(&store, "auth.json", auth(ACCOUNT));
        store.files.lock().unwrap().insert(
            PathBuf::from("synthetic-system/requirements.toml"),
            Vec::new(),
        );
        let before = store.files.lock().unwrap().clone();
        assert_error(
            source(store.clone()).load(),
            ReaderError::UnsupportedConfiguration,
        );
        assert_eq!(*store.files.lock().unwrap(), before);
        for name in [
            "CODEX_API_KEY",
            "CODEX_ACCESS_TOKEN",
            "OPENAI_API_KEY",
            "CODEX_AUTH_FILE",
            "CODEX_CONFIG_OVERRIDE",
        ] {
            assert!(reject_environment_overrides(std::iter::once((name.into(), true))).is_err());
        }
        assert!(reject_environment_overrides(std::iter::once(("CODEX_HOME".into(), true))).is_ok());
    }

    #[test]
    fn windows_password_decoding_matches_keyring_utf16le_not_utf8() {
        let password = "synthetic-json-中文";
        let bytes = password
            .encode_utf16()
            .flat_map(u16::to_le_bytes)
            .collect::<Vec<_>>();
        assert_eq!(decode_keyring_password(&bytes).unwrap(), password);
        assert!(decode_keyring_password(b"odd").is_err());
        assert!(decode_keyring_password(&[0, 0xd8]).is_err());
        assert!(decode_keyring_password(&[0, 0]).is_err());
    }

    #[test]
    fn native_file_adapter_reads_only_a_synthetic_temp_fixture_and_creates_nothing() {
        let directory = tempfile::tempdir().unwrap();
        let auth_path = directory.path().join("auth.json");
        std::fs::write(&auth_path, auth(ACCOUNT)).unwrap();
        let bytes = std::fs::read(&auth_path).unwrap();
        let reader = ExistingAuthSource {
            home: directory.path().to_path_buf(),
            policy_paths: Vec::new(),
            store: Arc::new(NativeReadOnlyStore),
            selected: Mutex::new(None),
        };
        assert_eq!(reader.load().unwrap().account_id, ACCOUNT);
        assert_eq!(std::fs::read(&auth_path).unwrap(), bytes);
        assert_eq!(std::fs::read_dir(directory.path()).unwrap().count(), 1);
        assert!(NativeReadOnlyStore.read_file(&auth_path, 1).is_err());
        assert!(NativeReadOnlyStore
            .read_file(directory.path(), 1024)
            .is_err());
    }

    #[test]
    fn canonical_windows_hash_is_exact_without_prefix_or_case_normalization() {
        assert_eq!(home_hash(Path::new(CANONICAL)).unwrap(), "bb13c7614694f4bf");
        assert_ne!(
            home_hash(Path::new(CANONICAL)).unwrap(),
            home_hash(Path::new(&CANONICAL.to_lowercase())).unwrap()
        );
    }

    #[test]
    fn absent_auth_entry_and_duplicate_secret_keys_cannot_fall_back() {
        for plaintext in [
            r#"{"version":1,"secrets":{}}"#,
            r#"{"version":1,"secrets":{"global/CODEX_AUTH":"one","global/CODEX_AUTH":"two"}}"#,
        ] {
            let store = Arc::new(FakeStore::default());
            put_file(
                &store,
                "config.toml",
                b"cli_auth_credentials_store='auto'".to_vec(),
            );
            put_file(
                &store,
                "secrets/codex_auth.age",
                encrypted(plaintext.as_bytes(), "synthetic-passphrase"),
            );
            put_file(&store, "auth.json", auth("stale-other-account"));
            put_key(&store, "codex", "secrets", "synthetic-passphrase".into());
            let files = store.files.lock().unwrap().clone();
            assert!(source(store.clone()).load().is_err());
            assert_eq!(*store.files.lock().unwrap(), files);
        }
    }

    #[test]
    fn age_work_factor_plaintext_and_ciphertext_are_bounded() {
        let ciphertext = encrypted(b"{}", "synthetic-passphrase");
        // Change only the ASCII work-factor stanza. It must fail before the
        // expensive KDF, regardless of the now-invalid header authentication.
        let split = ciphertext
            .windows(4)
            .position(|bytes| bytes == b" 10\n")
            .unwrap();
        let mut too_expensive = ciphertext.clone();
        too_expensive[split + 1..split + 3].copy_from_slice(b"19");
        assert!(matches!(
            decrypt_existing(&too_expensive, "synthetic-passphrase"),
            Err(ReaderError::UnsupportedConfiguration)
        ));
        assert!(
            decrypt_existing(&vec![0; MAX_CIPHERTEXT_BYTES + 1], "synthetic-passphrase").is_err()
        );
        let oversized_plaintext =
            encrypted(&vec![b' '; MAX_AUTH_BYTES + 1], "synthetic-passphrase");
        assert!(decrypt_existing(&oversized_plaintext, "synthetic-passphrase").is_err());
        assert!(decrypt_existing(&ciphertext, "incorrect-synthetic-passphrase").is_err());
    }

    #[test]
    fn secrets_version_zero_is_read_in_memory_without_migration() {
        let store = Arc::new(FakeStore::default());
        put_file(
            &store,
            "config.toml",
            b"cli_auth_credentials_store='keyring'".to_vec(),
        );
        put_file(&store, "secrets/codex_auth.age", secret_file(ACCOUNT, 0));
        put_key(&store, "codex", "secrets", "synthetic-passphrase".into());
        let before = store.files.lock().unwrap().clone();
        assert_eq!(source(store.clone()).load().unwrap().account_id, ACCOUNT);
        assert_eq!(*store.files.lock().unwrap(), before);
    }

    #[test]
    fn selected_profile_auth_features_are_resolved_without_other_profile_fallback() {
        let config = parse_config(b"profile='work'\ncli_auth_credentials_store='keyring'\n[features]\nsecret_auth_storage=true\n[profiles.work.features]\nsecret_auth_storage=false").unwrap();
        assert!(config.backend == KeyringBackend::Direct);
        assert!(parse_config(
            b"profile='work'\n[profiles.work]\nchatgpt_base_url='https://other.invalid'"
        )
        .is_err());
        assert!(parse_config(b"model_provider='openai'\n[model_providers.unselected]\nname='Other'\nbase_url='https://other.invalid'").is_ok());
    }

    #[test]
    fn realistic_runtime_and_sandbox_configuration_does_not_change_auth_selection() {
        let config = parse_config(
            br#"
model = "gpt-synthetic"
model_reasoning_effort = "high"
approval_policy = "on-request"
sandbox_mode = "workspace-write"
cli_auth_credentials_store = "file"
[sandbox_workspace_write]
network_access = true
writable_roots = ["C:/synthetic/work"]
[projects.'C:/synthetic/work']
trust_level = "trusted"
[windows]
sandbox = "unelevated"
[tui]
notifications = false
[mcp_servers.synthetic]
command = "synthetic-unused-command"
[features]
multi_agent = true
secret_auth_storage = true
"#,
        )
        .unwrap();
        assert!(config.mode == StoreMode::File);
        assert!(config.backend == KeyringBackend::Secrets);
        assert!(parse_auth(&auth(ACCOUNT), &config, 1).is_ok());
    }
}
