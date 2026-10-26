using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexQuota.Core;

/// <summary>Freshness is the upstream successful fetch time, never an announcement or HTTP cache date.</summary>
public static class ResetFeedMirror
{
    public const string Source = "https://codex-resets.com/api/v1/status";
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    public static bool IsFresh(DateTimeOffset success, DateTimeOffset now) =>
        success - now <= FutureTolerance && now - success < MaximumAge;

    public static bool TryParse(byte[] bytes, DateTimeOffset now, out ResetFeedSnapshot? snapshot)
    {
        snapshot = null;
        if (bytes.Length == 0 || bytes.Length > ResetFeedParser.MaximumBodyBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject()) if (!names.Add(property.Name)) return false;
            if (!names.SetEquals(new[] { "mirrorSchemaVersion", "upstreamSuccessAtUtc", "source", "data", "meta" }) ||
                !root.GetProperty("mirrorSchemaVersion").TryGetInt32(out var schema) || schema != 1 ||
                root.GetProperty("source").GetString() != Source) return false;
            var text = root.GetProperty("upstreamSuccessAtUtc").GetString();
            if (text is null || text.Length > 64 || !text.Contains("T") ||
                !(text.EndsWith("Z", StringComparison.Ordinal) || text.EndsWith("+00:00", StringComparison.Ordinal)) ||
                !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var success) ||
                success.Offset != TimeSpan.Zero || !IsFresh(success, now)) return false;
            var api = "{\"data\":" + root.GetProperty("data").GetRawText() + ",\"meta\":" + root.GetProperty("meta").GetRawText() + "}";
            if (!ResetFeedParser.TryParse(Encoding.UTF8.GetBytes(api), out var parsed)) return false;
            snapshot = parsed! with { UpstreamSuccessAtUtc = success };
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or ArgumentException)
        { return false; }
    }
}
