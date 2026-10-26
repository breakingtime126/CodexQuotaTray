namespace CodexQuota.Core;

/// <summary>Logical dimensions shared by the native card, raster preview, and portable layout checks.</summary>
public static class HoverCardLayout
{
    public const float Width = 280, Shadow = 10, Padding = 18, TicketHeight = 28, HeaderHeight = 42;
    public const float QuotaHeight = 104, CompactQuotaHeight = 138, CardsHeaderHeight = 28, CornerRadius = 18;
    public const float ColumnGap = 12;
    public const float UpdateNoticeHeight = 28;
    public const float OutputSpeedHeight = 32;
    public const float ResetFeedRowHeight = 25;
    public const float ResetRowTop = 77, ResetRowHeight = 26;
    // Relative duration and exact expiry share one row at an unchanged readable
    // size. The painter may choose a numeric date, never discard year or seconds.
    public static float OutputSpeedModelWidth(float lineWidth, float prefixWidth, float measuredModelWidth, float tailWidth) =>
        Math.Max(0, Math.Min(measuredModelWidth, lineWidth - prefixWidth - tailWidth - 4));
    public static float TicketTextSize(string expiryText) => 12;
    public static float QuotasHeight(HoverCardModel model) => model.Quotas.Count == 1 ? QuotaHeight
        : ((model.Quotas.Count + 1) / 2) * CompactQuotaHeight;
    public static (string Date, string Time) ResetParts(string text)
    {
        var separator = text.LastIndexOf(' ');
        return separator < 0 ? (text, "") : (text.Substring(0, separator), text.Substring(separator + 1));
    }
    public static float ResetFeedHeight(HoverCardModel model) =>
        ((model.ResetFeed?.Latest is null ? 0 : 1) + (model.ResetFeed?.Forecast is null ? 0 : 1)) * ResetFeedRowHeight;
    public static float ContentHeight(HoverCardModel model) => HeaderHeight +
        (model.UpdateNotice is null ? 0 : UpdateNoticeHeight) +
        QuotasHeight(model) + ResetFeedHeight(model) + CardsHeaderHeight +
        model.Cards.Count * TicketHeight + (model.CardNote.Length > 0 ? 28 : 0) +
        (model.Status.Length > 0 ? model.LastSuccess.Length > 0 ? 54 : 34 : 0) +
        (model.OutputSpeed is null ? 0 : OutputSpeedHeight) + 10;
    public static float Scale(int dpi, int workWidth) => Math.Min(Math.Max(.25f, dpi / 96f), Math.Max(1, workWidth) / (Width + 2 * Shadow));
    public static int PixelWidth(float scale) => Math.Max(1, (int)Math.Ceiling((Width + 2 * Shadow) * scale));
    public static int PixelHeight(HoverCardModel model, float scale, int workHeight) =>
        Math.Max(1, (int)Math.Min(Math.Max(1, workHeight), Math.Ceiling((ContentHeight(model) + 2 * Shadow) * scale)));
    public static float MaxScroll(HoverCardModel model, int viewportPixels, float scale) =>
        Math.Max(0, ContentHeight(model) - (viewportPixels / scale - 2 * Shadow));
    public static float ClampScroll(float value, HoverCardModel model, int viewportPixels, float scale) =>
        Math.Max(0, Math.Min(value, MaxScroll(model, viewportPixels, scale)));
    public static (int First, int Last) VisibleCards(int count, float rowsTop, float scroll, float viewportHeight) =>
        (Math.Min(count, Math.Max(0, (int)Math.Floor((scroll - rowsTop) / TicketHeight))),
         Math.Max(0, Math.Min(count, (int)Math.Ceiling((scroll + viewportHeight - rowsTop) / TicketHeight))));
}
