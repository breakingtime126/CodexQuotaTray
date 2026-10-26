# 0.2.32 verification scope

This release changes the optional public reset feed transport only. `TrayContext`, quota parsing/selection, hover/icon rendering, TPS readers, cleanup coordinator and process ownership implementation are unchanged from the recovered 0.2.30 source baseline. No WinHTTP package or alternate direct public-API client is shipped. The internal mock message-handler constructor cannot be selected by the production UI.

## Repeatable offline checks

- Build and run `tests/CodexQuota.Tests` for the portable regression suite. `--filter "reset"` selects the feed, UI, diagnostics, local protocol and fake-owned-process cases.
- Run `scripts/test-reset-powershell.ps1` with an installed PowerShell 7. The script extracts producer functions without running its network entry point. It covers strict JSON/duplicate keys, request and output bounds, conditional cache fields, credential rejection, safe failure labels, atomic first creation/replacement and temporary-file ownership.
- Run `scripts/test-reset-producer-core.ps1 -CoreDll <built-net8-Core.dll>` with PowerShell 7. This passes actual producer envelopes into the built Core parser and presentation, including historical latest-only data, 304, 429 and a sanitized body-read failure.
- Build the Windows Forms net48 and net8 targets and `tests/CodexQuota.FrameworkSmoke`. Cloud builds are not Windows runtime execution.
- Verify the net48 EXE's x64 GUI headers, 0.2.32 version/final commit, embedded Core/dependency bytes, embedded producer bytes, manifest and original non-elevated execution level. Verify every source archive entry against the committed tree.

## Runtime boundaries

The production runner reads only a fresh matching request ID after the one owned child exits with a compatible status. Exit 2, invalid/stale/oversized JSON and any startup/write/request failure cannot reuse an older success. Cancellation retains ownership until the root, owned descendants and output drains finish; a second request is rejected while cleanup is pending. Its temporary request is deleted only if that operation created it.

The normal installed PowerShell 7 is used without changing execution policy, TLS, proxy configuration, environment variables or Windows permissions. Both origin and proxy authentication remain disabled, and credential-bearing default proxies fail closed. The same-directory output write is never redirected or retried through an alternate file-writing method after denial.

The existing 12-second cancellation budget includes process startup and body handling; it is not a promise that an uninterruptible OS operation ends at an exact wall-clock deadline. The outer client may complete while owned cleanup safely finishes in the background; no concurrent child is then started.

A separately supplied tiny launcher was successfully run by the user on Windows, proving its first EXE→PowerShell→HTTP 200→JSON chain. This does not prove the final tray's Windows Job ownership or repeated replacement in that user's directory. Linux offline replacement tests do not establish the cause of the earlier Windows access-denied result. These remain explicit native validation limits, not claimed fixes.
