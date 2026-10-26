using System.Diagnostics;
using System.Reflection;
using CodexQuota.Core;
using Microsoft.Win32;

namespace CodexQuota.Tray;

/// <summary>Fixed publisher, opt-out automatic update. Network failure never blocks quota operation.</summary>
internal sealed class AutoUpdateController : IDisposable
{
    private const string Settings = @"Software\CodexQuotaTray";
    private readonly ToolStripMenuItem _automatic = new("自动更新") { CheckOnClick = false };
    private readonly ToolStripMenuItem _check = new("检查更新");
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15000 };
    private readonly CancellationTokenSource _stop = new();
    private readonly string? _cli;
    private readonly Func<Task> _exit;
    private bool _busy, _disposed;
    private DateTimeOffset _nextCheck = DateTimeOffset.MinValue;

    public AutoUpdateController(ToolStripItemCollection menu, string? cli, Func<Task> exit)
    {
        _cli = cli; _exit = exit;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Settings);
            _automatic.Checked = key?.GetValue("AutoUpdateEnabled") is not int enabled || enabled != 0;
            if (key?.GetValue("UpdateLastCheckUtc") is string last && DateTimeOffset.TryParse(last, out var checkedAt))
                _nextCheck = checkedAt.AddHours(6);
        }
        catch { _automatic.Checked = false; _check.ToolTipText = "无法读取自动更新设置，自动更新暂未开启。"; }
        _automatic.Click += (_, _) =>
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(Settings) ?? throw new IOException();
                key.SetValue("AutoUpdateEnabled", _automatic.Checked ? 0 : 1, RegistryValueKind.DWord);
                _automatic.Checked = !_automatic.Checked;
                if (_automatic.Checked) _nextCheck = DateTimeOffset.MinValue;
            }
            catch { MessageBox.Show("无法保存自动更新设置。", "Codex 更新", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        };
        _check.Click += async (_, _) => await CheckAsync(manual: true);
        menu.Add(_automatic); menu.Add(_check);
        _timer.Tick += async (_, _) =>
        {
            if (_automatic.Checked && DateTimeOffset.UtcNow >= _nextCheck) await CheckAsync(manual: false);
        };
        _timer.Start();
    }

    private async Task CheckAsync(bool manual)
    {
        if (_busy || _disposed) return;
        _busy = true; _check.Enabled = false; _check.Text = "正在检查更新…";
        string? staging = null;
        var handedOff = false;
        try
        {
            UpdateTransaction.RequireUnelevated();
            if (!UpdateTrust.IsConfigured) throw new InvalidOperationException("更新签名尚未配置，暂不自动安装。");
            var target = Path.GetFullPath(Application.ExecutablePath);
            UpdateTransaction.NoLinks(target);
            UpdateCleanup.KeepLatestRollback(target);
            var root = Path.GetDirectoryName(target)!;
            staging = Path.Combine(root, UpdateTransaction.Prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            UpdateTransaction.NoLinks(staging);
            using var download = new UpdateDownloadClient(staging);
            var envelope = await download.FetchManifestAsync(_stop.Token);
            var manifest = UpdateManifest.ParseAndVerify(envelope, UpdateTrust.PublicKey, DateTimeOffset.UtcNow);
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            if (!manifest.IsNewerThan(current))
            {
                if (manual) MessageBox.Show("当前已是最新版本。", "Codex 更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var key = Registry.CurrentUser.OpenSubKey(Settings))
                if (key?.GetValue("UpdateFailedVersion") as string == manifest.Version.ToString())
                    throw new InvalidOperationException("该版本上次启动失败，已保留旧版。等待下一版本后自动重试。");
            _check.Text = "正在下载 " + manifest.Version + "…";
            var candidate = await download.FetchAssetAsync(manifest.Version, _stop.Token);
            manifest.VerifyFile(candidate);
            var installCandidate = Path.Combine(staging, "CodexQuotaTray.exe");
            File.Move(candidate, installCandidate);
            candidate = installCandidate;
            var version = FileVersionInfo.GetVersionInfo(candidate);
            if (version.OriginalFilename != "CodexQuotaTray.exe" || !Version.TryParse(version.FileVersion, out var fileVersion) || fileVersion != manifest.Version)
                throw new InvalidDataException("更新文件版本或程序标识不一致。");
            UpdateTransaction.WriteNew(Path.Combine(staging, "update.json"), envelope);
            _stop.Token.ThrowIfCancellationRequested();
            // Opt-out during download cancels automatic application; manual checks intentionally update.
            if (!manual && !_automatic.Checked) return;
            _automatic.Enabled = false; // Commit window: no misleading opt-out while the helper takes ownership.
            using var helper = await UpdateTransaction.PrepareAsync(staging, target, _cli, _stop.Token);
            handedOff = true;
            _timer.Stop();
            _check.Text = "正在自动更新并重启…";
            await _exit();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            _check.ToolTipText = error.Message;
            if (manual && !_disposed) MessageBox.Show("更新未完成，继续使用当前版本。\n" + error.Message,
                "Codex 更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            _nextCheck = DateTimeOffset.UtcNow.AddHours(6);
            try { using var key = Registry.CurrentUser.CreateSubKey(Settings); key?.SetValue("UpdateLastCheckUtc", DateTimeOffset.UtcNow.ToString("O")); } catch { }
            if (!handedOff && staging is not null)
            {
                // Only our fresh transaction directory, never a recursive sweep of user data.
                UpdateCleanup.DeleteKnown(staging);
            }
            _busy = false;
            if (!_disposed) { _automatic.Enabled = true; _check.Enabled = true; _check.Text = "检查更新"; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop(); _timer.Dispose(); _stop.Cancel();
        // Async download may still observe the token; token source is deliberately not disposed here.
    }
}
