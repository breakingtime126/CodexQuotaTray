using CodexQuota.Core;

internal static class MetadataLabelTests
{
    public static void Register(Action<string, Action> test)
    {
        test("metadata label: model typography never changes version or aliases a family", () =>
        {
            Check.Equal("GPT6.1 Sol", OutputSpeedView.FormatModelName("gpt-6.1-sol"));
            Check.Equal("GPT5.4", OutputSpeedView.FormatModelName("gpt-5.4"));
            Check.Equal("GPT5 Codex Mini", OutputSpeedView.FormatModelName("gpt-5-codex-mini"));
            foreach (var raw in new[] { "other-model", "gpt-oss-120b", "gpt-", "gpt--6.1", "模型未知", "" })
                Check.Equal(raw, OutputSpeedView.FormatModelName(raw));
        });
        test("metadata label: verified effort translates without defaulting absent fields", () =>
        {
            Check.Equal("中", OutputSpeedView.FormatReasoningEffort("medium"));
            Check.Equal("低", OutputSpeedView.FormatReasoningEffort("low"));
            Check.Equal("高", OutputSpeedView.FormatReasoningEffort("high"));
            Check.Equal("超高", OutputSpeedView.FormatReasoningEffort("xhigh"));
            Check.Equal("", OutputSpeedView.FormatReasoningEffort(null));
            Check.Equal("", OutputSpeedView.FormatReasoningEffort(""));
            Check.Equal("", OutputSpeedView.FormatReasoningEffort("custom-future-level"));
        });
        test("metadata label: historical effort reaches one-line display and full accessibility", () =>
        {
            var now = new DateTimeOffset(2026, 10, 12, 6, 49, 43, TimeSpan.Zero);
            var sample = new OutputSpeedSample("thread", "response", "gpt-6.1-sol", 303, now.AddSeconds(-10), now)
                { ReasoningEffort = "medium", TimingReliability = OutputSpeedTimingReliability.VerifiedGeneration };
            var view = OutputSpeedView.Create(sample, now);
            Check.Equal("GPT6.1 Sol", view.CompactModel);
            Check.Equal("中", view.ReasoningLabel);
            Check.Equal("30.3 tok/s", view.Value);
            Check.Contains("medium", view.AccessibleText);
            Check.Contains("推理 token", view.AccessibleText);
            Check.Contains(now.ToLocalTime().ToString("HH:mm:ss"), view.AccessibleText);
            Check.False(view == OutputSpeedView.Create(sample with { ReasoningEffort = "high" }, now));
        });
        test("metadata label: pending history and hard limit are distinct from a missing model", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var sample = new OutputSpeedSample("thread", "response", null, 303, now.AddSeconds(-10), now)
                { TimingReliability = OutputSpeedTimingReliability.VerifiedGeneration };
            var pending = OutputSpeedView.Create(sample, now, modelMetadataStatus: RolloutModelMetadataStatus.PendingHistory);
            var limited = OutputSpeedView.Create(sample, now, modelMetadataStatus: RolloutModelMetadataStatus.HistoryLimit);
            var missing = OutputSpeedView.Create(sample, now);
            Check.Equal("模型回查中", pending.CompactModel);
            Check.Equal("模型回查达上限", limited.CompactModel);
            Check.Equal("模型未知", missing.CompactModel);
            Check.Equal("30.3 tok/s", pending.Value);
            Check.Equal(pending.Value, limited.Value);
            Check.Contains("固定IO预算", pending.AccessibleText);
            Check.Contains("安全上限", limited.AccessibleText);
        });
    }
}
