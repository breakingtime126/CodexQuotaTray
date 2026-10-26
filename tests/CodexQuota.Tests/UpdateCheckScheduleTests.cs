using CodexQuota.Core;

internal static class UpdateCheckScheduleTests
{
    public static void Register(Action<string, Action> test)
    {
        test("update cadence: startup remains 15 seconds and periodic attempts are ten minutes apart", () =>
        {
            Check.True(UpdateCheckSchedule.IsAutomaticEnabled(null));
            Check.True(UpdateCheckSchedule.IsAutomaticEnabled(1));
            Check.False(UpdateCheckSchedule.IsAutomaticEnabled(0));
            Check.Equal(15000, UpdateCheckSchedule.StartupDelayMilliseconds);
            Check.Equal(TimeSpan.FromMinutes(10), UpdateCheckSchedule.Interval);
            var now = new DateTimeOffset(2026, 10, 26, 11, 0, 0, TimeSpan.Zero);
            Check.False(UpdateCheckSchedule.IsDue(false, now, DateTimeOffset.MinValue));
            Check.True(UpdateCheckSchedule.IsDue(true, now, DateTimeOffset.MinValue));
            Check.False(UpdateCheckSchedule.IsDue(true, now.AddMinutes(9), UpdateCheckSchedule.AfterCheck(now)));
            Check.True(UpdateCheckSchedule.IsDue(true, now.AddMinutes(10), UpdateCheckSchedule.AfterCheck(now)));
            Check.Equal(now.AddMinutes(10), UpdateCheckSchedule.AfterCheck(now));
            Check.Equal(now.AddMinutes(20), UpdateCheckSchedule.AfterCheck(UpdateCheckSchedule.AfterCheck(now)));
        });
    }
}
