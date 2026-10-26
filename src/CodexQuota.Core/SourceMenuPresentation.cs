namespace CodexQuota.Core;

/// <summary>Keep the saved policy check separate from the current Auto winner marker.</summary>
public sealed record SourceMenuPresentation(string Text, bool Checked, bool IsActive, string Explanation)
{
    public static bool InvalidatesCurrentWinner(AutoSourceDiagnostic diagnostic, QuotaDataSource? active) =>
        diagnostic.Stage == AutoSourceStage.CleanupUnconfirmed ||
        diagnostic.Stage == AutoSourceStage.AttemptFailed && active is not null && diagnostic.Source == active;

    public static SourceMenuPresentation Create(QuotaDataSource row, QuotaDataSource selected,
        QuotaDataSource? active, QuotaState state, DateTimeOffset now, bool switching = false,
        bool cleanupBlocked = false, bool winnerInvalidated = false)
    {
        var validQuota = state.Quota is { } quota && !double.IsNaN(quota.RemainingPercent) &&
            !double.IsInfinity(quota.RemainingPercent) && quota.RemainingPercent >= 0 && quota.RemainingPercent <= 100;
        var marked = selected == QuotaDataSource.Auto && row != QuotaDataSource.Auto &&
            QuotaDataSourceNames.IsValid(row) && row == active && !switching && !cleanupBlocked && !winnerInvalidated &&
            state.Status == ReadStatus.Ready && validQuota && state.LastSuccess is not null && !state.IsStale(now);
        return new((marked ? "● " : "") + row.DisplayName(), row == selected, marked,
            marked ? "自动模式当前成功来源；勾选表示保存的选择策略" : row == QuotaDataSource.Auto
                ? "自动查找可用来源；● 表示当前成功来源" : "手动固定此来源，不尝试其他来源");
    }
}
