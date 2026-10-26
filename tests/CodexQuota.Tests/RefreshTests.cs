using CodexQuota.Core;

static class RefreshTests
{
    private static DateTimeOffset Now => new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    public static void Register(Action<string, Action> test)
    {
        test("hover: first entry refreshes once and staying never retriggers", () =>
        {
            var gate = new HoverRefreshGate();
            Check.False(gate.IsInside);
            Check.True(gate.Enter());
            Check.True(gate.IsInside);
            for (var i = 0; i < 100; i++)
            {
                gate.ObservePointer(true);
                Check.False(gate.Enter());
            }
        });

        test("hover: pointer departure and reentry allow another refresh", () =>
        {
            var gate = new HoverRefreshGate();
            Check.True(gate.Enter());
            gate.ObservePointer(false);
            Check.False(gate.IsInside);
            Check.True(gate.Enter());
            Check.False(gate.Enter());
        });

        test("hover: explicit leave rearms entry and is idempotent", () =>
        {
            var gate = new HoverRefreshGate();
            Check.True(gate.Enter());
            gate.Leave();
            gate.Leave();
            Check.False(gate.IsInside);
            Check.True(gate.Enter());
        });

        test("hover: lost icon bounds are treated as departure", () =>
        {
            var gate = new HoverRefreshGate();
            Check.True(gate.Enter());
            // NativeTrayIcon calls Leave when Shell_NotifyIconGetRect has no bounds.
            gate.Leave();
            Check.False(gate.IsInside);
            Check.True(gate.Enter());
        });

        test("hover: observing inside alone does not consume first entry", () =>
        {
            var gate = new HoverRefreshGate();
            gate.ObservePointer(true);
            Check.True(gate.Enter());
        });

        test("refresh: startup can immediately perform an automatic read", () =>
        {
            var policy = new RefreshPolicy();
            Check.True(policy.CanRequest(Now));
            Check.True(policy.TryBegin(Now, RefreshTrigger.Automatic));
            Check.True(policy.InFlight);
            Check.False(policy.CanRequest(Now));
        });

        test("refresh: success schedules automatic polling exactly 30 seconds later", () =>
        {
            var policy = new RefreshPolicy();
            Check.True(policy.TryBegin(Now, RefreshTrigger.Automatic));
            var completed = Now.AddSeconds(3);
            policy.Complete(ReadStatus.Ready, completed);
            Check.False(policy.InFlight);
            Check.Equal(completed.AddSeconds(30), policy.NextAutomaticRefresh);
            Check.False(policy.TryBegin(completed.AddSeconds(30).AddTicks(-1), RefreshTrigger.Automatic));
            Check.True(policy.TryBegin(completed.AddSeconds(30), RefreshTrigger.Automatic));
        });

        foreach (var initiatingTrigger in Enum.GetValues<RefreshTrigger>())
        {
            var firstTrigger = initiatingTrigger;
            test($"refresh: {firstTrigger} in flight coalesces click, hover, and automatic reads", () =>
            {
                var policy = new RefreshPolicy();
                Check.True(policy.TryBegin(Now, firstTrigger));
                foreach (var other in Enum.GetValues<RefreshTrigger>())
                    Check.False(policy.TryBegin(Now.AddSeconds(1), other));
                Check.True(policy.InFlight);
                Check.False(policy.CanRequest(Now.AddSeconds(1)));
                policy.Complete(ReadStatus.Ready, Now.AddSeconds(2));
                Check.False(policy.InFlight);
                Check.True(policy.TryBegin(Now.AddSeconds(2), RefreshTrigger.Manual));
            });
        }

        test("refresh: manual and hover attempts can bypass ordinary automatic delay", () =>
        {
            foreach (var trigger in new[] { RefreshTrigger.Manual, RefreshTrigger.Hover })
            {
                var policy = new RefreshPolicy();
                Check.True(policy.TryBegin(Now, RefreshTrigger.Automatic));
                policy.Complete(ReadStatus.Unavailable, Now);
                Check.False(policy.TryBegin(Now.AddSeconds(1), RefreshTrigger.Automatic));
                Check.True(policy.CanRequest(Now.AddSeconds(1)));
                Check.True(policy.TryBegin(Now.AddSeconds(1), trigger));
            }
        });

        test("refresh: consecutive ordinary failures back off 30, 60, 120, 240, 300 seconds", () =>
        {
            var policy = new RefreshPolicy();
            var now = Now;
            foreach (var delay in new[] { 30, 60, 120, 240, 300 })
            {
                Check.True(policy.TryBegin(now, RefreshTrigger.Automatic));
                policy.Complete(ReadStatus.Unavailable, now);
                Check.Equal(now.AddSeconds(delay), policy.NextAutomaticRefresh);
                Check.False(policy.TryBegin(now.AddSeconds(delay).AddTicks(-1), RefreshTrigger.Automatic));
                now = now.AddSeconds(delay);
            }
            Check.True(policy.TryBegin(now, RefreshTrigger.Automatic));
        });

        test("refresh: repeated failure backoff stays capped at five minutes", () =>
        {
            var policy = new RefreshPolicy();
            var now = Now;
            for (var failure = 0; failure < 100; failure++)
            {
                Check.True(policy.TryBegin(now, RefreshTrigger.Automatic));
                policy.Complete(ReadStatus.Timeout, now);
                var delay = new[] { 30, 60, 120, 240, 300 }[Math.Min(failure, 4)];
                Check.Equal(now.AddSeconds(delay), policy.NextAutomaticRefresh);
                now = now.AddSeconds(delay);
            }
        });

        test("refresh: different ordinary failure statuses share consecutive backoff", () =>
        {
            var policy = new RefreshPolicy();
            var now = Now;
            var statuses = new[] { ReadStatus.Timeout, ReadStatus.Unavailable, ReadStatus.ProtocolError, ReadStatus.CliMissing };
            for (var i = 0; i < statuses.Length; i++)
            {
                Check.True(policy.TryBegin(now, RefreshTrigger.Automatic));
                policy.Complete(statuses[i], now);
                Check.Equal(now.AddSeconds(30 * (1 << i)), policy.NextAutomaticRefresh);
                now = policy.NextAutomaticRefresh;
            }
        });

        test("refresh: successful read resets ordinary failure streak", () =>
        {
            var policy = new RefreshPolicy();
            Check.True(policy.TryBegin(Now, RefreshTrigger.Manual));
            policy.Complete(ReadStatus.Timeout, Now);
            Check.True(policy.TryBegin(Now.AddSeconds(1), RefreshTrigger.Manual));
            policy.Complete(ReadStatus.Unavailable, Now.AddSeconds(1));
            Check.Equal(Now.AddSeconds(61), policy.NextAutomaticRefresh);
            Check.True(policy.TryBegin(Now.AddSeconds(2), RefreshTrigger.Hover));
            policy.Complete(ReadStatus.Ready, Now.AddSeconds(2));
            Check.Equal(Now.AddSeconds(32), policy.NextAutomaticRefresh);
            Check.True(policy.TryBegin(Now.AddSeconds(32), RefreshTrigger.Automatic));
            policy.Complete(ReadStatus.Timeout, Now.AddSeconds(32));
            Check.Equal(Now.AddSeconds(62), policy.NextAutomaticRefresh);
        });

        test("refresh: rate limit imposes a five-minute cooldown on every trigger", () =>
        {
            foreach (var trigger in Enum.GetValues<RefreshTrigger>())
            {
                var policy = new RefreshPolicy();
                Check.True(policy.TryBegin(Now, RefreshTrigger.Manual));
                policy.Complete(ReadStatus.RateLimited, Now);
                Check.False(policy.InFlight);
                Check.Equal<DateTimeOffset?>(Now.AddMinutes(5), policy.RateLimitCooldownUntil);
                Check.Equal(Now.AddMinutes(5), policy.NextAutomaticRefresh);
                foreach (var blockedAt in new[] { Now, Now.AddSeconds(30), Now.AddMinutes(5).AddTicks(-1) })
                {
                    Check.False(policy.CanRequest(blockedAt));
                    Check.False(policy.TryBegin(blockedAt, trigger));
                }
                Check.True(policy.CanRequest(Now.AddMinutes(5)));
                Check.True(policy.TryBegin(Now.AddMinutes(5), trigger));
            }
        });

        test("refresh: blocked attempts do not extend rate-limit cooldown", () =>
        {
            var policy = new RefreshPolicy();
            Check.True(policy.TryBegin(Now, RefreshTrigger.Automatic));
            policy.Complete(ReadStatus.RateLimited, Now);
            for (var second = 1; second < 300; second++)
                foreach (var trigger in Enum.GetValues<RefreshTrigger>())
                    Check.False(policy.TryBegin(Now.AddSeconds(second), trigger));
            Check.Equal<DateTimeOffset?>(Now.AddMinutes(5), policy.RateLimitCooldownUntil);
            Check.Equal(Now.AddMinutes(5), policy.NextAutomaticRefresh);
            Check.True(policy.TryBegin(Now.AddMinutes(5), RefreshTrigger.Hover));
        });

        test("refresh: repeated rate-limit response restarts five-minute cooldown", () =>
        {
            var policy = new RefreshPolicy();
            Check.True(policy.TryBegin(Now, RefreshTrigger.Automatic));
            policy.Complete(ReadStatus.RateLimited, Now);
            Check.True(policy.TryBegin(Now.AddMinutes(5), RefreshTrigger.Manual));
            policy.Complete(ReadStatus.RateLimited, Now.AddMinutes(5).AddSeconds(2));
            Check.Equal<DateTimeOffset?>(Now.AddMinutes(10).AddSeconds(2), policy.RateLimitCooldownUntil);
            Check.False(policy.TryBegin(Now.AddMinutes(10), RefreshTrigger.Hover));
            Check.True(policy.TryBegin(Now.AddMinutes(10).AddSeconds(2), RefreshTrigger.Automatic));
        });

        test("refresh: success after cooldown restores normal automatic cadence", () =>
        {
            var policy = new RefreshPolicy();
            Check.True(policy.TryBegin(Now, RefreshTrigger.Manual));
            policy.Complete(ReadStatus.Timeout, Now);
            Check.True(policy.TryBegin(Now.AddSeconds(1), RefreshTrigger.Manual));
            policy.Complete(ReadStatus.RateLimited, Now.AddSeconds(1));
            var resumed = Now.AddMinutes(5).AddSeconds(1);
            Check.True(policy.TryBegin(resumed, RefreshTrigger.Automatic));
            policy.Complete(ReadStatus.Ready, resumed);
            Check.True(policy.CanRequest(resumed));
            Check.Equal(resumed.AddSeconds(30), policy.NextAutomaticRefresh);
            Check.True(policy.TryBegin(resumed.AddSeconds(30), RefreshTrigger.Automatic));
            policy.Complete(ReadStatus.Unavailable, resumed.AddSeconds(30));
            Check.Equal(resumed.AddSeconds(60), policy.NextAutomaticRefresh);
        });

        test("refresh: entry consumed during in-flight work waits for leave and reentry", () =>
        {
            var policy = new RefreshPolicy();
            var gate = new HoverRefreshGate();
            Check.True(policy.TryBegin(Now, RefreshTrigger.Manual));
            Check.True(gate.Enter());
            Check.False(policy.TryBegin(Now, RefreshTrigger.Hover));
            policy.Complete(ReadStatus.Ready, Now.AddSeconds(1));
            Check.False(gate.Enter());
            gate.ObservePointer(false);
            Check.True(gate.Enter());
            Check.True(policy.TryBegin(Now.AddSeconds(2), RefreshTrigger.Hover));
        });
    }
}
