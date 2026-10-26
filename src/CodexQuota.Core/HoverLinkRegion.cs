namespace CodexQuota.Core;

/// <summary>Painted text bounds in logical content coordinates, before DPI and scroll.</summary>
public sealed record HoverLinkRegion(string Key, string Url, string Description,
    float X, float Y, float Width, float Height, bool IsForecast = false)
{
    public bool Contains(float x, float y) => Width > 0 && Height > 0 &&
        x >= X && x < X + Width && y >= Y && y < Y + Height;

    public static HoverLinkRegion? HitTest(IReadOnlyList<HoverLinkRegion> links, float pixelX, float pixelY,
        float scale, float scroll, float viewportWidth, float viewportHeight, bool hasScrollbar)
    {
        if (scale <= 0 || float.IsNaN(scale) || float.IsInfinity(scale)) return null;
        var x = pixelX / scale;
        var y = pixelY / scale;
        // Shadow, clipped content, and the scrollbar never activate a link.
        var right = viewportWidth / scale - HoverCardLayout.Shadow - (hasScrollbar ? 20 : 0);
        if (x < HoverCardLayout.Shadow || x >= right || y < HoverCardLayout.Shadow ||
            y >= viewportHeight / scale - HoverCardLayout.Shadow) return null;
        return links.FirstOrDefault(link => link.Contains(x, y + scroll));
    }
}
