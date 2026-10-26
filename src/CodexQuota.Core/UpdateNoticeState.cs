namespace CodexQuota.Core;

public sealed record UpdateNoticeView(string Version, string Text, string ActionText, bool CanInstall);

/// <summary>UI-thread owned update gate. Checking never grants installation permission.</summary>
public sealed class UpdateNoticeState
{
    public string? AvailableVersion { get; private set; }
    public bool Checking { get; private set; }
    public bool Installing { get; private set; }
    public bool Stopped { get; private set; }
    private bool _failed;
    public bool HasAvailableUpdate => AvailableVersion is not null;
    public UpdateNoticeView? Notice => AvailableVersion is not { } version ? null : new(version,
        "发现新版本 " + version, Installing ? "更新中…" : _failed ? "重试" : "更新",
        !Checking && !Installing && !Stopped);
    public bool TryBeginCheck()
    {
        if (Stopped || Checking || Installing) return false;
        Checking = true;
        return true;
    }
    public void CompleteCheck(string? newerVersion)
    {
        if (Stopped || !Checking) return;
        AvailableVersion = newerVersion;
        Checking = false;
        _failed = false;
    }
    // A failed/cancelled check must not discard an already authenticated notice.
    public void CancelCheck() => Checking = false;
    public bool TryBeginInstall(string version)
    {
        if (Stopped || Checking || Installing || AvailableVersion != version) return false;
        Installing = true;
        _failed = false;
        return true;
    }
    public void InstallFailed()
    {
        if (!Installing) return;
        Installing = false;
        _failed = true;
    }
    public void Stop() { Stopped = true; Checking = false; }
}
