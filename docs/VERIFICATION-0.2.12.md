# Latest verification: 0.2.12 network compatibility correction (2026-10-12)

## Reproduced defect and scope

The shipped 0.2.11 ureq transport did not preserve official Codex networking behavior. A loopback-only probe against its exact ureq 2.12.1 dependency demonstrated ALL_PROXY overriding HTTPS_PROXY, NO_PROXY=* being ignored, disabled SOCKS pre-empting a usable HTTPS proxy, and an HTTPS proxy URL being silently skipped. No credentials or external service calls were involved. These are demonstrated compatibility defects, not proof of the specific private network condition that caused the user's screenshot.

The replacement pins official Codex v0.159.0's reqwest 0.12.28 and proxy matcher hyper-util 0.1.20, delegates proxy selection to that implementation, and restores the official custom-CA priority and TLS backend selection. It does not add PAC/WPAD, SOCKS, bootstrap account routing, or TLS-protocol fallback that the official default quota path itself does not enable. Fixed endpoints, no redirects, credential read-only behavior and bounded product deadlines remain deliberate restrictions.

## Verification record

- C# protocol red/green: the six new transport categories were rejected by the old whitelist, then accepted after the mapping fix. The additional custom-CA category is covered too. Final core regression: 907/907 passed in 31.80 seconds.
- Rust full suite: 55 passed (38 unit +17 contract). One helper fixture is intentionally ignored as a standalone test and is executed in isolated subprocesses by the passing proxy test. The actual production builder/execute path verifies HTTPS_PROXY precedence, NO_PROXY, DNS/connect/proxy/TLS/timeout classification, fixed marker matching, HTTP status preservation and blocked redirects. Valid standard/trusted/CRL certificate bundles build; invalid DER-in-PEM fails closed. A successful custom-CA TLS handshake was not executed.
- Final Windows GNU helper crossbuild passed: 3,047,936 bytes, SHA256 `7769bc846c89ab7f686bd23a4307087283121493f2fc1578b3b52944f3674af7`; compressed payload 1,557,681 bytes. Imports contain only Windows system DLLs/API sets. The existing proc-macro-error2 future-compatibility advisory remains; it does not fail the current pinned compiler.
- Independent static review found and fixed one missing official WebPKI root feature, then reported no remaining blocker. Both WebPKI and native-root features are present alongside the default native TLS backend.
- .NET Release targets and FrameworkSmoke are rebuilt for release. Embedded helper SHA, manifest version, executable version/source commit, asInvoker/PerMonitorV2 manifest, and all license resources are verified before saving. Library readback hashes are compared after saving.
- Dependency notices cover 214 locked Windows normal/build packages and 517 complete notice references, including ring and the WebPKI root-data license; archive hashes and offline generator --check pass. Public source/registry data only; no user log or credential evidence is part of this release.

## Explicitly not verified

No user's computer, real credentials or live authenticated quota endpoint was accessed for this correction. Windows static-proxy discovery, Schannel/native-root behavior, credential-store access and actual user connectivity were not executed on Windows in this cloud environment. Compilation, static review and synthetic loopback behavior do not prove the user's connection will succeed.

Earlier prototype checks and runtime limitations are retained in [0.2.11 verification](VERIFICATION-0.2.11.md).
