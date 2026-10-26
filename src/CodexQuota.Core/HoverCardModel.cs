using System.Globalization;

namespace CodexQuota.Core;

public enum HoverTone { Neutral, Mint, Amber, Red, Stale }
public sealed record HoverQuota(string Label, int? Percent, string Reset, bool IsStale, HoverTone Tone)
{
    public string RelativeReset { get; init; } = "";
    public string ResetScope { get; init; } = "";
    public string NumericReset { get; init; } = "";
}
public sealed record HoverTicket(string Date, string Detail, HoverTone Tone)
{
    public string RelativeExpiry { get; init; } = "";
    public string NumericDate { get; init; } = "";
}

/// <summary>Structured, read-only presentation. All values come from the current state and an injected clock.</summary>
public sealed record HoverCardModel(IReadOnlyList<HoverQuota> Quotas, HoverQuota Header, string CardCount,
    IReadOnlyList<HoverTicket> Cards, string CardNote, bool CardsStale, string Status, string LastSuccess,
    string AccessibleText)
{
    public OutputSpeedView? OutputSpeed { get; init; }
    public ResetFeedPresentation? ResetFeed { get; init; }
    // This is the successful quota read, never the completion time of the TPS sample.
    public string QuotaRefresh { get; init; } = "";
    public string QuotaRefreshLabel => QuotaRefresh.Length > 0 ? "更新于 " + QuotaRefresh : "尚未更新";

    public static string RelativeDuration(DateTimeOffset target, DateTimeOffset now)
    {
        var remaining = target - now;
        if (remaining <= TimeSpan.Zero) return "";
        if (remaining < TimeSpan.FromMinutes(1)) return "不到1分钟";
        var minutes = (long)Math.Floor(remaining.TotalMinutes);
        var days = minutes / (24 * 60);
        var hours = minutes / 60 % 24;
        var minute = minutes % 60;
        if (days > 0) return days.ToString(CultureInfo.InvariantCulture) + "天" +
            (hours > 0 ? hours.ToString(CultureInfo.InvariantCulture) + "小时" : "");
        if (hours > 0) return hours.ToString(CultureInfo.InvariantCulture) + "小时" +
            (minute > 0 ? minute.ToString(CultureInfo.InvariantCulture) + "分钟" : "");
        return minute.ToString(CultureInfo.InvariantCulture) + "分钟";
    }
    public static HoverTone QuotaTone(int? percent, bool stale) => stale || percent is null ? HoverTone.Stale
        : percent < 10 ? HoverTone.Red : percent <= 30 ? HoverTone.Amber : HoverTone.Mint;

    public static HoverTone ExpiryTone(ResetCard card, DateTimeOffset now, bool stale) => stale ? HoverTone.Stale
        : card.ExpiryKind != ResetCardExpiryKind.Date || card.ExpiresAt is not { } expiry ? HoverTone.Neutral
        : expiry - now <= TimeSpan.FromHours(24) ? HoverTone.Red
        : expiry - now <= TimeSpan.FromDays(7) ? HoverTone.Amber : HoverTone.Neutral;

    public static HoverCardModel Create(QuotaState state, DateTimeOffset now, OutputSpeedView? outputSpeed = null,
        string? cleanupStatus = null)
    {
        var cleanupPaused = !string.IsNullOrEmpty(cleanupStatus);
        var slots = new List<(QuotaWindow? Window, int? Minutes)>();
        if (state.Selection is { } selection)
            slots.AddRange(selection.DisplayWindows.Select(window => ((QuotaWindow?)window, window.WindowMinutes)));
        else if (state.Quota is { } quota) slots.Add((quota, quota.WindowMinutes));
        if (slots.Count == 0) slots.Add((null, null));

        var snapshotStale = state.Status != ReadStatus.Ready || state.LastSuccess is null || now - state.LastSuccess >= TimeSpan.FromMinutes(10);
        var quotas = slots.Select(slot =>
        {
            var label = slot.Minutes switch { 300 => "5 小时剩余", 10080 => "本周剩余", null => slot.Window is null ? "额度剩余" : "未知时段剩余", _ => $"{slot.Minutes} 分钟剩余" };
            var scope = slot.Minutes switch { 300 => "5小时", 10080 => "本周", null => "额度", _ => $"{slot.Minutes}分钟" };
            if (slot.Window is null) return new HoverQuota(label, null, cleanupPaused ? "等待查询恢复" : "服务未返回", false, HoverTone.Stale) { ResetScope = scope };
            var stale = snapshotStale || slot.Window.ResetsAt is { } reset && now >= reset;
            var at = slot.Window.ResetsAt?.ToLocalTime();
            var resetText = at?.ToString(at.Value.Year == now.ToLocalTime().Year ? "M月dd日 HH:mm" : "yyyy年M月dd日 HH:mm", CultureInfo.InvariantCulture) ?? "时间未知";
            return new HoverQuota(label, slot.Window.DisplayPercent, resetText, stale, QuotaTone(slot.Window.DisplayPercent, stale))
            {
                ResetScope = scope,
                NumericReset = at?.ToString(at.Value.Year == now.ToLocalTime().Year ? "M/dd HH:mm" : "yyyy/M/dd HH:mm", CultureInfo.InvariantCulture) ?? "",
                RelativeReset = slot.Window.ResetsAt is { } resetAt
                    ? now >= resetAt ? "重置时刻已过" : RelativeDuration(resetAt, now) + "后重置"
                    : ""
            };
        }).ToArray();

        var cards = state.ResetCards;
        var cardsStale = cards?.Status is ResetCardDataStatus.Details or ResetCardDataStatus.CountOnly && (state.Status is not (ReadStatus.Ready or ReadStatus.NoQuotaWindow or ReadStatus.NoWeeklyWindow) ||
            state.ResetCardsLastSuccess is null || now - state.ResetCardsLastSuccess >= TimeSpan.FromMinutes(10));
        var ticketRows = new List<HoverTicket>();
        var count = "";
        var note = "";
        if (cards is null || cards.Status == ResetCardDataStatus.NotReturned) note = cleanupPaused ? "等待查询恢复" : "服务未返回";
        else if (cards.Status == ResetCardDataStatus.Invalid) note = "数据异常";
        else
        {
            count = cards.AvailableCount.ToString(CultureInfo.InvariantCulture) + " 张";
            foreach (var card in cards.Cards)
            {
                var date = card.ExpiryText(now);
                var detail = card.ExpiryKind == ResetCardExpiryKind.Date && card.ExpiresAt is { } at ? now >= at ? "已到期" : "到期" : "";
                ticketRows.Add(new(date, detail, ExpiryTone(card, now, cardsStale))
                {
                    NumericDate = card.ExpiryKind == ResetCardExpiryKind.Date && card.ExpiresAt is { } absolute
                        ? absolute.ToLocalTime().ToString(absolute.ToLocalTime().Year == now.ToLocalTime().Year ? "M/dd HH:mm:ss" : "yyyy/M/dd HH:mm:ss", CultureInfo.InvariantCulture)
                        : "",
                    RelativeExpiry = card.ExpiryKind == ResetCardExpiryKind.Date && card.ExpiresAt is { } expiry
                        ? now >= expiry ? "已到期" : RelativeDuration(expiry, now) + "后"
                        : date
                });
            }
            var missing = cards.AvailableCount - ticketRows.Count;
            if (missing > 0) note = ticketRows.Count == 0 ? "到期日期未返回" : $"另 {missing.ToString(CultureInfo.InvariantCulture)} 张日期未返回";
        }

        var anyStale = quotas.Any(q => q.IsStale) || cardsStale;
        var status = cleanupPaused ? cleanupStatus! : state.Status == ReadStatus.Ready ? anyStale ? "数据过期，等待刷新" : "" : QuotaState.StatusText(state.Status);
        var last = status.Length == 0 ? "" : (state.LastSuccess ?? state.ResetCardsLastSuccess)?.ToLocalTime().ToString("M月dd日 HH:mm", CultureInfo.InvariantCulture) ?? "";
        var header = quotas[Math.Max(0, slots.FindIndex(slot => slot.Window is not null && Equals(slot.Window, state.Quota)))];
        var refreshed = state.LastSuccess?.ToLocalTime();
        var localNow = now.ToLocalTime();
        var refreshText = refreshed is { } value ? value.ToString(value.Date == localNow.Date ? "HH:mm"
            : value.Year == localNow.Year ? "M/dd HH:mm" : "yyyy/M/dd HH:mm", CultureInfo.InvariantCulture) : "";
        // Cleanup is a local ownership state, not evidence that the service omitted quota.
        // Keep known values accessible without announcing a contradictory automatic retry.
        var accessible = cleanupPaused
            ? (state with { Status = ReadStatus.Ready }).Tooltip(now)
                .Replace("（未返回）", "（等待查询恢复）").Replace("服务未返回", "等待查询恢复") + "\n" + cleanupStatus +
                "\n" + (refreshText.Length > 0 ? "更新于 " + refreshText : "尚未更新")
            : state.Tooltip(now);
        return new(quotas, header, count, ticketRows, note, cardsStale, status, last,
            accessible + (outputSpeed is null ? "" : "\n" + outputSpeed.AccessibleText))
            { OutputSpeed = outputSpeed, QuotaRefresh = refreshText };
    }

    /// <summary>Schedule visible countdown updates at most once per minute, plus exact warning/expiry transitions.</summary>
    public static DateTimeOffset? NextChange(QuotaState state, DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        void Consider(DateTimeOffset value) { if (value > now && (next is null || value < next)) next = value; }
        if (state.LastSuccess is { } success) Consider(success.AddMinutes(10));
        if (state.ResetCardsLastSuccess is { } cardSuccess) Consider(cardSuccess.AddMinutes(10));
        var hasFutureCountdown = false;
        foreach (var quota in state.Selection?.AvailableWindows ?? (state.Quota is { } q ? new[] { q } : Array.Empty<QuotaWindow>()))
            if (quota.ResetsAt is { } reset)
            {
                Consider(reset);
                hasFutureCountdown |= reset > now;
            }
        foreach (var card in state.ResetCards?.Cards ?? Array.Empty<ResetCard>())
            if (card.ExpiryKind == ResetCardExpiryKind.Date && card.ExpiresAt is { } expiry)
            {
                Consider(expiry);
                hasFutureCountdown |= expiry > now;
                if (expiry >= DateTimeOffset.MinValue.AddDays(7)) Consider(expiry.AddDays(-7));
                if (expiry >= DateTimeOffset.MinValue.AddDays(1)) Consider(expiry.AddDays(-1));
            }
        // Reuse the runtime's existing next-wake scheduling. This bounded local
        // countdown update adds neither a new timer nor a service request.
        if (hasFutureCountdown && now <= DateTimeOffset.MaxValue.AddMinutes(-1)) Consider(now.AddMinutes(1));
        return next;
    }
}
