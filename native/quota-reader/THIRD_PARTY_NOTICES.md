# Source provenance and changes

This helper is an independent, reduced read-only adaptation for CodexQuotaTray. It is
not the official Codex CLI, app server or AuthManager, and it does not embed those
crates or claim their full compatibility. OpenAI Codex is copyright 2025 OpenAI,
licensed under Apache License 2.0. The unmodified upstream LICENSE and NOTICE are
included in this directory. The upstream NOTICE also mentions Ratatui; no Ratatui
code or dependency is included in this helper.

Pinned source baseline: [OpenAI Codex rust-v0.159.0](https://github.com/openai/codex/tree/rust-v0.159.0).

## Quota mapping and request shape

- [backend-client/src/client.rs](https://github.com/openai/codex/blob/rust-v0.159.0/codex-rs/backend-client/src/client.rs): bearer/account header pairing, quota window conversion, plan mapping, and the primary Codex bucket.
- [backend-client/src/client/rate_limit_resets.rs](https://github.com/openai/codex/blob/rust-v0.159.0/codex-rs/backend-client/src/client/rate_limit_resets.rs): GET usage and reset-credit-detail endpoints only. No consume/reset action is copied.
- [backend-client/src/types.rs](https://github.com/openai/codex/blob/rust-v0.159.0/codex-rs/backend-client/src/types.rs): usage reset-credit count and detail field shapes.
- [protocol/src/account.rs](https://github.com/openai/codex/blob/rust-v0.159.0/codex-rs/protocol/src/account.rs): serialized plan SKU spellings.
- [codex-backend-openapi-models/src/models/rate_limit_status_payload.rs](https://github.com/openai/codex/blob/rust-v0.159.0/codex-rs/codex-backend-openapi-models/src/models/rate_limit_status_payload.rs): backend wire plan enum spellings.
- [app-server/src/request_processors/account_processor/rate_limit_resets.rs](https://github.com/openai/codex/blob/rust-v0.159.0/codex-rs/app-server/src/request_processors/account_processor/rate_limit_resets.rs): RFC3339-to-Unix-seconds conversion, reset type/status mapping, and fallback to usage when detail fetching/parsing fails.

Changed files `src/quota.rs` and `src/http.rs` keep only the fields and two read
operations this tray needs. They replace the full client/runtime with a small
synchronous fixed-endpoint transport and typed, size-bounded JSON. They remove
account/user metadata, arbitrary display text, redemption endpoints, routing,
logs and upstream errors containing bodies or URLs. Absent window duration and
reset times remain absent; unknown plan/reset enums remain unknown. Missing card
expiry is rejected as incomplete detail rather than reported as “never”.

## HTTP compatibility (0.2.12)

The transport restores the official quota path's reqwest 0.12.28 selection and
hyper-util 0.1.20 proxy matcher instead of the 0.2.11 ureq substitute. Proxy
selection is delegated to that implementation, not rewritten here. Relevant
upstream sources:

- [http-client/src/client_builder.rs](https://github.com/openai/codex/blob/rust-v0.159.0/codex-rs/http-client/src/client_builder.rs)
- [http-client/src/custom_ca.rs](https://github.com/openai/codex/blob/rust-v0.159.0/codex-rs/http-client/src/custom_ca.rs)
- [hyper-util proxy matcher](https://github.com/hyperium/hyper-util/blob/v0.1.20/src/client/proxy/matcher.rs)

The standalone transport remains changed: fixed endpoints, no redirects,
bounded responses/deadlines, and closed diagnostic categories. It does not copy
upstream login/refresh, bootstrap routing, arbitrary URL or request logging code.

## Existing credential reads

`src/auth.rs` is explicitly marked as changed and derives format/selection rules from:

- [login/src/auth/storage.rs](https://github.com/openai/codex/blob/rust-v0.159.0/codex-rs/login/src/auth/storage.rs)
- [login/src/token_data.rs](https://github.com/openai/codex/blob/rust-v0.159.0/codex-rs/login/src/token_data.rs)
- [secrets/src/lib.rs](https://github.com/openai/codex/blob/rust-v0.159.0/codex-rs/secrets/src/lib.rs)
- [secrets/src/local.rs](https://github.com/openai/codex/blob/rust-v0.159.0/codex-rs/secrets/src/local.rs)
- [core/src/config/auth_keyring.rs](https://github.com/openai/codex/blob/rust-v0.159.0/codex-rs/core/src/config/auth_keyring.rs)

All create, save, refresh, migration, logout and credential-generation operations
are removed. Storage and policy formats this reduced reader cannot safely resolve
are rejected explicitly, without invoking an installed CLI or writing anything.

## Dependencies

Cargo.lock records the actual dependency versions. The release's generated third-party
license bundle covers compiled dependencies separately; the OpenAI Apache license
alone does not cover every dependency. `reqwest` uses Windows native TLS (Schannel)
by default and rustls/WebPKI/native roots for an existing custom-CA bundle, with
certificate verification enabled and redirects disabled. `age` is used only
to decrypt an existing supported secrets envelope with an existing key. No age
command-line features are enabled.

## dotenv parser adaptation (0.2.13)

The read-only network bootstrap adapts `src/parse.rs` and `src/iter.rs` from
[dotenvy 0.15.7](https://crates.io/crates/dotenvy/0.15.7), the version pinned by
OpenAI Codex rust-v0.159.0. Downloaded crate SHA-256:
`1aaf95b3e5c8f23aa320147307562d361db0ae0d51242340f558153b4eb2439b`.
The complete MIT copyright and license are in `../../third_party/Dotenvy-MIT.txt`
and included with the application. The unmodified crate is a test-only dependency
for differential syntax tests, not a new production transport dependency.

Changed: process-environment lookup is injected through a private sequential
overlay, mutating load APIs are removed, failures contain no source text, and
file size, record count, expanded value length and total expansion are bounded.
The official parser's quoting, escaping, interpolation and multiline grammar are
retained. Only proxy and SSL certificate environment keys are exported after
successful parsing; other keys may be private expansion symbols only. `CODEX_`
keys are never exported or placed in the virtual process overlay, matching the
case-insensitive upstream filter. Unlike official Codex's best-effort load, any
unreadable, malformed or over-limit existing `.env` fails closed atomically.

## Windows proxy compatibility (0.2.16)

`src/system_proxy.rs` adapts current-user configuration and WinHTTP ownership from
[OpenAI Codex Windows resolver](https://github.com/openai/codex/blob/rust-v0.159.0/codex-rs/http-client/src/outbound_proxy/windows.rs).
Proxy-list and bypass parser functions are reused from
[outbound_proxy.rs](https://github.com/openai/codex/blob/rust-v0.159.0/codex-rs/http-client/src/outbound_proxy.rs).
Both are covered by the complete Apache-2.0 LICENSE and NOTICE included here.
Changed: cancellable bounded WinHTTP Ex calls; no implicit WPAD on missing settings;
no domain auto-logon; private address-bearing values and closed diagnostics.
`proxy_policy.rs` follows system decision first, environment only on Unavailable,
and returns selected-route connection failures without retrying other routes.
Explicit clients disable default implicit proxy selection. Unsupported static
credentials/schemes/components produce an error rather than a different route.
The vendored hyper-util observation patch is unchanged.
