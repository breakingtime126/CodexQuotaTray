using CodexQuota.Core;
using CodexQuota.Tray;

internal static class HoverCardTests
{
    public static void Register(Action<string, Action> test)
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        QuotaState State(string plan = "pro", int percent = 80, ResetCards? cards = null)
        {
            var five = new QuotaWindow(25, now.AddHours(2), 300);
            var week = new QuotaWindow(percent, now.AddDays(2), 10080);
            var selection = new QuotaSelection(plan, PlanSource.RateLimits, plan == "plus" ? 300 : 10080,
                plan == "plus" ? five : week, new[] { five, week }, QuotaSelectionReason.Preferred);
            return new(selection.Active, now, ReadStatus.Ready, selection, cards ?? ResetCards.NotReturned, now);
        }
        foreach (var plan in new[] { "prolite", "pro", "promax" })
            test($"card: {plan} contains both returned quotas and retains weekly header", () =>
            {
                var model = HoverCardModel.Create(State(plan), now);
                Check.Equal(2, model.Quotas.Count);
                Check.Equal("5 小时剩余", model.Quotas[0].Label);
                Check.Equal(25, model.Quotas[0].Percent!.Value);
                Check.Equal("本周剩余", model.Quotas[1].Label);
                Check.Equal(80, model.Quotas[1].Percent!.Value);
                Check.Equal(model.Quotas[1], model.Header);
                Check.Equal("", model.Status);
            });
        test("card: Plus preserves five-hour then weekly, independent percentages", () =>
        {
            var model = HoverCardModel.Create(State("plus"), now);
            Check.Equal(2, model.Quotas.Count);
            Check.Equal(25, model.Quotas[0].Percent!.Value);
            Check.Equal(80, model.Quotas[1].Percent!.Value);
            Check.Equal(HoverTone.Amber, model.Quotas[0].Tone);
        });
        test("card: Pro missing week retains actual five-hour row", () =>
        {
            var state = State();
            var five = state.Selection!.AvailableWindows[0];
            state = state with { Quota = five,
                Selection = state.Selection! with { Active = five, AvailableWindows = new[] { five } } };
            var model = HoverCardModel.Create(state, now);
            Check.Equal(1, model.Quotas.Count);
            Check.Equal(25, model.Quotas[0].Percent!.Value);
            Check.Equal("5 小时剩余", model.Quotas[0].Label);
            Check.Equal(model.Quotas[0], model.Header);
        });
        test("card: missing Plus slot is hidden; existing week remains valid", () =>
        {
            var state = State("plus");
            state = state with { Quota = state.Selection!.AvailableWindows[1],
                Selection = state.Selection! with { AvailableWindows = new[] { state.Selection!.AvailableWindows[1] } } };
            var model = HoverCardModel.Create(state, now);
            Check.Equal(1, model.Quotas.Count);
            Check.Equal(80, model.Quotas[0].Percent!.Value);
            Check.Equal(80, model.Header.Percent!.Value);
            Check.Equal(HoverTone.Mint, model.Header.Tone);
        });
        foreach (var p in new[] { 0, 9, 10, 30, 31, 100 })
            test($"card: quota color boundary {p}", () =>
            {
                var expected = p < 10 ? HoverTone.Red : p <= 30 ? HoverTone.Amber : HoverTone.Mint;
                Check.Equal(expected, HoverCardModel.QuotaTone(p, false));
                Check.Equal(HoverTone.Stale, HoverCardModel.QuotaTone(p, true));
            });
        test("card: unknown differs from real zero", () =>
        {
            Check.Null(HoverCardModel.Create(QuotaState.Initial, now).Quotas[0].Percent);
            Check.Equal(0, HoverCardModel.Create(State(percent: 0), now).Header.Percent!.Value);
            Check.Equal(HoverTone.Stale, HoverCardModel.QuotaTone(null, false));
        });
        foreach (var hours in new[] { -1d, 0d, 23.999d, 24d, 24.001d, 167.999d, 168d, 168.001d, 500d })
            test($"card: expiry warning real-duration boundary {hours}h", () =>
            {
                var card = new ResetCard(ResetCardExpiryKind.Date, now.AddHours(hours));
                var expected = hours <= 24 ? HoverTone.Red : hours <= 168 ? HoverTone.Amber : HoverTone.Neutral;
                Check.Equal(expected, HoverCardModel.ExpiryTone(card, now, false));
                Check.Equal(HoverTone.Stale, HoverCardModel.ExpiryTone(card, now, true));
            });
        foreach (var kind in new[] { ResetCardExpiryKind.Never, ResetCardExpiryKind.Unknown })
            test($"card: {kind} has no invented deadline color", () =>
                Check.Equal(HoverTone.Neutral, HoverCardModel.ExpiryTone(new(kind, null), now, false)));
        test("card: expired date is marked expired, never available by assumption", () =>
        {
            var model = HoverCardModel.Create(State(cards: new(ResetCardDataStatus.Details, 2, new[] {
                new ResetCard(ResetCardExpiryKind.Date, now), new ResetCard(ResetCardExpiryKind.Date, now.AddHours(1)) })), now);
            Check.Equal("已到期", model.Cards[0].Detail);
            Check.Equal("到期", model.Cards[1].Detail);
            Check.Equal(HoverTone.Red, model.Cards[0].Tone);
        });
        foreach (var count in new[] { 0, 1, 3, 20, 10000 })
            test($"card: dynamic {count} cards retained without truncation", () =>
            {
                var cards = Enumerable.Range(0, count).Select(i => new ResetCard(ResetCardExpiryKind.Date, now.AddDays(10 + i))).ToArray();
                var model = HoverCardModel.Create(State(cards: new(ResetCardDataStatus.Details, count, cards)), now);
                Check.Equal(count, model.Cards.Count);
                Check.Equal($"{count} 张", model.CardCount);
                Check.Equal("", model.CardNote);
            });
        test("card: partial huge count preserves known dates and exact missing count", () =>
        {
            var model = HoverCardModel.Create(State(cards: new(ResetCardDataStatus.Details, long.MaxValue,
                new[] { new ResetCard(ResetCardExpiryKind.Unknown, null) })), now);
            Check.Equal(1, model.Cards.Count);
            Check.Equal($"另 {long.MaxValue - 1} 张日期未返回", model.CardNote);
        });
        test("card: absent details and absent summary remain honest", () =>
        {
            var countOnly = HoverCardModel.Create(State(cards: new(ResetCardDataStatus.CountOnly, 3, Array.Empty<ResetCard>())), now);
            Check.Equal("3 张", countOnly.CardCount);
            Check.Equal("到期日期未返回", countOnly.CardNote);
            var missing = HoverCardModel.Create(State(cards: ResetCards.NotReturned), now);
            Check.Equal("", missing.CardCount);
            Check.Equal("服务未返回", missing.CardNote);
            Check.Equal("", missing.Status);
        });
        test("card: stale snapshot grays quota and expiry warnings with last successful read", () =>
        {
            var state = State(cards: new(ResetCardDataStatus.Details, 1, new[] { new ResetCard(ResetCardExpiryKind.Date, now.AddHours(1)) }));
            var model = HoverCardModel.Create(state with { Status = ReadStatus.Timeout }, now);
            Check.True(model.CardsStale);
            Check.True(model.Quotas[0].IsStale);
            Check.Equal(HoverTone.Stale, model.Cards[0].Tone);
            Check.True(model.Status.Contains("超时"));
            Check.True(model.LastSuccess.Length > 0);
        });
        test("card: one expired Plus window does not gray valid other window or fresh cards", () =>
        {
            var state = State("plus", cards: new(ResetCardDataStatus.Details, 1, new[] { new ResetCard(ResetCardExpiryKind.Date, now.AddDays(3)) }));
            var expiredFive = state.Selection!.AvailableWindows[0] with { ResetsAt = now };
            state = state with { Quota = expiredFive, Selection = state.Selection! with { AvailableWindows = new[] { expiredFive, state.Selection!.AvailableWindows[1] } } };
            var model = HoverCardModel.Create(state, now);
            Check.True(model.Quotas[0].IsStale);
            Check.False(model.Quotas[1].IsStale);
            Check.False(model.CardsStale);
            Check.Equal(HoverTone.Amber, model.Cards[0].Tone);
        });
        test("card: next wake catches expiry-color threshold ahead of quota reset", () =>
        {
            var state = State(cards: new(ResetCardDataStatus.Details, 1, new[] { new ResetCard(ResetCardExpiryKind.Date, now.AddDays(7).AddSeconds(3)) }));
            Check.Equal(now.AddSeconds(3), HoverCardModel.NextChange(state, now)!.Value);
        });
        test("card: next wake catches individual non-active quota reset", () =>
        {
            var state = State("plus");
            var week = state.Selection!.AvailableWindows[1] with { ResetsAt = now.AddSeconds(2) };
            state = state with { Selection = state.Selection! with { AvailableWindows = new[] { state.Quota!, week } } };
            Check.Equal(now.AddSeconds(2), HoverCardModel.NextChange(state, now)!.Value);
        });
        test("card: next wake ignores passed thresholds and updates future countdown at minute resolution", () =>
        {
            var state = State(cards: new(ResetCardDataStatus.Details, 1, new[] { new ResetCard(ResetCardExpiryKind.Date, now.AddHours(-1)) }));
            Check.Equal(now.AddMinutes(1), HoverCardModel.NextChange(state, now)!.Value);
        });
        test("card: Shell keyboard session transfers to pointer over scrollable card", () =>
        {
            var session = new TrayHoverSession();
            session.Open(true, false);
            Check.False(session.CloseFromShell(pointerOverPopup: true));
            Check.False(session.ShellOwnsLifetime);
            Check.True(session.ObservePointer(false, true, true, 1));
            Check.True(session.ObservePointer(false, false, true, 10));
            Check.False(session.ObservePointer(false, false, true, 210));
        });
        foreach (var dpi in new[] { 96, 120, 144, 192, 240, 288 })
        foreach (var width in new[] { 180, 384, 1920 })
            test($"card layout: {dpi} DPI in {width}px work area", () =>
            {
                var model = HoverCardModel.Create(State("plus"), now);
                var scale = HoverCardLayout.Scale(dpi, width);
                var measuredWidth = HoverCardLayout.PixelWidth(scale);
                Check.True(measuredWidth <= width + 1); // Last fractional pixel is clamped by native placement.
                Check.True(HoverCardLayout.PixelHeight(model, scale, 300) <= 300);
                Check.True(HoverCardLayout.ClampScroll(-10, model, 300, scale) == 0);
                Check.Equal(HoverCardLayout.MaxScroll(model, 300, scale), HoverCardLayout.ClampScroll(float.MaxValue, model, 300, scale));
            });
        test("card layout: virtual last row of 10000 is reachable in bounded viewport", () =>
        {
            var cards = Enumerable.Range(0, 10000).Select(_ => new ResetCard(ResetCardExpiryKind.Unknown, null)).ToArray();
            var model = HoverCardModel.Create(State(cards: new(ResetCardDataStatus.Details, 10000, cards)), now);
            var total = HoverCardLayout.ContentHeight(model);
            var max = HoverCardLayout.MaxScroll(model, 760, 1);
            var top = HoverCardLayout.Shadow + HoverCardLayout.HeaderHeight + HoverCardLayout.QuotaHeight + HoverCardLayout.CardsHeaderHeight;
            var visible = HoverCardLayout.VisibleCards(model.Cards.Count, top, max, 760);
            Check.Equal(10000, visible.Last);
            Check.True(visible.Last - visible.First <= (int)Math.Ceiling(760 / HoverCardLayout.TicketHeight) + 2);
            Check.True(total > 280000);
            Check.Equal(760, HoverCardLayout.PixelHeight(model, 1, 760));
        });
        test("card layout: no ticket rows are painted before their section enters viewport", () =>
        {
            var visible = HoverCardLayout.VisibleCards(3, 1000, 0, 760);
            Check.Equal(0, visible.First);
            Check.Equal(0, visible.Last);
        });

        test("card: keyboard to pointer ownership transfer does not require Shell close", () =>
        {
            var session = new TrayHoverSession();
            session.Open(true, false);
            Check.True(session.ObservePointer(false, true, true, 0));
            Check.False(session.ShellOwnsLifetime);
            Check.True(session.ObservePointer(false, false, true, 10));
            Check.False(session.ObservePointer(false, false, true, 210));
        });

        test("compact card: 96DPI three-card weekly-only is300x288 and dual-window is300x322", () =>
        {
            var cards = new ResetCards(ResetCardDataStatus.Details, 3, Enumerable.Range(0, 3)
                .Select(_ => new ResetCard(ResetCardExpiryKind.Never, null)).ToArray());
            var weeklyOnly = State(cards: cards);
            weeklyOnly = weeklyOnly with { Selection = weeklyOnly.Selection! with { AvailableWindows = new[] { weeklyOnly.Quota! } } };
            var pro = HoverCardModel.Create(weeklyOnly, now);
            var plus = HoverCardModel.Create(State("plus", cards: cards), now);
            Check.Equal(300, HoverCardLayout.PixelWidth(HoverCardLayout.Scale(96, 1920)));
            Check.Equal(288, HoverCardLayout.PixelHeight(pro, 1, 1080));
            Check.Equal(322, HoverCardLayout.PixelHeight(plus, 1, 1080));
            Check.Equal(2, plus.Quotas.Count);
            Check.True(HoverCardLayout.ContentHeight(plus) - HoverCardLayout.ContentHeight(pro) <= 36);
        });
        foreach (var dpi in new[] { 96, 120, 144, 192 })
            test($"compact card: {dpi}DPI is applied exactly once", () =>
                Check.Equal((int)(300 * dpi / 96f), HoverCardLayout.PixelWidth(HoverCardLayout.Scale(dpi, 4000))));
        test("compact card: reset date and clock split without losing cross-year or missing text", () =>
        {
            foreach (var text in new[] { "10月14日 12:25", "2027年10月14日 23:59", "时间未知", "服务未返回" })
            {
                var parts = HoverCardLayout.ResetParts(text);
                Check.Equal(text, parts.Time.Length > 0 ? parts.Date + " " + parts.Time : parts.Date);
            }
        });
        test("compact card: extra windows gain rows without losing slots", () =>
        {
            var original = HoverCardModel.Create(State(), now);
            var four = original with { Quotas = Enumerable.Repeat(original.Quotas[0], 4).ToArray() };
            Check.Equal(2 * HoverCardLayout.CompactQuotaHeight, HoverCardLayout.QuotasHeight(four));
        });

        test("cleanup card: successful quota and reset cards retain their actual read time", () =>
        {
            var readAt = now.AddMinutes(-2);
            var cards = new ResetCards(ResetCardDataStatus.Details, 1,
                new[] { new ResetCard(ResetCardExpiryKind.Date, now.AddDays(1)) });
            var read = new QuotaRead(ReadStatus.Ready, new(72, now.AddHours(1), 10080), ResetCards: cards);
            var owner = new object();
            var cleanup = new QuotaCleanupPresentation();
            cleanup.BeginRead(owner);
            Check.True(cleanup.Observe(owner, new(QuotaDataSource.Cli, BackendCleanupState.Pending, read, readAt, 2000)));
            var state = cleanup.ApplyPendingRead(State());
            Check.Equal(72, state.Quota!.DisplayPercent);
            Check.Equal(readAt, state.LastSuccess!.Value);
            Check.Equal(readAt, state.ResetCardsLastSuccess!.Value);
            var model = HoverCardModel.Create(state, now, cleanupStatus: cleanup.Status);
            Check.Equal(72, model.Header.Percent!.Value);
            Check.Equal("1 张", model.CardCount);
            Check.Equal(readAt.ToLocalTime().ToString("HH:mm"), model.QuotaRefresh);
            Check.Equal(QuotaCleanupPresentation.PendingStatus, model.Status);
            Check.Contains(QuotaCleanupPresentation.PendingStatus, model.AccessibleText);
            Check.False(model.Quotas[0].IsStale);
        });

        test("cleanup card: a failed read preserves last trusted numbers and timestamps", () =>
        {
            var owner = new object();
            var cleanup = new QuotaCleanupPresentation();
            cleanup.BeginRead(owner);
            cleanup.Observe(owner, new(QuotaDataSource.Cli, BackendCleanupState.Pending, null, null, 2000));
            var trusted = State(cards: new(ResetCardDataStatus.CountOnly, 3, Array.Empty<ResetCard>()));
            var state = cleanup.ApplyPendingRead(trusted);
            Check.Equal(trusted.Quota, state.Quota);
            Check.Equal(trusted.LastSuccess, state.LastSuccess);
            Check.Equal(trusted.ResetCards, state.ResetCards);
            Check.Equal(trusted.ResetCardsLastSuccess, state.ResetCardsLastSuccess);
            var model = HoverCardModel.Create(state, now.AddMinutes(1), cleanupStatus: cleanup.Status);
            Check.Equal(80, model.Header.Percent!.Value);
            Check.Equal("3 张", model.CardCount);
            Check.True(model.Header.IsStale);
            Check.Equal(QuotaCleanupPresentation.PendingStatus, model.Status);
            Check.False(model.AccessibleText.Contains("读取失败", StringComparison.Ordinal));
            Check.False(model.AccessibleText.Contains("稍后重试", StringComparison.Ordinal));
        });

        test("cleanup card: absent data describes paused local cleanup instead of service omission", () =>
        {
            var model = HoverCardModel.Create(QuotaState.Initial, now,
                cleanupStatus: QuotaCleanupPresentation.PendingStatus);
            Check.Null(model.Header.Percent);
            Check.Equal("等待查询恢复", model.Header.Reset);
            Check.Equal("等待查询恢复", model.CardNote);
            Check.Equal("尚未更新", model.QuotaRefreshLabel);
            Check.False(model.AccessibleText.Contains("服务未返回", StringComparison.Ordinal));
            Check.Contains(QuotaCleanupPresentation.PendingStatus, model.AccessibleText);
        });

        test("cleanup state: confirmed cleanup does not refresh the quota timestamp", () =>
        {
            var owner = new object();
            var cleanup = new QuotaCleanupPresentation();
            var readAt = now.AddMinutes(-3);
            var read = new QuotaRead(ReadStatus.Ready, new(65, now.AddHours(2), 10080));
            cleanup.BeginRead(owner);
            cleanup.Observe(owner, new(QuotaDataSource.VsCode, BackendCleanupState.Pending, read, readAt, 2000));
            cleanup.Observe(owner, new(QuotaDataSource.VsCode, BackendCleanupState.Confirmed, read, readAt, 180000));
            Check.False(cleanup.RefreshPaused);
            Check.Equal(readAt, cleanup.ResultReadAt(owner, read, QuotaDataSource.VsCode, now));
            Check.Equal(now, cleanup.ResultReadAt(owner, read, QuotaDataSource.Cli, now));
            Check.Equal(now, cleanup.ResultReadAt(owner, read with { }, QuotaDataSource.VsCode, now));
            var state = QuotaState.Initial.Apply(read, cleanup.ResultReadAt(owner, read, QuotaDataSource.VsCode, now));
            Check.Equal(readAt, state.LastSuccess!.Value);
            Check.Equal("", HoverCardModel.Create(state, now).Status);
        });

        test("cleanup state: switching waits and separates selected policy from cleanup origin", () =>
        {
            using var gate = new SourceRequestGate(QuotaDataSource.Cli);
            Check.True(gate.TryBeginRead(out var request));
            var cleanup = new QuotaCleanupPresentation();
            cleanup.BeginRead(request!);
            var read = new QuotaRead(ReadStatus.Ready, new(91, now.AddHours(1), 10080));
            var pending = new BackendCleanupProgress(QuotaDataSource.Cli, BackendCleanupState.Pending, read, now, 2000);
            cleanup.Observe(request!, pending);
            Check.False(gate.TryBeginRead(out _));
            var change = gate.BeginSwitch(QuotaDataSource.Desktop);
            var selectedState = QuotaState.Initial;
            Check.False(request!.TryApply(() => selectedState = cleanup.ApplyPendingRead(selectedState)));
            Check.Null(selectedState.Quota);
            Check.False(change.Cleanup.IsCompleted);
            Check.False(gate.CompleteSwitch(change));
            Check.Contains("所选策略：Codex桌面端", cleanup.SourceExplanation(gate.Source));
            Check.Contains("清理来源：Codex CLI", cleanup.SourceExplanation(gate.Source));
            cleanup.Observe(request, pending with { State = BackendCleanupState.Confirmed });
            Check.False(cleanup.RefreshPaused);
            Check.False(gate.TryBeginRead(out _)); // Presentation cannot release the owned process lease.
            cleanup.CompleteRead(request);
            request.Dispose();
            Check.True(change.Cleanup.IsCompleted);
            Check.True(gate.CompleteSwitch(change));
            Check.True(gate.TryBeginRead(out var next));
            cleanup.BeginRead(next!);
            Check.False(cleanup.Observe(request, pending)); // Queued callback cannot revive old state.
            Check.False(cleanup.RefreshPaused);
            Check.Null(selectedState.Quota);
            next!.Dispose();
        });

        test("cleanup state: interrupted confirmation remains paused without erasing trusted quota", () =>
        {
            var owner = new object();
            var cleanup = new QuotaCleanupPresentation();
            cleanup.BeginRead(owner);
            cleanup.Observe(owner, new(QuotaDataSource.Cli, BackendCleanupState.Pending, null, null, 2000));
            var state = cleanup.ApplyPendingRead(State());
            Check.True(cleanup.Interrupt(owner, QuotaDataSource.Cli));
            Check.True(cleanup.CompleteRead(owner));
            Check.True(cleanup.RefreshPaused);
            Check.Equal(80, state.Quota!.DisplayPercent);
            Check.Equal(now, state.LastSuccess!.Value);
            Check.Equal(QuotaCleanupPresentation.InterruptedStatus, cleanup.Status);
            Check.False(cleanup.Observe(owner, new(QuotaDataSource.Cli, BackendCleanupState.Confirmed, null, null, 3000)));
        });

    }
}
