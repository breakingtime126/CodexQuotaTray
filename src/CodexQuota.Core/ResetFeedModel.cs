using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodexQuota.Core;

/// <summary>Public, non-account reset information. Never used to change personal quota state.</summary>
public sealed record ResetFeedReset(string Id, string ResetType, DateTimeOffset AnnouncedAt,
    string Text, Uri? SourceUrl);

public sealed record ResetFeedScheduledReset(string Id, string ResetType, DateTimeOffset AnnouncedAt,
    DateTimeOffset? ScheduledFor, string Text, Uri? SourceUrl);

public sealed record ResetFeedWatch(string Level, int? ResetChancePercent, string ForecastWindow,
    DateTimeOffset ObservedAt, DateTimeOffset ExpiresAt, string Text, Uri? SourceUrl);

public sealed record ResetFeedSnapshot(ResetFeedReset? LatestReset, ResetFeedScheduledReset? ScheduledReset,
    ResetFeedWatch? ActiveWatch, DateTimeOffset GeneratedAt)
{
    public DateTimeOffset? UpstreamSuccessAtUtc { get; init; }
}

public enum ResetFeedReadStatus
{
    Success, NotModified, NetworkError, Timeout, Cancelled, HttpError, RateLimited, InvalidResponse
}

/// <summary>A failed result deliberately contains no snapshot, including after a cached success.</summary>
public sealed record ResetFeedReadResult(ResetFeedReadStatus Status, ResetFeedSnapshot? Snapshot = null,
    TimeSpan? RetryAfter = null, ResetFeedDiagnostic? Diagnostic = null)
{
    public bool IsSuccess => Status is ResetFeedReadStatus.Success or ResetFeedReadStatus.NotModified &&
        Snapshot is not null;
}

public sealed record ResetFeedRow(string Label, string Detail, string? SourceUrl, string EventIdentity,
    string Description)
{
    // Optional compact form: keep the relative estimate when the exact time cannot fit.
    public string? CompactDetail { get; init; }

    public string DetailForWidth(float availableWidth, Func<string, float> measure) =>
        CompactDetail is { } compact && measure(Detail) > availableWidth ? compact : Detail;
}

public sealed record ResetFeedPresentation(bool IsVisible, ResetFeedRow? Latest, ResetFeedRow? Forecast,
    string? EventIdentity, DateTimeOffset? NextChange)
{
    public static readonly Uri AttributionUrl = new("https://codex-resets.com/");
    public static readonly TimeSpan ScheduledConfirmationWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan UndatedAnnouncementLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan AllowedClockSkew = TimeSpan.FromMinutes(5);
    public static readonly ResetFeedPresentation Hidden = new(false, null, null, null, null);

    public static ResetFeedPresentation Create(ResetFeedReadResult? result, DateTimeOffset now)
    {
        if (result is null || !result.IsSuccess || result.Snapshot is not { } snapshot ||
            snapshot.GeneratedAt > AddClamped(now, AllowedClockSkew) ||
            snapshot.UpstreamSuccessAtUtc is { } success && !ResetFeedMirror.IsFresh(success, now)) return Hidden;

        ResetFeedRow? latest = null;
        DateTimeOffset? nextChange = snapshot.UpstreamSuccessAtUtc?.Add(ResetFeedMirror.MaximumAge);
        if (snapshot.LatestReset is { } reset && reset.AnnouncedAt <= now)
        {
            latest = new("上次重置", RelativeSince(reset.AnnouncedAt, now), reset.SourceUrl?.AbsoluteUri,
                Identity("latest", reset.Id),
                "公告或首次观测时间：" + LocalTime(reset.AnnouncedAt) +
                "\n不代表此账户实际额度重置时间。\n" + reset.Text);
            var elapsed = now - reset.AnnouncedAt;
            var unit = elapsed < TimeSpan.FromHours(1) ? TimeSpan.FromMinutes(1) :
                elapsed < TimeSpan.FromDays(1) ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
            nextChange = Earlier(nextChange, AddClamped(now, TimeSpan.FromTicks(unit.Ticks - elapsed.Ticks % unit.Ticks)));
        }

        ResetFeedRow? forecast = null;
        if (snapshot.ScheduledReset is { } scheduled &&
            scheduled.AnnouncedAt <= AddClamped(now, AllowedClockSkew) &&
            (scheduled.ScheduledFor is { } due
                ? now <= AddClamped(due, ScheduledConfirmationWindow)
                : now <= AddClamped(scheduled.AnnouncedAt, UndatedAnnouncementLifetime)))
        {
            var relative = scheduled.ScheduledFor is not { } scheduledFor ? "已预告，时间未定" :
                scheduledFor <= now ? "已到预定时间" : RelativeUntil(scheduledFor, now);
            var detail = scheduled.ScheduledFor is { } future && future > now
                ? relative + " · " + ForecastLocalTime(future, now) : relative;
            forecast = new("预计重置", detail, scheduled.SourceUrl?.AbsoluteUri,
                Identity("forecast", scheduled.SourceUrl?.AbsoluteUri ?? scheduled.Id),
                "公开预告，尚无执行确认；经过预定时间不代表已经重置。\n" +
                (scheduled.ScheduledFor is { } exact ? "预定时间：" + LocalTime(exact) + "\n" : "") + scheduled.Text)
                { CompactDetail = relative };
            var boundary = scheduled.ScheduledFor is { } scheduledTime
                ? scheduledTime > now ? NextCountdownChange(scheduledTime, now) : AddClamped(scheduledTime, ScheduledConfirmationWindow + TimeSpan.FromTicks(1))
                : AddClamped(scheduled.AnnouncedAt, UndatedAnnouncementLifetime + TimeSpan.FromTicks(1));
            nextChange = Earlier(nextChange, boundary);
        }
        else if (snapshot.ActiveWatch is { } watch && watch.ExpiresAt > now &&
            watch.ObservedAt <= AddClamped(now, AllowedClockSkew))
        {
            var level = watch.Level == "strong" ? "强信号" : "信号升温";
            var chance = watch.ResetChancePercent is { } percent
                ? " · " + percent.ToString(CultureInfo.InvariantCulture) + "%" : "";
            forecast = new("预计重置", SingleLine(watch.ForecastWindow), watch.SourceUrl?.AbsoluteUri,
                Identity("forecast", watch.SourceUrl?.AbsoluteUri ??
                    "watch:" + watch.ObservedAt.ToUniversalTime().ToString("O")),
                "第三方 AI 预测，非 OpenAI 承诺；不是个人额度倒计时。\n预测窗口：" + watch.ForecastWindow +
                "\n" + level + chance + "\n观测时间：" +
                LocalTime(watch.ObservedAt) + "\n到期时间：" + LocalTime(watch.ExpiresAt) + "\n" + watch.Text);
            nextChange = Earlier(nextChange, watch.ExpiresAt);
        }

        if (latest is null && forecast is null) return Hidden;
        // Only forecasts generate unread notifications. Poll, relative time and chance changes do not.
        if (nextChange <= now) nextChange = null;
        return new(true, latest, forecast, forecast?.EventIdentity, nextChange);
    }

    public static string RelativeSince(DateTimeOffset timestamp, DateTimeOffset now)
    {
        var elapsed = now - timestamp;
        if (elapsed < TimeSpan.FromMinutes(1)) return "刚刚";
        if (elapsed < TimeSpan.FromHours(1)) return ((int)elapsed.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " 分钟前";
        if (elapsed < TimeSpan.FromDays(1)) return ((int)elapsed.TotalHours).ToString(CultureInfo.InvariantCulture) + " 小时前";
        return ((int)elapsed.TotalDays).ToString(CultureInfo.InvariantCulture) + " 天前";
    }

    public static string RelativeUntil(DateTimeOffset timestamp, DateTimeOffset now)
    {
        if (timestamp <= now) return "已到预定时间";
        // Round upward, never advertise a shorter upper bound than the remaining time.
        var ticks = (timestamp - now).Ticks;
        var minutes = ticks / TimeSpan.TicksPerMinute + (ticks % TimeSpan.TicksPerMinute == 0 ? 0 : 1);
        var days = minutes / 1440;
        var hours = minutes % 1440 / 60;
        var remainder = minutes % 60;
        var text = days > 0 ? days.ToString(CultureInfo.InvariantCulture) + "天" : "";
        if (hours > 0) text += hours.ToString(CultureInfo.InvariantCulture) + "小时";
        if (remainder > 0) text += remainder.ToString(CultureInfo.InvariantCulture) + (days == 0 && hours == 0 ? "分钟" : "分");
        return "约" + text + "内";
    }

    private static DateTimeOffset NextCountdownChange(DateTimeOffset timestamp, DateTimeOffset now)
    {
        var ticks = (timestamp - now).Ticks % TimeSpan.TicksPerMinute;
        return AddClamped(now, TimeSpan.FromTicks(ticks == 0 ? TimeSpan.TicksPerMinute : ticks));
    }

    private static string ForecastLocalTime(DateTimeOffset value, DateTimeOffset now) =>
        value.ToLocalTime().ToString(value.ToLocalTime().Date == now.ToLocalTime().Date
            ? "HH:mm" : "MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string LocalTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string SingleLine(string value) =>
        string.Join(" ", value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));

    private static DateTimeOffset Earlier(DateTimeOffset? left, DateTimeOffset right) =>
        left is { } value && value < right ? value : right;

    private static DateTimeOffset AddClamped(DateTimeOffset value, TimeSpan offset)
    {
        // Work in UTC so offset-local representational limits cannot overflow either.
        var utc = value.ToUniversalTime();
        return offset > DateTimeOffset.MaxValue - utc ? DateTimeOffset.MaxValue : utc + offset;
    }

    internal static string Identity(string kind, string value)
    {
        using var hash = SHA256.Create();
        var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(kind + "|" + value));
        return string.Concat(bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }
}

