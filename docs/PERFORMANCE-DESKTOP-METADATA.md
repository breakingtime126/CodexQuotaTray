# Desktop quota queries: metadata-only executable checks

The tool no longer actively invokes WinVerifyTrust / Authenticode for Desktop
quota queries. The native trust verifier, certificate publisher extraction and
signature-success flag were removed, not cached or skipped conditionally.
Current runtime diagnostics use RegisteredPackageMetadata. Legacy enum values
remain readable for older diagnostics but are not produced as current evidence.

Retained checks: current-user Store package registration and origin, package
name/publisher/version/architecture manifest identity, canonical bounded runtime
paths, no reparse traversal, x64 executable PE header, image size and matching
available PE versions, read-only file leases denying write/delete, launch-time
registration/metadata revalidation, cancellation and elapsed-time budgets.

This is a deliberate reduction in the tool's executable-authenticity checks.
Matching package registration, size and version is NOT proof that an executable
was signed by OpenAI or has unmodified contents. Same-size/same-version modified
content is not detected by this metadata path. No Windows policy, protection or
system signature enforcement setting is changed.

Automatic update manifest RSA/SHA-256 signature verification and downloaded
package SHA-256/size validation remain unchanged. Their existing tampered-signature,
wrong-key, modified-payload and modified-file rejection tests still run.

Synthetic production-helper tests use virtual 1 KiB, 373,698,352-byte and 512 MiB
images whose bodies cannot be read. Each performs only 16 explicit PE-header read
bytes, no whole-image hashing and no active trust verification. Reflection also
asserts the Core assembly contains neither the removed trust verifier type nor
any wintrust.dll import. Version metadata calls and file/path system APIs remain;
this is not a measurement of total OS I/O or Windows whole-process CPU reduction.

The TPS discovery cadence change is described in PERFORMANCE-TPS-DISCOVERY.md.
This cloud candidate has not itself been installed or published.
