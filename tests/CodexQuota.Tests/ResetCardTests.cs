using System.Globalization;
using System.Text.Json;
using CodexQuota.Core;

static class ResetCardTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Expiry = Now.AddDays(12);

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        foreach (var input in new[] { "{}", "null", "[]", "{\"rateLimitResetCredits\":null}" })
            test("reset cards: absent summary is unknown, not zero: " + input, () =>
            {
                var cards = Parse(input);
                Check.Equal(ResetCardDataStatus.NotReturned, cards.Status);
                Check.Equal("重置卡：未返回", string.Join("\n", cards.TooltipLines(Now)));
            });

        foreach (var count in new[] { "null", "-1", "\"3\"", "1.5", "9223372036854775808", "true" })
            test("reset cards: malformed count stays invalid: " + count, () =>
            {
                var cards = Parse("{\"rateLimitResetCredits\":{\"availableCount\":" + count + ",\"credits\":[]}}");
                Check.Equal(ResetCardDataStatus.Invalid, cards.Status);
                Check.Equal("重置卡：数据异常", cards.TooltipLines(Now).Single());
            });

        foreach (var details in new[] { "null", "[]", "{}" })
            test("reset cards: authoritative zero produces no fabricated expiry rows: " + details, () =>
                Check.Equal(0, Summary(0, details).TooltipLines(Now).Count));

        foreach (var details in new[] { "null", "[]", "{}", "\"unsupported\"" })
            test("reset cards: count without details never becomes no cards: " + details, () =>
                Check.Equal("重置卡：3张，日期未返回", Summary(3, details).TooltipLines(Now).Single()));

        test("reset cards: missing credits field is count-only", () =>
            Check.Equal(ResetCardDataStatus.CountOnly, Parse("{\"rateLimitResetCredits\":{\"availableCount\":2}}").Status));

        foreach (var count in new[] { 1, 2, 3, 4, 12, 1000 })
            test($"reset cards: {count} actual cards create exactly {count} expiry lines", () =>
            {
                var cards = Summary(count, "[" + string.Join(",", Enumerable.Range(0, count).Select(i => Card("card-" + i, Expiry.AddMinutes(i)))) + "]");
                var lines = cards.TooltipLines(Now);
                Check.Equal(count, cards.Cards.Count);
                Check.Equal(count, lines.Count);
                Check.True(lines.All(line => line.StartsWith("重置卡：", StringComparison.Ordinal)));
                Check.False(string.Join("\n", lines).Contains("完全重置", StringComparison.Ordinal));
            });

        test("reset cards: equal expiry dates remain separate cards", () =>
            Check.Equal(2, Summary(2, "[" + Card("a", Expiry) + "," + Card("b", Expiry) + "]").TooltipLines(Now).Count));

        test("reset cards: duplicate identifiers are not duplicated and incompleteness is visible", () =>
        {
            var cards = Summary(2, "[" + Card("a", Expiry) + "," + Card("a", Expiry) + "]");
            Check.Equal(1, cards.Cards.Count);
            Check.Equal("另1张日期未返回", cards.TooltipLines(Now).Last());
        });

        test("reset cards: backend-capped detail list keeps count and every returned row", () =>
        {
            var cards = Summary(8, "[" + Card("a", Expiry) + "," + Card("b", Expiry.AddDays(1)) + "]");
            Check.Equal(8L, cards.AvailableCount);
            Check.Equal(3, cards.TooltipLines(Now).Count);
            Check.Equal("另6张日期未返回", cards.TooltipLines(Now).Last());
        });

        test("reset cards: available count caps stale overcomplete details after sorting", () =>
        {
            var cards = Summary(1, "[" + Card("later", Expiry.AddDays(1)) + "," + Card("first", Expiry) + "]");
            Check.Equal(1, cards.Cards.Count);
            Check.Equal(Expiry, cards.Cards.Single().ExpiresAt);
            Check.Equal(0, Summary(0, "[" + Card("a", Expiry) + "]").TooltipLines(Now).Count);
        });

        foreach (var status in new[] { "redeeming", "redeemed", "unknown", "expired", "AVAILABLE" })
            test("reset cards: nonavailable status is not offered as a card: " + status, () =>
            {
                var cards = Summary(1, "[" + Card("a", Expiry, status) + "]");
                Check.Equal(0, cards.Cards.Count);
                Check.Equal("重置卡：1张，日期未返回", cards.TooltipLines(Now).Single());
            });

        test("reset cards: unknown type and malformed rows remain missing details", () =>
        {
            var rows = new[] { "null", "3", "{}", Card("a", Expiry, type: "unknown"), Card("", Expiry) };
            foreach (var row in rows)
                Check.Equal("重置卡：1张，日期未返回", Summary(1, "[" + row + "]").TooltipLines(Now).Single());
        });

        test("reset cards: explicit null expiry means no expiry and sorts after dated cards", () =>
        {
            var cards = Summary(2, "[" + Card("never", null) + "," + Card("date", Expiry) + "]");
            Check.Equal(ResetCardExpiryKind.Date, cards.Cards.First().ExpiryKind);
            Check.Equal("重置卡：不过期", cards.TooltipLines(Now).Last());
        });

        foreach (var expiry in new[] { "\"123\"", "1.5", "true", "[]", "253402300800", "-62135596801", "1e400" })
            test("reset cards: invalid expiry is unknown, never no-expiry: " + expiry, () =>
                Check.Equal("重置卡：日期未知", Summary(1,
                    "[{\"id\":\"a\",\"status\":\"available\",\"resetType\":\"codexRateLimits\",\"expiresAt\":" + expiry + "}]").TooltipLines(Now).Single()));

        test("reset cards: missing expiry field is unknown", () =>
            Check.Equal("重置卡：日期未知", Summary(1, "[{\"id\":\"a\",\"status\":\"available\",\"resetType\":\"codexRateLimits\"}]").TooltipLines(Now).Single()));

        test("reset cards: expiration is inclusive and derived from timestamp, not invented status", () =>
        {
            var cards = Summary(1, "[" + Card("a", Expiry) + "]");
            Check.True(cards.TooltipLines(Expiry.AddTicks(-1)).Single().EndsWith("到期", StringComparison.Ordinal));
            Check.False(cards.TooltipLines(Expiry.AddTicks(-1)).Single().Contains("已到期", StringComparison.Ordinal));
            Check.True(cards.TooltipLines(Expiry).Single().EndsWith("已到期", StringComparison.Ordinal));
        });

        test("reset cards: local date conversion and cross-year disambiguation", () =>
        {
            var utcExpiry = new DateTimeOffset(2027, 1, 1, 0, 30, 0, TimeSpan.Zero);
            var local = utcExpiry.ToLocalTime();
            var expected = "重置卡：" + local.ToString(local.Year == Now.ToLocalTime().Year ? "M月dd日 HH:mm:ss" : "yyyy年M月dd日 HH:mm:ss", CultureInfo.InvariantCulture) + "到期";
            Check.Equal(expected, Summary(1, "[" + Card("a", utcExpiry) + "]").TooltipLines(Now).Single());
        });

        foreach (var at in new[] {
            new DateTimeOffset(2026, 10, 23, 14, 30, 37, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero),
            new DateTimeOffset(2027, 1, 1, 0, 0, 1, TimeSpan.Zero) })
            test("reset cards: exact official seconds and local year preserved " + at.ToUnixTimeSeconds(), () =>
            {
                var cards = Summary(1, "[" + Card("seconds", at) + "]");
                var actual = cards.Cards.Single();
                var local = at.ToLocalTime();
                var expected = local.ToString(local.Year == Now.ToLocalTime().Year ? "M月dd日 HH:mm:ss" : "yyyy年M月dd日 HH:mm:ss", CultureInfo.InvariantCulture);
                Check.Equal(at.ToUnixTimeSeconds(), actual.ExpiresAt!.Value.ToUnixTimeSeconds());
                Check.Equal(expected, actual.ExpiryText(Now));
                Check.Contains(expected, cards.TooltipLines(Now).Single());
                var state = QuotaState.Initial.Apply(new(ReadStatus.Ready, new(80, Now.AddDays(2), 10080), null, cards), Now);
                var model = HoverCardModel.Create(state, Now);
                Check.Equal(expected, model.Cards.Single().Date);
                Check.Contains(expected, model.AccessibleText);
            });
        test("reset cards: different expiry seconds remain distinct and expiration is inclusive", () =>
        {
            var at = Now.AddHours(2).AddSeconds(59);
            var first = new ResetCard(ResetCardExpiryKind.Date, at.AddSeconds(-1));
            var last = new ResetCard(ResetCardExpiryKind.Date, at);
            Check.False(first.ExpiryText(Now) == last.ExpiryText(Now));
            var cards = new ResetCards(ResetCardDataStatus.Details, 1, new[] { last });
            Check.False(cards.TooltipLines(at.AddTicks(-1)).Single().Contains("已到期", StringComparison.Ordinal));
            Check.True(cards.TooltipLines(at).Single().Contains("已到期", StringComparison.Ordinal));
        });
        test("reset cards: missing and never-expiring values do not invent clock precision", () =>
        {
            foreach (var card in new[] { new ResetCard(ResetCardExpiryKind.Never, null), new ResetCard(ResetCardExpiryKind.Unknown, null), new ResetCard(ResetCardExpiryKind.Date, null) })
                Check.False(card.ExpiryText(Now).Contains(":", StringComparison.Ordinal));
        });
        test("reset cards: seconds use a dedicated readable line without widening the card", () =>
        {
            Check.Equal(12f, HoverCardLayout.TicketTextSize("10月23日 14:30:59"));
            Check.Equal(12f, HoverCardLayout.TicketTextSize("2027年10月23日 14:30:59"));
            Check.Equal(280f, HoverCardLayout.Width);
            Check.Equal(28f, HoverCardLayout.TicketHeight);
        });

        test("reset cards: day is zero-padded without padding month or losing seconds", () =>
        {
            var at = new DateTimeOffset(2027, 1, 7, 12, 42, 50, TimeSpan.Zero);
            var local = at.ToLocalTime();
            var card = new ResetCard(ResetCardExpiryKind.Date, at);
            var text = card.ExpiryText(Now);
            Check.Contains($"{local.Month}月{local.Day:00}日", text);
            Check.False(text.Contains("01月", StringComparison.Ordinal));
            Check.True(text.EndsWith(local.ToString("HH:mm:ss", CultureInfo.InvariantCulture), StringComparison.Ordinal));
        });
        test("hover dates: quota reset and last-success day use the same two-digit day", () =>
        {
            var at = new DateTimeOffset(2027, 1, 7, 12, 42, 50, TimeSpan.Zero);
            var local = at.ToLocalTime();
            var state = new QuotaState(new(80, at, 10080), Now, ReadStatus.Timeout);
            var model = HoverCardModel.Create(state, Now);
            Check.Contains($"{local.Month}月{local.Day:00}日", model.Quotas.Single().Reset);
            Check.Equal(Now.ToLocalTime().ToString("M月dd日 HH:mm", CultureInfo.InvariantCulture), model.LastSuccess);
        });

        test("reset cards: backend strings and identifiers never appear in tooltip", () =>
        {
            var card = Card("private-identifier", Expiry).TrimEnd('}') + ",\"title\":\"完全重置\",\"description\":\"private text\"}";
            var text = Summary(1, "[" + card + "]").TooltipLines(Now).Single();
            Check.False(text.Contains("private", StringComparison.Ordinal));
            Check.False(text.Contains("完全重置", StringComparison.Ordinal));
        });

        test("reset cards: tooltip preserves original quota rows and all card rows beyond native limit", () =>
        {
            var state = QuotaState.Initial.Apply(new(ReadStatus.Ready, new(94, Expiry, 10080)), Now);
            var quota = state.Tooltip(Now);
            var cards = Summary(100, "[" + string.Join(",", Enumerable.Range(0, 100).Select(i => Card(i.ToString(), Expiry))) + "]");
            var text = (state with { ResetCards = cards }).Tooltip(Now);
            Check.True(text.StartsWith(quota + "\n", StringComparison.Ordinal));
            Check.Equal(quota.Split('\n').Length + 100, text.Split('\n').Length);
            Check.True(text.Length > 127);
        });

        foreach (var plan in new[] { "plus", "prolite", "pro", "promax" })
            test("reset cards: actual quota rows remain an identical prefix for every plan: " + plan, () =>
            {
                using var json = JsonDocument.Parse("{\"rateLimits\":{\"planType\":\"" + plan + "\",\"primary\":{\"usedPercent\":25,\"windowDurationMins\":300},\"secondary\":{\"usedPercent\":6,\"windowDurationMins\":10080}}}");
                var selected = QuotaParser.ReadSelection(json.RootElement);
                var state = QuotaState.Initial.Apply(new(ReadStatus.Ready, selected.Active, selected), Now);
                var prefix = state.Tooltip(Now);
                var cards = Summary(2, "[" + Card("a", Expiry) + "," + Card("b", Expiry.AddDays(1)) + "]");
                var text = (state with { ResetCards = cards }).Tooltip(Now);
                Check.Equal(prefix + "\n" + string.Join("\n", cards.TooltipLines(Now)), text);
                Check.Equal(6, text.Split('\n').Length);
            });

        test("reset cards: transient failures retain last snapshot with existing stale diagnostics", () =>
        {
            var cards = Summary(1, "[" + Card("a", Expiry) + "]");
            var state = QuotaState.Initial.Apply(new(ReadStatus.Ready, new(94, Expiry, 10080), null, cards), Now)
                .Apply(new(ReadStatus.Timeout), Now.AddMinutes(1));
            Check.True(ReferenceEquals(cards, state.ResetCards));
            Check.Contains("读取超时", state.Tooltip(Now.AddMinutes(1)));
            Check.Contains("上次成功", state.Tooltip(Now.AddMinutes(1)));
            Check.Contains(cards.TooltipLines(Now).Single(), state.Tooltip(Now.AddMinutes(1)));
        });

        test("reset cards: fresh empty or unavailable snapshot replaces prior cards", () =>
        {
            var state = QuotaState.Initial.Apply(new(ReadStatus.Ready, new(94, Expiry, 10080), null, Summary(1, "[" + Card("a", Expiry) + "]")), Now);
            foreach (var fresh in new[] { Summary(0, "[]"), ResetCards.NotReturned })
            {
                var updated = state.Apply(new(ReadStatus.Ready, state.Quota, null, fresh), Now.AddMinutes(1));
                Check.True(ReferenceEquals(fresh, updated.ResetCards));
                Check.False(updated.Tooltip(Now.AddMinutes(1)).Contains("到期", StringComparison.Ordinal));
            }
        });

        test("reset cards: account changes clear old cards; quota-less read can still return real cards", () =>
        {
            var cards = Summary(1, "[" + Card("a", Expiry) + "]");
            var state = QuotaState.Initial.Apply(new(ReadStatus.Ready, new(94, Expiry, 10080), null, cards), Now);
            foreach (var status in new[] { ReadStatus.NotSignedIn, ReadStatus.ApiKey, ReadStatus.UnsupportedAccount })
                Check.Null(state.Apply(new(status), Now).ResetCards);
            Check.True(ReferenceEquals(cards, state.Apply(new(ReadStatus.NoQuotaWindow, null, null, cards), Now).ResetCards));
        });

        test("reset cards: quota-less card snapshot retains its own success timestamp on failure", () =>
        {
            var cards = Summary(1, "[" + Card("a", Expiry) + "]");
            var state = QuotaState.Initial.Apply(new(ReadStatus.NoQuotaWindow, null, null, cards), Now)
                .Apply(new(ReadStatus.Timeout), Now.AddMinutes(1));
            Check.Null(state.LastSuccess);
            Check.Equal(Now, state.ResetCardsLastSuccess);
            Check.Contains("上次成功：" + Now.ToLocalTime().ToString("MM-dd HH:mm"), state.Tooltip(Now.AddMinutes(1)));
            Check.Contains("读取超时", state.Tooltip(Now.AddMinutes(1)));
            Check.Contains(cards.TooltipLines(Now).Single(), state.Tooltip(Now.AddMinutes(1)));
            Check.Null(state.Apply(new(ReadStatus.NotSignedIn), Now.AddMinutes(2)).ResetCardsLastSuccess);
        });

        asyncTest("reset cards: protocol reads official details in existing request without mutation RPCs", async () =>
        {
            await using var peer = new FakePeer();
            var task = CodexQuotaProtocol.ReadAsync(peer.Rpc, TimeSpan.FromSeconds(2), CancellationToken.None);
            await peer.RespondAsync(await peer.ReadAsync(), new { });
            Check.Method(await peer.ReadAsync(), "initialized");
            var limits = await peer.ReadAsync();
            Check.Method(limits, "account/rateLimits/read");
            Check.False(limits.GetProperty("params").TryGetProperty("excludeResetCreditDetails", out var excluded) && excluded.GetBoolean());
            using var response = JsonDocument.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":6,\"windowDurationMins\":10080}},\"rateLimitResetCredits\":{\"availableCount\":1,\"credits\":[" + Card("a", Expiry) + "]}}");
            await peer.RespondAsync(limits, response.RootElement);
            var read = await task;
            Check.Equal(ReadStatus.Ready, read.Status);
            Check.Equal(1L, read.ResetCards!.AvailableCount);
            Check.Equal(Expiry, read.ResetCards.Cards.Single().ExpiresAt);
            await Check.StillPendingAsync(peer.ReadAsync());
        });
    }

    private static ResetCards Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ResetCards.Parse(document.RootElement);
    }
    private static ResetCards Summary(long count, string rows) => Parse("{\"rateLimitResetCredits\":{\"availableCount\":" + count.ToString(CultureInfo.InvariantCulture) + ",\"credits\":" + rows + "}}");
    private static string Card(string id, DateTimeOffset? expiresAt, string status = "available", string type = "codexRateLimits") =>
        JsonSerializer.Serialize(new { id, resetType = type, status, grantedAt = Now.AddDays(-1).ToUnixTimeSeconds(), expiresAt = expiresAt?.ToUnixTimeSeconds(), title = "完全重置", description = (string?)null });
}
