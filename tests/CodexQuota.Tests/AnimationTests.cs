using CodexQuota.Core;

static class AnimationTests
{
    public static void Register(Action<string, Action> test)
    {
        test("animation: idle has no active frames or delay", () =>
        {
            var animation = new TrayAnimation();
            var frame = animation.Sample(97, false, 0);
            Check.False(frame.IsActive); Check.Equal(0, frame.NextDelayMilliseconds);
            Check.Equal(97d, frame.RingPercent); Check.True(double.IsNaN(frame.SweepPhase));
        });
        test("animation: success sweep is finite and never fills quota to 100", () =>
        {
            var animation = new TrayAnimation();
            Check.True(animation.Start(ReadStatus.Ready, 68, 68, false, 1000, true));
            for (var t = 1000; t < 1420; t++)
            {
                var frame = animation.Sample(68, false, t);
                Check.True(frame.IsActive); Check.Equal(68d, frame.RingPercent);
                Check.True(frame.SweepPhase >= 0 && frame.SweepPhase < 1);
                Check.True(frame.NextDelayMilliseconds >= 1 && frame.NextDelayMilliseconds <= 67);
            }
            var stopped = animation.Sample(68, false, 1420);
            Check.False(stopped.IsActive); Check.Equal(0, stopped.NextDelayMilliseconds);
        });
        test("animation: frames are quantized to bounded low frame rate", () =>
        {
            var animation = new TrayAnimation();
            animation.Start(ReadStatus.Ready, 68, 68, false, 0, true);
            var frames = Enumerable.Range(0, 420).Select(t => animation.Sample(68, false, t).Key).Distinct().ToArray();
            Check.True(frames.Length <= 7);
        });
        test("animation: quota transition is monotonic and settles at true value", () =>
        {
            var animation = new TrayAnimation();
            animation.Start(ReadStatus.Ready, 97, 68, false, 0, true);
            double last = 97;
            foreach (var t in new[] { 0, 67, 134, 201, 268, 335, 402 })
            {
                var value = animation.Sample(68, false, t).RingPercent!.Value;
                Check.True(value >= 68 && value <= last); last = value;
            }
            Check.Equal(68d, animation.Sample(68, false, 268).RingPercent);
        });
        test("animation: initial reading does not fabricate a zero-to-full transition", () =>
        {
            var animation = new TrayAnimation();
            animation.Start(ReadStatus.Ready, null, 97, false, 0, true);
            Check.Equal(97d, animation.Sample(97, false, 0).RingPercent);
        });
        test("animation: errors, unknown and stale cancel success effects", () =>
        {
            foreach (var status in Enum.GetValues<ReadStatus>().Where(status => status != ReadStatus.Ready))
            {
                var animation = new TrayAnimation();
                animation.Start(ReadStatus.Ready, 68, 68, false, 0, true);
                Check.False(animation.Start(status, 68, 68, true, 50, true));
                Check.False(animation.Sample(68, true, 67).IsActive);
            }
            var unknown = new TrayAnimation();
            Check.False(unknown.Start(ReadStatus.Ready, 68, null, false, 0, true));
            Check.False(unknown.Start(ReadStatus.Ready, 68, 68, true, 0, true));
        });
        test("animation: reduced motion disables all frames", () =>
        {
            var animation = new TrayAnimation();
            Check.False(animation.Start(ReadStatus.Ready, 97, 68, false, 0, false));
            Check.Equal(68d, animation.Sample(68, false, 100).RingPercent);
        });
        test("animation: unchanged zero stays static and decline to zero is finite", () =>
        {
            var animation = new TrayAnimation();
            Check.False(animation.Start(ReadStatus.Ready, 0, 0, false, 0, true));
            Check.True(animation.Start(ReadStatus.Ready, 7, 0, false, 0, true));
            Check.True(double.IsNaN(animation.Sample(0, false, 67).SweepPhase));
            Check.False(animation.Sample(0, false, 220).IsActive);
            Check.Equal(0d, animation.Sample(0, false, 220).RingPercent);
        });
        test("animation: explicit stop and unexpected value changes cannot leave a timer active", () =>
        {
            var animation = new TrayAnimation();
            animation.Start(ReadStatus.Ready, 68, 68, false, 0, true);
            animation.Stop(); Check.False(animation.Sample(68, false, 50).IsActive);
            animation.Start(ReadStatus.Ready, 68, 68, false, 0, true);
            Check.False(animation.Sample(20, false, 50).IsActive);
        });
        test("animation: replacing a success does not accumulate independent loops", () =>
        {
            var animation = new TrayAnimation();
            animation.Start(ReadStatus.Ready, 97, 90, false, 0, true);
            var visible = animation.Sample(90, false, 134).RingPercent;
            animation.Start(ReadStatus.Ready, 90, 68, false, 134, true);
            Check.Equal(visible, animation.Sample(68, false, 134).RingPercent);
            Check.False(animation.Sample(68, false, 554).IsActive);
        });
    }
}
