using System.Globalization;
using CodexQuota.Core;

internal static class OutputSpeedViewTests
{
    public static void Register(Action<string, Action> test)
    {
        var now = new DateTimeOffset(2026, 10, 12, 12, 34, 56, TimeSpan.Zero);
        OutputSpeedSample Sample(DateTimeOffset end, long tokens = 100) => new("thread", "response", "gpt-test", tokens, end.AddSeconds(-2), end)
            { TimingReliability = OutputSpeedTimingReliability.VerifiedGeneration };
        test("speed view: default timing is unverified at the numeric API and all displays", () =>
        {
            var recorded = now.AddSeconds(-1);
            var sample = new OutputSpeedSample("thread", "response", "gpt-6.1-sol", 1000,
                now.AddSeconds(-2.25), now.AddSeconds(-2))
                { RecordedAt = recorded, ReasoningEffort = "medium" };
            Check.Null(sample.TokensPerSecond);
            Check.Null(sample.GenerationDuration);
            var view = OutputSpeedView.Create(sample, now);
            Check.Equal("计时未核验", view.Value);
            Check.Equal("GPT6.1 Sol", view.CompactModel);
            Check.Equal("中", view.ReasoningLabel);
            Check.Equal("记录", view.SampleLabel);
            Check.Equal(recorded.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), view.SampleTime);
            Check.Contains("生命周期通知", view.AccessibleText);
            Check.Contains("用量记录时间", view.AccessibleText);
            Check.False(view.AccessibleText.Contains("4000", StringComparison.Ordinal));
            Check.False(view.HasVerifiedTiming);
            var stale = OutputSpeedView.Create(sample, now.AddHours(1), unavailable: true);
            Check.Equal("计时未核验", stale.Value);
            Check.Equal("上次记录", stale.SampleLabel);
            Check.True(stale.IsOld);
        });
        test("speed view: unverified lifecycle samples are not filtered by token rate or duration", () =>
        {
            foreach (var duration in new[] { .001, .25, 2.5, 10d, 100d })
            foreach (var tokens in new long[] { 0, 1, 1000, 1000000 })
            {
                var sample = Sample(now, tokens) with
                {
                    StartedAt = now.AddSeconds(-duration),
                    TimingReliability = OutputSpeedTimingReliability.UnverifiedItemLifecycle
                };
                Check.Null(sample.TokensPerSecond);
                Check.Equal("计时未核验", OutputSpeedView.Create(sample, now).Value);
            }
        });
        test("speed view: generation evidence still requires a positive interval and valid count", () =>
        {
            foreach (var sample in new[] { Sample(now) with { StartedAt = now },
                Sample(now) with { StartedAt = now.AddSeconds(1) }, Sample(now, -1) })
            {
                Check.Null(sample.TokensPerSecond);
                Check.False(OutputSpeedView.Create(sample, now).HasVerifiedTiming);
            }
        });
        test("speed view: completed output estimate contains reasoning and exact local time", () =>
        {
            var view = OutputSpeedView.Create(Sample(now), now);
            Check.Equal("50.0 tok/s", view.Value);
            Check.Equal("gpt-test", view.Model);
            Check.Equal("含推理·" + now.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), view.Detail);
            Check.Contains("最近有效完成响应", view.AccessibleText);
            Check.Contains("不重复相加", view.AccessibleText);
            Check.Contains("不是实时速度", view.AccessibleText);
            Check.False(view.IsOld);
        });
        test("speed view: unknown and unavailable never pretend to be zero", () =>
        {
            Check.Equal("—", OutputSpeedView.Create(null, now).Value);
            Check.Equal("暂无可靠样本", OutputSpeedView.Create(null, now).Detail);
            Check.Equal("暂时无法读取日志", OutputSpeedView.Create(null, now, unavailable: true).Detail);
            Check.Equal("正在读取近期日志", OutputSpeedView.Create(null, now, pendingWork: true).Detail);
        });
        test("speed view: stale measurement retained with real timestamp", () =>
        {
            var end = now.AddMinutes(-10);
            var view = OutputSpeedView.Create(Sample(end), now, unavailable: true, historyLimited: true);
            Check.True(view.IsOld); Check.True(view.IsUnavailable);
            Check.Equal("50.0 tok/s", view.Value);
            Check.Contains("保留上次结果", view.AccessibleText);
            Check.Contains("不代表完整历史", view.AccessibleText);
            Check.Contains("上次", view.Detail);
            Check.Contains(end.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), view.Detail);
        });
        test("speed view: cross-day and cross-year use local date with zero-padded day", () =>
        {
            foreach (var end in new[] { now.AddDays(-5), now.AddYears(-1).AddDays(-5) })
            {
                var at = end.ToLocalTime();
                var view = OutputSpeedView.Create(Sample(end), now);
                Check.Equal("含推理·上次 " + at.ToString(at.Year == now.ToLocalTime().Year ? "M/dd HH:mm:ss" : "yyyy/M/dd HH:mm:ss", CultureInfo.InvariantCulture), view.Detail);
            }
        });
        test("speed view: model missing stays unknown; numeric formatting invariant", () =>
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var view = OutputSpeedView.Create(Sample(now, 101) with { Model = null }, now);
                Check.Equal("50.5 tok/s", view.Value); Check.Equal("模型未知", view.Model);
            }
            finally { CultureInfo.CurrentCulture = previous; }
        });
        test("speed view: tiny positive output does not round to misleading zero", () =>
        {
            var view = OutputSpeedView.Create(Sample(now, 1) with { StartedAt = now.AddSeconds(-100) }, now);
            Check.Equal("<0.1 tok/s", view.Value);
        });
        test("speed view: genuine zero and large finite rates remain truthful and compact", () =>
        {
            Check.Equal("0.0 tok/s", OutputSpeedView.Create(Sample(now, 0), now).Value);
            var view = OutputSpeedView.Create(Sample(now, long.MaxValue) with { StartedAt = now.AddMilliseconds(-1) }, now);
            Check.Contains("E+", view.Value);
            Check.True(view.Value.Length <= 14);
        });
        test("speed view: compact block adds only32 logical pixels and preserves card width", () =>
        {
            var original = HoverCardModel.Create(QuotaState.Initial, now);
            var model = HoverCardModel.Create(QuotaState.Initial, now, OutputSpeedView.Create(Sample(now), now));
            Check.Equal(32f, HoverCardLayout.ContentHeight(model) - HoverCardLayout.ContentHeight(original));
            Check.Equal(300, HoverCardLayout.PixelWidth(1));
            Check.Contains("50.0 tok/s", model.AccessibleText);
            Check.Equal(original.Cards.Count, model.Cards.Count);
        });
    }
}
