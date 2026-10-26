namespace CodexQuota.Core;

/// <summary>Independent UI-thread schedule: approximately five minutes, jitter, bounded error backoff.</summary>
public sealed class ResetFeedPollPolicy
{
    public static readonly TimeSpan NormalInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromHours(1);
    private readonly Func<double> _nextRandom;
    private int _failures;
    public bool InFlight { get; private set; }
    public DateTimeOffset NextPollAt { get; private set; } = DateTimeOffset.MinValue;

    public ResetFeedPollPolicy(Func<double>? nextRandom = null)
    {
        var random = new Random();
        _nextRandom = nextRandom ?? random.NextDouble;
    }

    public bool TryBegin(DateTimeOffset now)
    {
        if (InFlight || now < NextPollAt) return false;
        InFlight = true;
        return true;
    }

    public void Complete(ResetFeedReadResult result, DateTimeOffset now)
    {
        InFlight = false;
        _failures = result.IsSuccess ? 0 : Math.Min(_failures + 1, 5);
        var seconds = result.IsSuccess ? NormalInterval.TotalSeconds :
            Math.Min(MaximumBackoff.TotalSeconds, NormalInterval.TotalSeconds * (1 << (_failures - 1)));
        var random = _nextRandom();
        if (double.IsNaN(random) || double.IsInfinity(random)) random = .5;
        random = Math.Max(0, Math.Min(1, random));
        var interval = TimeSpan.FromSeconds(Math.Min(MaximumBackoff.TotalSeconds, seconds * (.9 + random * .2)));
        if (result.Status == ResetFeedReadStatus.RateLimited && result.RetryAfter is { } retry && retry > interval)
            interval = retry;
        NextPollAt = interval > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + interval;
    }
}
