using System.Text.Json;
using CodexQuota.Core;

static class PlanQuotaTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
    private static readonly long FiveHourReset = Now.AddHours(3).ToUnixTimeSeconds();
    private static readonly long WeekReset = Now.AddDays(4).ToUnixTimeSeconds();

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        foreach (var plan in new[] { "plus", "prolite", "pro", "promax" })
        foreach (var swapped in new[] { false, true })
            test($"plan quota: {plan} selects exact duration with swapped={swapped}", () =>
            {
                var result = SelectBoth(plan, swapped);
                Check.Equal(plan == "plus" ? 300 : 10080, result.Active!.WindowMinutes);
                Check.Quota(result.Active, plan == "plus" ? 68 : 87, plan == "plus" ? 68 : 87);
                Check.Equal(DateTimeOffset.FromUnixTimeSeconds(plan == "plus" ? FiveHourReset : WeekReset), result.Active.ResetsAt);
                Check.Equal(2, result.AvailableWindows.Count);
                Check.Equal(QuotaSelectionReason.Preferred, result.Reason);
                Check.Equal(PlanSource.RateLimits, result.PlanSource);
                Check.False(result.PlanConflict);
            });

        foreach (var plan in new string?[] { null, "unknown", "free", "team", "business", "enterprise", "edu_pro", "self_serve_business_prolite", "future_plan" })
            test($"plan quota: no configured preference for {plan ?? "missing"} uses actual weekly", () =>
            {
                var result = SelectBoth(plan);
                Check.Equal(10080, result.Active!.WindowMinutes);
                Check.Null(result.PreferredWindowMinutes);
                Check.Equal(plan is null or "unknown" ? QuotaSelectionReason.UnknownPlan : QuotaSelectionReason.OtherPlan, result.Reason);
                Check.Contains("按可用窗口", result.DisplaySummary);
            });

        foreach (var plan in new[] { "plus", "prolite", "pro", "promax" })
            test($"plan quota: {plan} absent preferred window falls back to actual returned window", () =>
            {
                var alternate = plan == "plus" ? 10080 : 300;
                var result = QuotaParser.ReadSelection(Snapshot(plan, Window(alternate, 20), "null"));
                Check.Equal(QuotaSelectionReason.PreferredWindowUnavailable, result.Reason);
                Check.True(result.IsValid);
                Check.Equal(alternate, result.Active!.WindowMinutes);
                Check.Equal(1, result.AvailableWindows.Count);
                Check.Contains("回退", result.DisplaySummary);
                Check.Equal(plan == "plus" ? "周" : "5小时", result.Active.WindowLabel);
            });

        test("plan quota: primary-only weekly Plus stays weekly, never invented 5h", () =>
        {
            var result = QuotaParser.ReadSelection(Json("""{"rateLimits":{"planType":"plus","primary":{"usedPercent":15,"windowDurationMins":10080},"secondary":null}}"""));
            Check.Quota(result.Active, 85, 85);
            Check.Equal(10080, result.Active!.WindowMinutes);
            Check.Equal(QuotaSelectionReason.PreferredWindowUnavailable, result.Reason);
        });

        foreach (var plan in new string?[] { null, "unknown" })
            test($"plan quota: absent known quota plan {plan ?? "null"} falls back to account/read", () =>
            {
                var accountPlan = QuotaParser.ReadAccountPlanType(Json("""{"account":{"type":"chatgpt","planType":"plus","email":"not-saved@example.test"}}"""));
                var result = QuotaParser.ReadSelection(Snapshot(plan, Window(10080, 13), Window(300, 32)), accountPlan);
                Check.Equal(300, result.Active!.WindowMinutes);
                Check.Equal(PlanSource.Account, result.PlanSource);
                Check.False(result.PlanConflict);
            });

        foreach (var payload in new[] { "{}", "null", "{\"account\":null}", "{\"account\":{\"type\":\"apiKey\",\"planType\":\"plus\"}}", "{\"account\":{\"type\":\"chatgpt\",\"planType\":17}}" })
            test($"plan quota: account plan safely absent for {payload}", () => Check.Null(QuotaParser.ReadAccountPlanType(Json(payload))));

        test("plan quota: snapshot plan wins disagreement with account plan and flags conflict", () =>
        {
            var result = QuotaParser.ReadSelection(Snapshot("pro", Window(300, 32), Window(10080, 13)), "plus");
            Check.Equal("pro", result.PlanType);
            Check.Equal(10080, result.Active!.WindowMinutes);
            Check.True(result.PlanConflict);
            Check.Contains("以额度套餐为准", result.DisplaySummary);
        });

        test("plan quota: arbitrary plan string never leaks into display", () =>
        {
            var hostile = "untrusted private text\n" + new string('x', 1000);
            var result = SelectBoth(hostile);
            Check.False(result.DisplaySummary.Contains("untrusted", StringComparison.Ordinal));
            Check.Contains("其他套餐", result.DisplaySummary);
            Check.True(State(result).Tooltip(Now).Length <= 127);
        });

        test("plan quota: Codex bucket owns plan windows and resets", () =>
        {
            var result = QuotaParser.ReadSelection(Json("""
                {"rateLimitsByLimitId":{"other":{"planType":"pro","primary":{"usedPercent":99,"windowDurationMins":10080}},
                 "codex":{"planType":"plus","primary":{"usedPercent":32,"windowDurationMins":300,"resetsAt":1800000000},
                 "secondary":{"usedPercent":13,"windowDurationMins":10080,"resetsAt":1800500000}}},
                 "rateLimits":{"planType":"pro","primary":{"usedPercent":90,"windowDurationMins":10080}}}
                """), "plus");
            Check.Quota(result.Active, 68, 68);
            Check.Equal(300, result.Active!.WindowMinutes);
            Check.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000000), result.Active.ResetsAt);
            Check.Equal(DateTimeOffset.FromUnixTimeSeconds(1800500000), result.AvailableWindows[1].ResetsAt);
        });

        foreach (var badBucket in new[] { "null", "17", "{}", "{\"primary\":null,\"secondary\":null}" })
            test($"plan quota: authoritative empty Codex entry {badBucket} forbids legacy fallback", () =>
            {
                var result = QuotaParser.ReadSelection(Json("{\"rateLimitsByLimitId\":{\"codex\":" + badBucket + "},\"rateLimits\":{\"planType\":\"pro\",\"primary\":" + Window(10080, 1) + "}}"), "plus");
                Check.Null(result.Active);
                Check.Equal(badBucket is "{}" or "{\"primary\":null,\"secondary\":null}", result.IsValid);
                Check.Equal(0, result.AvailableWindows.Count);
                Check.Equal(QuotaSelectionReason.NoAvailableWindow, result.Reason);
                Check.Equal("plus", result.PlanType);
                Check.Null(QuotaParser.ReadWeekly(Json("{\"rateLimitsByLimitId\":{\"codex\":" + badBucket + "},\"rateLimits\":{\"primary\":" + Window(10080, 1) + "}}")));
            });

        test("plan quota: unrelated bucket cannot supply plan or quota", () =>
        {
            var result = QuotaParser.ReadSelection(Json("""{"rateLimitsByLimitId":{"other":{"planType":"plus","primary":{"usedPercent":20,"windowDurationMins":300}}},"rateLimits":{"limitId":"other","planType":"plus","primary":{"usedPercent":20,"windowDurationMins":300}}}"""));
            Check.Null(result.Active);
            Check.Null(result.PlanType);
        });

        test("plan quota: explicit null primary permits valid secondary", () =>
        {
            var result = QuotaParser.ReadSelection(Snapshot("plus", "null", Window(300, 32)));
            Check.True(result.IsValid);
            Check.Quota(result.Active, 68, 68);
            Check.Equal(QuotaSelectionReason.Preferred, result.Reason);
        });

        foreach (var primary in new[] { "{}", "17", "[]", "{\"usedPercent\":null,\"windowDurationMins\":300}", "{\"usedPercent\":\"20\",\"windowDurationMins\":300}" })
            test($"plan quota: malformed primary {primary} rejects snapshot despite valid secondary", () =>
            {
                var result = QuotaParser.ReadSelection(Snapshot("plus", primary, Window(10080, 32)));
                Check.False(result.IsValid);
                Check.Null(result.Active);
                Check.Equal(0, result.AvailableWindows.Count);
                var prior = State(SelectBoth("plus"));
                var state = prior.Apply(new(ReadStatus.Ready, result.Active, result), Now.AddSeconds(30));
                Check.Equal(ReadStatus.ProtocolError, state.Status);
                Check.Equal(prior.Selection, state.Selection);
                Check.Equal(prior.LastSuccess, state.LastSuccess);
                Check.Equal(2, HoverCardModel.Create(state, Now).Quotas.Count);
            });

        foreach (var window in new[] { "{\"usedPercent\":20}", "{\"usedPercent\":20,\"windowDurationMins\":null}" })
            test($"plan quota: duration missing/null remains explicitly unknown for {window}", () =>
            {
                var result = QuotaParser.ReadSelection(Snapshot("plus", window, "null"));
                Check.Quota(result.Active, 80, 80);
                Check.Null(result.Active!.WindowMinutes);
                Check.Equal("未知时段", result.Active.WindowLabel);
                Check.Equal(QuotaSelectionReason.PreferredWindowUnavailable, result.Reason);
                Check.Null(QuotaParser.ReadWeekly(Snapshot("plus", window, "null")));
            });

        foreach (var duration in new[] { "-1", "0", "300.5", "\"300\"", "true", "2147483648" })
            test($"plan quota: malformed duration {duration} cannot masquerade as a window", () =>
                Check.Null(QuotaParser.ReadSelection(Snapshot("plus", "{\"usedPercent\":20,\"windowDurationMins\":" + duration + "}", "null")).Active));

        test("plan quota: unknown plan with only 5h does not claim missing weekly", () =>
        {
            var result = QuotaParser.ReadSelection(Snapshot(null, "null", Window(300, 40)));
            Check.Equal(300, result.Active!.WindowMinutes);
            Check.Equal(QuotaSelectionReason.UnknownPlan, result.Reason);
            Check.Equal(ReadStatus.Ready, State(result).Status);
        });

        test("plan quota: another actual duration is labeled accurately", () =>
        {
            var result = QuotaParser.ReadSelection(Snapshot("plus", Window(60, 8), Window(null, 99)));
            Check.Equal(60, result.Active!.WindowMinutes);
            Check.Equal("60分钟", result.Active.WindowLabel);
            Check.Equal(2, result.AvailableWindows.Count);
        });

        test("plan quota: null or invalid reset does not discard either quota", () =>
        {
            var result = QuotaParser.ReadSelection(Snapshot("plus", "{\"usedPercent\":32,\"windowDurationMins\":300,\"resetsAt\":9223372036854775807}", Window(10080, 13)));
            Check.Equal(2, result.AvailableWindows.Count);
            Check.Null(result.Active!.ResetsAt);
            Check.Null(result.AvailableWindows[1].ResetsAt);
        });

        test("plan state: active duration reset and selection change together", () =>
        {
            var plus = SelectBoth("plus");
            var pro = SelectBoth("pro");
            var state = State(plus).Apply(new(ReadStatus.Ready, pro.Active, pro), Now.AddSeconds(30));
            Check.Equal(10080, state.Quota!.WindowMinutes);
            Check.Equal("pro", state.Selection!.PlanType);
            Check.Equal(DateTimeOffset.FromUnixTimeSeconds(WeekReset), state.Quota.ResetsAt);
            Check.False(state.IsStale(Now.AddSeconds(30)));
        });

        test("plan state: transient failure retains all windows and selection as stale", () =>
        {
            var selected = SelectBoth("plus");
            var state = State(selected).Apply(new(ReadStatus.Timeout), Now.AddSeconds(30));
            Check.Equal(selected, state.Selection);
            Check.Equal(selected.Active, state.Quota);
            Check.True(state.IsStale(Now.AddSeconds(30)));
            Check.Contains("读取超时", state.Tooltip(Now.AddSeconds(30)));
        });

        test("plan state: absent windows clear old selection and percentages", () =>
        {
            var empty = QuotaParser.ReadSelection(Json("{\"rateLimits\":{}}"), "pro");
            var state = State(SelectBoth("plus")).Apply(new(ReadStatus.NoQuotaWindow, null, empty), Now.AddSeconds(30));
            Check.Null(state.Quota);
            Check.Null(state.LastSuccess);
            Check.Equal(empty, state.Selection);
            Check.False(state.Tooltip(Now).Contains('%'));
        });

        test("plan tooltip: active and other window percentages and reset times visible", () =>
        {
            var text = State(SelectBoth("plus")).Tooltip(Now);
            Check.Contains("5小时", text);
            Check.Contains("68%", text);
            Check.Contains("周", text);
            Check.Contains("87%", text);
            Check.Contains(DateTimeOffset.FromUnixTimeSeconds(FiveHourReset).ToLocalTime().ToString("MM-dd HH:mm"), text);
            Check.Contains(DateTimeOffset.FromUnixTimeSeconds(WeekReset).ToLocalTime().ToString("MM-dd HH:mm"), text);
            Check.False(text.Contains("Plus", StringComparison.Ordinal));
            Check.True(text.Length <= 127);
        });

        test("plan tooltip: other reset becomes stale without staling active", () =>
        {
            var selected = QuotaParser.ReadSelection(Snapshot("plus", Window(300, 32, FiveHourReset), Window(10080, 13, Now.ToUnixTimeSeconds())));
            var state = State(selected);
            Check.False(state.IsStale(Now));
            Check.Contains("周额度剩余 87%（过期）", state.Tooltip(Now));
        });

        test("plan tooltip: longest real durations preserve truthful resets within127", () =>
        {
            var selected = QuotaParser.ReadSelection(Snapshot("plus", Window(int.MaxValue, 0, FiveHourReset), Window(int.MaxValue - 1, 0, WeekReset)), "pro");
            foreach (var status in Enum.GetValues<ReadStatus>())
            {
                var state = State(selected) with { Status = status };
                var text = state.Tooltip(Now);
                Check.True(text.Length <= 127, $"Tooltip length {text.Length}: {text}");
                Check.Contains(int.MaxValue.ToString(), text);
                Check.Contains((int.MaxValue - 1).ToString(), text);
                Check.Contains(DateTimeOffset.FromUnixTimeSeconds(FiveHourReset).ToLocalTime().ToString("MM-dd HH:mm"), text);
                Check.Contains(DateTimeOffset.FromUnixTimeSeconds(WeekReset).ToLocalTime().ToString("MM-dd HH:mm"), text);
                Check.False(text.Contains("回退", StringComparison.Ordinal));
                if (status == ReadStatus.Ready)
                {
                    Check.False(text.Contains("更新", StringComparison.Ordinal));
                    Check.False(text.Contains("上次成功", StringComparison.Ordinal));
                }
                else
                {
                    Check.True(text.Contains("更新", StringComparison.Ordinal) || text.Contains("上次成功", StringComparison.Ordinal));
                    Check.Contains(Now.ToLocalTime().ToString("MM-dd HH:mm"), text);
                }
            }
        });

        foreach (var plan in new[] { "prolite", "pro", "promax" })
        test($"plan tooltip: {plan} shows both returned windows in stable duration order", () =>
        {
            var selection = SelectBoth(plan);
            var text = State(selection).Tooltip(Now);
            Check.Equal(2, selection.AvailableWindows.Count);
            Check.Contains("周", text);
            Check.Contains("87%", text);
            Check.Contains("68%", text);
            Check.Contains("5小时", text);
            Check.True(text.IndexOf("5小时", StringComparison.Ordinal) < text.IndexOf("周", StringComparison.Ordinal));
        });

        test("plan tooltip: Pro absent weekly displays the returned five-hour window", () =>
        {
            var selection = QuotaParser.ReadSelection(Snapshot("pro", Window(300, 32), "null"));
            var state = State(selection);
            var text = state.Tooltip(Now);
            Check.Equal(300, state.Quota!.WindowMinutes);
            Check.Contains("5小时剩余 68%", text);
            Check.False(text.Contains("周", StringComparison.Ordinal));
            Check.False(text.Contains("未返回", StringComparison.Ordinal));
        });

        foreach (var duration in new[] { 300, 10080 })
            test($"plan tooltip: Plus only displays actual duration {duration}", () =>
            {
                var selected = QuotaParser.ReadSelection(Snapshot("plus", Window(duration, 32), "null"));
                var text = State(selected).Tooltip(Now);
                Check.Contains(duration == 300 ? "5小时" : "周", text);
                Check.False(text.Contains(duration == 300 ? "周" : "5小时", StringComparison.Ordinal));
                Check.False(text.Contains("未返回", StringComparison.Ordinal));
                Check.Contains("68%", text);
            });

        asyncTest("plan protocol: Pro without weekly falls back to returned five-hour window", async () =>
        {
            await using var peer = new FakePeer();
            var pending = CodexQuotaProtocol.ReadAsync(peer.Rpc, TimeSpan.FromSeconds(2), CancellationToken.None);
            await RespondNext(peer, new { });
            await peer.ReadAsync();
            await RespondNext(peer, Snapshot("pro", Window(300, 32), "null"));
            var result = await pending;
            Check.Equal(ReadStatus.Ready, result.Status);
            Check.Equal(300, result.Quota!.WindowMinutes);
            Check.Equal(1, result.Selection!.AvailableWindows.Count);
        });

        foreach (var plan in new[] { "plus", "prolite", "pro", "promax" })
            asyncTest($"plan protocol: quota snapshot {plan} selects preferred window without account reads", async () =>
            {
                await using var peer = new FakePeer();
                var pending = CodexQuotaProtocol.ReadAsync(peer.Rpc, TimeSpan.FromSeconds(2), CancellationToken.None);
                Check.Method(await RespondNext(peer, new { }), "initialize");
                Check.Method(await peer.ReadAsync(), "initialized");
                Check.Method(await RespondNext(peer, Snapshot(plan, Window(10080, 13), Window(300, 32))), "account/rateLimits/read");
                var result = await pending;
                Check.Equal(ReadStatus.Ready, result.Status);
                Check.Equal(plan == "plus" ? 300 : 10080, result.Quota!.WindowMinutes);
                Check.Equal(PlanSource.RateLimits, result.Selection!.PlanSource);
                Check.Equal(2, result.Selection.AvailableWindows.Count);
                await Check.StillPendingAsync(peer.ReadAsync());
            });
        foreach (var plan in new string?[] { "plus", "prolite", "pro", "promax", "renamed-plan", "unknown", null })
        foreach (var swapped in new[] { false, true })
            test($"data-driven rows: {plan ?? "missing"} displays both real durations regardless of slot {swapped}", () =>
            {
                var selection = SelectBoth(plan, swapped);
                Check.True(selection.IsValid);
                var model = HoverCardModel.Create(State(selection), Now);
                Check.Equal(2, model.Quotas.Count);
                Check.Equal("5 小时剩余", model.Quotas[0].Label);
                Check.Equal(68, model.Quotas[0].Percent!.Value);
                Check.Equal("本周剩余", model.Quotas[1].Label);
                Check.Equal(87, model.Quotas[1].Percent!.Value);
                Check.Equal(plan == "plus" ? model.Quotas[0] : model.Quotas[1], model.Header);
            });

        foreach (var plan in new string?[] { "plus", "pro", "renamed-plan", null })
        foreach (var duration in new[] { 300, 10080 })
            test($"data-driven rows: {plan ?? "missing"} real zero and no reset still display duration {duration}", () =>
            {
                var selection = QuotaParser.ReadSelection(Snapshot(plan, Window(duration, 100), "null"));
                Check.True(selection.IsValid);
                var state = State(selection);
                var quota = HoverCardModel.Create(state, Now).Quotas.Single();
                Check.Equal(0, quota.Percent!.Value);
                Check.Equal(duration == 300 ? "5 小时剩余" : "本周剩余", quota.Label);
                Check.Equal("时间未知", quota.Reset);
                Check.Equal("", quota.RelativeReset);
                Check.False(quota.IsStale);
                Check.Contains("0%", state.Tooltip(Now));
            });

        foreach (var plan in new string?[] { "plus", "pro", "renamed-plan", null })
        foreach (var retained in new[] { 300, 10080 })
            test($"data-driven rows: {plan ?? "missing"} authoritative null removes only absent window, keeps {retained}", () =>
            {
                var original = State(SelectBoth(plan));
                var selection = QuotaParser.ReadSelection(Snapshot(plan, "null", Window(retained, 10)));
                var refreshed = original.Apply(new(ReadStatus.Ready, selection.Active, selection), Now.AddSeconds(30));
                Check.Equal(Now.AddSeconds(30), refreshed.LastSuccess);
                Check.Equal(90, refreshed.Quota!.DisplayPercent);
                Check.Equal(retained, refreshed.Quota.WindowMinutes);
                var model = HoverCardModel.Create(refreshed, Now.AddSeconds(30));
                Check.Equal(1, model.Quotas.Count);
                Check.Equal(retained == 300 ? "5 小时剩余" : "本周剩余", model.Quotas[0].Label);
                Check.False(model.Quotas[0].IsStale);
                Check.False(model.AccessibleText.Contains(retained == 300 ? "周额度" : "5小时", StringComparison.Ordinal));
            });

        foreach (var bucket in new[] { "{}", "{\"primary\":null}", "{\"primary\":null,\"secondary\":null}" })
            test($"data-driven rows: valid empty bucket {bucket} clears obsolete quota without inventing duration", () =>
            {
                var selection = QuotaParser.ReadSelection(Json("{\"rateLimits\":" + bucket + "}"));
                Check.True(selection.IsValid);
                Check.Null(selection.Active);
                var refreshed = State(SelectBoth("plus")).Apply(new(ReadStatus.NoQuotaWindow, Selection: selection), Now.AddSeconds(30));
                Check.Null(refreshed.Quota);
                Check.Null(refreshed.LastSuccess);
                var model = HoverCardModel.Create(refreshed, Now.AddSeconds(30));
                Check.Equal(1, model.Quotas.Count);
                Check.Equal("额度剩余", model.Quotas[0].Label);
                Check.Null(model.Quotas[0].Percent);
                Check.False(model.AccessibleText.Contains("5小时", StringComparison.Ordinal));
                Check.False(model.AccessibleText.Contains("周额度", StringComparison.Ordinal));
            });

        foreach (var duration in new string?[] { null, "null", "60" })
            test($"data-driven rows: nonstandard duration {duration ?? "missing"} never guesses a five-hour or weekly identity", () =>
            {
                var window = "{\"usedPercent\":20" + (duration is null ? "" : ",\"windowDurationMins\":" + duration) + "}";
                var selection = QuotaParser.ReadSelection(Snapshot("pro", window, "null"));
                Check.True(selection.IsValid);
                var model = HoverCardModel.Create(State(selection), Now);
                Check.Equal(1, model.Quotas.Count);
                Check.Equal(duration == "60" ? "60 分钟剩余" : "未知时段剩余", model.Quotas[0].Label);
                Check.Equal(80, model.Quotas[0].Percent!.Value);
                Check.False(model.AccessibleText.Contains("5小时", StringComparison.Ordinal));
                Check.False(model.AccessibleText.Contains("周额度", StringComparison.Ordinal));
            });

        var malformedSnapshots = new[]
        {
            "{}", "null", "[]", "{\"rateLimits\":null}", "{\"rateLimits\":7}",
            "{\"rateLimitsByLimitId\":[]}",
            "{\"rateLimitsByLimitId\":{\"codex\":null},\"rateLimits\":{\"primary\":" + Window(300, 10) + "}}",
            "{\"rateLimitsByLimitId\":{\"codex\":false},\"rateLimits\":{\"primary\":" + Window(300, 10) + "}}",
            Snapshot("plus", "{}", Window(10080, 10)).GetRawText(),
            Snapshot("plus", "{\"windowDurationMins\":300}", Window(10080, 10)).GetRawText(),
            Snapshot("plus", "{\"usedPercent\":\"bad\",\"windowDurationMins\":300}", Window(10080, 10)).GetRawText(),
            Snapshot("plus", "{\"usedPercent\":1e400,\"windowDurationMins\":300}", Window(10080, 10)).GetRawText(),
            Snapshot("pro", Window(300, 10), "{\"usedPercent\":20,\"windowDurationMins\":0}").GetRawText(),
            Snapshot("pro", Window(300, 10), "{\"usedPercent\":20,\"windowDurationMins\":\"10080\"}").GetRawText()
        };
        foreach (var payload in malformedSnapshots)
        {
            asyncTest("data-driven protocol: malformed snapshot preserves prior rows " + payload, async () =>
            {
                await using var peer = new FakePeer();
                var pending = CodexQuotaProtocol.ReadAsync(peer.Rpc, TimeSpan.FromSeconds(2), CancellationToken.None);
                await RespondNext(peer, new { });
                await peer.ReadAsync();
                await RespondNext(peer, Json(payload));
                var read = await pending;
                Check.Equal(ReadStatus.ProtocolError, read.Status);
                Check.False(read.Selection!.IsValid);
                Check.Null(read.Quota);
                var original = State(SelectBoth("plus"));
                var failed = original.Apply(read, Now.AddSeconds(30));
                Check.Equal(original.Selection, failed.Selection);
                Check.Equal(original.Quota, failed.Quota);
                Check.Equal(original.LastSuccess, failed.LastSuccess);
                var model = HoverCardModel.Create(failed, Now.AddSeconds(30));
                Check.Equal(2, model.Quotas.Count);
                Check.True(model.Quotas.All(quota => quota.IsStale));
                Check.Equal(68, model.Quotas[0].Percent!.Value);
                Check.Equal(87, model.Quotas[1].Percent!.Value);
            });
            if (payload.StartsWith("{", StringComparison.Ordinal))
                test("data-driven embedded protocol: malformed snapshot is error not absence " + payload, () =>
                {
                    var envelope = ReadOnlyQuotaReaderTests.Envelope(result: payload);
                    var parsed = ReadOnlyQuotaProtocol.Parse(System.Text.Encoding.UTF8.GetBytes(envelope));
                    Check.Equal(ReadStatus.ProtocolError, parsed.Read.Status);
                    Check.Equal(CliErrorKind.InvalidResponse, parsed.ErrorKind);
                    var original = State(SelectBoth("pro"));
                    var failed = original.Apply(parsed.Read, Now.AddSeconds(30));
                    Check.Equal(original.Selection, failed.Selection);
                    Check.Equal(original.LastSuccess, failed.LastSuccess);
                    Check.Equal(2, HoverCardModel.Create(failed, Now.AddSeconds(30)).Quotas.Count);
                });
        }

        foreach (var failure in new[] { ReadStatus.Timeout, ReadStatus.ProtocolError, ReadStatus.Unavailable, ReadStatus.RateLimited })
            test($"data-driven rows: transient {failure} never removes the five-hour row", () =>
            {
                var original = State(SelectBoth("pro"));
                var failed = original.Apply(new(failure), Now.AddSeconds(30));
                var model = HoverCardModel.Create(failed, Now.AddSeconds(30));
                Check.Equal(2, model.Quotas.Count);
                Check.Equal("5 小时剩余", model.Quotas[0].Label);
                Check.True(model.Quotas.All(quota => quota.IsStale));
                Check.Equal(original.LastSuccess, failed.LastSuccess);
            });

    }

    private static async Task<JsonElement> RespondNext(FakePeer peer, object response)
    {
        var request = await peer.ReadAsync();
        await peer.RespondAsync(request, response);
        return request;
    }
    private static QuotaSelection SelectBoth(string? plan, bool swapped = false) => QuotaParser.ReadSelection(Snapshot(plan,
        swapped ? Window(10080, 13, WeekReset) : Window(300, 32, FiveHourReset),
        swapped ? Window(300, 32, FiveHourReset) : Window(10080, 13, WeekReset)));
    private static QuotaState State(QuotaSelection selected) => QuotaState.Initial.Apply(new(ReadStatus.Ready, selected.Active, selected), Now);
    private static string Window(int? minutes, double used, long? reset = null) => JsonSerializer.Serialize(new { windowDurationMins = minutes, usedPercent = used, resetsAt = reset });
    private static JsonElement Snapshot(string? plan, string primary, string secondary) => Json("{\"rateLimitsByLimitId\":{\"codex\":{\"planType\":" + JsonSerializer.Serialize(plan) + ",\"primary\":" + primary + ",\"secondary\":" + secondary + "}}}");
    private static JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
}
