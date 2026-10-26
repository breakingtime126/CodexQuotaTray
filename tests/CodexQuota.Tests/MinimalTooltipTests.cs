using System.Text.Json;
using CodexQuota.Core;

static class MinimalTooltipTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FiveHourReset = Now.AddHours(3);
    private static readonly DateTimeOffset WeekReset = Now.AddDays(5).AddMinutes(-18);

    public static void Register(Action<string, Action> test)
    {
        foreach (var plan in new[] { "prolite", "pro", "promax" })
        foreach (var swapped in new[] { false, true })
            test($"minimal tooltip: healthy {plan} shows both actual windows in five-hour then weekly order, swapped={swapped}", () =>
            {
                var localReset = new DateTimeOffset(new DateTime(2026, 10, 10, 9, 42, 0, DateTimeKind.Local));
                var selection = Select(plan, Window(300, 32, FiveHourReset), Window(10080, 6, localReset), swapped);
                var text = State(selection).Tooltip(Now);
                Check.Equal($"5小时剩余 68%\n重置：{Local(FiveHourReset)}\n周额度剩余 94%\n重置：10-10 09:42", text);
                Check.Equal(2, selection.AvailableWindows.Count);
                Check.Equal(10080, selection.Active!.WindowMinutes);
                CheckHealthy(text);
            });

        foreach (var swapped in new[] { false, true })
            test($"minimal tooltip: healthy Plus has exactly two quota-reset pairs, swapped={swapped}", () =>
            {
                var selection = Select("plus", Window(300, 32, FiveHourReset), Window(10080, 6, WeekReset), swapped);
                var text = State(selection).Tooltip(Now);
                Check.Equal($"5小时剩余 68%\n重置：{Local(FiveHourReset)}\n周额度剩余 94%\n重置：{Local(WeekReset)}", text);
                Check.Equal(300, selection.Active!.WindowMinutes);
                CheckHealthy(text);
            });

        test("minimal tooltip: service Unix reset is displayed in the system local timezone", () =>
        {
            var serviceReset = new DateTimeOffset(2026, 10, 10, 1, 42, 0, TimeSpan.Zero);
            var selection = Select("pro", Window(10080, 6, serviceReset));
            Check.Equal(serviceReset, selection.Active!.ResetsAt);
            Check.Equal($"周额度剩余 94%\n重置：{Local(serviceReset)}", State(selection).Tooltip(Now));
        });

        test("minimal tooltip: healthy plan conflict has no selection explanation", () =>
        {
            var selection = Select("plus", Window(300, 32, FiveHourReset), Window(10080, 6, WeekReset), accountPlan: "pro");
            Check.True(selection.PlanConflict);
            var text = State(selection).Tooltip(Now);
            Check.Equal($"5小时剩余 68%\n重置：{Local(FiveHourReset)}\n周额度剩余 94%\n重置：{Local(WeekReset)}", text);
            CheckHealthy(text);
        });

        foreach (var plan in new[] { "prolite", "pro", "promax" })
            test($"minimal tooltip: {plan} missing weekly shows actual five-hour data", () =>
            {
                var selection = Select(plan, Window(300, 32, FiveHourReset));
                var text = State(selection).Tooltip(Now);
                Check.Equal($"5小时剩余 68%\n重置：{Local(FiveHourReset)}", text);
                CheckHealthy(text);
            });

        foreach (var duration in new[] { 300, 10080 })
            test($"minimal tooltip: Plus absent counterpart is hidden for actual duration {duration}", () =>
            {
                var selection = Select("plus", Window(duration, 32, WeekReset));
                var text = State(selection).Tooltip(Now);
                var actual = duration == 300 ? "5小时" : "周额度";
                var missing = duration == 300 ? "周额度" : "5小时";
                Check.Equal(actual + "剩余 68%\n重置：" + Local(WeekReset), text);
                Check.False(text.Contains(missing, StringComparison.Ordinal));
                CheckHealthy(text);
            });

        test("minimal tooltip: unknown reset remains unknown without suppressing known zero", () =>
        {
            var text = State(Select("pro", Window(10080, 100))).Tooltip(Now);
            Check.Equal("周额度剩余 0%\n重置：未知", text);
            CheckHealthy(text);
        });

        test("minimal tooltip: Plus reset values are independent and unknown is explicit", () =>
        {
            var text = State(Select("plus", Window(300, 32), Window(10080, 6, WeekReset))).Tooltip(Now);
            Check.Equal($"5小时剩余 68%\n重置：未知\n周额度剩余 94%\n重置：{Local(WeekReset)}", text);
            CheckHealthy(text);
        });

        test("minimal tooltip: nonstandard fallback keeps its actual cycle label", () =>
        {
            var selection = Select("plus", Window(60, 20, FiveHourReset));
            Check.Equal(60, selection.Active!.WindowMinutes);
            var text = State(selection).Tooltip(Now);
            Check.Contains("60分钟剩余 80%", text);
            Check.False(text.Contains("5小时", StringComparison.Ordinal));
            Check.False(text.Contains("周额度", StringComparison.Ordinal));
            Check.False(text.Contains("5小时剩余 80%", StringComparison.Ordinal));
            Check.False(text.Contains("周额度剩余 80%", StringComparison.Ordinal));
            CheckHealthy(text);
        });

        test("minimal tooltip: absent cycle duration is never invented as five hours or weekly", () =>
        {
            var text = State(Select(null, Window(null, 20, FiveHourReset))).Tooltip(Now);
            Check.Contains("未知时段剩余 80%", text);
            Check.Contains("重置：" + Local(FiveHourReset), text);
            Check.False(text.Contains("5小时", StringComparison.Ordinal));
            Check.False(text.Contains("周额度", StringComparison.Ordinal));
            CheckHealthy(text);
        });

        foreach (var expiredDuration in new[] { 300, 10080 })
            test($"minimal tooltip: only the expired {expiredDuration}-minute window gets a reset stale marker", () =>
            {
                var selection = Select("plus", Window(300, 32, expiredDuration == 300 ? Now : FiveHourReset),
                    Window(10080, 6, expiredDuration == 10080 ? Now : WeekReset));
                var state = State(selection);
                var text = state.Tooltip(Now);
                var stale = expiredDuration == 300 ? "5小时剩余 68%" : "周额度剩余 94%";
                var fresh = expiredDuration == 300 ? "周额度剩余 94%" : "5小时剩余 68%";
                Check.Contains(stale + "（过期）", text);
                Check.Contains(fresh + "\n", text);
                Check.False(text.Contains(fresh + "（过期）", StringComparison.Ordinal));
                Check.Equal(expiredDuration == 300, state.IsStale(Now));
                CheckLastSuccess(text);
            });

        test("minimal tooltip: ten-minute snapshot expiry marks both windows and adds last success", () =>
        {
            var state = State(Select("plus", Window(300, 32, FiveHourReset), Window(10080, 6, WeekReset)));
            CheckHealthy(state.Tooltip(Now.AddMinutes(10).AddTicks(-1)));
            var text = state.Tooltip(Now.AddMinutes(10));
            Check.Contains("5小时剩余 68%（过期）", text);
            Check.Contains("周额度剩余 94%（过期）", text);
            CheckLastSuccess(text);
            Check.False(text.Contains("每 30 秒", StringComparison.Ordinal));
        });

        foreach (var status in new[] { ReadStatus.Timeout, ReadStatus.Unavailable, ReadStatus.ProtocolError, ReadStatus.CliMissing, ReadStatus.Connecting, ReadStatus.RateLimited })
            test($"minimal tooltip: {status} retains values with per-window stale and last successful read", () =>
            {
                var state = State(Select("plus", Window(300, 32, FiveHourReset), Window(10080, 6, WeekReset)))
                    .Apply(new(status), Now.AddMinutes(1));
                var text = state.Tooltip(Now.AddMinutes(1));
                Check.Equal(Now, state.LastSuccess);
                Check.Contains("5小时剩余 68%（过期）", text);
                Check.Contains("周额度剩余 94%（过期）", text);
                CheckLastSuccess(text);
                Check.True(text.Split('\n').Length >= 6, "A failure must append last-success and short status lines.");
                Check.False(text.Contains("每 30 秒", StringComparison.Ordinal));
                Check.False(text.Contains("悬停即刷新", StringComparison.Ordinal));
                Check.True(text.Length <= 127);
            });

        test("minimal tooltip: successful recovery removes failure diagnostics", () =>
        {
            var selection = Select("pro", Window(10080, 6, WeekReset));
            var failed = State(selection).Apply(new(ReadStatus.Timeout), Now.AddMinutes(1));
            CheckLastSuccess(failed.Tooltip(Now.AddMinutes(1)));
            var recovered = failed.Apply(new(ReadStatus.Ready, selection.Active, selection), Now.AddMinutes(2));
            var text = recovered.Tooltip(Now.AddMinutes(2));
            Check.Equal($"周额度剩余 94%\n重置：{Local(WeekReset)}", text);
            CheckHealthy(text);
        });

        test("minimal tooltip: all status and plan combinations obey the native UTF-16 limit", () =>
        {
            foreach (var plan in new string?[] { "plus", "prolite", "pro", "promax", null, "future-plan-😀" })
            foreach (var status in Enum.GetValues<ReadStatus>())
            foreach (var age in new[] { TimeSpan.Zero, TimeSpan.FromMinutes(10) })
            {
                var selection = Select(plan, Window(300, 0, FiveHourReset), Window(10080, 0, WeekReset));
                var text = (State(selection) with { Status = status }).Tooltip(Now + age);
                Check.True(text.Length <= 127, $"Tooltip has {text.Length} UTF-16 code units for {plan}/{status}/{age}: {text}");
                Check.False(text.Contains("future-plan", StringComparison.Ordinal));
                Check.False(text.Contains("😀", StringComparison.Ordinal));
                Check.True(text.Contains("5小时", StringComparison.Ordinal) || text.Contains("5h", StringComparison.Ordinal));
                Check.Contains("周", text);
            }
        });
    }

    private static void CheckHealthy(string text)
    {
        foreach (var extra in new[] { "Codex", "Pro", "Plus", "套餐", "优先", "回退", "按可用窗口", "以额度套餐为准", "更新", "上次成功", "本地", "30 秒", "刷新", "过期" })
            Check.False(text.Contains(extra, StringComparison.Ordinal), $"Healthy tooltip must omit '{extra}': {text}");
        Check.True(text.Length <= 127);
    }

    private static void CheckLastSuccess(string text)
    {
        Check.True(text.Contains("更新", StringComparison.Ordinal) || text.Contains("上次成功", StringComparison.Ordinal),
            "Stale/error tooltip must include a last-success diagnostic.");
        Check.Contains(Now.ToLocalTime().ToString("MM-dd HH:mm"), text);
    }

    private static string Local(DateTimeOffset time) => time.ToLocalTime().ToString("MM-dd HH:mm");
    private static QuotaState State(QuotaSelection selection) => QuotaState.Initial.Apply(new(ReadStatus.Ready, selection.Active, selection), Now);
    private static string Window(int? duration, double used, DateTimeOffset? reset = null) =>
        JsonSerializer.Serialize(new { windowDurationMins = duration, usedPercent = used, resetsAt = reset?.ToUnixTimeSeconds() });
    private static QuotaSelection Select(string? plan, string primary, string secondary = "null", bool swapped = false, string? accountPlan = null)
    {
        if (swapped) (primary, secondary) = (secondary, primary);
        using var json = JsonDocument.Parse("{\"rateLimits\":{\"planType\":" + JsonSerializer.Serialize(plan) + ",\"primary\":" + primary + ",\"secondary\":" + secondary + "}}");
        return QuotaParser.ReadSelection(json.RootElement, accountPlan);
    }
}
