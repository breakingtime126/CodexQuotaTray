namespace CodexQuota.Core;

public enum ReadStatus { Ready, Connecting, NotSignedIn, ApiKey, UnsupportedAccount, NoWeeklyWindow, CliMissing, Timeout, ProtocolError, Unavailable, RateLimited, NoQuotaWindow, CredentialsExpired, CredentialsUnavailable, AuthConfigurationUnsupported }
public sealed record QuotaRead(ReadStatus Status, QuotaWindow? Quota = null, QuotaSelection? Selection = null, ResetCards? ResetCards = null);

public sealed record QuotaState(QuotaWindow? Quota, DateTimeOffset? LastSuccess, ReadStatus Status, QuotaSelection? Selection = null,
    ResetCards? ResetCards = null, DateTimeOffset? ResetCardsLastSuccess = null)
{
    public static QuotaState Initial => new(null, null, ReadStatus.Connecting);
    public bool IsStale(DateTimeOffset now) => Quota is not null &&
        (Status != ReadStatus.Ready || LastSuccess is null || now - LastSuccess >= TimeSpan.FromMinutes(10) ||
         (Quota.ResetsAt is { } reset && now >= reset));
    public QuotaState Apply(QuotaRead read, DateTimeOffset now)
    {
        if (read.Selection is { IsValid: false } && read.Status is ReadStatus.Ready or ReadStatus.NoQuotaWindow or ReadStatus.NoWeeklyWindow)
            return this with { Status = ReadStatus.ProtocolError };
        var cardsReadAt = read.ResetCards?.Status is ResetCardDataStatus.Details or ResetCardDataStatus.CountOnly ? now : (DateTimeOffset?)null;
        if (read.Status == ReadStatus.Ready && read.Quota is not null)
            return new(read.Quota, now, ReadStatus.Ready, read.Selection, read.ResetCards, cardsReadAt);
        // Do not retain the previous account's numbers after a login/provider/window change.
        if (read.Status is ReadStatus.NotSignedIn or ReadStatus.ApiKey or ReadStatus.UnsupportedAccount or ReadStatus.NoWeeklyWindow or ReadStatus.NoQuotaWindow or ReadStatus.CredentialsExpired or ReadStatus.CredentialsUnavailable or ReadStatus.AuthConfigurationUnsupported)
            return new(null, null, read.Status, read.Selection,
                read.Status is ReadStatus.NoWeeklyWindow or ReadStatus.NoQuotaWindow ? read.ResetCards : null,
                read.Status is ReadStatus.NoWeeklyWindow or ReadStatus.NoQuotaWindow ? cardsReadAt : null);
        return this with { Status = read.Status };
    }
    public string Tooltip(DateTimeOffset now)
    {
        var quota = QuotaTooltip(now);
        var cards = ResetCards?.TooltipLines(now);
        // Preserve the existing quota/reset rows verbatim. NativeTrayIcon handles long
        // readouts separately, so actual card rows are never truncated to a fixed count.
        return cards is { Count: > 0 } ? quota + "\n" + string.Join("\n", cards) : quota;
    }
    private string QuotaTooltip(DateTimeOffset now)
    {
        // Native tooltip stays a glanceable readout; plan names, selection rules,
        // refresh cadence and full diagnostics remain in the existing context menu.
        var slots = new List<(QuotaWindow? Window, int? MissingMinutes)>();
        if (Selection is { } selection)
            slots.AddRange(selection.DisplayWindows.Select(window => ((QuotaWindow?)window, (int?)null)));
        else if (Quota is not null) slots.Add((Quota, null));

        bool snapshotStale = Status != ReadStatus.Ready || LastSuccess is null || now - LastSuccess >= TimeSpan.FromMinutes(10);
        bool WindowStale(QuotaWindow window) => snapshotStale || window.ResetsAt is { } reset && now >= reset;
        bool stale = slots.Any(slot => slot.Window is { } window && WindowStale(window));
        bool needsStatus = stale || Status != ReadStatus.Ready;
        string WindowLabel(QuotaWindow window, int compact) => compact == 0 && window.WindowMinutes == QuotaParser.WeeklyMinutes
            ? "周额度" : compact == 0 ? window.WindowLabel : window.ShortLabel;
        string LastUpdate() => "上次成功：" + ((LastSuccess ?? ResetCardsLastSuccess)?.ToLocalTime().ToString("MM-dd HH:mm") ?? "未知");
        string Reason() => Status == ReadStatus.Ready ? "数据过期，等待刷新" : Status switch
        {
            ReadStatus.Connecting => "正在读取…",
            ReadStatus.NotSignedIn => "请先登录 Codex",
            ReadStatus.ApiKey => "API Key 模式无订阅额度",
            ReadStatus.UnsupportedAccount => "当前登录类型不支持",
            ReadStatus.NoWeeklyWindow => "服务未返回周额度",
            ReadStatus.NoQuotaWindow => "服务未返回可用额度",
            ReadStatus.CliMissing => "未找到 Codex CLI",
            ReadStatus.Timeout => "读取超时，稍后重试",
            ReadStatus.RateLimited => "服务限流，稍后重试",
            ReadStatus.ProtocolError => "协议不兼容，请更新 CLI",
            ReadStatus.CredentialsExpired => "登录已过期",
            ReadStatus.CredentialsUnavailable => "登录缓存不可读",
            ReadStatus.AuthConfigurationUnsupported => "认证配置不支持",
            _ => "读取失败，稍后重试"
        };

        // Ordinary known data uses exactly two lines per window. Rare long/unknown
        // durations compact to one row without hiding an available reset or error.
        for (int compact = 0; compact <= 2; compact++)
        {
            var lines = new List<string>();
            foreach (var slot in slots)
            {
                if (slot.Window is not { } window)
                {
                    var missing = new QuotaWindow(0, null, slot.MissingMinutes);
                    lines.Add(WindowLabel(missing, compact) + (compact == 0 ? "剩余 未知（未返回）" : "：未知（未返回）"));
                    continue;
                }
                var label = WindowLabel(window, compact);
                var value = $"{window.DisplayPercent}%{(WindowStale(window) ? "（过期）" : "")}";
                var reset = window.ResetsAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "未知";
                if (compact == 0)
                {
                    lines.Add($"{label}剩余 {value}");
                    lines.Add($"重置：{reset}");
                }
                else lines.Add($"{label} {value}{(compact == 1 ? " · " : " ")}重置{reset}");
            }
            if (lines.Count == 0) lines.Add("额度未知");
            if (needsStatus)
            {
                if (LastSuccess is not null || ResetCardsLastSuccess is not null || slots.Any(slot => slot.Window is not null)) lines.Add(LastUpdate());
                lines.Add(Reason());
            }
            var text = string.Join("\n", lines);
            if (text.Length <= 127) return text;
        }
        // Only manually constructed states can exceed the schema's two windows.
        // Preserve the active value, reset, staleness and reason rather than truncate.
        var fallback = Quota is null ? "额度未知" : $"{Quota.ShortLabel}剩余 {Quota.DisplayPercent}%{(IsStale(now) ? "（过期）" : "")}\n重置：{Quota.ResetsAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "未知"}";
        if (needsStatus) fallback += "\n" + LastUpdate() + "\n" + Reason();
        return fallback;
    }
    public static string StatusText(ReadStatus status) => status switch
    {
        ReadStatus.Ready => "每 30 秒刷新 · 悬停即刷新",
        ReadStatus.Connecting => "正在读取…",
        ReadStatus.NotSignedIn => "请先在 Codex 中登录",
        ReadStatus.ApiKey => "API Key 模式无订阅额度",
        ReadStatus.UnsupportedAccount => "当前登录类型不支持",
        ReadStatus.NoWeeklyWindow => "服务未返回周额度",
        ReadStatus.NoQuotaWindow => "服务未返回可用额度窗口",
        ReadStatus.CliMissing => "未找到 Codex CLI，见 README",
        ReadStatus.Timeout => "读取超时，稍后自动重试",
        ReadStatus.RateLimited => "服务限流，暂停 5 分钟后重试",
        ReadStatus.ProtocolError => "额度数据不兼容，请查看连接诊断",
        ReadStatus.CredentialsExpired => "登录已过期，请打开 Codex 更新登录",
        ReadStatus.CredentialsUnavailable => "现有登录不可读取，请查看连接诊断",
        ReadStatus.AuthConfigurationUnsupported => "当前认证配置暂不支持，请查看诊断",
        _ => "读取失败，稍后自动重试"
    };
}
