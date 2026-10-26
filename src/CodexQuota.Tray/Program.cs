namespace CodexQuota.Tray;

internal static class Program
{
    [STAThread]
    internal static void Run(string[] args)
    {
        // Dispatch before WinForms, startup settings, and singleton handoff. Helpers have no UI.
        if (UpdateTransaction.TryRun(args)) return;
        if (TrayVisibilityHelper.TryRun(args)) return;
        string? healthDirectory = null, healthNonce = null;
        if (args.Length >= 3 && args[0] == "--update-health")
        {
            healthDirectory = args[1]; healthNonce = args[2]; args = args.Skip(3).ToArray();
            if (!UpdateHealth.Validate(healthDirectory, healthNonce)) return;
        }
#if NETFRAMEWORK
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
#else
        ApplicationConfiguration.Initialize();
#endif
        string? cliPath = null;
        if (args.Length == 2 && args[0] == "--codex-path") cliPath = args[1];
        else if (args.Length != 0)
        {
            MessageBox.Show("用法：CodexQuotaTray.exe [--codex-path \"C:\\路径\\codex.exe\"]\n也支持 npm 安装的 codex.cmd 或 codex.js。", "Codex 额度", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (cliPath is not null)
        {
            try { cliPath = Path.GetFullPath(cliPath); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.Security.SecurityException or PathTooLongException)
            {
                MessageBox.Show("Codex 路径无效，请检查后再试。", "Codex 额度", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        using var singleton = SingleInstanceCoordinator.Enter(cliPath, out var effectiveCliPath, out var warning);
        if (warning is not null) MessageBox.Show(warning, "Codex 额度", MessageBoxButtons.OK, MessageBoxIcon.Information);
        if (singleton is null) return;
        using var context = new TrayContext(effectiveCliPath);
        using var dispatcher = new Control();
        _ = dispatcher.Handle;
        try
        {
            singleton.StartServer(effectiveCliPath, () =>
            {
                if (dispatcher.IsDisposed) return;
                dispatcher.BeginInvoke(new Action(async () => await context.RequestHandoffExitAsync()));
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show("无法建立本用户的版本交接通道，程序未启动。请退出旧版后重试。", "Codex 额度", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        using var health = new UpdateHealth(context, healthDirectory, healthNonce);
        Application.Run(context);
    }
}
