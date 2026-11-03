namespace CodexQuota.Tray;

/// <summary>Moves the existing live items, retaining their handlers, checks and disabled states.</summary>
internal static class TrayMenuLayout
{
    internal static void Compose(TrayContextMenuStrip root, ToolStripMenuItem settings, ToolStripMenuItem help,
        ToolStripMenuItem refresh, ToolStripMenuItem source, ToolStripMenuItem sourceWarning,
        ToolStripMenuItem theme, ToolStripMenuItem themeWarning, ToolStripMenuItem startup,
        ToolStripMenuItem startupWarning, ToolStripMenuItem autoReset, ToolStripMenuItem autoUpdate,
        ToolStripMenuItem diagnostics, ToolStripMenuItem latest, ToolStripMenuItem forecast,
        ToolStripMenuItem about, ToolStripMenuItem checkUpdate, ToolStripMenuItem exit)
    {
        settings.DropDown = new TraySubmenuStrip(root);
        help.DropDown = new TraySubmenuStrip(root);
        settings.DropDownItems.AddRange(new ToolStripItem[]
        {
            source, sourceWarning, theme, themeWarning, startup, startupWarning, autoReset, autoUpdate
        });
        help.DropDownItems.AddRange(new ToolStripItem[] { diagnostics, latest, forecast, about });
        root.Items.AddRange(new ToolStripItem[] { refresh, settings, help, checkUpdate, new ToolStripSeparator(), exit });
    }
}
