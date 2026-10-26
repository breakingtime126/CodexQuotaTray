namespace CodexQuota.Core;

public enum RefreshTrigger { Automatic, Manual, Hover }

/// <summary>UI-thread state machine: single in-flight read, 30s polling, bounded error backoff.</summary>
public sealed class RefreshPolicy
{
    public static readonly TimeSpan NormalInterval = TimeSpan.FromSeconds(30);
    public bool InFlight { get; private set; }
    public DateTimeOffset NextAutomaticRefresh { get; private set; } = DateTimeOffset.MinValue;
    public DateTimeOffset RateLimitCooldownUntil { get; private set; } = DateTimeOffset.MinValue;
    private int _failures;

    public bool CanRequest(DateTimeOffset now) => !InFlight && now >= RateLimitCooldownUntil;
    public bool TryBegin(DateTimeOffset now, RefreshTrigger trigger)
    {
        if (!CanRequest(now) || (trigger == RefreshTrigger.Automatic && now < NextAutomaticRefresh)) return false;
        InFlight = true;
        return true;
    }
    public void Complete(ReadStatus status, DateTimeOffset now)
    {
        InFlight = false;
        if (status == ReadStatus.Ready)
        {
            _failures = 0;
            RateLimitCooldownUntil = DateTimeOffset.MinValue;
            NextAutomaticRefresh = now + NormalInterval;
            return;
        }
        _failures = Math.Min(_failures + 1, 5);
        var seconds = Math.Min(300, 30 * (1 << (_failures - 1)));
        if (status == ReadStatus.RateLimited)
        {
            seconds = 300;
            RateLimitCooldownUntil = now.AddSeconds(seconds);
        }
        NextAutomaticRefresh = now.AddSeconds(seconds);
    }
}
