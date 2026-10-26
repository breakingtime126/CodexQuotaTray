namespace CodexQuota.Core;

/// <summary>
/// Owns one read at a time. A source change invalidates all old callbacks before cancellation,
/// and the latest selection can start only after the old read has finished cleanup.
/// </summary>
public sealed class SourceRequestGate : IDisposable
{
    private readonly object _sync = new();
    private long _generation;
    private QuotaDataSource _source;
    private SourceReadLease? _active;
    private bool _switchPending;
    private bool _disposed;
    private bool _readsBlocked;

    public SourceRequestGate(QuotaDataSource source = QuotaDataSource.Auto)
    {
        if (!QuotaDataSourceNames.IsValid(source)) throw new ArgumentOutOfRangeException(nameof(source));
        _source = source;
    }

    public QuotaDataSource Source { get { lock (_sync) return _source; } }
    public bool SwitchPending { get { lock (_sync) return _switchPending; } }
    public bool ReadsBlocked { get { lock (_sync) return _readsBlocked; } }

    // Process-wide for this gate: a source change must not bypass unresolved cleanup.
    public void BlockFurtherReads() { lock (_sync) _readsBlocked = true; }

    public SourceSwitch BeginSwitch(QuotaDataSource source)
    {
        if (!QuotaDataSourceNames.IsValid(source)) throw new ArgumentOutOfRangeException(nameof(source));
        SourceReadLease? previous;
        SourceSwitch change;
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SourceRequestGate));
            ++_generation; // Invalidation must precede synchronous cancellation callbacks.
            _source = source;
            _switchPending = true;
            previous = _active;
            change = new(this, source, _generation, previous?.Completion ?? Task.CompletedTask);
        }
        previous?.Cancel();
        return change;
    }

    /// <summary>Call after awaiting the switch's Cleanup task. Older switches always return false.</summary>
    public bool CompleteSwitch(SourceSwitch change)
    {
        lock (_sync)
        {
            if (_disposed || !ReferenceEquals(change.Owner, this) || change.Generation != _generation ||
                change.Source != _source || !change.Cleanup.IsCompleted || _active is not null) return false;
            _switchPending = false;
            return true;
        }
    }

    public bool TryBeginRead(out SourceReadLease? read)
    {
        lock (_sync)
        {
            read = null;
            if (_disposed || _readsBlocked || _switchPending || _active is not null) return false;
            read = _active = new SourceReadLease(this, _source, _generation);
            return true;
        }
    }

    internal bool TryApply(SourceReadLease read, Action action)
    {
        lock (_sync)
        {
            if (_disposed || _switchPending || !ReferenceEquals(read, _active) ||
                read.Generation != _generation || read.Source != _source) return false;
            action();
            return true;
        }
    }

    internal void Release(SourceReadLease read)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_active, read)) _active = null;
            read.FinishCleanup();
        }
    }

    public void Dispose()
    {
        SourceReadLease? previous;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            ++_generation;
            previous = _active;
        }
        previous?.Cancel();
    }
}

public sealed class SourceSwitch
{
    internal SourceSwitch(SourceRequestGate owner, QuotaDataSource source, long generation, Task cleanup)
    { Owner = owner; Source = source; Generation = generation; Cleanup = cleanup; }
    internal SourceRequestGate Owner { get; }
    internal long Generation { get; }
    public QuotaDataSource Source { get; }
    public Task Cleanup { get; }
}

public sealed class SourceReadLease : IDisposable
{
    private readonly SourceRequestGate _owner;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource<bool> _cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _released;
    internal SourceReadLease(SourceRequestGate owner, QuotaDataSource source, long generation)
    { _owner = owner; Source = source; Generation = generation; Token = _cancel.Token; }
    internal long Generation { get; }
    internal Task Completion => _cleanup.Task;
    public QuotaDataSource Source { get; }
    public CancellationToken Token { get; }
    public bool TryApply(Action action) => _owner.TryApply(this, action);
    internal void Cancel()
    {
        try { _cancel.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { } // A faulty cancellation observer must not undo source invalidation.
    }
    internal void FinishCleanup()
    {
        _cancel.Dispose();
        _cleanup.TrySetResult(true);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) _owner.Release(this);
    }
}
