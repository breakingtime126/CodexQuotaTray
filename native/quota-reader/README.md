# CodexQuotaTray read-only quota helper 0.2.16

A narrow Windows x64 helper for the tray prototype. It reads an existing supported
ChatGPT login and the two official usage endpoints. It is not a login tool and
is not the official Codex CLI or app server.

## Capability boundary

The binary accepts exactly `--read`. It accepts no credential, file, endpoint,
proxy or arbitrary command argument. It contains no login, token renewal,
writeback, migration, key creation, logout, purchase or reset-credit consumption
path. It does not start an external Codex executable or choose another backend
when the selected credential format is unsupported.

Only these GET URLs are reachable through the production transport:

- `https://chatgpt.com/backend-api/wham/usage`
- `https://chatgpt.com/backend-api/wham/rate-limit-reset-credits`

Both headers come from the same validated credential snapshot. Redirects are
never followed, including same-origin redirects. The transport pins the official
Codex v0.159.0 reqwest 0.12.28 and proxy matcher hyper-util 0.1.20. On Windows it follows the official RespectSystemProxy route selection: configured
PAC, enabled auto-detection, static proxy/bypass, then environment only when
system resolution is unavailable. Environment NO_PROXY applies only to that
environment fallback route. It does not change any proxy settings. Native TLS/Schannel
is the default; an existing CODEX_CA_CERTIFICATE (preferred) or SSL_CERT_FILE
PEM bundle adds trust roots using the official rustls selection with both
bundled WebPKI and native-root features. Custom-CA input is capped at 1 MiB. Verification
remains enabled. Invalid configured certificate files fail closed.

Before creating the client, the helper resolves one existing authentication home,
then reads exactly that home's `.env`. It never searches parents or installation
folders. The adapted dotenvy 0.15.7 parser preserves quoting, expansion and
sequential overwrite behavior; Windows environment names are case-insensitive.
Only HTTP_PROXY, HTTPS_PROXY, ALL_PROXY, NO_PROXY (and platform lowercase aliases),
SSL_CERT_FILE and SSL_CERT_DIR are applied to this helper process. Other names may
be private expansion symbols but are never exported. CODEX_ names are filtered
case-insensitively, so CODEX_HOME and credentials cannot be changed. An inherited
CODEX_CA_CERTIFICATE retains its official precedence; .env cannot set it.

Parsing is atomic and bounded: 256 KiB input, 1,024 records, 32 KiB per expanded
value and 1 MiB aggregate expansion. A missing .env is allowed; malformed,
unreadable, dangling-link, nonregular or oversized input returns the closed
NetworkConfiguration category. This fail-closed behavior is intentionally
stricter than the upstream CLI's partial/ignored-error loader. No file is written.
The preflight worker is joined before network variables are applied on the sole
main thread; client/credential work starts afterward within the same 30-second
wall-clock budget and uses the pinned original authentication source.

Version 0.2.15 adds a conservative adaptation of the official Windows system
proxy resolver. Explicit HTTPS_PROXY/ALL_PROXY retain the existing default transport selection
instead of invoking new automatic resolution; NO_PROXY is honored first. This
preserves the old matcher's ALL_PROXY-versus-Windows-static ordering rather than
promising that every environment value overrides every static system setting. Only the current user's explicitly configured PAC URL or
enabled AutoDetect setting is used. WinHTTP resolves each exact endpoint URL;
PAC/WPAD may read the configured script or use system DHCP/DNS discovery.
No system proxy setting changes, implicit WPAD, domain auto-logon or unconfigured
DIRECT fallback is allowed. Ordered system routes are retained. Only positively
classified DNS/socket connection failures can advance to the next route; access
denial, ambiguous timeouts, proxy HTTP/auth rejection and TLS failure stop.
Unsupported schemes stop rather than being skipped. The resolver does not use
CLI bootstrap requests, cloud configuration, token refresh or TLS-backend retries.
No PAC/WPAD configuration keeps the prior default reqwest/static-proxy behavior.

Automatic resolution has one five-second budget per endpoint, including configured
PAC-to-WPAD fallback; request timeouts remain ten seconds for usage and five for
details, with five-second connect timeouts. The whole credential/resolve/request
transaction retains its thirty-second process deadline. HTTP response bodies are
limited to 256 KiB. These product bounds do not claim Windows runtime acceptance.

A 401 permits one read-only credential reload in the entire transaction. The
request is retried only if the account is unchanged and the already-saved access
token changed. This may observe renewal performed by official Codex; this helper
never performs renewal. A second 401, unchanged token, or other HTTP status is
never retried. Account change or an invalid/unreadable credential reload discards
the transaction rather than reporting the old account's usage.

Reset-detail fetch or parse failure falls back to the count actually supplied by
usage. An absent count remains null; the helper never synthesizes zero. Actual
plan SKU and window durations are retained, unknown stays unknown, and reset
and expiry timestamps retain Unix-second precision. Only whitelisted typed quota
fields leave the helper; account/user identifiers, token claims, titles,
descriptions, raw responses and error strings do not.

## Existing credential stores

The default is the current user's `.codex/auth.json`, or the existing absolute
`CODEX_HOME` selected by the launching process. The supported user configuration
modes are `file`, `keyring`, and `auto`; `ephemeral` reports that the host-only
login cannot be inherited. The Windows `keyring` backend uses encrypted
`secrets/codex_auth.age` plus an already-existing OS credential-store key by
default. A supported explicit `secret_auth_storage=false` selects the older
Direct keyring format. Neither backend creates a key or migrates data.

`auto` may use the file fallback only when the configured store is genuinely
absent. An inaccessible/corrupt store or missing decryption key is an error, not
permission to select another account. The original source/configuration is pinned
for the one possible 401 reload. Token/account claims and any configured workspace
restriction must agree; unrecognized authentication modes fail closed.

This prototype does not implement managed/system configuration merges, custom
provider routing, FedRAMP routing or arbitrary base URLs. Relevant
unsupported settings report UnsupportedConfiguration without modifying them.
Ordinary unrelated model, UI, sandbox and MCP settings are ignored. Plaintext
inputs are bounded to256KiB; encrypted inputs to512KiB. Age scrypt work factors are
capped at logN18, which can require roughly256MiB of temporary KDF memory; stronger
files report unsupported parameters instead of performing unbounded work.

## Output protocol

stdout contains exactly one UTF-8 JSON object plus one newline, at most 512 KiB.
stderr is intentionally empty, including on caught panics. Expected failures use
exit code 0 and the fixed protocol status, so they are not confused with process
launch errors. Cancellation/forced termination may leave no complete object.

Root fields are `protocolVersion` (1), `helperVersion` (`0.2.14`), `status`,
`stage`, `code` (HTTP number or null), `errorKind` (fixed enum or null), and `result`
(object only when Ready, otherwise null).

- Status: Ready, NeedsLogin, UnsupportedAuth, Unavailable, InvalidResponse.
- Stage: AuthLoad, UsageRead, ResetCardsRead, RateLimitsRead.
- Normal success: Ready / RateLimitsRead / null code / null errorKind.
- Recoverable reset-detail failure: Ready / ResetCardsRead / safe diagnostic,
  with the original usage result/count.
- An optional `network` object contains exactly twelve closed diagnostic fields:
  route, proxyProtocol, noProxyBypass, tlsBackend, caOverride, dotenv, errorLayer,
  ioKind, osCode, chainTruncated, systemProxyConfig and systemProxyResolution. They contain only enums, booleans or a nullable
  signed OS error number, never destinations or arbitrary error strings.
- Route is observed at the existing hyper-util system matcher's actual interception
  call. The pinned upstream code is vendored with passive provenance/atomic
  telemetry only; request routing, URI parsing, authentication and NO_PROXY rules
  are unchanged. The observation relies on this helper's single default client,
  fixed HTTPS origin and disabled redirects. Unknown means that no decision has
  been observed; Direct describes selection, not successful connectivity.
- The same request supplies network evidence; no diagnostic probe is sent. OS code
  is separate from HTTP status and remains null when the library exposes none.
  Native TLS does not guarantee that all platform HRESULTs are exposed.
- Fixed error kinds are defined in `src/error.rs`; they contain no dynamic text.
  Network diagnostics distinguish DNS, connect, proxy configuration/connection/
  authentication, TLS, custom-CA configuration and timeout when the locked
  transport exposes reliable evidence. Unknown errors stay unknown; raw library
  error strings, proxy addresses, certificate paths and response bodies are never
  returned.

`result` contains `rateLimits`, `rateLimitsByLimitId`, and
`rateLimitResetCredits`, matching the subset used by the existing C# QuotaParser
and ResetCards parser. Windows use `usedPercent`, `windowDurationMins`, `resetsAt`.
Reset details use `availableCount`, `credits`, `id`, `resetType`, `status`,
`grantedAt`, and `expiresAt`. Explicit null card expiry means never expires;
incomplete/invalid detail falls back to count-only data.

## Build and verification

Use the repository build scripts for the embedded production artifact. Standalone:

```
cargo test --locked --manifest-path native/quota-reader/Cargo.toml
cargo build --locked --release --target x86_64-pc-windows-gnu --manifest-path native/quota-reader/Cargo.toml
```

A Windows MSVC target is also suitable when its toolchain is available. Native
non-Windows production transport intentionally returns UnsupportedConfiguration;
Linux tests inject synthetic credentials and transport. Tests never read real
credentials or make live authenticated service requests. The HTTP redirect test
uses only loopback HTTP and synthetic credentials.

Keep Cargo.lock and the generated dependency-license inventory with releases.
See THIRD_PARTY_NOTICES.md, LICENSE and NOTICE for the pinned OpenAI source
baseline and changes. A test pass does not establish live service acceptance or
that every future official credential/policy format is supported.
