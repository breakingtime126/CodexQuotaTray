namespace CodexQuota.Core;

/// <summary>UI-thread one-shot wake policy; preserves deadlines across reads and process cleanup.
/// A due read consumes the extra wake regardless of outcome, leaving retries to normal polling.</summary>
public sealed class AutoResetWakeSchedule
{
    public DateTimeOffset? Deadline { get; private set; }
    public void Reset() => Deadline = null;
    public void Observe(bool enabled, DateTimeOffset? futureDeadline)
    {
        if (!enabled) Reset();
        else if (futureDeadline is { } due && (Deadline is null || due < Deadline.Value)) Deadline = due;
    }
    public bool IsDue(bool enabled, DateTimeOffset now) => enabled && Deadline is { } due && now >= due;
    public void ReadStarted(DateTimeOffset now)
    {
        // A read begun before the deadline may finish its backend check before it, but spend
        // enough time cleaning up to cross it. Such a read must not consume the scheduled wake.
        if (Deadline is { } due && now >= due) Reset();
    }
    public DateTimeOffset? NextWake(RefreshPolicy refresh, bool paused = false)
    {
        if (paused || refresh.InFlight) return null;
        var due = refresh.NextAutomaticRefresh;
        if (Deadline is { } cardDue && cardDue < due) due = cardDue;
        // Applies to overdue deadlines too: never spin or bypass a backend cooldown.
        if (due < refresh.RateLimitCooldownUntil) due = refresh.RateLimitCooldownUntil;
        return due;
    }
}
