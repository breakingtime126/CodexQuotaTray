namespace CodexQuota.Tray;
internal static class UpdateFailureStore
{
    internal static void Record(string version)
    {
        try { using var settings = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\CodexQuotaTray");
            settings?.SetValue("UpdateFailedVersion", version); } catch { }
    }
}
