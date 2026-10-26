using System.Diagnostics;
using System.Reflection;
using CodexQuota.Core;
using Microsoft.Win32;

namespace CodexQuota.Tray;

/// <summary>Fixed publisher, opt-out automatic checks; installation requires an explicit click. Network failure never blocks quota operation.</summary>
internal sealed class AutoUpdateController : IDisposable
{
    private const string Settings = @"Software\CodexQuotaTray";
    private readonly ToolStripMenuItem _automatic = new("自动检查更新") { CheckOnClick = false };
    private readonly ToolStripMenuItem _check = new("检查更新");
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15000 };
    private readonly CancellationTokenSource _stop = new();
    private readonly string? _cli;
    private readonly Func<Task> _exit;
    private bool _disposed;
    private readonly UpdateNoticeState _state = new();
    private byte[]? _availableEnvelope;
    public event Action? StateChanged;
    public UpdateNoticeView? Notice => _state.Notice;
    public bool HasAvailableUpdate => _state.HasAvailableUpdate;
    private void Changed() { if (!_disposed) StateChanged?.Invoke(); }
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
        catch { _automatic.Checked = false; _check.ToolTipText = "无法读取更新设置，自动检查暂未开启。"; }
        _automatic.Click += (_, _) =>
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(Settings) ?? throw new IOException();
                key.SetValue("AutoUpdateEnabled", _automatic.Checked ? 0 : 1, RegistryValueKind.DWord);
                _automatic.Checked = !_automatic.Checked;
                if (_automatic.Checked) _nextCheck = DateTimeOffset.MinValue;
            }
            catch { MessageBox.Show("无法保存自动检查设置。", "Codex 更新", MessageBoxButtons.OK, MessageBoxIcon.Information); }
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
        if (!_state.TryBeginCheck()) return;
        _check.Enabled = false; _check.Text = "正在检查更新…"; Changed();
        string? staging = null;
        try
        {
            if (!UpdateTrust.IsConfigured) throw new InvalidOperationException("更新签名尚未配置。");
            // Manifest-only scratch directory: checks never stage an executable or invoke the helper.
            staging = Path.Combine(Path.GetTempPath(), UpdateTransaction.Prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            UpdateTransaction.NoLinks(staging);
            using var download = new UpdateDownloadClient(staging);
            var envelope = await download.FetchManifestAsync(_stop.Token);
            var manifest = UpdateManifest.ParseAndVerify(envelope, UpdateTrust.PublicKey, DateTimeOffset.UtcNow);
            _stop.Token.ThrowIfCancellationRequested();
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            var newer = manifest.IsNewerThan(current);
            _availableEnvelope = newer ? envelope : null;
            _state.CompleteCheck(newer ? manifest.Version.ToString() : null);
            _check.ToolTipText = newer ? "发现新版本，请在悬浮窗口点击更新。" : "当前已是最新版本。";
            if (manual && !_disposed) MessageBox.Show(_check.ToolTipText, "Codex 更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            _check.ToolTipText = error.Message;
            if (manual && !_disposed) MessageBox.Show("检查未完成，继续使用当前版本。\n" + error.Message,
                "Codex 更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            _state.CancelCheck();
            _nextCheck = DateTimeOffset.UtcNow.AddHours(6);
            try { using var key = Registry.CurrentUser.CreateSubKey(Settings); key?.SetValue("UpdateLastCheckUtc", DateTimeOffset.UtcNow.ToString("O")); } catch { }
            if (staging is not null) UpdateCleanup.DeleteKnown(staging);
            if (!_disposed) { _check.Enabled = true; _check.Text = "检查更新"; Changed(); }
        }
    }

    // The hover action is the only caller. Version identity prevents a stale mouse-down
    // from installing a different release after the notice has changed.
    public async Task InstallAsync(string requestedVersion)
    {
        if (_availableEnvelope is not { } envelope || !_state.TryBeginInstall(requestedVersion)) return;
        _check.Enabled = false; _automatic.Enabled = false; Changed();
        string? staging = null;
        var handedOff = false;
        try
        {
            UpdateTransaction.RequireUnelevated();
            var manifest = UpdateManifest.ParseAndVerify(envelope, UpdateTrust.PublicKey, DateTimeOffset.UtcNow);
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            if (!manifest.IsNewerThan(current) || manifest.Version.ToString() != requestedVersion)
                throw new InvalidOperationException("版本信息已变更，请重新检查更新。");
            using (var key = Registry.CurrentUser.OpenSubKey(Settings))
                if (key?.GetValue("UpdateFailedVersion") as string == requestedVersion)
                    throw new InvalidOperationException("该版本上次启动失败，已保留旧版。请等待下一版本。");
            var target = Path.GetFullPath(Application.ExecutablePath);
            UpdateTransaction.NoLinks(target);
            UpdateCleanup.KeepLatestRollback(target);
            staging = Path.Combine(Path.GetDirectoryName(target)!, UpdateTransaction.Prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            UpdateTransaction.NoLinks(staging);
            using var download = new UpdateDownloadClient(staging);
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
            using var helper = await UpdateTransaction.PrepareAsync(staging, target, _cli, _stop.Token);
            handedOff = true;
            _timer.Stop();
            await _exit();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            _check.ToolTipText = error.Message;
            if (!_disposed) MessageBox.Show("更新未完成，继续使用当前版本。可点击重试。\n" + error.Message,
                "Codex 更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            if (!handedOff)
            {
                if (staging is not null) UpdateCleanup.DeleteKnown(staging);
                _state.InstallFailed();
                if (!_disposed) { _automatic.Enabled = true; _check.Enabled = true; Changed(); }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _state.Stop(); _timer.Stop(); _timer.Dispose(); _stop.Cancel();
        // Async download may still observe the token; token source is deliberately not disposed here.
    }
}
