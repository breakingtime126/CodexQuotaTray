using CodexQuota.Core;

internal static class AutoResetWakeScheduleTests
{
    public static void Register(Action<string, Action> test)
    {
        var now = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
        test("auto reset wake: early read cleanup crossing deadline preserves exact overdue wake", () =>
        {
            var wake = new AutoResetWakeSchedule();
            var refresh = new RefreshPolicy();
            var due = now.AddSeconds(5);
            wake.Observe(true, due);
            Check.True(refresh.TryBegin(now, RefreshTrigger.Manual));
            wake.ReadStarted(now);
            Check.Null(wake.NextWake(refresh));
            refresh.Complete(ReadStatus.Ready, now.AddSeconds(6));
            // The controller excludes cards already in their final minute, but the planned
            // wake must survive an early check plus delayed process cleanup.
            wake.Observe(true, null);
            Check.Equal(due, wake.Deadline!.Value);
            Check.Equal(due, wake.NextWake(refresh)!.Value);
            Check.True(wake.IsDue(true, now.AddSeconds(6)));
            Check.False(refresh.TryBegin(now.AddSeconds(6), RefreshTrigger.Automatic));
            Check.True(refresh.TryBegin(now.AddSeconds(6), RefreshTrigger.Manual));
            wake.ReadStarted(now.AddSeconds(6));
            Check.Null(wake.Deadline);
            refresh.Complete(ReadStatus.Ready, now.AddSeconds(8));
            Check.Equal(now.AddSeconds(38), wake.NextWake(refresh)!.Value);
        });
        test("auto reset wake: due read failure consumes wake without a busy retry loop", () =>
        {
            var wake = new AutoResetWakeSchedule();
            var refresh = new RefreshPolicy();
            wake.Observe(true, now);
            Check.True(refresh.TryBegin(now, RefreshTrigger.Manual));
            wake.ReadStarted(now);
            refresh.Complete(ReadStatus.Unavailable, now.AddSeconds(2));
            wake.Observe(true, null);
            Check.Null(wake.Deadline);
            Check.False(wake.IsDue(true, now.AddSeconds(2)));
            Check.Equal(now.AddSeconds(32), wake.NextWake(refresh)!.Value);
        });
        test("auto reset wake: cooldown postpones preserved deadline without bypass or spin", () =>
        {
            var wake = new AutoResetWakeSchedule();
            var refresh = new RefreshPolicy();
            wake.Observe(true, now.AddSeconds(5));
            Check.True(refresh.TryBegin(now, RefreshTrigger.Manual));
            wake.ReadStarted(now);
            refresh.Complete(ReadStatus.RateLimited, now.AddSeconds(6));
            Check.Equal(now.AddSeconds(306), wake.NextWake(refresh)!.Value);
            Check.False(refresh.TryBegin(now.AddSeconds(7), RefreshTrigger.Manual));
            Check.Equal(now.AddSeconds(5), wake.Deadline!.Value);
            Check.True(refresh.TryBegin(now.AddSeconds(306), RefreshTrigger.Manual));
            wake.ReadStarted(now.AddSeconds(306));
            Check.Null(wake.Deadline);
        });
        test("auto reset wake: pause and inflight never arm a second read; disable and switch clear", () =>
        {
            var wake = new AutoResetWakeSchedule();
            var refresh = new RefreshPolicy();
            wake.Observe(true, now.AddSeconds(10));
            Check.Null(wake.NextWake(refresh, paused: true));
            Check.Equal(now.AddSeconds(10), wake.Deadline!.Value);
            Check.True(refresh.TryBegin(now, RefreshTrigger.Manual));
            Check.Null(wake.NextWake(refresh));
            wake.Observe(false, now.AddSeconds(5));
            Check.Null(wake.Deadline);
            Check.False(wake.IsDue(false, now.AddSeconds(20)));
            wake.Observe(true, now.AddSeconds(10));
            wake.Reset(); // Source selection/account generation invalidates its old deadline.
            Check.Null(wake.Deadline);
        });
        test("auto reset wake: clock rollback does not consume or move the absolute deadline", () =>
        {
            var wake = new AutoResetWakeSchedule();
            var refresh = new RefreshPolicy();
            wake.Observe(true, now.AddSeconds(10));
            Check.True(refresh.TryBegin(now, RefreshTrigger.Manual));
            wake.ReadStarted(now);
            // Clock changes from now to one minute earlier while cleanup finishes.
            refresh.Complete(ReadStatus.Ready, now.AddMinutes(-1));
            wake.Observe(true, now.AddSeconds(10));
            Check.False(wake.IsDue(true, now.AddMinutes(-1)));
            Check.Equal(now.AddSeconds(10), wake.Deadline!.Value);
            Check.Equal(now.AddSeconds(-30), wake.NextWake(refresh)!.Value);
            wake.ReadStarted(now.AddSeconds(-30));
            Check.Equal(now.AddSeconds(10), wake.Deadline!.Value);
            Check.True(wake.IsDue(true, now.AddSeconds(10)));
        });
        test("auto reset wake: earlier future target wins but later observations never erase pending deadline", () =>
        {
            var wake = new AutoResetWakeSchedule();
            wake.Observe(true, now.AddSeconds(20));
            wake.Observe(true, now.AddSeconds(10));
            wake.Observe(true, now.AddSeconds(30));
            Check.Equal(now.AddSeconds(10), wake.Deadline!.Value);
            wake.ReadStarted(now.AddSeconds(9));
            Check.Equal(now.AddSeconds(10), wake.Deadline!.Value);
            wake.ReadStarted(now.AddSeconds(10));
            Check.Null(wake.Deadline);
        });
    }
}
