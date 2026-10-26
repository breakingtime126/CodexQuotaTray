# Latest verification: 0.2.11 read-only prototype (2026-10-12)

## Completed in the cloud

- .NET synthetic/core/integration regression: 900/900 passed in31.88seconds. Includes67 new reader protocol, extraction, malformed/oversized stdout/stderr, cancellation, timeout and owned-child lifecycle checks. Existing compact tooltip tests include the new authentication states.
- Rust synthetic tests:43/43 (22 authentication/store tests,4 HTTP tests,17 quota/transaction contracts). No real login cache or authenticated remote endpoint was used. The transport test uses loopback and fictitious headers only.
- Linux and Windows x64 GNU Rust compilation passed; formatting passed. Rust reports a future-compatibility advisory for the pinned build dependency proc-macro-error2 v2.0.1; the current pinned compiler succeeds. Final helper has only Windows system DLL/API-set imports, no MinGW runtime, Node or OpenSSL sidecar.
- Helper executable:1,208,320bytes; SHA256 `bc6db23057e27d6c43704c5d2cb6da314c9960c75615ef1475949f4249f9f097`. Embedded gzip584,574bytes. Release package metadata and payload are generated from these exact bytes.
- Both .NET target builds and the net48 FrameworkSmoke project compiled with0warnings/errors. FrameworkSmoke includes exact Windows directory/lease/process/mitigation/exit paths using a synthetic child; compilation is not execution.
- Independent static review completed. Fixed stderr cleanup escaping its sanitized result, directory reparse races during extraction, UI-thread file work, remote-path validation ordering, and unsupported encryption classification.
- Restored `asInvoker`, uiAccess=false, PerMonitorV2 and ordinary current-user startup behavior. No scheduled tasks, services, ACL changes or UAC bypass.

## Explicitly not verified

Windows runtime behavior was not executed for CredRead, Schannel, NtCreateFile, native Job ownership, creation mitigation, extraction or process reaping. No user's computer, real credentials, live authenticated quota response, or Desktop/CLI/VS Code login combination was tested for this prototype. Old elevated0.2.10 may need to be exited manually before starting ordinary0.2.11.

The single-file net48 deliverable has stronger native launch protection than the optional net8 development build. Fixed upstream HTTP/storage formats remain a compatibility boundary rather than a supported universal API guarantee. Expired or host-only credentials are reported instead of refreshed or modified.

## Release verification

- Exact embedded helper manifest, decompressed SHA-256 and all embedded license bytes are checked against build inputs before saving. EXE/source/portable ZIP are then downloaded from Library and hash-compared.
- Dependency-license inventory covers170 registry packages including build-time dependencies, the exact GNU/runtime closure, applicable OpenAI notices, and the complete MPL option-ext source. Missing archive license texts are pinned to verified upstream commits or explicit Apache alternatives, documented in the inventory.
- Prior shipped evidence is retained in [0.2.10 verification](VERIFICATION-0.2.10.md).
