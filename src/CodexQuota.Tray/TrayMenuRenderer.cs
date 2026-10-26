namespace CodexQuota.Tray;

/// <summary>Only paint changes: ToolStrip retains normal keyboard, check, disabled, focus and AutoClose semantics.</summary>
internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    private static float Scale(ToolStrip? menu) => Math.Max(.5f, (menu?.DeviceDpi ?? 96) / 96f);
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) =>
        TrayMenuPainter.Surface(e.Graphics, e.ToolStrip.ClientRectangle, Scale(e.ToolStrip), SystemInformation.HighContrast, WindowsAppTheme.IsDark);
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) =>
        TrayMenuPainter.Border(e.Graphics, e.ToolStrip.ClientRectangle, Scale(e.ToolStrip), SystemInformation.HighContrast, WindowsAppTheme.IsDark);
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item.Enabled && e.Item.Selected)
            TrayMenuPainter.Selection(e.Graphics, new Rectangle(Point.Empty, e.Item.Size), Scale(e.ToolStrip), SystemInformation.HighContrast, WindowsAppTheme.IsDark);
    }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) =>
        TrayMenuPainter.Separator(e.Graphics, new Rectangle(Point.Empty, e.Item.Size), Scale(e.ToolStrip), SystemInformation.HighContrast, WindowsAppTheme.IsDark);
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var bounds = e.ImageRectangle;
        bounds.Y = (e.Item.Height - bounds.Height) / 2;
        TrayMenuPainter.Check(e.Graphics, bounds, Scale(e.ToolStrip), e.Item.Enabled, e.Item.Selected, SystemInformation.HighContrast, WindowsAppTheme.IsDark);
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var emphasis = e.Item.Owner?.Items.IndexOf(e.Item) == 0;
        var textFont = e.TextFont ?? Control.DefaultFont;
        var text = e.Text ?? "";
        var bounds = e.TextRectangle;
        bounds.Y = 0;
        bounds.Height = e.Item.Height;
        var foreground = TrayMenuPainter.TextColor(e.Item.Enabled, e.Item.Selected, emphasis, SystemInformation.HighContrast, WindowsAppTheme.IsDark);
        var flags = e.TextFormat | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        if (emphasis)
        {
            using var font = new Font(textFont, FontStyle.Bold);
            TextRenderer.DrawText(e.Graphics, text, font, bounds, foreground, flags);
        }
        else TextRenderer.DrawText(e.Graphics, text, textFont, bounds, foreground, flags);
    }
}
