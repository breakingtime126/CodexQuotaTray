using System.Text.Json;
using CodexQuota.Core;

namespace CodexQuota.Tray;

internal sealed class UpdateHealth : IDisposable
{
    private readonly System.Windows.Forms.Timer? _timer;
    internal UpdateHealth(TrayContext context, string? directory, string? nonce)
    {
        if (directory is null || nonce is null) return;
        _timer = new System.Windows.Forms.Timer { Interval = 250 };
        _timer.Tick += (_, _) =>
        {
            if (!context.IsTrayReady) return;
            _timer.Stop();
            try { UpdateTransaction.WriteNew(Path.Combine(directory, "healthy"), System.Text.Encoding.UTF8.GetBytes(nonce)); }
            catch { /* Helper treats missing acknowledgement as failure and restores old version. */ }
        };
        _timer.Start();
    }
    internal static bool Validate(string directory, string nonce)
    {
        try
        {
            directory = Path.GetFullPath(directory); UpdateTransaction.NoLinks(directory);
            var name = Path.GetFileName(directory);
            if (!name.StartsWith(UpdateTransaction.Prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(name.Substring(UpdateTransaction.Prefix.Length), "N", out _) ||
                !Guid.TryParseExact(nonce, "N", out _)) return false;
            var request = JsonSerializer.Deserialize<UpdateRequest>(UpdateTransaction.ReadBounded(Path.Combine(directory, "request.json"), 8192));
            if (request is null || request.Nonce != nonce) return false;
            var executable = Path.GetFullPath(Application.ExecutablePath);
            if (!string.Equals(executable, Path.Combine(Path.GetDirectoryName(directory)!, request.TargetName), StringComparison.OrdinalIgnoreCase)) return false;
            var manifest = UpdateManifest.ParseAndVerify(UpdateTransaction.ReadBounded(Path.Combine(directory, "update.json"), 32768), UpdateTrust.PublicKey, DateTimeOffset.UtcNow);
            manifest.VerifyFile(executable);
            return true;
        }
        catch { return false; }
    }
    public void Dispose() { _timer?.Stop(); _timer?.Dispose(); }
}
