using System.Drawing;
using System.Windows.Forms;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace CodexQuota.Tray;

/// <summary>
/// GDI text is first painted onto an opaque RGB tile and copied 1:1 into the card.
/// GDI can zero an ARGB target's alpha, so it must never draw directly into the layered frame.
/// </summary>
internal sealed class NativeHoverTextPainter : IHoverTextPainter
{
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
        TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
    public Size Measure(Graphics graphics, string text, Font font) => TextRenderer.MeasureText(graphics,
        text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    public void Draw(Graphics graphics, string text, Font font, Rectangle bounds, Color foreground, Color background, bool alignRight)
    {
        if (text.Length == 0 || bounds.Width <= 0 || bounds.Height <= 0) return;
        using var tile = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var target = Graphics.FromImage(tile))
        {
            target.Clear(background);
            target.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            TextRenderer.DrawText(target, text, font, new Rectangle(Point.Empty, tile.Size), foreground, background,
                Flags | (alignRight ? TextFormatFlags.Right : TextFormatFlags.Left));
        }
        // Bitmap is RGB (implicitly opaque), so SourceOver restores alpha255 for every text pixel.
        // The inherited rounded-card clip excludes any translucent edge pixels.
        graphics.DrawImageUnscaled(tile, bounds.Location);
    }
}
