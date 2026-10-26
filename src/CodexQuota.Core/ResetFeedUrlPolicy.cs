namespace CodexQuota.Core;

public static class ResetFeedUrlPolicy
{
    public const string SiteUrl = "https://codex-resets.com/";
    public static bool IsAllowedSource(string? url) => ResetFeedParser.TryGetSourceUrl(url, out _);
    public static bool TryNormalizeSource(string? value, out string? url)
    {
        var allowed = ResetFeedParser.TryGetSourceUrl(value, out var parsed);
        url = parsed?.AbsoluteUri;
        return allowed;
    }
}
