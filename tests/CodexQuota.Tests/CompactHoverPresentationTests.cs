using System.Globalization;
using CodexQuota.Core;

internal static class CompactHoverPresentationTests
{
    public static void Register(Action<string, Action> test)
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        QuotaState State(DateTimeOffset? reset, params ResetCard[] cards) => new(new(81, reset, 10080), now,
            ReadStatus.Ready, ResetCards: new(ResetCardDataStatus.Details, cards.Length, cards), ResetCardsLastSuccess: now);

        test("compact presentation: quota has relative and complete absolute reset", () =>
        {
            var reset = now.AddDays(6).AddHours(3).AddMinutes(12);
            var quota = HoverCardModel.Create(State(reset), now).Quotas.Single();
            Check.Equal("6天3小时后重置", quota.RelativeReset);
            Check.Equal(reset.ToLocalTime().ToString("M月dd日 HH:mm", CultureInfo.InvariantCulture), quota.Reset);
        });
        test("compact presentation: minute countdown is bounded without invented zero", () =>
        {
            Check.Equal("不到1分钟", HoverCardModel.RelativeDuration(now.AddSeconds(20), now));
            Check.Equal("1分钟", HoverCardModel.RelativeDuration(now.AddMinutes(1), now));
            Check.Equal("2小时12分钟", HoverCardModel.RelativeDuration(now.AddHours(2).AddMinutes(12), now));
            Check.Equal("", HoverCardModel.RelativeDuration(now, now));
        });
        test("compact presentation: expired reset never promises a future refill", () =>
        {
            var quota = HoverCardModel.Create(State(now), now).Quotas.Single();
            Check.Equal("重置时刻已过", quota.RelativeReset);
            Check.True(quota.IsStale);
            Check.Equal(HoverTone.Stale, quota.Tone);
            Check.Equal(81, quota.Percent!.Value);
        });
        test("compact presentation: missing reset does not invent a countdown", () =>
        {
            var quota = HoverCardModel.Create(State(null), now).Quotas.Single();
            Check.Equal("时间未知", quota.Reset);
            Check.Equal("", quota.RelativeReset);
            var missing = HoverCardModel.Create(QuotaState.Initial, now).Quotas.Single();
            Check.Equal("服务未返回", missing.Reset);
            Check.Equal("", missing.RelativeReset);
        });
        test("compact presentation: expiry relative label retains exact year day and seconds", () =>
        {
            var expiry = new DateTimeOffset(2027, 1, 7, 12, 42, 59, TimeSpan.Zero);
            var row = HoverCardModel.Create(State(now.AddDays(2), new ResetCard(ResetCardExpiryKind.Date, expiry)), now).Cards.Single();
            Check.True(row.RelativeExpiry.EndsWith("后", StringComparison.Ordinal));
            Check.Equal(expiry.ToLocalTime().ToString("yyyy年M月dd日 HH:mm:ss", CultureInfo.InvariantCulture), row.Date);
            Check.Equal(12f, HoverCardLayout.TicketTextSize(row.Date));
            Check.Equal(HoverCardLayout.TicketTextSize("10月08日 01:02:03"), HoverCardLayout.TicketTextSize(row.Date));
        });
        test("compact presentation: expired never and unknown cards remain distinct", () =>
        {
            var rows = HoverCardModel.Create(State(now.AddDays(2), new ResetCard(ResetCardExpiryKind.Date, now),
                new ResetCard(ResetCardExpiryKind.Never, null), new ResetCard(ResetCardExpiryKind.Unknown, null)), now).Cards;
            Check.Equal("已到期", rows[0].RelativeExpiry);
            Check.Equal("不过期", rows[1].RelativeExpiry);
            Check.Equal("日期未知", rows[2].RelativeExpiry);
            Check.Equal("已到期", rows[0].Detail);
        });
        test("compact presentation: header refresh differs from TPS completion", () =>
        {
            var completed = now.AddMinutes(-4).AddSeconds(-13);
            var sample = new OutputSpeedSample("thread", "response", "model", 180, completed.AddSeconds(-3), completed)
                { TimingReliability = OutputSpeedTimingReliability.VerifiedGeneration };
            var speed = OutputSpeedView.Create(sample, now);
            var card = HoverCardModel.Create(State(now.AddDays(1)), now, speed);
            Check.Equal(now.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), card.QuotaRefresh);
            Check.Equal(completed.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), speed.SampleTime);
            Check.Equal("样本", speed.SampleLabel);
            Check.False(card.QuotaRefresh == speed.SampleTime);
        });
        test("compact presentation: refresh label means last quota success and failures never advance it", () =>
        {
            var state = State(now.AddHours(4));
            var expected = "更新于 " + now.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            foreach (var failure in new[] { ReadStatus.Timeout, ReadStatus.Unavailable, ReadStatus.ProtocolError, ReadStatus.RateLimited })
            {
                var failed = state.Apply(new(failure), now.AddMinutes(3));
                Check.Equal(expected, HoverCardModel.Create(failed, now.AddMinutes(3)).QuotaRefreshLabel);
                Check.Equal(now, failed.LastSuccess!.Value);
            }
            var refreshed = state.Apply(new(ReadStatus.Ready, state.Quota), now.AddMinutes(4));
            Check.Equal("更新于 " + now.AddMinutes(4).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
                HoverCardModel.Create(refreshed, now.AddMinutes(4)).QuotaRefreshLabel);
        });
        test("compact presentation: no quota success has no invented refresh timestamp", () =>
        {
            Check.Equal("尚未更新", HoverCardModel.Create(QuotaState.Initial, now).QuotaRefreshLabel);
            Check.Equal("尚未更新", HoverCardModel.Create(QuotaState.Initial.Apply(new(ReadStatus.Timeout), now), now).QuotaRefreshLabel);
        });
        test("compact presentation: old sample preserves year and source timestamp", () =>
        {
            var completed = now.AddYears(-1);
            var sample = new OutputSpeedSample("thread", "response", "model", 180, completed.AddSeconds(-3), completed)
                { TimingReliability = OutputSpeedTimingReliability.VerifiedGeneration };
            foreach (var unavailable in new[] { false, true })
            {
                var speed = OutputSpeedView.Create(sample, now, unavailable: unavailable);
                Check.Equal("上次", speed.SampleLabel);
                Check.Equal(completed.ToLocalTime().ToString("yyyy/M/dd HH:mm:ss", CultureInfo.InvariantCulture), speed.SampleTime);
            }
        });
        test("compact presentation: no reliable TPS sample has no invented time", () =>
        {
            Check.Equal("", OutputSpeedView.Create(null, now).SampleTime);
            Check.Equal("暂无可靠样本", OutputSpeedView.Create(null, now).SampleLabel);
            Check.Equal("暂时无法读取日志", OutputSpeedView.Create(null, now, unavailable: true).SampleLabel);
        });
        test("compact presentation: next change advances relative text within one minute", () =>
        {
            var state = State(now.AddHours(3));
            var next = HoverCardModel.NextChange(state, now)!.Value;
            Check.Equal(now.AddMinutes(1), next);
            var before = HoverCardModel.Create(state, now).Quotas.Single().RelativeReset;
            var after = HoverCardModel.Create(state, next).Quotas.Single().RelativeReset;
            Check.False(before == after);
        });
        test("compact presentation: exact expiry wins over minute wake", () =>
        {
            Check.Equal(now.AddSeconds(7), HoverCardModel.NextChange(State(now.AddSeconds(7)), now)!.Value);
            var state = State(null, new ResetCard(ResetCardExpiryKind.Date, now.AddDays(7).AddSeconds(8)));
            Check.Equal(now.AddSeconds(8), HoverCardModel.NextChange(state, now)!.Value);
        });
        test("compact presentation: all expired data does not create a timer spin", () =>
        {
            var state = State(now.AddHours(-1), new ResetCard(ResetCardExpiryKind.Date, now.AddDays(-1))) with
                { LastSuccess = now.AddHours(-1), ResetCardsLastSuccess = now.AddHours(-1) };
            Check.Null(HoverCardModel.NextChange(state, now));
        });
        test("compact presentation: card and TPS layout stays bounded and readable", () =>
        {
            Check.Equal(280f, HoverCardLayout.Width);
            Check.Equal(28f, HoverCardLayout.TicketHeight);
            Check.Equal(32f, HoverCardLayout.OutputSpeedHeight);
            var state = State(now.AddDays(1), Enumerable.Repeat(new ResetCard(ResetCardExpiryKind.Date, now.AddDays(2)), 10000).ToArray());
            var model = HoverCardModel.Create(state, now, OutputSpeedView.Create(null, now));
            var top = HoverCardLayout.Shadow + HoverCardLayout.HeaderHeight + HoverCardLayout.QuotasHeight(model) + HoverCardLayout.CardsHeaderHeight;
            var viewport = 760;
            var scroll = HoverCardLayout.MaxScroll(model, viewport, 1);
            var visible = HoverCardLayout.VisibleCards(model.Cards.Count, top, scroll, viewport);
            Check.Equal(10000, visible.Last);
            Check.True(visible.Last - visible.First <= 30);
            Check.Equal(viewport, HoverCardLayout.PixelHeight(model, 1, viewport));
        });
    }
}
