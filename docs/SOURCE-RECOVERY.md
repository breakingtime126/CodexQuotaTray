# 0.2.32 source provenance

This release is based on the recovered **0.2.30 source archive**, not on a claim that the lost 0.2.31 Git tree was restored.

- Archive: `CodexQuotaTray-0.2.30-source.zip`, 4,732,059 bytes.
- SHA-256: `b9c654d01f82e3650b61a8fe7e3131f2ae274f97c626982f385e1712867b2140`.
- Archive Git comment: `604e1b8269db9b0fdad6b07f08fd1d5b9dd79bb0`.
- All 483 source files were recovered byte-for-byte. The archive contains no Git objects.
- New local recovery baseline: `c259f7a`, including the archived upstream Cargo.lock that its nested ignore rules otherwise hide.

The recovered baseline includes the 0.2.29 data-driven quota windows, compact public-reset rows and unread forecast dots, incremental TPS reader optimizations, and 0.2.30 sanitized diagnostics. Those quota/TPS/rendering implementations are retained.

The 0.2.31 compatibility experiment's exact source is unavailable. Its WinHTTP transport is not reconstructed or retained: at the user's explicit request 0.2.32 uses only the installed PowerShell 7 and a correlated local JSON result for this public API. Closed transport/error diagnostics and owned-process cancellation are implemented and tested in this source tree, without claiming byte-equivalence to 0.2.31.

The PowerShell producer began from the separately verified small launcher package `CqtPowerShellHandoff-frozen-20261022.zip` (13,070 bytes, SHA-256 `4a0b107d2609bdd25dfb161b6ca3b4cde17cdbf1f539a38f3d75b60dfa92cbca`). The original launcher is not embedded or executed by the tray. Production changes add conditional-cache metadata, explicit anonymous credential safeguards, bounded strict parsing, and closed failure labels; they do not change proxy routes, TLS validation or execution policy.
