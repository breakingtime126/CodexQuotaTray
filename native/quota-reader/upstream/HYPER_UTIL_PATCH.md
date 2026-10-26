# hyper-util 0.1.20: passive system-route diagnostics

The subdirectory `hyper-util-0.1.20` was extracted without modification from the
published crate archive, then patched only as documented below. The original
MIT license is retained at `hyper-util-0.1.20/LICENSE`.

- Upstream: https://github.com/hyperium/hyper-util
- Archive: https://static.crates.io/crates/hyper-util/hyper-util-0.1.20.crate
- Archive SHA-256: `96547c2556ec9d12fb1578c4eaf448b04993e7fb79cbaad930a656880a6bdfa0`
- Archive VCS commit: `b23a13e2b7ee73e15ba008cd9b19dcd2d3861957`
- Patch: `hyper-util-0.1.20-diagnostics.patch`
- Patch SHA-256: `aae316476d0f0f45a048a8544227148cc9b83dd19bb99e24c273e554287422a2`

## Exact changed-file allowlist

All other archive files must remain byte-identical. No original file is deleted.

- Modified `src/client/proxy/matcher.rs`
  SHA-256: `368cb6c9d56e700b5bf9df05c7b33e5e5937483b21a90fc71ebaba026f1e7ae2`
- Added `src/client/proxy/matcher/diagnostics.rs`
  SHA-256: `ad986d04b2aad421ce7980edbea692b994560953841148f7be618b70bc2fd84e`

## Behavior and integration contract

The original proxy parser, environment lookup order, NO_PROXY implementation,
CGI behavior, Windows registry reads, macOS reads, and proxy authentication are
retained. The only Windows refactor moves the existing ProxyServer fill block
unchanged into a testable function and adds provenance alongside each assignment.
Provenance follows the successfully parsed component, including ALL_PROXY fallback.
There are no additional registry reads, DNS lookups, HTTP requests, callbacks,
logging, formatting, or routing overrides.

Only a matcher created by `Matcher::from_system()` records observations, and only
when its existing `intercept` operation is invoked. The public getter is
`hyper_util::client::proxy::matcher::last_system_route()` and returns
`Option<SystemRouteDiagnostic>`. The process-wide, single-atomic snapshot is `None`
before observation. Its closed fields are `source`, `protocol`, and
`no_proxy_bypass`; the last is true only when the existing NO_PROXY branch
suppresses a configured proxy for the destination scheme. No address, hostname,
credential, authorization header, or raw environment value is exposed.

This is an observation of a matcher call, not a general per-request transport API.
The helper uses one client and reqwest's single default system matcher. Both
allowed destinations are HTTPS on the same origin; redirects are disabled.
reqwest 0.12.28's connection dispatcher calls this matcher at
`src/connect.rs:941-944`. Its proxy-auth and custom-header hooks skip HTTPS at
`src/async_impl/client.rs:2667-2669` and `2688-2690`, so production observations
come from the actual connection decision. Connection reuse retains the previous
snapshot. Additional clients, custom proxy lists, different origins, HTTP
requests, or a reqwest/hyper-util upgrade require a fresh audit of this contract.

## Validation

The exact production matcher and the unchanged archive matcher were compiled in
an isolated, offline test harness using the helper's locked versions of `http`,
`ipnet`, `percent-encoding`, and `base64`. All 26 tests passed: 12 upstream tests
for each copy, one new closed-observation test, and a differential matrix covering
3,630 combinations of proxy strings, NO_PROXY rules, and destination URIs.
The matrix compared proxy presence, URI, HTTP Basic authorization, and raw SOCKS
authorization. The diagnostic test verifies Windows-versus-environment precedence,
invalid-specific fallback to ALL_PROXY, empty/nonempty Windows filling, all six
supported proxy protocols, CGI suppression, unobserved matchers, and NO_PROXY
with and without a configured proxy. All inputs were synthetic; no network,
user credentials, or Windows settings were accessed.
