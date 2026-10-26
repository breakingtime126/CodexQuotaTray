// Copyright 2025 OpenAI
// SPDX-License-Identifier: Apache-2.0
// Portions adapted from OpenAI Codex rust-v0.159.0 (Apache-2.0).
// CHANGED: standalone read-only subset; bounded typed inputs/outputs; no account metadata,
// arbitrary display text, redemption, routing, logging or full Codex runtime dependencies.
// See THIRD_PARTY_NOTICES.md for exact upstream files and the extraction boundary.
use crate::error::ReaderError;
use crate::http::MAX_RESPONSE_BYTES;
use chrono::DateTime;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct QuotaResult {
    pub rate_limits: RateLimitSnapshot,
    pub rate_limits_by_limit_id: BTreeMap<String, RateLimitSnapshot>,
    pub rate_limit_reset_credits: Option<ResetSummary>,
}
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RateLimitSnapshot {
    pub limit_id: String,
    pub primary: Option<RateWindow>,
    pub secondary: Option<RateWindow>,
    pub plan_type: Option<&'static str>,
}
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RateWindow {
    pub used_percent: f64,
    pub window_duration_mins: Option<i64>,
    pub resets_at: Option<i64>,
}
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResetSummary {
    pub available_count: i64,
    pub credits: Option<Vec<ResetCredit>>,
}
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResetCredit {
    pub id: String,
    pub reset_type: &'static str,
    pub status: &'static str,
    pub granted_at: i64,
    pub expires_at: Option<i64>,
}

#[derive(Deserialize)]
struct Usage {
    plan_type: Option<String>,
    rate_limit: Option<RawRateLimit>,
    additional_rate_limits: Option<Vec<AdditionalRateLimit>>,
    rate_limit_reset_credits: Option<RawResetSummary>,
    // Used solely to reject a mismatched snapshot; never included in output.
    account_id: Option<String>,
}
#[derive(Deserialize)]
struct RawRateLimit {
    primary_window: Option<RawWindow>,
    secondary_window: Option<RawWindow>,
}
#[derive(Deserialize)]
struct RawWindow {
    used_percent: f64,
    limit_window_seconds: Option<i32>,
    reset_at: Option<i64>,
}
#[derive(Deserialize)]
struct AdditionalRateLimit {
    metered_feature: String,
    rate_limit: Option<RawRateLimit>,
}
#[derive(Deserialize)]
struct RawResetSummary {
    available_count: i64,
}
#[derive(Deserialize)]
struct RawResetDetails {
    available_count: i64,
    credits: Vec<RawResetCredit>,
}
#[derive(Deserialize)]
struct RawResetCredit {
    id: String,
    reset_type: String,
    status: String,
    granted_at: String,
    // Unlike a missing expiry, explicit null means never expires. Upstream requires the
    // optional field semantically; our deserializer rejects an omitted field below.
    expires_at: serde_json::Value,
}

fn decode<T: serde::de::DeserializeOwned>(body: &[u8]) -> Result<T, ReaderError> {
    if body.len() > MAX_RESPONSE_BYTES {
        return Err(ReaderError::ResponseTooLarge);
    }
    serde_json::from_slice(body).map_err(|_| ReaderError::InvalidResponse)
}

pub fn parse_usage(body: &[u8], expected_account: &str) -> Result<QuotaResult, ReaderError> {
    let usage: Usage = decode(body)?;
    if usage
        .account_id
        .as_deref()
        .is_some_and(|account| account != expected_account)
    {
        return Err(ReaderError::AccountChanged);
    }
    // A JSON object with only unrelated fields is not a usage snapshot. An explicitly
    // unknown plan still is; absent windows/counts stay absent, never fabricated as zero.
    if usage.plan_type.is_none()
        && usage.rate_limit.is_none()
        && usage.additional_rate_limits.is_none()
        && usage.rate_limit_reset_credits.is_none()
    {
        return Err(ReaderError::InvalidResponse);
    }
    let plan = usage.plan_type.as_deref().map(map_plan_type);
    let main = snapshot("codex".into(), usage.rate_limit, plan)?;
    let mut by_id = BTreeMap::from([("codex".into(), main.clone())]);
    if let Some(additional) = usage.additional_rate_limits {
        if additional.len() > 64 {
            return Err(ReaderError::ResponseTooLarge);
        }
        for entry in additional {
            if !safe_id(&entry.metered_feature) || by_id.contains_key(&entry.metered_feature) {
                return Err(ReaderError::InvalidResponse);
            }
            by_id.insert(
                entry.metered_feature.clone(),
                snapshot(entry.metered_feature, entry.rate_limit, plan)?,
            );
        }
    }
    let summary = usage
        .rate_limit_reset_credits
        .map(|summary| {
            if summary.available_count < 0 {
                return Err(ReaderError::InvalidResponse);
            }
            Ok(ResetSummary {
                available_count: summary.available_count,
                credits: None,
            })
        })
        .transpose()?;
    Ok(QuotaResult {
        rate_limits: main,
        rate_limits_by_limit_id: by_id,
        rate_limit_reset_credits: summary,
    })
}

fn snapshot(
    id: String,
    raw: Option<RawRateLimit>,
    plan: Option<&'static str>,
) -> Result<RateLimitSnapshot, ReaderError> {
    let (primary, secondary) = match raw {
        Some(limit) => (
            limit.primary_window.map(window).transpose()?,
            limit.secondary_window.map(window).transpose()?,
        ),
        None => (None, None),
    };
    Ok(RateLimitSnapshot {
        limit_id: id,
        primary,
        secondary,
        plan_type: plan,
    })
}
fn window(raw: RawWindow) -> Result<RateWindow, ReaderError> {
    if !raw.used_percent.is_finite() {
        return Err(ReaderError::InvalidResponse);
    }
    // Same ceil-to-minutes conversion as official backend-client, without claiming a
    // duration when the server omitted it or supplied a nonpositive duration.
    let minutes = raw
        .limit_window_seconds
        .filter(|value| *value > 0)
        .map(|seconds| (i64::from(seconds) + 59) / 60);
    Ok(RateWindow {
        used_percent: raw.used_percent,
        window_duration_mins: minutes,
        resets_at: raw.reset_at,
    })
}

/// Official backend-client PlanType -> protocol/account mapping. Unknown strings never
/// cross the process boundary and are not guessed from access-token claims.
fn map_plan_type(plan: &str) -> &'static str {
    match plan {
        "free" => "free",
        "go" => "go",
        "plus" => "plus",
        "pro" => "pro",
        "prolite" => "prolite",
        "promax" => "promax",
        "team" => "team",
        "self_serve_business_prolite" => "self_serve_business_prolite",
        "self_serve_business_usage_based" => "self_serve_business_usage_based",
        "business" => "business",
        "ent26" => "ent26",
        "enterprise_cbp_automation" => "enterprise_cbp_automation",
        "enterprise_cbp_usage_based" => "enterprise_cbp_usage_based",
        "enterprise" => "enterprise",
        "edu" | "education" => "edu",
        "edu_plus" => "edu_plus",
        "edu_pro" => "edu_pro",
        _ => "unknown",
    }
}

pub fn parse_reset_credits(body: &[u8]) -> Result<ResetSummary, ReaderError> {
    let raw: RawResetDetails = decode(body)?;
    if raw.available_count < 0 {
        return Err(ReaderError::InvalidResponse);
    }
    if raw.credits.len() > 256 {
        return Err(ReaderError::ResponseTooLarge);
    }
    let credits = raw
        .credits
        .into_iter()
        .map(|credit| {
            if !safe_id(&credit.id) {
                return Err(ReaderError::InvalidResponse);
            }
            let expires_at = match credit.expires_at {
                serde_json::Value::Null => None,
                serde_json::Value::String(timestamp) => Some(parse_timestamp(&timestamp)?),
                _ => return Err(ReaderError::InvalidResponse),
            };
            Ok(ResetCredit {
                id: credit.id,
                reset_type: match credit.reset_type.as_str() {
                    "codex_rate_limits" => "codexRateLimits",
                    _ => "unknown",
                },
                status: match credit.status.as_str() {
                    "available" => "available",
                    "redeeming" => "redeeming",
                    "redeemed" => "redeemed",
                    _ => "unknown",
                },
                granted_at: parse_timestamp(&credit.granted_at)?,
                expires_at,
            })
        })
        .collect::<Result<Vec<_>, ReaderError>>()?;
    Ok(ResetSummary {
        available_count: raw.available_count,
        credits: Some(credits),
    })
}
fn parse_timestamp(value: &str) -> Result<i64, ReaderError> {
    DateTime::parse_from_rfc3339(value)
        .map(|timestamp| timestamp.timestamp())
        .map_err(|_| ReaderError::InvalidResponse)
}
fn safe_id(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || b"_-.:".contains(&byte))
}
