using System.Globalization;
using System.Text.Json;

namespace CodexQuota.Core;

public enum ResetCardDataStatus { NotReturned, Invalid, CountOnly, Details }
public enum ResetCardExpiryKind { Date, Never, Unknown }
public sealed record ResetCard(ResetCardExpiryKind ExpiryKind, DateTimeOffset? ExpiresAt)
{
    // The service supplies Unix seconds; keep all of that precision in both the
    // visible card and its accessible/fallback text, using the computer's local time.
    public string ExpiryText(DateTimeOffset now)
    {
        if (ExpiryKind == ResetCardExpiryKind.Never) return "不过期";
        if (ExpiryKind != ResetCardExpiryKind.Date || ExpiresAt is not { } value) return "日期未知";
        var local = value.ToLocalTime();
        return local.ToString(local.Year == now.ToLocalTime().Year ? "M月dd日 HH:mm:ss" : "yyyy年M月dd日 HH:mm:ss",
            CultureInfo.InvariantCulture);
    }
}

/// <summary>A read-only view of the official account/rateLimits/read reset-credit summary.</summary>
public sealed record ResetCards(ResetCardDataStatus Status, long AvailableCount, IReadOnlyList<ResetCard> Cards)
{
    public static ResetCards NotReturned { get; } = new(ResetCardDataStatus.NotReturned, 0, Array.Empty<ResetCard>());
    private static ResetCards Invalid { get; } = new(ResetCardDataStatus.Invalid, 0, Array.Empty<ResetCard>());

    public static ResetCards Parse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("rateLimitResetCredits", out var summary) || summary.ValueKind == JsonValueKind.Null)
            return NotReturned;
        if (summary.ValueKind != JsonValueKind.Object || !summary.TryGetProperty("availableCount", out var countValue) ||
            countValue.ValueKind != JsonValueKind.Number || !countValue.TryGetInt64(out var count) || count < 0)
            return Invalid;
        if (!summary.TryGetProperty("credits", out var credits) || credits.ValueKind == JsonValueKind.Null)
            return new(ResetCardDataStatus.CountOnly, count, Array.Empty<ResetCard>());
        if (credits.ValueKind != JsonValueKind.Array)
            return new(ResetCardDataStatus.CountOnly, count, Array.Empty<ResetCard>());

        var cards = new List<ResetCard>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in credits.EnumerateArray())
        {
            // Only available, recognized reset cards belong in this expiry list. Ignore titles,
            // descriptions and all redemption methods; never turn unknown types into usable cards.
            if (row.ValueKind != JsonValueKind.Object || ReadString(row, "status") != "available" ||
                ReadString(row, "resetType") != "codexRateLimits" || ReadString(row, "id") is not { Length: > 0 } id ||
                !ids.Add(id)) continue;
            var card = new ResetCard(ResetCardExpiryKind.Unknown, null);
            if (row.TryGetProperty("expiresAt", out var expiry))
            {
                if (expiry.ValueKind == JsonValueKind.Null) card = new(ResetCardExpiryKind.Never, null);
                else if (expiry.ValueKind == JsonValueKind.Number && expiry.TryGetInt64(out var seconds))
                {
                    try { card = new(ResetCardExpiryKind.Date, DateTimeOffset.FromUnixTimeSeconds(seconds)); }
                    catch (ArgumentOutOfRangeException) { /* Unknown is not the same as never expires. */ }
                }
            }
            cards.Add(card);
        }
        // Match the official client: earliest expiry first, count authoritative. Equal dates
        // remain separate cards. The backend can cap details, so never synthesize missing rows.
        var ordered = cards.OrderBy(card => card.ExpiresAt ?? DateTimeOffset.MaxValue)
            .Take((int)Math.Min(count, int.MaxValue)).ToArray();
        return new(ResetCardDataStatus.Details, count, ordered);
    }

    public IReadOnlyList<string> TooltipLines(DateTimeOffset now)
    {
        if (Status == ResetCardDataStatus.NotReturned) return new[] { "重置卡：未返回" };
        if (Status == ResetCardDataStatus.Invalid) return new[] { "重置卡：数据异常" };
        if (AvailableCount == 0) return Array.Empty<string>();
        if (Cards.Count == 0) return new[] { $"重置卡：{AvailableCount.ToString(CultureInfo.InvariantCulture)}张，日期未返回" };

        var lines = new List<string>();
        foreach (var card in Cards)
        {
            var expiry = card.ExpiryText(now);
            if (card.ExpiryKind == ResetCardExpiryKind.Date && card.ExpiresAt is { } at)
                expiry += now >= at ? "已到期" : "到期";
            lines.Add("重置卡：" + expiry);
        }
        var missing = AvailableCount - Cards.Count;
        if (missing > 0) lines.Add($"另{missing.ToString(CultureInfo.InvariantCulture)}张日期未返回");
        return lines;
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
}
