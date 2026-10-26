CodexQuotaTray isolated Windows update lifecycle smoke

Run run-smoke.ps1 normally, not as Administrator (Windows .NET Framework 4.8 required).
No downloads, production app startup/shutdown, production registry changes, account access or private production signing key.
All mutations occur under a unique %TEMP%\CodexQuotaTray UpdateSmoke-* directory.
A temporary signing key exists only in the driver process memory; only its public key is written.
The actual production UpdateTransaction.cs and Core validator are linked into two fixture EXEs, versions 0.0.1.0 and 0.0.2.0.
The test-only trust loader and failure recorder replace production public-key/registry adapters.
Tests: healthy update; corrupt candidate rejection before old process exits; locked replacement restores old running app; startup-health failure rolls back; temporary health-marker lock is tolerated; old process shutdown lasting 65 seconds is awaited without abandonment.
Report location is printed as REPORT ...\results.json. Retain it for review.
This verifies Windows replacement/restart logic, not production UI, full app startup, network fetching, policy or antivirus behavior.

Fetch-Update.ps1 is included unchanged for separately authorized PowerShell 5.1 parsing/network smoke; the lifecycle driver never invokes it.
