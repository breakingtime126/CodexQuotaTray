// SPDX-License-Identifier: Apache-2.0
//! Deliberately narrow existing-login quota reader. No authentication mutation API exists.
pub mod auth;
pub mod error;
pub mod http;
pub mod network_config;
#[cfg(any(windows, test))]
mod proxy_policy;
pub mod quota;
pub mod system_proxy;

use auth::{CredentialSource, Credentials};
use error::{Failure, NetworkDiagnostics, ReaderError};
use http::{Endpoint, Transport};
use quota::QuotaResult;
use serde::Serialize;

pub const MAX_OUTPUT_BYTES: usize = 512 * 1024;

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
pub enum Status {
    Ready,
    NeedsLogin,
    UnsupportedAuth,
    Unavailable,
    InvalidResponse,
}
#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
pub enum Stage {
    AuthLoad,
    UsageRead,
    ResetCardsRead,
    RateLimitsRead,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Envelope {
    pub protocol_version: u8,
    pub helper_version: &'static str,
    pub status: Status,
    pub stage: Stage,
    pub code: Option<u16>,
    pub error_kind: Option<ReaderError>,
    pub result: Option<QuotaResult>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub network: Option<NetworkDiagnostics>,
}

impl Envelope {
    pub fn failed(stage: Stage, failure: impl Into<Failure>) -> Self {
        let Failure {
            kind,
            code,
            network,
        } = failure.into();
        let status = match kind {
            ReaderError::MissingCredentials
            | ReaderError::InvalidCredentials
            | ReaderError::ExpiredCredentials
            | ReaderError::Unauthorized => Status::NeedsLogin,
            ReaderError::UnsupportedAuth
            | ReaderError::EphemeralCredentials
            | ReaderError::UnsupportedConfiguration => Status::UnsupportedAuth,
            ReaderError::InvalidResponse | ReaderError::ResponseTooLarge => Status::InvalidResponse,
            _ => Status::Unavailable,
        };
        Self {
            protocol_version: 1,
            helper_version: env!("CARGO_PKG_VERSION"),
            status,
            stage,
            code,
            error_kind: Some(kind),
            result: None,
            network,
        }
    }
    fn ready(result: QuotaResult, warning: Option<Failure>) -> Self {
        Self {
            protocol_version: 1,
            helper_version: env!("CARGO_PKG_VERSION"),
            status: Status::Ready,
            stage: if warning.is_some() {
                Stage::ResetCardsRead
            } else {
                Stage::RateLimitsRead
            },
            code: warning.and_then(|f| f.code),
            error_kind: warning.map(|f| f.kind),
            result: Some(result),
            network: warning.and_then(|f| f.network),
        }
    }
    pub fn with_network(mut self, network: NetworkDiagnostics) -> Self {
        self.network = Some(network);
        self
    }
    pub fn to_bounded_json(&self) -> Vec<u8> {
        match serde_json::to_vec(self) {
            Ok(bytes) if bytes.len() < MAX_OUTPUT_BYTES => bytes,
            _ => br#"{"protocolVersion":1,"helperVersion":"0.2.16","status":"InvalidResponse","stage":"RateLimitsRead","code":null,"errorKind":"ResponseTooLarge","result":null}"#.to_vec(),
        }
    }
}

/// A single retry budget covers the complete usage + reset-detail transaction. On 401
/// a read-only reload may observe official Codex having renewed its token. This helper
/// never renews a token itself, and never switches the account underneath a transaction.
struct ReadTransaction<'a, S, T> {
    source: &'a S,
    transport: &'a T,
    credentials: Credentials,
    reload_used: bool,
}
impl<S: CredentialSource, T: Transport> ReadTransaction<'_, S, T> {
    fn get(&mut self, endpoint: Endpoint) -> Result<Vec<u8>, Failure> {
        match self.transport.get(endpoint, &self.credentials) {
            Err(f) if f.code == Some(401) && !self.reload_used => {
                self.reload_used = true;
                let next = self.source.load().map_err(Failure::from)?;
                if next.account_id != self.credentials.account_id {
                    return Err(ReaderError::AccountChanged.into());
                }
                if next.access_token == self.credentials.access_token {
                    return Err(f);
                }
                self.credentials = next;
                self.transport.get(endpoint, &self.credentials)
            }
            other => other,
        }
    }
}

pub fn read_quota<S: CredentialSource, T: Transport>(source: &S, transport: &T) -> Envelope {
    let mut envelope = read_quota_inner(source, transport);
    if envelope.network.is_none() {
        envelope.network = transport.network_diagnostics();
    }
    envelope
}

fn read_quota_inner<S: CredentialSource, T: Transport>(source: &S, transport: &T) -> Envelope {
    let credentials = match source.load() {
        Ok(value) => value,
        Err(kind) => return Envelope::failed(Stage::AuthLoad, kind),
    };
    let mut transaction = ReadTransaction {
        source,
        transport,
        credentials,
        reload_used: false,
    };
    let usage = match transaction.get(Endpoint::Usage) {
        Ok(body) => body,
        Err(failure) => return Envelope::failed(Stage::UsageRead, failure),
    };
    let mut result = match quota::parse_usage(&usage, &transaction.credentials.account_id) {
        Ok(value) => value,
        Err(kind) => return Envelope::failed(Stage::UsageRead, kind),
    };
    match transaction
        .get(Endpoint::ResetCredits)
        .and_then(|body| quota::parse_reset_credits(&body).map_err(Failure::from))
    {
        Ok(details) => {
            result.rate_limit_reset_credits = Some(details);
            Envelope::ready(result, None)
        }
        // An account change means the snapshot is now stale. Do not show the old account.
        Err(failure)
            if matches!(
                failure.kind,
                ReaderError::AccountChanged
                    | ReaderError::MissingCredentials
                    | ReaderError::InvalidCredentials
                    | ReaderError::CredentialReadFailed
                    | ReaderError::CredentialKeyMissing
                    | ReaderError::ExpiredCredentials
                    | ReaderError::EphemeralCredentials
                    | ReaderError::UnsupportedAuth
                    | ReaderError::UnsupportedConfiguration
            ) =>
        {
            Envelope::failed(Stage::ResetCardsRead, failure)
        }
        Err(failure) => Envelope::ready(result, Some(failure)),
    }
}
