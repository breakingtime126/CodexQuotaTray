namespace CodexQuota.Core;

/// <summary>Fast bounded catch-up only while the reader can make actual progress.</summary>
public sealed class RolloutPollSchedule
{
    public const int IdleMilliseconds = 3000;
    public const int CatchUpMilliseconds = 150;
    public const int YieldMilliseconds = 1000;
    public const int MaximumBurst = 8;
    private int burst;

    public int NextDelay(bool canCatchUp)
    {
        if (!canCatchUp) { burst = 0; return IdleMilliseconds; }
        if (burst++ < MaximumBurst) return CatchUpMilliseconds;
        burst = 0;
        return YieldMilliseconds;
    }
}
