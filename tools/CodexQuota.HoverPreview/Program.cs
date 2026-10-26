using System.Drawing;
using System.Drawing.Imaging;
using CodexQuota.Core;
using CodexQuota.Tray;

var output = Path.GetFullPath(args.Length > 0 ? args[0] : "docs/previews/hover");
Directory.CreateDirectory(output);
// These are explicitly synthetic QA fixtures, never runtime fallback values.
var now = new DateTimeOffset(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);
QuotaState State(int percent, string plan = "pro", int cards = 3, bool stale = false)
{
    var weekly = new QuotaWindow(percent, now.AddDays(6).AddHours(5).AddMinutes(5), QuotaParser.WeeklyMinutes);
    var five = new QuotaWindow(28, now.AddHours(3), QuotaParser.FiveHourMinutes);
    var selection = new QuotaSelection(plan, PlanSource.RateLimits, plan == "plus" ? 300 : 10080,
        plan == "plus" ? five : weekly, plan == "plus" ? new[] { five, weekly } : new[] { weekly }, QuotaSelectionReason.Preferred);
    var tickets = Enumerable.Range(0, cards).Select(index => new ResetCard(ResetCardExpiryKind.Date,
        now.AddDays(index == 0 ? 10 : index == 1 ? 17 : index == 2 ? 25 : index + 30)
            .AddHours(index == 0 ? 14 : index == 1 ? 12 : 16)
            .AddMinutes(index == 0 ? 2 : index == 1 ? 34 : 45)
            .AddSeconds(index == 1 ? 27 : 50))).ToArray();
    return new(selection.Active, now.AddMinutes(stale ? -11 : -1), stale ? ReadStatus.Timeout : ReadStatus.Ready,
        selection, new(ResetCardDataStatus.Details, cards, tickets), now.AddMinutes(stale ? -11 : -1));
}
var warning = State(9) with { ResetCards = new(ResetCardDataStatus.Details, 5, new[] {
    new ResetCard(ResetCardExpiryKind.Date, now.AddHours(-1)), new ResetCard(ResetCardExpiryKind.Date, now.AddHours(12)),
    new ResetCard(ResetCardExpiryKind.Date, now.AddDays(4)), new ResetCard(ResetCardExpiryKind.Never, null),
    new ResetCard(ResetCardExpiryKind.Unknown, null) }) };
var plusFull = State(100, "plus");
var fullFive = plusFull.Selection!.AvailableWindows[0] with { RemainingPercent = 100, ResetsAt = now.AddMonths(4) };
var fullWeek = plusFull.Selection.AvailableWindows[1] with { ResetsAt = now.AddYears(1).AddDays(6) };
plusFull = plusFull with { Quota = fullFive, Selection = plusFull.Selection with { Active = fullFive, AvailableWindows = new[] { fullFive, fullWeek } } };
var plusMissing = State(100, "plus");
plusMissing = plusMissing with { Quota = plusMissing.Selection!.AvailableWindows[1], Selection = plusMissing.Selection with {
    Active = plusMissing.Selection.AvailableWindows[1], AvailableWindows = new[] { plusMissing.Selection.AvailableWindows[1] } } };
var exactExpiry = State(87) with { ResetCards = new(ResetCardDataStatus.Details, 3, new[] {
    new ResetCard(ResetCardExpiryKind.Date, new DateTimeOffset(2026, 10, 23, 20, 0, 37, TimeSpan.Zero)),
    new ResetCard(ResetCardExpiryKind.Date, new DateTimeOffset(2027, 12, 23, 14, 30, 59, TimeSpan.Zero)),
    new ResetCard(ResetCardExpiryKind.Unknown, null) }) };
var expiryBoundaries = State(87) with { ResetCards = new(ResetCardDataStatus.Details, 8, new[] {
    new ResetCard(ResetCardExpiryKind.Date, now.AddDays(11).AddHours(5)),
    new ResetCard(ResetCardExpiryKind.Date, now.AddDays(11).AddHours(23)),
    new ResetCard(ResetCardExpiryKind.Date, now.AddHours(23).AddMinutes(59)),
    new ResetCard(ResetCardExpiryKind.Date, now.AddMinutes(59)),
    new ResetCard(ResetCardExpiryKind.Date, now.AddSeconds(1)),
    new ResetCard(ResetCardExpiryKind.Date, now),
    new ResetCard(ResetCardExpiryKind.Never, null),
    new ResetCard(ResetCardExpiryKind.Unknown, null) }) };
var fixtures = new Dictionary<string, QuotaState>
{
    ["weekly"] = State(90), ["plus"] = State(90, "plus"), ["red-expiries"] = warning,
    ["amber"] = State(30), ["zero"] = State(0, cards: 0), ["full"] = State(100, cards: 0),
    ["stale"] = State(90, stale: true), ["unknown"] = QuotaState.Initial,
    ["missing"] = State(62) with { ResetCards = new(ResetCardDataStatus.CountOnly, 12, Array.Empty<ResetCard>()) },
    ["long"] = State(81, "plus", 10000), ["plus-full-crossyear"] = plusFull, ["plus-missing"] = plusMissing, ["plus-stale"] = State(80, "plus", stale: true), ["expiry-seconds"] = exactExpiry,
    ["expiry-boundaries"] = expiryBoundaries,
    ["tps-response"] = State(87), ["tps-response-history"] = State(87), ["tps-user-format"] = State(87), ["tps-retained"] = State(87), ["tps-retained-long"] = State(87), ["tps-retained-crossyear"] = State(87),
    ["tps-unknown"] = State(87), ["tps-unknown-model"] = State(87), ["tps-no-effort"] = State(87),
    ["tps-crossyear"] = State(87), ["tps-long-model"] = State(87), ["tps-unavailable"] = State(87)
};
var resetPlus = State(100, "plus", cards: 0);
var resetFive = resetPlus.Selection!.AvailableWindows[0] with { RemainingPercent = 100, ResetsAt = now.AddHours(4).AddMinutes(47) };
var resetWeek = resetPlus.Selection.AvailableWindows[1] with { ResetsAt = now.AddDays(6).AddHours(23) };
resetPlus = resetPlus with { Quota = resetFive,
    Selection = resetPlus.Selection with { Active = resetFive, AvailableWindows = new[] { resetFive, resetWeek } } };
var resetWeekly = State(100);
foreach (var key in new[] { "reset-plus", "reset-latest-only", "reset-forecast-only", "reset-no-link",
    "reset-offline", "reset-expired", "reset-baseline", "reset-long" })
    fixtures[key] = resetPlus;
fixtures["reset-weekly"] = resetWeekly;
foreach (var key in new[] { "reset-scheduled-hour", "reset-scheduled-75min", "reset-scheduled-nextday", "reset-scheduled-overdue" })
{
    fixtures[key] = resetWeekly;
    fixtures[key + "-plus"] = resetPlus;
}
var resetUrl = new Uri("https://x.com/thsottiaux/status/123456789");
var feedSnapshot = new ResetFeedSnapshot(new("qa-latest", "regular", now.AddMinutes(-30), "Synthetic announcement; not account reset.", resetUrl),
    null, new("strong", 85, "今晚可能重置", now.AddMinutes(-10), now.AddHours(6), "Synthetic prediction.", resetUrl), now);
ResetFeedPresentation Feed(string key)
{
    var snapshot = feedSnapshot;
    if (key.StartsWith("reset-scheduled-", StringComparison.Ordinal))
    {
        var due = key.Contains("75min") ? now.AddMinutes(75) : key.Contains("nextday") ? now.AddDays(1).AddMinutes(75)
            : key.Contains("overdue") ? now.AddMinutes(-1) : now.AddHours(1);
        snapshot = snapshot with { ActiveWatch = null, ScheduledReset = new("qa-scheduled", "regular",
            now.AddHours(-1), due, "Synthetic scheduled reset for layout QA.", resetUrl) };
    }
    if (key == "reset-latest-only") snapshot = snapshot with { ActiveWatch = null };
    if (key == "reset-forecast-only") snapshot = snapshot with { LatestReset = null };
    if (key == "reset-no-link") snapshot = snapshot with {
        LatestReset = snapshot.LatestReset! with { SourceUrl = null },
        ActiveWatch = snapshot.ActiveWatch! with { SourceUrl = null } };
    if (key == "reset-expired") snapshot = snapshot with { LatestReset = null,
        ActiveWatch = snapshot.ActiveWatch! with { ExpiresAt = now } };
    if (key == "reset-long") snapshot = snapshot with { ActiveWatch = snapshot.ActiveWatch! with {
        ForecastWindow = "A very long forecast window stays one line with an accessible complete tooltip" } };
    return ResetFeedPresentation.Create(new(key == "reset-offline" ? ResetFeedReadStatus.NetworkError :
        ResetFeedReadStatus.Success, snapshot), now);
}
if (args.Contains("--reset-only"))
    fixtures = fixtures.Where(pair => pair.Key.StartsWith("reset-", StringComparison.Ordinal))
        .ToDictionary(pair => pair.Key, pair => pair.Value);
OutputSpeedView Speed(string fixture)
{
    var end = fixture is "tps-crossyear" or "tps-retained-crossyear" ? now.AddYears(-1).AddDays(-1) : now.AddSeconds(-30);
    var response = fixture is "tps-response" or "tps-response-history";
    var average = fixture is "tps-user-format" or "tps-retained" or "tps-retained-long" or "tps-retained-crossyear";
    var model = response ? "gpt-6.1-sol" : fixture is "tps-user-format" or "tps-retained" or "tps-retained-crossyear" ? "gpt-6.1-sol" : fixture is "tps-long-model" or "tps-retained-long" ? "gpt-5-codex-a-very-long-experimental-model-name"
        : fixture == "tps-unknown-model" ? null : "gpt-5.4";
    var sample = new OutputSpeedSample("synthetic-thread", "synthetic-response", model, 302, end.AddSeconds(-10), end)
        {
            ReasoningEffort = fixture is "tps-unknown-model" or "tps-no-effort" ? null : average || response ? "medium" : "xhigh",
            TimingReliability = response ? OutputSpeedTimingReliability.ResponseRateEstimate : average ? OutputSpeedTimingReliability.TurnAverage : OutputSpeedTimingReliability.UnverifiedItemLifecycle,
            ThreadRequestedSetting = average || response ? ThreadRequestedSetting.Fast : ThreadRequestedSetting.Unknown,
            IsHistoricalOtherThread = fixture == "tps-response-history",
            ResponseRate = response ? new(ResponseRateStartBoundary.ToolResult, ResponseRateEndBoundary.TokenUsageRecord, ResponseRateUsageKind.PerResponse) : null,
            IsRetainedTurnAverage = fixture is "tps-retained" or "tps-retained-long" or "tps-retained-crossyear",
            TurnAverage = average ? new TurnAverageEvidence(10000, new[] { new TurnResponseUsage("synthetic-response", 302, 200, 302) }) : null
        };
    return OutputSpeedView.Create(fixture == "tps-unknown" ? null : sample, now, unavailable: fixture == "tps-unavailable");
}
var overflow = new List<string>();
foreach (var fixture in fixtures)
foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
foreach (var darkMode in new[] { false, true })
foreach (var highContrast in new[] { false, true })
{
    var model = HoverCardModel.Create(fixture.Value, now, Speed(fixture.Key));
    if (fixture.Key.StartsWith("reset-", StringComparison.Ordinal) && fixture.Key != "reset-baseline")
        model = model with { ResetFeed = Feed(fixture.Key) };
    var height = Math.Min(760, HoverCardLayout.ContentHeight(model) + 2 * HoverCardLayout.Shadow);
    var size = new Size((int)Math.Ceiling((HoverCardLayout.Width + 2 * HoverCardLayout.Shadow) * scale), (int)Math.Ceiling(height * scale));
    var suffix = highContrast ? darkMode ? "-dark-contrast" : "-contrast" : darkMode ? "-dark" : "";
    var painter = new AuditedHoverTextPainter(fixture.Key + suffix + " / " + scale, overflow,
        (model.OutputSpeed?.FastPrefix ?? "") + (model.OutputSpeed?.CompactModel ?? ""),
        fixture.Key == "reset-long" ? model.ResetFeed?.Forecast?.Detail : null);
    using var image = HoverCardRenderer.Render(model, size, scale, 0, highContrast,
        uiFamily: "Noto Sans CJK SC", numberFamily: "DejaVu Sans", textPainter: painter, darkMode: darkMode);
    image.Save(Path.Combine(output, $"{fixture.Key}{suffix}-{scale:0.##}.png"), ImageFormat.Png);
    if (fixture.Key == "long")
    {
        using var end = HoverCardRenderer.Render(model, size, scale, HoverCardLayout.ContentHeight(model) - (height - 2 * HoverCardLayout.Shadow), highContrast,
            uiFamily: "Noto Sans CJK SC", numberFamily: "DejaVu Sans", textPainter: painter, darkMode: darkMode);
        end.Save(Path.Combine(output, $"long-end{suffix}-{scale:0.##}.png"), ImageFormat.Png);
    }
    Console.WriteLine($"{fixture.Key}{suffix} / {scale}: {size.Width}×{size.Height}, {model.Cards.Count} rows");
}
var hcModel = HoverCardModel.Create(warning, now, Speed("high-contrast"));
using (var highContrast = HoverCardRenderer.Render(hcModel, new Size(HoverCardLayout.PixelWidth(1), HoverCardLayout.PixelHeight(hcModel, 1, 760)), 1, 0, true,
    uiFamily: "Noto Sans CJK SC", numberFamily: "DejaVu Sans")) highContrast.Save(Path.Combine(output, "high-contrast.png"), ImageFormat.Png);
Console.WriteLine("Shared production GDI+ painter; synthetic data and Linux fonts. Not Windows/Shell screenshots.");
File.WriteAllLines(Path.Combine(output, "text-overflow.txt"), overflow);
Console.WriteLine($"Measured text overflow: {overflow.Count} (intentional model ellipsis excluded).");
foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
foreach (var suffix in new[] { "", "-dark", "-contrast", "-dark-contrast" })
{
    var baseline = Path.Combine(output, $"reset-baseline{suffix}-{scale:0.##}.png");
    var offline = Path.Combine(output, $"reset-offline{suffix}-{scale:0.##}.png");
    var expired = Path.Combine(output, $"reset-expired{suffix}-{scale:0.##}.png");
    if (!File.ReadAllBytes(baseline).SequenceEqual(File.ReadAllBytes(offline)) ||
        !File.ReadAllBytes(baseline).SequenceEqual(File.ReadAllBytes(expired)))
        throw new InvalidDataException("Hidden reset feature changed baseline pixels.");
}
Console.WriteLine("Offline and expired-reset fixtures match the baseline PNG byte-for-byte at all DPI/themes.");
if (args.Contains("--reset-only")) return;

// Native menu paint primitives, synthetic content and a measured fixture layout.
// ToolStrip itself and Windows message/input behavior are not running on Linux.
foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
foreach (var checkedState in new[] { false, true })
foreach (var highContrast in new[] { false, true })
foreach (var darkMode in new[] { false, true })
{
    (string Text, bool Enabled, bool Separator)[] rows = {
        ("立即刷新", true, false), ("数据来源：自动（Codex CLI）", true, false),
        ("主题：跟随系统", true, false), ("开机启动", true, false), ("", false, true),
        ("查看连接诊断…", true, false), ("关于 / 许可", true, false), ("退出", true, false)
    };
    const int logicalWidth = 320;
    var logicalHeight = 14 + rows.Sum(r => r.Separator ? 13 : 31);
    using var menu = new Bitmap((int)(logicalWidth * scale), (int)(logicalHeight * scale));
    using var graphics = Graphics.FromImage(menu);
    TrayMenuPainter.Surface(graphics, new Rectangle(Point.Empty, menu.Size), scale, highContrast, darkMode);
    using var font = new Font("Noto Sans CJK SC", 12.66f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
    var y = 7f * scale;
    for (var index = 0; index < rows.Length; index++)
    {
        var row = rows[index];
        var height = (row.Separator ? 13 : 31) * scale;
        var bounds = new Rectangle((int)(5 * scale), (int)y, (int)((logicalWidth - 10) * scale), (int)height);
        if (row.Separator) TrayMenuPainter.Separator(graphics, bounds, scale, highContrast, darkMode);
        else
        {
            if (index == 1) TrayMenuPainter.Selection(graphics, bounds, scale, highContrast, darkMode);
            if (index == 3 && checkedState) TrayMenuPainter.Check(graphics,
                new Rectangle((int)(11 * scale), (int)y, (int)(20 * scale), (int)height), scale, true, false, highContrast, darkMode);
            var textBounds = new Rectangle((int)(37 * scale), (int)y, (int)((logicalWidth - 52) * scale), (int)height);
            using var titleFont = new Font(font, index == 0 ? FontStyle.Bold : FontStyle.Regular);
            TrayMenuPainter.Text(graphics, row.Text, titleFont, textBounds, row.Enabled, index == 1, index == 0, highContrast, true, darkMode);
        }
        y += height;
    }
    TrayMenuPainter.Border(graphics, new Rectangle(Point.Empty, menu.Size), scale, highContrast, darkMode);
    menu.Save(Path.Combine(output, $"menu-{(checkedState ? "on" : "off")}-{(highContrast ? darkMode ? "dark-contrast" : "contrast" : darkMode ? "dark" : "light")}-{scale:0.##}.png"), ImageFormat.Png);
}

// Source submenu uses the production presentation: check = saved policy,
// independent filled dot = current valid automatic winner.
foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
foreach (var darkMode in new[] { false, true })
foreach (var manual in new[] { false, true })
{
    var selected = manual ? QuotaDataSource.Cli : QuotaDataSource.Auto;
    var rows = new[] { QuotaDataSource.Auto, QuotaDataSource.Cli, QuotaDataSource.VsCode, QuotaDataSource.Desktop }
        .Select(row => SourceMenuPresentation.Create(row, selected, QuotaDataSource.Cli, State(73), now)).ToArray();
    using var menu = new Bitmap((int)(200 * scale), (int)(138 * scale));
    using var graphics = Graphics.FromImage(menu);
    TrayMenuPainter.Surface(graphics, new Rectangle(Point.Empty, menu.Size), scale, false, darkMode);
    using var font = new Font("Noto Sans CJK SC", 12.66f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
    for (var index = 0; index < rows.Length; index++)
    {
        var y = (7 + index * 31) * scale;
        if (rows[index].Checked) TrayMenuPainter.Check(graphics,
            new Rectangle((int)(11 * scale), (int)y, (int)(20 * scale), (int)(31 * scale)), scale, true, false, false, darkMode);
        TrayMenuPainter.Text(graphics, rows[index].Text, font,
            new Rectangle((int)(37 * scale), (int)y, (int)(151 * scale), (int)(31 * scale)), true, false, index == 0, false, true, darkMode);
    }
    TrayMenuPainter.Border(graphics, new Rectangle(Point.Empty, menu.Size), scale, false, darkMode);
    menu.Save(Path.Combine(output, $"source-{(manual ? "manual" : "auto")}-{(darkMode ? "dark" : "light")}-{scale:0.##}.png"), ImageFormat.Png);
}

// Three mutually exclusive theme choices, using production labels, policy and paint primitives.
foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
foreach (var systemDark in new[] { false, true })
foreach (var selected in new[] { ThemePreference.System, ThemePreference.Light, ThemePreference.Dark })
{
    var state = new ThemeSelection(selected, systemDark);
    var rows = new[] { ThemePreference.System, ThemePreference.Light, ThemePreference.Dark };
    using var menu = new Bitmap((int)(190 * scale), (int)(107 * scale));
    using var graphics = Graphics.FromImage(menu);
    TrayMenuPainter.Surface(graphics, new Rectangle(Point.Empty, menu.Size), scale, false, state.IsDark);
    using var font = new Font("Noto Sans CJK SC", 12.66f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
    for (var index = 0; index < rows.Length; index++)
    {
        var y = (7 + index * 31) * scale;
        if (selected == rows[index]) TrayMenuPainter.Check(graphics,
            new Rectangle((int)(11 * scale), (int)y, (int)(20 * scale), (int)(31 * scale)), scale, true, false, false, state.IsDark);
        TrayMenuPainter.Text(graphics, rows[index].DisplayName(), font,
            new Rectangle((int)(37 * scale), (int)y, (int)(141 * scale), (int)(31 * scale)), true, false, index == 0, false, true, state.IsDark);
    }
    TrayMenuPainter.Border(graphics, new Rectangle(Point.Empty, menu.Size), scale, false, state.IsDark);
    menu.Save(Path.Combine(output, $"theme-{selected}-{(systemDark ? "system-dark" : "system-light")}-{scale:0.##}.png"), ImageFormat.Png);
}

internal sealed class AuditedHoverTextPainter(string fixture, List<string> overflow, string allowedModel,
    string? allowedForecast = null) : IHoverTextPainter
{
    public Size Measure(Graphics graphics, string text, Font font) => PreviewHoverTextPainter.Instance.Measure(graphics, text, font);
    public void Draw(Graphics graphics, string text, Font font, Rectangle bounds, Color foreground, Color background, bool alignRight)
    {
        var measured = Measure(graphics, text, font);
        if (text.Length > 0 && text != allowedModel && text != allowedForecast && measured.Width > bounds.Width)
            overflow.Add($"{fixture}: {text} requires {measured.Width}px, allocated {bounds.Width}px at {font.Size}px.");
        PreviewHoverTextPainter.Instance.Draw(graphics, text, font, bounds, foreground, background, alignRight);
    }
}
