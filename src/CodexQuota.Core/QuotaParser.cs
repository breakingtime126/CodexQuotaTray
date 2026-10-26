using System.Text.Json;

namespace CodexQuota.Core;

public sealed record QuotaWindow(double RemainingPercent, DateTimeOffset? ResetsAt, int? WindowMinutes = QuotaParser.WeeklyMinutes)
{
    // Round down: never claim more quota is left than the service reported.
    public int DisplayPercent => (int)Math.Floor(RemainingPercent);
    public string WindowLabel => WindowMinutes switch
    {
        QuotaParser.FiveHourMinutes => "5小时",
        QuotaParser.WeeklyMinutes => "周",
        null => "未知时段",
        _ => $"{WindowMinutes}分钟"
    };
    public string ShortLabel => WindowMinutes switch
    {
        QuotaParser.FiveHourMinutes => "5h",
        QuotaParser.WeeklyMinutes => "周",
        null => "时段?",
        _ => $"{WindowMinutes}分"
    };
}

public enum AccountKind { ChatGpt, NotSignedIn, ApiKey, Unsupported }
public enum PlanSource { None, Account, RateLimits }
public enum QuotaSelectionReason { Preferred, PreferredWindowUnavailable, UnknownPlan, OtherPlan, NoAvailableWindow }

public sealed record QuotaSelection(string? PlanType, PlanSource PlanSource, int? PreferredWindowMinutes,
    QuotaWindow? Active, IReadOnlyList<QuotaWindow> AvailableWindows, QuotaSelectionReason Reason, bool PlanConflict = false)
{
    // Malformed/partial quota data is not evidence that a previously known window vanished.
    // Protocol consumers must preserve the preceding snapshot when this is false.
    public bool IsValid { get; init; } = true;
    public IEnumerable<QuotaWindow> DisplayWindows => AvailableWindows.OrderBy(window => window.WindowMinutes switch
    {
        QuotaParser.FiveHourMinutes => 0,
        QuotaParser.WeeklyMinutes => 1,
        _ => 2
    });

    // Display only trusted labels, not arbitrary strings from a newer/unrecognized server.
    public bool IsProPlan => QuotaParser.IsProPlan(PlanType);
    public string PlanLabel => PlanType switch { "plus" => "Plus", "prolite" => "Pro", "pro" => "Pro (More)", "promax" => "Pro (Max)", null or "unknown" => "套餐未知", _ => "其他套餐" };
    public string DisplaySummary
    {
        get
        {
            var preferred = PreferredWindowMinutes == QuotaParser.FiveHourMinutes ? "5小时" : "周";
            var reason = Reason switch
            {
                QuotaSelectionReason.Preferred => $"优先{preferred}",
                QuotaSelectionReason.PreferredWindowUnavailable => Active is null ? $"未返回{preferred}" : $"未返回{preferred}，回退{Active.WindowLabel}",
                QuotaSelectionReason.NoAvailableWindow => "无可用额度窗口",
                _ => "按可用窗口"
            };
            return $"{PlanLabel} · {reason}{(PlanConflict ? "（以额度套餐为准）" : "")}";
        }
    }
    internal string TooltipSummary => PlanLabel + (Reason == QuotaSelectionReason.PreferredWindowUnavailable ? " · 窗口回退"
        : Reason is QuotaSelectionReason.UnknownPlan or QuotaSelectionReason.OtherPlan ? " · 按可用窗口" : "") +
        (PlanConflict ? " · 以额度套餐为准" : "");
}

public static class QuotaParser
{
    public const int FiveHourMinutes = 5 * 60;
    public const int WeeklyMinutes = 7 * 24 * 60;

    public static AccountKind ReadAccountKind(JsonElement result)
    {
        if (!ObjectProperty(result, "account", out var account)) return AccountKind.NotSignedIn;
        if (!account.TryGetProperty("type", out var type)) return AccountKind.Unsupported;
        return type.ValueKind == JsonValueKind.String ? type.GetString() switch
        {
            "chatgpt" => AccountKind.ChatGpt,
            "apiKey" => AccountKind.ApiKey,
            _ => AccountKind.Unsupported
        } : AccountKind.Unsupported;
    }

    public static string? ReadAccountPlanType(JsonElement result) =>
        ReadAccountKind(result) == AccountKind.ChatGpt && ObjectProperty(result, "account", out var account)
            ? ReadPlanType(account) : null;

    public static QuotaSelection ReadSelection(JsonElement result, string? accountPlanType = null)
    {
        var found = TryReadCodexBucket(result, out var bucket);
        var bucketPlan = found ? ReadPlanType(bucket) : null;
        // Prefer the plan attached to the same usage snapshot. An older CLI may omit it;
        // account/read is the official fallback. Never consult unrelated model buckets.
        var useBucketPlan = IsKnownPlan(bucketPlan);
        var useAccountPlan = !useBucketPlan && IsKnownPlan(accountPlanType);
        var plan = useBucketPlan ? bucketPlan : useAccountPlan ? accountPlanType : bucketPlan ?? accountPlanType;
        var source = useBucketPlan ? PlanSource.RateLimits : useAccountPlan ? PlanSource.Account : PlanSource.None;
        var conflict = useBucketPlan && IsKnownPlan(accountPlanType) && bucketPlan != accountPlanType;
        int? preferred = plan == "plus" ? FiveHourMinutes : IsProPlan(plan) ? WeeklyMinutes : null;
        var validWindows = true;
        var windows = found ? ReadWindows(bucket, out validWindows) : Array.Empty<QuotaWindow>();
        var validSnapshot = found && validWindows;
        if (!validSnapshot) windows = Array.Empty<QuotaWindow>();
        var active = preferred is { } target ? windows.FirstOrDefault(window => window.WindowMinutes == target) : null;
        var matched = active is not null;
        // Stable fallback preserves the previous weekly behavior, then 5-hour, then an
        // actually returned window. Unknown duration stays unknown, never called weekly.
        active ??= windows.FirstOrDefault(window => window.WindowMinutes == WeeklyMinutes)
            ?? windows.FirstOrDefault(window => window.WindowMinutes == FiveHourMinutes)
            ?? windows.FirstOrDefault(window => window.WindowMinutes is not null)
            ?? windows.FirstOrDefault();
        var reason = active is null ? QuotaSelectionReason.NoAvailableWindow
            : matched ? QuotaSelectionReason.Preferred
            : preferred is not null ? QuotaSelectionReason.PreferredWindowUnavailable
            : IsKnownPlan(plan) ? QuotaSelectionReason.OtherPlan : QuotaSelectionReason.UnknownPlan;
        return new(plan, source, preferred, active, Array.AsReadOnly(windows), reason, conflict) { IsValid = validSnapshot };
    }

    // Compatibility helper: its contract remains strictly weekly, independent of plan.
    public static QuotaWindow? ReadWeekly(JsonElement result) => TryReadCodexBucket(result, out var bucket)
        ? ReadWindows(bucket, out _).FirstOrDefault(window => window.WindowMinutes == WeeklyMinutes) : null;

    private static bool TryReadCodexBucket(JsonElement result, out JsonElement bucket)
    {
        bucket = default;
        // An explicit Codex entry is authoritative, even when null/malformed. Never
        // substitute another model or the legacy snapshot in that case.
        if (result.ValueKind != JsonValueKind.Object) return false;
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind != JsonValueKind.Null)
        {
            if (buckets.ValueKind != JsonValueKind.Object) return false;
            if (buckets.TryGetProperty("codex", out bucket))
                return bucket.ValueKind == JsonValueKind.Object;
        }
        if (!ObjectProperty(result, "rateLimits", out bucket)) return false;
        if (bucket.TryGetProperty("limitId", out var id) && id.ValueKind != JsonValueKind.Null &&
            (id.ValueKind != JsonValueKind.String || id.GetString() != "codex")) return false;
        return true;
    }

    private static QuotaWindow[] ReadWindows(JsonElement bucket, out bool valid)
    {
        valid = true;
        var windows = new List<QuotaWindow>(2);
        foreach (var name in new[] { "primary", "secondary" })
        {
            // Optional absent/null windows are authoritative absence. A present but
            // malformed window invalidates the snapshot, not just that display row.
            if (!bucket.TryGetProperty(name, out var window) || window.ValueKind == JsonValueKind.Null) continue;
            if (window.ValueKind != JsonValueKind.Object ||
                !window.TryGetProperty("usedPercent", out var used) ||
                used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) ||
                double.IsNaN(percent) || double.IsInfinity(percent))
            {
                valid = false;
                continue;
            }

            int? minutes = null;
            if (window.TryGetProperty("windowDurationMins", out var duration) && duration.ValueKind != JsonValueKind.Null)
            {
                if (!duration.TryGetInt32Safe(out var parsed) || parsed <= 0)
                {
                    valid = false;
                    continue;
                }
                minutes = parsed;
            }
            DateTimeOffset? resetsAt = null;
            if (window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind == JsonValueKind.Number &&
                reset.TryGetInt64(out var seconds))
            {
                try { resetsAt = DateTimeOffset.FromUnixTimeSeconds(seconds); }
                catch (ArgumentOutOfRangeException) { /* Timestamp unavailable, not quota unavailable. */ }
            }
            windows.Add(new QuotaWindow(Math.Min(100, Math.Max(0, 100 - percent)), resetsAt, minutes));
        }
        return windows.ToArray();
    }

    // Verified in OpenAI codex rust-v0.159.2 protocol/src/auth.rs KnownPlan display mapping.
    public static bool IsProPlan(string? plan) => plan is "prolite" or "pro" or "promax";

    private static string? ReadPlanType(JsonElement parent) => parent.TryGetProperty("planType", out var plan) &&
        plan.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(plan.GetString()) ? plan.GetString() : null;
    private static bool IsKnownPlan(string? value) => !string.IsNullOrWhiteSpace(value) && value != "unknown";
    private static bool ObjectProperty(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value) &&
            value.ValueKind == JsonValueKind.Object;
    }
    private static bool TryGetInt32Safe(this JsonElement element, out int value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }
}
