using CodexQuota.Core;

internal static class ResponseRateViewTests
{
    public static void Register(Action<string, Action> test)
    {
        test("response view: estimates retain compact format and explicitly describe log timing", () =>
        {
            var at = DateTimeOffset.UtcNow;
            var sample = Sample(at);
            var view = OutputSpeedView.Create(sample, at);
            Check.Equal("⚡ GPT6.1 Sol 中  ≈ 50.0 tok/s", view.CompactLine);
            Check.Contains("响应有效速度（估算）", view.AccessibleText);
            Check.Contains("首 token 等待", view.AccessibleText);
            Check.Contains("不是模型纯生成 TPS", view.AccessibleText);
            Check.Contains("CompletionSnapshot", view.AccessibleText);
            Check.Null(sample.GenerationDuration);
        });
        test("response view: unrelated current activity keeps the historical response group and time", () =>
        {
            var at = DateTimeOffset.UtcNow;
            var sample = Sample(at) with { IsHistoricalOtherThread = true };
            var view = OutputSpeedView.Create(sample, at.AddMinutes(1), pendingWork: true);
            Check.Contains("上次" + at.ToLocalTime().ToString("HH:mm"), view.CompactLine);
            Check.Contains("⚡ GPT6.1 Sol 中  ≈ 50.0 tok/s", view.CompactLine);
            Check.Contains("独立历史有效样本", view.AccessibleText);
        });
        test("response view: an enum label without complete interval evidence cannot make a rate", () =>
        {
            var sample = Sample(DateTimeOffset.UtcNow) with { ResponseRate = null };
            Check.Null(sample.TokensPerSecond);
            Check.False(OutputSpeedView.Create(sample, DateTimeOffset.UtcNow).HasVerifiedTiming);
        });
        test("response diagnostic: current read progress is separate from displayed sample evidence", () =>
        {
            var result = new RolloutSpeedReadResult(Sample(DateTimeOffset.UtcNow), RolloutSpeedReadStatus.Ready)
            {
                CanCatchUp = true, ForwardBacklogPending = true,
                ResponseRateDiagnostic = new(ResponseRateReason.MissingStart, ResponseRateStartBoundary.NotObserved,
                    ResponseRateEndBoundary.TokenUsageRecord, ResponseRateUsageKind.PerResponse, false, true, true, 1)
            };
            var value = OutputMetadataDiagnostic.Format(result);
            Check.Contains("metricScope=ResponseEffectiveIncludingTtftAndLogDelay", value);
            Check.Contains("responseRateReason=MissingStart", value);
            Check.Contains("sampleStartBoundary=TaskStarted sampleEndBoundary=TokenUsageRecord", value);
            Check.Contains("canCatchUp=true discoveryPending=false forwardBacklog=true historyRecovery=false", value);
            Check.False(value.Contains("gpt-6.1-sol", StringComparison.Ordinal));
        });
    }

    private static OutputSpeedSample Sample(DateTimeOffset at) => new("thread", "response", "gpt-6.1-sol", 100,
        at.AddSeconds(-2), at)
    {
        TurnId = "turn", ReasoningEffort = "medium", ThreadRequestedSetting = ThreadRequestedSetting.Fast,
        TimingReliability = OutputSpeedTimingReliability.ResponseRateEstimate,
        ResponseRate = new(ResponseRateStartBoundary.TaskStarted, ResponseRateEndBoundary.TokenUsageRecord,
            ResponseRateUsageKind.PerResponse)
    };
}
