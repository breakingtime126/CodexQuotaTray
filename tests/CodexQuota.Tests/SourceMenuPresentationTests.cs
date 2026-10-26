using CodexQuota.Core;

internal static class SourceMenuPresentationTests
{
    public static void Register(Action<string, Action> test)
    {
        var now = DateTimeOffset.Parse("2026-10-13T02:00:00Z");
        var ready = QuotaState.Initial.Apply(new(ReadStatus.Ready, new(73, now.AddHours(1), 10080)), now);
        var rows = new[] { QuotaDataSource.Auto, QuotaDataSource.Cli, QuotaDataSource.VsCode, QuotaDataSource.Desktop };
        test("source menu: Auto keeps its check and only actual winner gets independent dot", () =>
        {
            foreach (var winner in rows.Skip(1))
            {
                var items = rows.Select(row => SourceMenuPresentation.Create(row, QuotaDataSource.Auto, winner, ready, now)).ToArray();
                Check.Equal(1, items.Count(item => item.Checked));
                Check.True(items[0].Checked && !items[0].IsActive);
                Check.Equal(1, items.Count(item => item.IsActive));
                Check.True(items[Array.IndexOf(rows, winner)].Text.StartsWith("● ", StringComparison.Ordinal));
            }
        });
        test("source menu: manual policies never show Auto winner dot", () =>
        {
            foreach (var selected in rows.Skip(1))
            foreach (var row in rows)
            {
                var item = SourceMenuPresentation.Create(row, selected, selected, ready, now);
                Check.Equal(row == selected, item.Checked);
                Check.True(!item.IsActive && !item.Text.Contains('●'));
            }
        });
        test("source menu: discovered or last failed candidate is never marked", () =>
        {
            foreach (ReadStatus status in Enum.GetValues(typeof(ReadStatus)))
            {
                if (status == ReadStatus.Ready) continue;
                Check.True(!SourceMenuPresentation.Create(QuotaDataSource.Cli, QuotaDataSource.Auto,
                    QuotaDataSource.Cli, ready with { Status = status }, now).IsActive);
            }
            Check.True(!SourceMenuPresentation.Create(QuotaDataSource.Cli, QuotaDataSource.Auto, null, ready, now).IsActive);
            Check.True(!SourceMenuPresentation.Create(QuotaDataSource.Cli, QuotaDataSource.Auto, QuotaDataSource.Cli, QuotaState.Initial, now).IsActive);
        });
        test("source menu: switching cleanup expiry and missing quota clear winner dot", () =>
        {
            Check.True(!SourceMenuPresentation.Create(QuotaDataSource.Cli, QuotaDataSource.Auto, QuotaDataSource.Cli, ready, now, switching: true).IsActive);
            Check.True(!SourceMenuPresentation.Create(QuotaDataSource.Cli, QuotaDataSource.Auto, QuotaDataSource.Cli, ready, now, cleanupBlocked: true).IsActive);
            foreach (var state in new[] { ready with { LastSuccess = now.AddMinutes(-10) }, ready with { LastSuccess = null },
                ready with { Quota = null }, ready with { Quota = ready.Quota! with { ResetsAt = now } },
                ready with { Quota = ready.Quota! with { RemainingPercent = double.NaN } } })
                Check.True(!SourceMenuPresentation.Create(QuotaDataSource.Cli, QuotaDataSource.Auto, QuotaDataSource.Cli, state, now).IsActive);
        });
        test("source menu: failed sticky winner is hidden while another source is still being tried", () =>
        {
            var failed = new AutoSourceDiagnostic(QuotaDataSource.Auto, QuotaDataSource.Cli, AutoSourceStage.AttemptFailed, ReadStatus.Timeout);
            Check.True(SourceMenuPresentation.InvalidatesCurrentWinner(failed, QuotaDataSource.Cli));
            Check.True(!SourceMenuPresentation.InvalidatesCurrentWinner(failed, QuotaDataSource.VsCode));
            Check.True(!SourceMenuPresentation.InvalidatesCurrentWinner(failed with { Stage = AutoSourceStage.AttemptStarted }, QuotaDataSource.Cli));
            Check.True(!SourceMenuPresentation.Create(QuotaDataSource.Cli, QuotaDataSource.Auto,
                QuotaDataSource.Cli, ready, now, winnerInvalidated: true).IsActive);
        });
        test("source menu: success failure and recovered alternate move marker without changing Auto", () =>
        {
            Check.True(SourceMenuPresentation.Create(QuotaDataSource.Cli, QuotaDataSource.Auto, QuotaDataSource.Cli, ready, now).IsActive);
            var failed = ready.Apply(new(ReadStatus.Timeout), now.AddSeconds(30));
            Check.True(!SourceMenuPresentation.Create(QuotaDataSource.Cli, QuotaDataSource.Auto, QuotaDataSource.Cli, failed, now).IsActive);
            var recovered = failed.Apply(new(ReadStatus.Ready, ready.Quota), now.AddSeconds(31));
            Check.True(SourceMenuPresentation.Create(QuotaDataSource.VsCode, QuotaDataSource.Auto, QuotaDataSource.VsCode, recovered, now.AddSeconds(31)).IsActive);
            Check.True(SourceMenuPresentation.Create(QuotaDataSource.Auto, QuotaDataSource.Auto, QuotaDataSource.VsCode, recovered, now.AddSeconds(31)).Checked);
        });
    }
}
