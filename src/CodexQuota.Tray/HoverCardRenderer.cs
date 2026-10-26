using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using CodexQuota.Core;
using static CodexQuota.Core.HoverCardLayout;

namespace CodexQuota.Tray;

/// <summary>The production GDI+ painter is also used by the headless visual QA tool.</summary>
internal static class HoverCardRenderer
{
    private static readonly Color Ink = Color.FromArgb(24, 39, 52), Muted = Color.FromArgb(86, 105, 122);
    private static readonly Color DarkInk = Color.FromArgb(231, 238, 244), DarkMuted = Color.FromArgb(161, 178, 191);

    public static Color ToneColor(HoverTone tone, bool progress = false, bool darkMode = false) => tone switch
    {
        HoverTone.Mint => darkMode ? Color.FromArgb(64, 214, 178)
            : progress ? Color.FromArgb(39, 193, 157) : Color.FromArgb(13, 132, 109),
        HoverTone.Amber => darkMode ? Color.FromArgb(236, 186, 88)
            : progress ? Color.FromArgb(221, 171, 53) : Color.FromArgb(152, 99, 12),
        HoverTone.Red => darkMode ? Color.FromArgb(248, 117, 125)
            : progress ? Color.FromArgb(228, 70, 77) : Color.FromArgb(193, 48, 56),
        HoverTone.Stale => darkMode ? Color.FromArgb(145, 158, 170) : Color.FromArgb(123, 136, 148),
        _ => darkMode ? DarkInk : Ink
    };

    public static Bitmap Render(HoverCardModel model, Size pixels, float scale, float scroll, bool highContrast = false,
        string uiFamily = "Microsoft YaHei UI", string numberFamily = "Segoe UI", IHoverTextPainter? textPainter = null,
        bool darkMode = false, ICollection<HoverLinkRegion>? links = null)
    {
        textPainter ??= PreviewHoverTextPainter.Instance;
        var bitmap = new Bitmap(Math.Max(1, pixels.Width), Math.Max(1, pixels.Height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit; // Text surfaces are fully opaque; only edges/shadow carry alpha.
        g.ScaleTransform(scale, scale);
        var width = pixels.Width / scale;
        var height = pixels.Height / scale;
        var card = new RectangleF(Shadow, Shadow, Math.Max(1, width - Shadow * 2), Math.Max(1, height - Shadow * 2));
        for (var spread = 8; spread >= 1; spread--)
        {
            var shadow = card;
            shadow.Inflate(spread, spread);
            shadow.Offset(0, 2);
            using var brush = new SolidBrush(Color.FromArgb(highContrast ? 0 : darkMode ? 5 : 3, 8, 16, 24));
            using var path = Rounded(shadow, CornerRadius + spread);
            g.FillPath(brush, path);
        }
        using var outline = Rounded(card, CornerRadius);
        var surface = highContrast ? SystemColors.Window : darkMode ? Color.FromArgb(23, 35, 45) : Color.FromArgb(251, 252, 253);
        var borderColor = highContrast ? SystemColors.WindowText : darkMode ? Color.FromArgb(47, 61, 72) : Color.FromArgb(231, 235, 239);
        using (var brush = new SolidBrush(surface)) g.FillPath(brush, outline);
        using (var border = new Pen(borderColor, 1.2f)) g.DrawPath(border, outline);

        var saved = g.Save();
        g.SetClip(outline);
        g.TranslateTransform(0, -scroll);
        var x = Shadow + HoverCardLayout.Padding;
        var right = width - Shadow - HoverCardLayout.Padding;
        var textWidth = Math.Max(1, right - x);
        var fg = highContrast ? SystemColors.WindowText : darkMode ? DarkInk : Ink;
        var subdued = highContrast ? SystemColors.WindowText : darkMode ? DarkMuted : Muted;
        var rule = highContrast ? SystemColors.WindowText : darkMode ? Color.FromArgb(48, 61, 72) : Color.FromArgb(228, 233, 237);
        var trackColor = highContrast ? SystemColors.GrayText : darkMode ? Color.FromArgb(48, 66, 78) : Color.FromArgb(225, 232, 237);
        void Text(string text, float left, float top, float w, float h, float size, Color color, bool bold = false,
            bool alignRight = false, bool number = false)
        {
            // Layout remains in logical units; rasterization uses one rounded physical
            // font and device-aligned bounds. No text bitmap is subsequently resized.
            var points = new[] { new PointF(left, top), new PointF(left + w, top + h) };
            using (var transform = g.Transform) transform.TransformPoints(points);
            var bounds = Rectangle.FromLTRB((int)Math.Round(points[0].X), (int)Math.Round(points[0].Y),
                (int)Math.Round(points[1].X), (int)Math.Round(points[1].Y));
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            using var font = new Font(number ? numberFamily : uiFamily, Math.Max(1, (float)Math.Round(size * scale)),
                bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            var textState = g.Save();
            g.ResetTransform(); // Preserve the device clip and the already-transformed scroll offset.
            textPainter.Draw(g, text, font, bounds, color, surface, alignRight);
            g.Restore(textState);
        }
        float MeasureText(string value, float size, bool bold = false, bool number = false)
        {
            using var font = new Font(number ? numberFamily : uiFamily, Math.Max(1, (float)Math.Round(size * scale)),
                bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            var textState = g.Save();
            g.ResetTransform();
            var width = textPainter.Measure(g, value, font).Width / scale;
            g.Restore(textState);
            return width;
        }
        void DateRow(string relative, string date, string numericDate, float left, float top, float available,
            float size, Color relativeColor, bool bold = false)
        {
            // Both sides use the same rounded physical fonts as the final draw.
            // Keep the complete timestamp; compact notation is the only fallback.
            const float gap = 6;
            var leftWidth = MeasureText(relative, size, bold) + 2;
            var rightWidth = MeasureText(date, size) + 2;
            if (leftWidth + gap + rightWidth > available && numericDate.Length > 0)
            {
                date = numericDate;
                rightWidth = MeasureText(date, size) + 2;
            }
            rightWidth = Math.Min(available, rightWidth);
            Text(relative, left, top, Math.Max(0, available - rightWidth - gap), 24, size, relativeColor, bold);
            Text(date, left + available - rightWidth, top, rightWidth, 24, size, subdued, alignRight: true);
        }

        void Line(float y) { using var pen = new Pen(rule, .65f); g.DrawLine(pen, x, y, right, y); }
        Color ColorFor(HoverTone tone, bool progress = false) => highContrast ? SystemColors.WindowText : ToneColor(tone, progress, darkMode);

        // One status dot is enough beside the title; percentage and bar below carry quota detail.
        using (var dot = new SolidBrush(ColorFor(model.Header.Tone, true)))
            g.FillEllipse(dot, x, Shadow + 18, 7, 7);
        Text("Codex", x + 15, Shadow + 9, 62, 26, 14, fg, bold: true, number: true);
        Text(model.QuotaRefreshLabel,
            x + 78, Shadow + 10, textWidth - 78, 24, 11, subdued, alignRight: true);

        var y = Shadow + HeaderHeight;
        if (model.UpdateNotice is { } update)
        {
            var actionWidth = Math.Min(textWidth / 2, MeasureText(update.ActionText, 12, bold: true) + 8);
            var actionX = right - actionWidth;
            using (var dot = new SolidBrush(ColorFor(HoverTone.Red, true)))
                g.FillEllipse(dot, x, y + 9, 5, 5);
            Text(update.Text, x + 11, y, Math.Max(0, textWidth - actionWidth - 17), 23, 11, fg);
            Text(update.ActionText, actionX, y, actionWidth, 23, 12,
                update.CanInstall ? ColorFor(HoverTone.Mint) : subdued, bold: true, alignRight: true);
            if (update.CanInstall)
                links?.Add(new("update-" + update.Version, "", "下载、验证并安装更新，完成后重新启动。",
                    actionX, y, actionWidth, 24));
            y += UpdateNoticeHeight;
        }
        var compact = model.Quotas.Count > 1;
        for (var index = 0; index < model.Quotas.Count; index++)
        {
            var quota = model.Quotas[index];
            var cellY = y + (compact ? index / 2 * CompactQuotaHeight : 0);
            var cellWidth = compact ? (textWidth - ColumnGap) / 2 : textWidth;
            var cellX = x + (compact ? index % 2 * (cellWidth + ColumnGap) : 0);
            var cellRight = cellX + cellWidth;
            var qHeight = compact ? CompactQuotaHeight : QuotaHeight;
            if (cellY + qHeight <= scroll || cellY >= scroll + height) continue;
            Text(quota.Label, cellX, cellY, cellWidth - (quota.IsStale ? 35 : 0), 18, 12, subdued);
            if (quota.IsStale) Text("已过期", cellRight - 35, cellY, 35, 18, 11, subdued, alignRight: true);
            var numberSize = compact ? 36 : 38;
            var numberTop = cellY + 17;
            const float numberHeight = 43;
            var value = quota.Percent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—";
            var measured = MeasureText(value, numberSize, bold: true, number: true);
            Text(value, cellX - 1, numberTop, cellWidth, numberHeight, numberSize, ColorFor(quota.Tone), bold: true, number: true);
            if (quota.Percent is not null) Text("%", cellX + measured + 1, numberTop + 19,
                24, 24, 16, ColorFor(quota.Tone), bold: true, number: true);
            var barY = cellY + 63;
            const float barHeight = 7;
            var bar = new RectangleF(cellX, barY, cellWidth, barHeight);
            using (var path = Rounded(bar, barHeight / 2))
            using (var track = new SolidBrush(trackColor)) g.FillPath(track, path);
            if (quota.Percent is > 0)
            {
                var filled = new RectangleF(cellX, barY, cellWidth * quota.Percent.Value / 100, barHeight);
                using var path = Rounded(filled, barHeight / 2);
                using var fill = new SolidBrush(ColorFor(quota.Tone, true));
                g.FillPath(fill, path);
            }
            // Plus keeps the two percentage/bar columns, but each reset gets
            // the full body width and an explicit scope label below those bars.
            var resetY = cellY + ResetRowTop + (compact ? index % 2 * ResetRowHeight : 0);
            var relative = (compact ? quota.ResetScope + " " : "") + quota.RelativeReset;
            DateRow(relative, quota.Reset, quota.NumericReset, x, resetY, textWidth, 11,
                quota.IsStale ? subdued : fg, bold: true);
        }
        y += QuotasHeight(model);

        // Two ordinary text rows, not another panel. Quota/reset-card geometry is unchanged.
        if (model.ResetFeed is { } feed)
        {
            foreach (var row in new[] { feed.Latest, feed.Forecast })
            {
                if (row is null) continue;
                var forecast = ReferenceEquals(row, feed.Forecast);
                var labelWidth = Math.Min(70, MeasureText(row.Label, 11) + 2);
                var maximumDetailWidth = textWidth - labelWidth - 20;
                var detail = row.DetailForWidth(maximumDetailWidth, text => MeasureText(text, 11) + 2);
                if (MeasureText(detail, 11) + 2 > maximumDetailWidth)
                {
                    // Explicit prefix ellipsis is identical in the native and preview painters.
                    // The unabridged forecast remains in the source-link tooltip/accessibility text.
                    var length = detail.Length;
                    do
                    {
                        length--;
                        if (length > 0 && char.IsHighSurrogate(detail[length - 1])) length--;
                    } while (length > 0 && MeasureText(detail.Substring(0, length) + "…", 11) + 2 > maximumDetailWidth);
                    detail = detail.Substring(0, Math.Max(0, length)) + "…";
                }
                var detailWidth = Math.Min(maximumDetailWidth, MeasureText(detail, 11) + 2);
                var detailX = right - detailWidth;
                var linkColor = highContrast ? SystemColors.HotTrack :
                    darkMode ? Color.FromArgb(70, 170, 255) : Color.FromArgb(0, 113, 215);
                Text(row.Label, x, y, labelWidth, 24, 11, subdued);
                using (var pen = new Pen(subdued, .6f)) g.DrawLine(pen, x, y + 19, x + labelWidth - 2, y + 19);
                links?.Add(new("credit-" + row.Label, ResetFeedUrlPolicy.SiteUrl,
                    "数据来源：Codex Resets。\n" + row.Description,
                    x, y, labelWidth, 24));
                if (forecast)
                {
                    using var dot = new SolidBrush(highContrast ? SystemColors.HotTrack : Color.FromArgb(239, 60, 69));
                    g.FillEllipse(dot, x + labelWidth + 6, y + 9, 6, 6);
                }
                var linked = row.SourceUrl is not null;
                Text(detail, detailX, y, detailWidth, 24, 11, linked ? linkColor : subdued, alignRight: true);
                if (linked)
                {
                    using var pen = new Pen(linkColor, .7f);
                    g.DrawLine(pen, detailX, y + 19, right - 1, y + 19);
                    links?.Add(new(row.EventIdentity, row.SourceUrl!, row.Description,
                        detailX, y, detailWidth, 24, forecast));
                }
                y += ResetFeedRowHeight;
            }
        }

        Line(y);
        Text("重置卡", x, y + 3, 65, 24, 12, fg);
        Text(model.CardCount, x + 66, y + 3, textWidth - 66, 24, 11, subdued, alignRight: true);
        y += CardsHeaderHeight;
        var (first, last) = VisibleCards(model.Cards.Count, y, scroll, height);
        for (var index = first; index < last; index++)
        {
            var row = model.Cards[index];
            var top = y + index * TicketHeight;
            var color = model.CardsStale ? subdued : ColorFor(row.Tone);
            var relative = row.RelativeExpiry.Length > 0 ? row.RelativeExpiry : row.Date;
            using (var dot = new SolidBrush(row.Tone is HoverTone.Amber or HoverTone.Red && !model.CardsStale ? color : subdued))
                g.FillEllipse(dot, x + 1, top + 12, 4, 4);
            if (relative == row.Date) Text(relative, x + 12, top + 2, textWidth - 12, 24, 12, color);
            else DateRow(relative, row.Date, row.NumericDate, x + 12, top + 2, textWidth - 12,
                TicketTextSize(row.Date), color);
        }
        y += model.Cards.Count * TicketHeight;
        if (model.CardNote.Length > 0)
        {
            Text(model.CardNote, x, y + 1, textWidth, 26, 11, subdued);
            y += 28;
        }
        if (model.Status.Length > 0)
        {
            Text(model.Status, x, y + 6, textWidth, 23, 11, subdued);
            if (model.LastSuccess.Length > 0) Text("上次成功：" + model.LastSuccess, x, y + 27, textWidth, 22, 11, subdued);
            y += model.LastSuccess.Length > 0 ? 54 : 34;
        }

        // One secondary line: only the model may truncate. Keep the real effort
        // and timing status intact; the complete record timestamp remains in accessibility text.
        if (model.OutputSpeed is { } speed)
        {
            Line(y + 3);
            var speedColor = speed.IsOld || speed.IsUnavailable ? subdued : fg;
            var hasSample = speed.SampleTime.Length > 0;
            var prefix = hasSample ? speed.HistoryPrefix : "";
            var prefixWidth = prefix.Length > 0 ? MeasureText(prefix, 11) + 2 : 0;
            var modelText = hasSample ? speed.FastPrefix + (speed.CompactModel.Length > 0 ? speed.CompactModel : speed.Model) : speed.Detail;
            var tail = hasSample ? speed.CompactTail : "";
            var tailWidth = Math.Min(textWidth, MeasureText(tail, 11) + 2);
            var modelWidth = HoverCardLayout.OutputSpeedModelWidth(textWidth, prefixWidth, MeasureText(modelText, 11) + 2, tailWidth);
            Text(prefix, x, y + 7, prefixWidth, 22, 11, speedColor);
            Text(modelText, x + prefixWidth, y + 7, modelWidth, 22, 11, speedColor);
            // Keep short metadata as one left-aligned phrase, rather than stretching
            // model and rate to opposite card edges. Only long models give up space.
            Text(tail, x + prefixWidth + modelWidth + 4, y + 7, tailWidth, 22, 11, speedColor);
        }
        g.Restore(saved);
        // Opaque text tiles near a scrolled viewport edge must not repaint the hairline border.
        using (var border = new Pen(borderColor, 1.2f)) g.DrawPath(border, outline);

        var viewport = height - 2 * Shadow;
        var total = ContentHeight(model);
        if (total > viewport)
        {
            var trackHeight = Math.Max(1, viewport - 36);
            var thumbHeight = Math.Min(trackHeight, Math.Max(28, trackHeight * viewport / total));
            var thumbY = Shadow + 18 + scroll / (total - viewport) * (trackHeight - thumbHeight);
            using var brush = new SolidBrush(highContrast ? SystemColors.WindowText : Color.FromArgb(145, subdued));
            using var thumb = Rounded(new RectangleF(width - Shadow - 9, thumbY, 3, thumbHeight), 1.5f);
            g.FillPath(brush, thumb);
        }
        return bitmap;
    }

    internal static GraphicsPath Rounded(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        if (d <= 0) { path.AddRectangle(rectangle); return path; }
        path.AddArc(rectangle.X, rectangle.Y, d, d, 180, 90);
        path.AddArc(rectangle.Right - d, rectangle.Y, d, d, 270, 90);
        path.AddArc(rectangle.Right - d, rectangle.Bottom - d, d, d, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }


}


internal interface IHoverTextPainter
{
    Size Measure(Graphics graphics, string text, Font font);
    void Draw(Graphics graphics, string text, Font font, Rectangle bounds, Color foreground, Color background, bool alignRight);
}

/// <summary>Portable preview fallback. Windows runtime injects NativeHoverTextPainter instead.</summary>
internal sealed class PreviewHoverTextPainter : IHoverTextPainter
{
    public static readonly PreviewHoverTextPainter Instance = new();
    public Size Measure(Graphics graphics, string text, Font font) => Size.Ceiling(graphics.MeasureString(text, font,
        new SizeF(int.MaxValue, int.MaxValue), StringFormat.GenericTypographic));
    public void Draw(Graphics graphics, string text, Font font, Rectangle bounds, Color foreground, Color background, bool alignRight)
    {
        var measured = Measure(graphics, text, font);
        var y = bounds.Top + Math.Max(0, (bounds.Height - measured.Height) / 2);
        using var brush = new SolidBrush(foreground);
        using var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = alignRight ? StringAlignment.Far : StringAlignment.Near,
            LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        graphics.DrawString(text, font, brush, new Rectangle(bounds.Left, y, bounds.Width, Math.Max(bounds.Height, measured.Height)), format);
    }
}
