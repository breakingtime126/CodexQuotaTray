# Latest verification: 0.2.13 dotenv bootstrap and timing reliability (2026-10-12)

## Demonstrated corrections

### Selected-home network configuration

Official Codex loads CODEX_HOME/.env before constructing its clients. The embedded reader omitted that bootstrap. A credential-free loopback regression using the actual production reqwest builder failed against the old path: the proxy present only in .env was not contacted and the controlled direct DNS path was attempted. The new loader causes that same client to issue CONNECT to the fake loopback proxy. No external service or real credentials were used.

The existing official Linux codex-cli 0.159.2 was also executed in three clean temporary-home runs. With proxies only in .env, its bootstrap traffic contacted the fake proxy; with conflicting inherited/file settings, only the file-specified proxy was contacted; with no .env, inherited proxy settings were used. The proxy returned 502 and forwarded nothing. initialize reported the isolated home, account/read returned null, and rateLimits/read returned -32600 authentication required. Thus the official bootstrap behavior is executed evidence; authenticated quota transport is not tested. No model request or actual cloud/user login was used.

The new reader pins one auth home and exports only network variables, preserving official dotenvy syntax, sequential overrides and Windows name casing. Unsupported or invalid input fails closed without partial export. Home/auth/token/routing keys cannot change the source. Independent review found and fixed dangling-symlink handling; no remaining blocker. Parsing and quota work retain one shared 30-second deadline.

This establishes a missing compatibility behavior, not proof that it is the only cause of the user's ConnectFailure. Existing reqwest system-proxy/native TLS features were already compiled into 0.2.12; this release does not claim to restore features that were absent only in speculation.

### Unverified rollout timings

Official source can emit lifecycle started/completed notifications after the item has already completed, and completion timestamps include asynchronous lifecycle processing. Rollout does not preserve an independent full generation timeline. A synthetic file through the production reader proves 1,000 output tokens divided by a 250 ms notification interval produced 4,000 tok/s before the change.

Current rollout samples now return null numeric speed/generation duration, retain model and effort, and display 计时未核验. No rate cap, arbitrary minimum duration or tool-inclusive turn timing is substituted. Historical numerical claims based on the same interval are superseded. The exact record behind the user's screenshot was not reacquired or identified. See [timing evidence](OUTPUT-SPEED.md).

## Checks

- C# portable regression: 913/913 passed; final net8/net48 Release targets and FrameworkSmoke compilation passed, with zero build warnings/errors.
- Rust: 72 passed (51 library, 4 deadline/startup, 17 contract); four isolated fixture tests are invoked by passing parent tests. The closed-envelope test was rerun after adding every network category. Formatting passes.
- Windows GNU helper crossbuild: 3,085,312 bytes, SHA256 `e11ff6de5d84b69353ec23972772cbaaeeeec7a0467f63c386e7666581333aa5`; compressed payload 1,577,384 bytes. Imports are Windows system DLLs/API sets. Existing proc-macro-error2 future-compatibility advisory remains.
- Production renderer generated light/dark/high-contrast previews at 96/120/144/192 DPI. No measured text overflow (intentional long-model ellipsis excluded); compact Plus/Pro and the unverified timing row were inspected. These are synthetic Linux-font renders, not Windows captures.
- Dependency notices cover the same 214 locked Windows normal/build packages. No new runtime dependency; adapted dotenvy parser includes its complete MIT license and attribution. The reference crate is test-only. Offline archive-hash and notice-generation checks pass.
- Release validation: final executable version/commit, embedded helper hash, asInvoker/PerMonitorV2 manifest and all license resources are byte-verified. Saved EXE/source/portable ZIP are materialized and hash-compared.

## Not verified

No Windows execution, real credential-store access, authenticated service call, Schannel/system-proxy behavior or actual user network success was tested. The old CLI path is not enabled as a fallback. No credential refresh/writeback, proxy modification, login request or elevated task was introduced. Prior temporary private evidence remains deleted; only synthetic fixtures/public source are in this release.

Earlier results: [0.2.12](VERIFICATION-0.2.12.md), [0.2.11](VERIFICATION-0.2.11.md).
