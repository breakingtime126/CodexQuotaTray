using Microsoft.Win32;
using CodexQuota.Core;

namespace CodexQuota.Tray;

/// <summary>Read-only Windows application theme preference. Missing/older Windows falls back to light.</summary>
internal static class WindowsAppTheme
{
    private static readonly ThemeSelection Selection = new(systemIsDark: ReadDarkPreference());
    public static bool IsDark => Selection.IsDark;
    public static void Select(ThemePreference preference) => Selection.Select(preference);
    public static void Refresh() => Selection.SetSystemTheme(ReadDarkPreference());

    private static bool ReadDarkPreference()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            return false;
        }
    }
}
