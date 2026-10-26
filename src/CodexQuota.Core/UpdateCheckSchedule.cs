namespace CodexQuota.Core;

/// <summary>Automatic checks start shortly after launch and repeat ten minutes after each completed attempt.</summary>
public static class UpdateCheckSchedule
{
    public const int StartupDelayMilliseconds = 15000;
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    public static bool IsAutomaticEnabled(object? storedValue) => storedValue is not int enabled || enabled != 0;
    public static bool IsDue(bool automaticEnabled, DateTimeOffset now, DateTimeOffset nextCheck) =>
        automaticEnabled && now >= nextCheck;
    public static DateTimeOffset AfterCheck(DateTimeOffset completedAt) => completedAt.Add(Interval);
}
