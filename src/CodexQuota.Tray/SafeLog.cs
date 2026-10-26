using CodexQuota.Core;

namespace CodexQuota.Tray;

internal sealed class SafeLog
{
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTray");
    private readonly object _sync = new();
    public void Record(CliDiagnostic diagnostic) => RecordCode(diagnostic.ToSafeLogCode());
    public void Record(SourceBackendDiagnostic diagnostic) => RecordCode(diagnostic.ToSafeLogCode());
    public void Record(AutoSourceDiagnostic diagnostic) => RecordCode(diagnostic.ToSafeLogCode());
    public void Record(SourceReadRoundDiagnostic diagnostic) => RecordCode(diagnostic.ToSafeLogCode());
    public void Record(SourcePreferenceResult result) => RecordCode(result.ToSafeLogCode());
    public void RecordCleanupUnconfirmed() => RecordCode("BackendCleanupUnconfirmed");
    public void Record(ReadStatus status) => RecordCode(status.ToString());
    public void Record(TrayDiagnostic status) => RecordCode(status.ToString());
    private void RecordCode(string code)
    {
        lock (_sync) RecordLocked(code);
    }
    private void RecordLocked(string code)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "status.log");
            if (File.Exists(path) && new FileInfo(path).Length >= 32 * 1024)
            {
                var previous = Path.Combine(_directory, "status.previous.log");
#if NETFRAMEWORK
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(path, previous);
#else
                File.Move(path, previous, overwrite: true);
#endif
            }
            // Deliberate allowlist. No exceptions, CLI path, email, tokens, or raw JSON.
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {code}\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
