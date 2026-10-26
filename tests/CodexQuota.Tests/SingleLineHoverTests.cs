using System.Globalization;
using CodexQuota.Core;

internal static class SingleLineHoverTests
{
    public static void Register(Action<string, Action> test)
    {
        var now = new DateTimeOffset(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);
        QuotaState State(params ResetCard[] cards) => new(new(81, now.AddDays(6).AddHours(5), 10080), now,
            ReadStatus.Ready, ResetCards: new(ResetCardDataStatus.Details, cards.Length, cards), ResetCardsLastSuccess: now);

        test("single-line hover: card countdown retains hours and floors remaining days", () =>
        {
            var expiry = now.AddDays(11).AddHours(5).AddMinutes(59).AddSeconds(59);
            var row = HoverCardModel.Create(State(new ResetCard(ResetCardExpiryKind.Date, expiry)), now).Cards.Single();
            Check.Equal("11天5小时后", row.RelativeExpiry);
            Check.Equal(expiry.ToLocalTime().ToString("M月dd日 HH:mm:ss", CultureInfo.InvariantCulture), row.Date);
            Check.Equal(expiry.ToLocalTime().ToString("M/dd HH:mm:ss", CultureInfo.InvariantCulture), row.NumericDate);
        });
        foreach (var (remaining, expected) in new[] {
            (TimeSpan.FromDays(1), "1天后"),
            (TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59), "23小时59分钟后"),
            (TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59), "59分钟后"),
            (TimeSpan.FromMinutes(1), "1分钟后"),
            (TimeSpan.FromTicks(1), "不到1分钟后"),
            (TimeSpan.Zero, "已到期") })
            test($"single-line hover: exact expiry boundary {remaining.Ticks}", () =>
            {
                var row = HoverCardModel.Create(State(new ResetCard(ResetCardExpiryKind.Date, now + remaining)), now).Cards.Single();
                Check.Equal(expected, row.RelativeExpiry);
                Check.Equal(remaining > TimeSpan.Zero ? "到期" : "已到期", row.Detail);
            });
        test("single-line hover: numeric fallback retains zero-padded day year and seconds", () =>
        {
            var expiry = new DateTimeOffset(2027, 1, 7, 14, 32, 59, TimeSpan.Zero);
            var row = HoverCardModel.Create(State(new ResetCard(ResetCardExpiryKind.Date, expiry)), now).Cards.Single();
            Check.Equal(expiry.ToLocalTime().ToString("yyyy/M/dd HH:mm:ss", CultureInfo.InvariantCulture), row.NumericDate);
            Check.True(row.NumericDate.EndsWith(":59", StringComparison.Ordinal));
            Check.Equal(12f, HoverCardLayout.TicketTextSize(row.NumericDate));
        });
        test("single-line hover: Plus shows only the returned weekly reset scope", () =>
        {
            var weekly = new QuotaWindow(81, now.AddDays(6).AddHours(5), 10080);
            var selection = new QuotaSelection("plus", PlanSource.RateLimits, 300, weekly,
                new[] { weekly }, QuotaSelectionReason.Preferred);
            var model = HoverCardModel.Create(State() with { Quota = weekly, Selection = selection }, now);
            Check.Equal(1, model.Quotas.Count);
            Check.Equal("本周", model.Quotas[0].ResetScope);
            Check.Equal("6天5小时后重置", model.Quotas[0].RelativeReset);
            Check.Equal(104f, HoverCardLayout.QuotasHeight(model));
        });
        test("single-line hover: unknown and never expiry have no invented numeric date", () =>
        {
            var rows = HoverCardModel.Create(State(new ResetCard(ResetCardExpiryKind.Unknown, null), new ResetCard(ResetCardExpiryKind.Never, null)), now).Cards;
            Check.Equal("日期未知", rows[0].RelativeExpiry);
            Check.Equal("不过期", rows[1].RelativeExpiry);
            Check.True(rows.All(row => row.NumericDate.Length == 0));
        });
        test("single-line hover: one-line TPS preserves accessible timing and reasoning-token meaning", () =>
        {
            var completed = now.AddSeconds(-20);
            var sample = new OutputSpeedSample("synthetic-thread", "synthetic-response", "gpt-5.4", 302,
                completed.AddSeconds(-10), completed)
                { TimingReliability = OutputSpeedTimingReliability.VerifiedGeneration };
            var view = OutputSpeedView.Create(sample, now);
            Check.Equal("30.2 tok/s", view.Value);
            Check.Equal("GPT5.4", view.CompactModel);
            Check.Equal("", view.ReasoningLabel);
            Check.Contains("推理 token", view.AccessibleText);
            Check.Contains(completed.ToLocalTime().ToString("yyyy年M月dd日 HH:mm:ss zzz", CultureInfo.InvariantCulture), view.AccessibleText);
            Check.Equal("xhigh", (view with { ReasoningLabel = "xhigh" }).ReasoningLabel);
            Check.Equal("", OutputSpeedView.Create(null, now).ReasoningLabel);
        });
        test("single-line hover: short model keeps TPS together at the left edge", () =>
        {
            Check.Equal(65f, HoverCardLayout.OutputSpeedModelWidth(244, 0, 65, 100));
            Check.Equal(65f, HoverCardLayout.OutputSpeedModelWidth(244, 28, 65, 100));
            Check.Equal(140f, HoverCardLayout.OutputSpeedModelWidth(244, 0, 400, 100));
            Check.Equal(112f, HoverCardLayout.OutputSpeedModelWidth(244, 28, 400, 100));
            Check.Equal(0f, HoverCardLayout.OutputSpeedModelWidth(100, 28, 400, 100));
        });
        test("single-line hover: geometry stays compact without font or body-width reduction", () =>
        {
            Check.Equal(280f, HoverCardLayout.Width);
            Check.Equal(28f, HoverCardLayout.TicketHeight);
            Check.Equal(104f, HoverCardLayout.QuotaHeight);
            Check.Equal(32f, HoverCardLayout.OutputSpeedHeight);
            Check.True(HoverCardLayout.ResetRowTop + 2 * HoverCardLayout.ResetRowHeight < HoverCardLayout.CompactQuotaHeight);
            var cards = Enumerable.Repeat(new ResetCard(ResetCardExpiryKind.Date, now.AddDays(11).AddHours(5)), 10000).ToArray();
            var model = HoverCardModel.Create(State(cards), now);
            foreach (var dpi in new[] { 96, 120, 144, 192 })
            {
                var scale = HoverCardLayout.Scale(dpi, 1920);
                var height = HoverCardLayout.PixelHeight(model, scale, 760);
                var scroll = HoverCardLayout.MaxScroll(model, height, scale);
                var top = HoverCardLayout.Shadow + HoverCardLayout.HeaderHeight + HoverCardLayout.QuotasHeight(model) + HoverCardLayout.CardsHeaderHeight;
                var visible = HoverCardLayout.VisibleCards(model.Cards.Count, top, scroll, height / scale);
                Check.Equal(10000, visible.Last);
                Check.True(visible.Last - visible.First <= 30);
            }
        });
    }
}
