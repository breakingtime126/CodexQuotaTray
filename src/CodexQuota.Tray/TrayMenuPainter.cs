using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace CodexQuota.Tray;

/// <summary>Shared paint primitives for the real native menu renderer and its headless QA preview.</summary>
internal static class TrayMenuPainter
{
    public static Color Background => Color.FromArgb(244, 248, 253);
    public static Color Foreground => Color.FromArgb(24, 39, 70);
    public static Color Secondary => Color.FromArgb(86, 106, 133);
    public static Color SurfaceColor(bool darkMode) => darkMode ? Color.FromArgb(21, 35, 45) : Background;
    public static Color InkColor(bool darkMode) => darkMode ? Color.FromArgb(235, 243, 247) : Foreground;
    public static Color SecondaryColor(bool darkMode) => darkMode ? Color.FromArgb(160, 182, 196) : Secondary;
    public static float Radius(float scale, Size size) => Math.Max(0, Math.Min(14 * scale, Math.Min(size.Width, size.Height) / 2f));

    public static void Surface(Graphics g, Rectangle bounds, float scale, bool highContrast, bool darkMode = false)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = HoverCardRenderer.Rounded(new RectangleF(bounds.X + .5f, bounds.Y + .5f,
            Math.Max(1, bounds.Width - 1), Math.Max(1, bounds.Height - 1)), Radius(scale, bounds.Size));
        using var fill = new LinearGradientBrush(bounds, highContrast ? SystemColors.Menu : darkMode ? SurfaceColor(true) : Color.FromArgb(252, 253, 255),
            highContrast ? SystemColors.Menu : SurfaceColor(darkMode), 90f);
        g.FillPath(fill, path);
    }
    public static void Border(Graphics g, Rectangle bounds, float scale, bool highContrast, bool darkMode = false)
    {
        using var path = HoverCardRenderer.Rounded(new RectangleF(bounds.X + .5f, bounds.Y + .5f,
            Math.Max(1, bounds.Width - 1), Math.Max(1, bounds.Height - 1)), Radius(scale, bounds.Size));
        using var pen = new Pen(highContrast ? SystemColors.MenuText : darkMode ? Color.FromArgb(53, 72, 85) : Color.FromArgb(207, 219, 232), Math.Max(1, scale * .7f));
        g.DrawPath(pen, path);
    }
    public static void Selection(Graphics g, Rectangle bounds, float scale, bool highContrast, bool darkMode = false)
    {
        var rect = new RectangleF(bounds.X + 3 * scale, bounds.Y + 1 * scale,
            Math.Max(1, bounds.Width - 6 * scale), Math.Max(1, bounds.Height - 2 * scale));
        using var path = HoverCardRenderer.Rounded(rect, 8 * scale);
        using var fill = new SolidBrush(highContrast ? SystemColors.Highlight : darkMode ? Color.FromArgb(40, 65, 80) : Color.FromArgb(224, 238, 247));
        g.FillPath(fill, path);
    }
    public static void Separator(Graphics g, Rectangle bounds, float scale, bool highContrast, bool darkMode = false)
    {
        using var pen = new Pen(highContrast ? SystemColors.GrayText : darkMode ? Color.FromArgb(48, 67, 81) : Color.FromArgb(218, 227, 238), Math.Max(1, scale * .7f));
        var y = bounds.Top + bounds.Height / 2f;
        g.DrawLine(pen, bounds.Left + 11 * scale, y, bounds.Right - 11 * scale, y);
    }
    public static void Check(Graphics g, Rectangle bounds, float scale, bool enabled, bool selected, bool highContrast, bool darkMode = false)
    {
        var edge = 16 * scale;
        var rect = new RectangleF(bounds.X + (bounds.Width - edge) / 2, bounds.Y + (bounds.Height - edge) / 2, edge, edge);
        using var path = HoverCardRenderer.Rounded(rect, 4 * scale);
        var color = highContrast ? selected ? SystemColors.HighlightText : SystemColors.MenuText : darkMode ? Color.FromArgb(71, 219, 179) : Color.FromArgb(20, 140, 114);
        using var fill = new SolidBrush(highContrast ? selected ? SystemColors.Highlight : SystemColors.Menu : darkMode ? Color.FromArgb(32, 76, 66) : Color.FromArgb(209, 242, 231));
        using var outline = new Pen(enabled ? color : SecondaryColor(darkMode), Math.Max(1, scale));
        g.FillPath(fill, path);
        g.DrawPath(outline, path);
        using var pen = new Pen(enabled ? color : SecondaryColor(darkMode), Math.Max(1, scale * 1.7f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(rect.X + edge * .23f, rect.Y + edge * .52f), new PointF(rect.X + edge * .44f, rect.Y + edge * .73f), new PointF(rect.X + edge * .8f, rect.Y + edge * .29f) });
    }
    public static Color TextColor(bool enabled, bool selected, bool emphasis, bool highContrast, bool darkMode = false) => highContrast
        ? selected && enabled ? SystemColors.HighlightText : enabled || emphasis ? SystemColors.MenuText : SystemColors.GrayText
        : enabled || emphasis ? InkColor(darkMode) : SecondaryColor(darkMode);

    // Portable preview text only. Runtime menu text uses native TextRenderer on its opaque HWND.
    public static void Text(Graphics g, string text, Font font, Rectangle bounds, bool enabled, bool selected,
        bool emphasis, bool highContrast, bool showKeyboardCues, bool darkMode = false)
    {
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var color = TextColor(enabled, selected, emphasis, highContrast, darkMode);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap,
            HotkeyPrefix = showKeyboardCues ? HotkeyPrefix.Show : HotkeyPrefix.Hide
        };
        g.DrawString(text, font, brush, bounds, format);
    }
}
