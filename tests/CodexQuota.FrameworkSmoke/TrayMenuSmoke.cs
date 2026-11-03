global using System.Windows.Forms;
global using System.Drawing;
using CodexQuota.Tray;
using CodexQuota.Core;

namespace CodexQuota.FrameworkSmoke;

/// <summary>Synthetic native menu only: no TrayContext, updater, registry writes, accounts or network.</summary>
internal static class TrayMenuSmoke
{
    internal static int Run()
    {
        var count = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidDataException("FAIL " + name);
            count++; Console.WriteLine("PASS menu " + name);
        }
        using var root = new TrayContextMenuStrip { Renderer = new TrayMenuRenderer(), ShowItemToolTips = true };
        var settings = new ToolStripMenuItem("设置");
        var help = new ToolStripMenuItem("帮助与诊断");
        var refresh = new ToolStripMenuItem("立即刷新");
        var source = new ToolStripMenuItem("数据来源");
        var sourceWarning = new ToolStripMenuItem("来源提示") { Visible = false };
        var theme = new ToolStripMenuItem("主题");
        var themeWarning = new ToolStripMenuItem("主题提示") { Visible = false };
        var startup = new ToolStripMenuItem("开机启动") { Checked = true, Enabled = false };
        var startupWarning = new ToolStripMenuItem("启动提示") { Visible = false };
        var autoReset = new ToolStripMenuItem("到期自动用卡") { Checked = true, CheckOnClick = false };
        var autoUpdate = new ToolStripMenuItem("自动更新") { Checked = true, CheckOnClick = false, Enabled = false };
        var diagnostics = new ToolStripMenuItem("查看连接诊断…");
        var latest = new ToolStripMenuItem("上次重置原文") { Visible = false };
        var forecast = new ToolStripMenuItem("预计重置原文") { Enabled = false };
        var about = new ToolStripMenuItem("关于 / 许可");
        var check = new ToolStripMenuItem("检查更新") { Enabled = false };
        var exit = new ToolStripMenuItem("退出");
        source.DropDown = new TraySubmenuStrip(root);
        theme.DropDown = new TraySubmenuStrip(root);
        var autoSource = new ToolStripMenuItem("自动") { Checked = true };
        var darkTheme = new ToolStripMenuItem("深色") { Checked = true };
        source.DropDownItems.Add(autoSource);
        theme.DropDownItems.Add(darkTheme);
        var actions = new Dictionary<ToolStripMenuItem, int>();
        foreach (var item in new[] { refresh, autoReset, autoUpdate, diagnostics, latest, forecast, about, check, exit, autoSource, darkTheme })
        {
            actions.Add(item, 0);
            item.Click += (_, _) => actions[item]++;
        }
        TrayMenuLayout.Compose(root, settings, help, refresh, source, sourceWarning, theme, themeWarning,
            startup, startupWarning, autoReset, autoUpdate, diagnostics, latest, forecast, about, check, exit);
        Check(root.Items.Count == 6 && root.Items.OfType<ToolStripMenuItem>().Count() == 5, "five top-level items and one separator");
        Check(root.Items.Cast<ToolStripItem>().SequenceEqual(new ToolStripItem[] { refresh, settings, help, check, root.Items[4], exit }) &&
            root.Items[4] is ToolStripSeparator, "root order is refresh settings help check separator exit");
        Check(settings.DropDownItems.Cast<ToolStripItem>().SequenceEqual(new ToolStripItem[]
            { source, sourceWarning, theme, themeWarning, startup, startupWarning, autoReset, autoUpdate }), "settings retain each original item");
        Check(help.DropDownItems.Cast<ToolStripItem>().SequenceEqual(new ToolStripItem[] { diagnostics, latest, forecast, about }), "help retains all diagnostics and optional links");
        Check(!latest.Available && !sourceWarning.Available && !themeWarning.Available && !startupWarning.Available, "optional absent rows remain hidden");
        Check(!forecast.Enabled && !startup.Enabled && startup.Checked, "disabled link and startup check remain unchanged");
        Check(!autoUpdate.Enabled && !check.Enabled, "updater busy/commit disabled state survives moving live items");
        autoUpdate.Enabled = true; check.Enabled = true;
        Check(autoReset.Checked && autoUpdate.Checked && !autoReset.CheckOnClick && !autoUpdate.CheckOnClick, "toggle state stays controller-owned");
        foreach (var group in new[] { settings, help, source, theme })
        {
            Check(group.DropDown is TraySubmenuStrip, group.Text + " uses native themed submenu");
            var dropDown = (ToolStripDropDownMenu)group.DropDown;
            Check(ReferenceEquals(dropDown.Renderer, root.Renderer) && dropDown.ShowCheckMargin && !dropDown.ShowImageMargin && dropDown.ShowItemToolTips,
                group.Text + " preserves renderer checks and tooltip support");
            Check(dropDown.AutoClose, group.Text + " retains native dismissal");
        }
        Check(source.Owner == settings.DropDown && theme.Owner == settings.DropDown, "nested choices attach to Settings");
        Check(autoSource.Owner == source.DropDown && autoSource.Checked, "source policy selection survives nesting");
        Check(darkTheme.Owner == theme.DropDown && darkTheme.Checked, "theme selection survives nesting");
        foreach (var item in new[] { refresh, autoReset, autoUpdate, diagnostics, about, check, exit, autoSource, darkTheme })
        {
            item.PerformClick();
            Check(actions[item] == 1, item.Text + " original action fires exactly once");
        }
        Check(autoReset.Checked && autoUpdate.Checked, "clicks do not silently toggle before controller approval");
        forecast.PerformClick();
        Check(actions[forecast] == 0, "disabled forecast does not invoke its action");
        latest.Visible = true; latest.PerformClick();
        Check(latest.Available && actions[latest] == 1, "new valid latest link becomes reachable");
        var drops = new[] { settings.DropDown, help.DropDown, source.DropDown, theme.DropDown };
        root.Dispose();
        Check(drops.All(dropDown => dropDown.IsDisposed), "root disposal releases every explicit submenu");
        var directory = Path.Combine(Path.GetTempPath(), "cqt-menu-preference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "auto-reset.json");
            var store = new AutoResetPreferenceStore(path);
            Check(store.Load().Enabled && !File.Exists(path), "net48 missing preference defaults on without writing");
            foreach (var enabled in new[] { false, true, false })
            {
                Check(store.Save(enabled).Status == AutoResetPreferenceStatus.Saved, "net48 explicit choice saves atomically");
                Check(store.Load().Enabled == enabled, "net48 saved choice survives load");
            }
            File.WriteAllText(path, "corrupt");
            Check(!store.Load().Enabled && store.Load().Status == AutoResetPreferenceStatus.Invalid, "net48 corrupt preference fails closed");
            var blocked = new AutoResetPreferenceStore(Path.Combine(path, "auto-reset.json")).Load();
            Check(!blocked.Enabled && blocked.Status == AutoResetPreferenceStatus.ReadFailed, "net48 blocked ancestor fails closed");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine($"{count} native menu/preference assertions passed.");
        return 0;
    }
}
