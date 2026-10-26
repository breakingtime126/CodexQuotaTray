# Latest verification: 0.2.14 read-only diagnostic build (2026-10-12)

## What this build establishes

The user's 0.2.13 screenshot still reports UsageRead / ConnectFailure. Its cause is not established, and this release does not claim that the connection or the unknown model has been fixed. It adds evidence to the existing user-opened connection diagnostic window without changing the quota request policy.

The existing request now reports its observed system-proxy matcher decision: unknown, direct, environment proxy, Windows proxy or other system proxy; the selected proxy protocol; and whether NO_PROXY bypassed a configured proxy. It also reports the selected TLS backend, CA-override and selected-home .env state, a closed error-layer/IO-kind label, any available numeric OS error code, and whether error-chain inspection was truncated. HTTP status and OS error codes remain separate. Route selection does not imply successful connection; a missing OS code does not imply absence of a lower-level error.

The window shows the last two quota attempts and the already-read model metadata status. Metadata fields are fixed status labels and presence/availability flags only. Opening it performs no additional request, credential read or session scan. No account, proxy address, URL, path, session identifier, raw model name or raw error text is added to diagnostics.

## Evidence and checks

- Production reqwest 0.12.28 and its existing proxy/TLS configuration are retained. A small source patch observes the original hyper-util 0.1.20 matcher rather than independently reconstructing proxy rules. Static review compared the patch with the cached official crate and the pinned reqwest call sites; no routing, authentication, registry-read or TLS-verification change was found.
- Rust portable suite: 82 passed (57 library, 5 deadline/startup, 20 contract); five ignored entry points are excluded by default. This includes real loopback connection refusal and its numeric OS error through the serialized protocol, proxy rejection, malformed TLS handshakes, bounded error chains, deadline snapshots and privacy assertions. These are controlled fixtures, not authenticated Windows acceptance.
- The isolated matcher library harness passed 26 tests, including 3,630 differential routing/authentication combinations against the unchanged upstream matcher. Its four upstream documentation examples do not compile under the standalone harness's different crate name; they are not counted as passing tests. The production reader's own doctest run passes (zero examples).
- Portable C# regression: 936/936 passed. net48/net8 Release builds and FrameworkSmoke compilation are checked for this version. FrameworkSmoke was compiled, not executed on Windows.
- Windows GNU helper crossbuild: 3,098,112 bytes; SHA256 `3391846985719ef81c8013e4a23ece1720b4baa7b2b3750cd0762265c7c69ac0`; compressed payload 1,582,584 bytes. The pre-existing proc-macro-error2 future-compatibility advisory remains.
- Offline dependency notice audit verifies all 214 locked Windows normal/build dependencies and the exact changed-file allowlist against the checksum-verified upstream crate. No original upstream file is omitted. Full license text is preserved.
- Release gates include final commit/version metadata, embedded helper payload and legal-resource byte checks, asInvoker / PerMonitorV2 manifest, source-archive completeness and Library readback hashes. No new visual acceptance is claimed: hover layout and fonts are unchanged.

## Previously executed cloud reachability observation

One explicitly authorized, unauthenticated GET to the fixed official usage endpoint used the production builder in an isolated empty home. It returned HTTP 401 in 0.91 seconds with EnvironmentProxy / Http / Rustls / CA override. No Authorization, Account-ID or Cookie header, response-body read, model request or real login was used. The probe is test-only and ignored by default; it is absent from the release helper. It was not repeated during the final handoff validation.

This proves only that the cloud machine's observed route reached the official endpoint. It does not test the user's Windows network, Schannel, proxy settings, login, subscription or quota response.

## Not verified or changed

No Windows execution, real credential-store access, authenticated service call or actual user network success was tested. No CLI fallback, authentication refresh/writeback, login request, proxy modification, elevated task or TLS bypass was introduced. The model-metadata parser and unverified-timing behavior are unchanged. Historical private evidence remains deleted; no replacement collection was performed.

Earlier results: [0.2.13](VERIFICATION-0.2.13.md), [0.2.12](VERIFICATION-0.2.12.md), [0.2.11](VERIFICATION-0.2.11.md).
