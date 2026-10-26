using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexQuota.Core;

/// <summary>Fail-closed bounded parser for the published v1 status envelope.</summary>
public static class ResetFeedParser
{
    public const int MaximumBodyBytes = 64 * 1024;
    public const int MaximumTextLength = 4096;

    public static bool TryParse(byte[] bytes, out ResetFeedSnapshot? snapshot)
    {
        snapshot = null;
        if (bytes.Length == 0 || bytes.Length > MaximumBodyBytes) return false;
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
            var root = doc.RootElement;
            Object(root, "data", "meta");
            var data = root.GetProperty("data");
            Object(data, "latest_reset", "scheduled_reset", "active_watch", "stats");
            var meta = root.GetProperty("meta");
            Object(meta, "api_version", "generated_at");
            if (Text(meta, "api_version", 8) != "v1") return false;
            var generated = Timestamp(meta, "generated_at");
            ValidateStats(data.GetProperty("stats"));

            ResetFeedReset? latest = null;
            var item = data.GetProperty("latest_reset");
            if (item.ValueKind != JsonValueKind.Null)
            {
                Object(item, "id", "reset_type", "announced_at", "text", "source");
                latest = new(Text(item, "id", 64), ResetType(item), Timestamp(item, "announced_at"),
                    Text(item, "text", MaximumTextLength, allowEmpty: true), Source(item.GetProperty("source")));
            }

            ResetFeedScheduledReset? scheduled = null;
            item = data.GetProperty("scheduled_reset");
            if (item.ValueKind != JsonValueKind.Null)
            {
                Object(item, "id", "status", "reset_type", "announced_at", "scheduled_for", "text", "source");
                if (Text(item, "status", 16) != "scheduled") return false;
                scheduled = new(Text(item, "id", 64), ResetType(item), Timestamp(item, "announced_at"),
                    NullableTimestamp(item, "scheduled_for"), Text(item, "text", MaximumTextLength, allowEmpty: true),
                    Source(item.GetProperty("source")));
            }

            ResetFeedWatch? watch = null;
            item = data.GetProperty("active_watch");
            if (item.ValueKind != JsonValueKind.Null)
            {
                Object(item, "level", "reset_chance_percent", "forecast_window", "observed_at", "expires_at", "text", "source");
                var level = Text(item, "level", 16);
                if (level is not ("elevated" or "strong")) return false;
                var percentElement = item.GetProperty("reset_chance_percent");
                int? percent = null;
                if (percentElement.ValueKind != JsonValueKind.Null)
                {
                    if (!percentElement.TryGetInt32(out var p) || p is < 0 or > 100) return false;
                    percent = p;
                }
                var observed = Timestamp(item, "observed_at");
                var expires = Timestamp(item, "expires_at");
                if (expires <= observed) return false;
                watch = new(level, percent, Text(item, "forecast_window", 160), observed, expires,
                    Text(item, "text", MaximumTextLength, allowEmpty: true), Source(item.GetProperty("source")));
            }
            snapshot = new(latest, scheduled, watch, generated);
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or
            ArgumentException or KeyNotFoundException or OverflowException) { return false; }
    }

    public static bool TryParse(string json, out ResetFeedSnapshot? snapshot)
    {
        snapshot = null;
        return json.Length <= MaximumBodyBytes && TryParse(Encoding.UTF8.GetBytes(json), out snapshot);
    }

    /// <summary>Only direct Tibo posts, never arbitrary redirects, profile links, query strings or ports.</summary>
    public static bool TryGetSourceUrl(string? value, out Uri? url)
    {
        url = null;
        if (value is null || value.Length > 512 || value.Any(char.IsWhiteSpace) ||
            value.IndexOf('\\') >= 0 || value.IndexOf('%') >= 0 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps || parsed.UserInfo.Length != 0 ||
            parsed.Query.Length != 0 || parsed.Fragment.Length != 0 ||
            !parsed.IsDefaultPort || parsed.Port != 443) return false;
        // Reject explicit ports, including :443, rather than relying on Uri's normalization.
        var authorityEnd = value.IndexOf('/', "https://".Length);
        var authority = authorityEnd < 0 ? value.Substring("https://".Length) :
            value.Substring("https://".Length, authorityEnd - "https://".Length);
        if (authority.IndexOf(':') >= 0 ||
            !(parsed.Host.Equals("x.com", StringComparison.OrdinalIgnoreCase) ||
              parsed.Host.Equals("twitter.com", StringComparison.OrdinalIgnoreCase) ||
              parsed.Host.Equals("www.x.com", StringComparison.OrdinalIgnoreCase) ||
              parsed.Host.Equals("www.twitter.com", StringComparison.OrdinalIgnoreCase))) return false;
        const string prefix = "/thsottiaux/status/";
        var path = parsed.AbsolutePath;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !value.Substring(authorityEnd).Equals(path, StringComparison.Ordinal)) return false;
        var id = path.Substring(prefix.Length);
        if (id.Length is < 1 or > 32 || !id.All(c => c is >= '0' and <= '9')) return false;
        // Normalize to the verified host and route; never pass through untrusted raw input.
        url = new Uri("https://x.com/thsottiaux/status/" + id);
        return true;
    }

    private static Uri? Source(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException();
        var type = Text(element, "type", 16);
        if (type == "x_post")
        {
            var hasUrl = element.TryGetProperty("url", out var urlElement);
            Object(element, hasUrl ? new[] { "type", "author", "url" } : new[] { "type", "author" });
            if (Text(element, "author", 32) != "thsottiaux") throw new FormatException();
            // Treat absent source links as non-clickable data, never manufacture a post URL from an ID.
            if (!hasUrl || urlElement.ValueKind == JsonValueKind.Null) return null;
            if (!TryGetSourceUrl(Text(element, "url", 512), out var url)) throw new FormatException();
            return url;
        }
        if (type == "observed")
        {
            Object(element, element.TryGetProperty("url", out _) ? new[] { "type", "url" } : new[] { "type" });
            if (!element.TryGetProperty("url", out _)) return null;
            var value = Text(element, "url", 512);
            // Observations may cite a site rather than a post. Such links are intentionally not opened.
            if (!Uri.TryCreate(value, UriKind.Absolute, out _)) throw new FormatException();
            return TryGetSourceUrl(value, out var url) ? url : null;
        }
        throw new FormatException();
    }

    private static string ResetType(JsonElement item)
    {
        var value = Text(item, "reset_type", 16);
        return value is "regular" or "banked" ? value : throw new FormatException();
    }

    private static void ValidateStats(JsonElement stats)
    {
        Object(stats, "total", "last_reset_at", "days_since_last", "avg_interval_days");
        if (!stats.GetProperty("total").TryGetInt32(out var total) || total < 0) throw new FormatException();
        _ = NullableTimestamp(stats, "last_reset_at");
        foreach (var name in new[] { "days_since_last", "avg_interval_days" })
        {
            var value = stats.GetProperty(name);
            if (value.ValueKind == JsonValueKind.Null) continue;
            if (!value.TryGetDouble(out var number) || double.IsInfinity(number) || double.IsNaN(number) || number < 0)
                throw new FormatException();
        }
    }

    private static void Object(JsonElement element, params string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in element.EnumerateObject())
            if (!names.Add(p.Name) || !required.Contains(p.Name)) throw new FormatException();
        if (names.Count != required.Length || required.Any(name => !names.Contains(name))) throw new FormatException();
    }

    private static string Text(JsonElement element, string name, int maximum, bool allowEmpty = false)
    {
        var property = element.GetProperty(name);
        if (property.ValueKind != JsonValueKind.String) throw new FormatException();
        var value = property.GetString()!;
        if (value.Length > maximum || (!allowEmpty && string.IsNullOrWhiteSpace(value)) ||
            value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) throw new FormatException();
        return value;
    }

    private static DateTimeOffset Timestamp(JsonElement element, string name)
    {
        var value = Text(element, name, 64);
        // A timezone is mandatory. Never interpret missing offsets in the machine's local zone.
        if (value.IndexOf('T') < 0 || !(value.EndsWith("Z", StringComparison.Ordinal) ||
            (value.Length >= 6 && value[value.Length - 3] == ':' &&
             value[value.Length - 6] is '+' or '-')) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed) || parsed.Year is < 2000 or > 2100) throw new FormatException();
        return parsed;
    }

    private static DateTimeOffset? NullableTimestamp(JsonElement element, string name) =>
        element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Timestamp(element, name);
}
