using CodexQuota.Core;

internal static class RolloutPollScheduleTests
{
    public static void Register(Action<string, Action> test)
    {
        test("rollout polling: catch-up has a finite fast burst and mandatory yield", () =>
        {
            var schedule = new RolloutPollSchedule();
            for (var cycle = 0; cycle < 3; cycle++)
            {
                for (var i = 0; i < RolloutPollSchedule.MaximumBurst; i++)
                    Check.Equal(150, schedule.NextDelay(true));
                Check.Equal(1000, schedule.NextDelay(true));
            }
        });
        test("rollout polling: idle and incomplete-writer waits reset the burst", () =>
        {
            var schedule = new RolloutPollSchedule();
            Check.Equal(3000, schedule.NextDelay(false));
            for (var i = 0; i < 8; i++) schedule.NextDelay(true);
            Check.Equal(3000, schedule.NextDelay(false));
            Check.Equal(150, schedule.NextDelay(true));
        });
    }
}
