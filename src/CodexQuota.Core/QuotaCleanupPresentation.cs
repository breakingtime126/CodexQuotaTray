namespace CodexQuota.Core;

/// <summary>
/// UI-thread-owned presentation for one read's cleanup lifetime. The owner remains the same
/// across source selections; only the request gate may apply quota to the selected generation.
/// This class never releases the request or authorizes a new backend query.
/// </summary>
public sealed class QuotaCleanupPresentation
{
    public const string PendingStatus = "清理待确认，刷新暂停";
    public const string InterruptedStatus = "清理确认已中断，刷新暂停，请退出后重开";
    private object? _owner;
    private BackendCleanupProgress? _latest;
    private bool _pending;
    private bool _interrupted;
    private QuotaDataSource? _origin;

    public bool RefreshPaused => _pending || _interrupted;
    public QuotaDataSource? Origin => RefreshPaused ? _origin : null;
    public string Status => _interrupted ? InterruptedStatus : _pending ? PendingStatus : "";

    public void BeginRead(object owner)
    {
        if (owner is null) throw new ArgumentNullException(nameof(owner));
        if (RefreshPaused) throw new InvalidOperationException("Cleanup still owns the read lifetime.");
        _owner = owner;
        _latest = null;
        _origin = null;
    }

    public bool Observe(object owner, BackendCleanupProgress progress)
    {
        if (!ReferenceEquals(_owner, owner) || _interrupted) return false;
        _latest = progress;
        _pending = progress.State == BackendCleanupState.Pending;
        _origin = _pending ? progress.Source : null;
        return true;
    }

    /// <summary>Call only inside the current generation's request.TryApply callback.</summary>
    public QuotaState ApplyPendingRead(QuotaState previous)
    {
        if (!_pending) return previous;
        return _latest is { Read: { Status: ReadStatus.Ready, Quota: not null } read, ReadAt: { } at }
            ? previous.Apply(read, at)
            : previous with { Status = ReadStatus.Unavailable };
    }

    public bool HasFreshQuota => _pending && _latest is
        { Read: { Status: ReadStatus.Ready, Quota: not null }, ReadAt: not null };

    /// <summary>Cleanup completion must not become the quota's successful-read time.</summary>
    public DateTimeOffset ResultReadAt(object owner, QuotaRead read, QuotaDataSource? source,
        DateTimeOffset fallback) => ReferenceEquals(_owner, owner) && _latest is { ReadAt: { } at } latest &&
        latest.Source == source && ReferenceEquals(latest.Read, read) ? at : fallback;

    public bool Interrupt(object owner, QuotaDataSource? source)
    {
        if (!ReferenceEquals(_owner, owner)) return false;
        _interrupted = true;
        _origin = _origin ?? source;
        return true;
    }

    public bool CompleteRead(object owner)
    {
        if (!ReferenceEquals(_owner, owner)) return false;
        _owner = null;
        _latest = null;
        if (!_interrupted) { _pending = false; _origin = null; }
        return true;
    }

    public string SourceExplanation(QuotaDataSource selected) => "所选策略：" + selected.DisplayName() +
        (Origin is { } source ? "\n清理来源：" + source.DisplayName() : "");
}
