using System.Diagnostics;
using System.Globalization;

namespace CodexQuota.Core;

public enum TrayVisibilityStatus
{
    PreferenceRequested, AlreadyAlways, UserHidden, UnknownPreference, NoMatchingIcon,
    AmbiguousIdentity, EnumerationLimit, UnsupportedPlatform, InvalidRequest, ParentUnavailable,
    ComUnavailable, EnumerationFailed, PreferenceFailed, HelperTimedOut, Cancelled,
    HelperStartFailed, HelperCrashed, CleanupUnconfirmed
}

/// <summary>Closed diagnostics only: never contains icon names, executable paths or COM exception text.</summary>
public sealed record TrayVisibilityResult(TrayVisibilityStatus Status, int? NativeCode = null)
{
    // Enum validation prevents arbitrary data, including a cast enum, reaching a log.
    public string ToSafeLogCode() => "tray_visibility_" +
        (Enum.IsDefined(typeof(TrayVisibilityStatus), Status) ? Status.ToString() : "Unknown") +
        (NativeCode is { } code ? ":" + code.ToString(CultureInfo.InvariantCulture) : "");

    public string Explanation => Status switch
    {
        TrayVisibilityStatus.PreferenceRequested => "已请求 Windows 优先显示图标；最终位置仍由系统决定。",
        TrayVisibilityStatus.AlreadyAlways => "系统已将本图标设为始终显示；最终位置仍由系统决定。",
        TrayVisibilityStatus.UserHidden => "检测到手动隐藏设置，已保留。可在 Windows 任务栏设置中调整。",
        TrayVisibilityStatus.UnknownPreference => "无法确认本图标的显示偏好，未作更改。请在 Windows 任务栏设置中调整。",
        TrayVisibilityStatus.UnsupportedPlatform => "此实验功能仅支持 Windows 10。请在 Windows 任务栏设置中调整。",
        TrayVisibilityStatus.Cancelled => "尝试已取消。",
        TrayVisibilityStatus.HelperTimedOut => "系统响应超时，已结束本次尝试。请在 Windows 任务栏设置中调整。",
        TrayVisibilityStatus.CleanupUnconfirmed => "无法确认辅助进程已退出，本次运行不再自动尝试。请在 Windows 任务栏设置中调整。",
        _ => "本次未能调整图标显示偏好。请在 Windows 任务栏设置中调整。"
    };
}

public readonly record struct TrayVisibilityRequest(int ParentProcessId, long Window, uint IconId);

/// <summary>Only a numeric own-window request crosses the process boundary. A path is never accepted.</summary>
public static class TrayVisibilityProtocol
{
    public const string HelperArgument = "--tray-visibility-helper";
    public const uint OwnIconId = 1;
    public const int MaximumCallbacks = 1024;
    public const int MaximumPathCharacters = 32768;
    private const int ExitBase = 180;

    public static bool IsHelperInvocation(string[] args) => args.Length > 0 && args[0] == HelperArgument;
    public static bool TryParse(string[] args, out TrayVisibilityRequest request)
    {
        request = default;
        if (args.Length != 4 || !IsHelperInvocation(args) ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent) || parent <= 0 ||
            !long.TryParse(args[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var window) || window == 0 ||
            !uint.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var icon) || icon != OwnIconId) return false;
        request = new TrayVisibilityRequest(parent, window, icon);
        return true;
    }

    public static string Arguments(TrayVisibilityRequest request) => HelperArgument + " " +
        request.ParentProcessId.ToString(CultureInfo.InvariantCulture) + " " + request.Window.ToString(CultureInfo.InvariantCulture) + " " +
        request.IconId.ToString(CultureInfo.InvariantCulture);
    public static bool SupportsOs(uint major, uint minor, uint build) => major == 10 && minor == 0 && build is >= 10240 and < 22000;
    public static int ExitCode(TrayVisibilityStatus status) => ExitBase + (int)status;
    public static TrayVisibilityResult FromExitCode(int exitCode)
    {
        var value = exitCode - ExitBase;
        return value >= 0 && value <= (int)TrayVisibilityStatus.CleanupUnconfirmed
            ? new TrayVisibilityResult((TrayVisibilityStatus)value)
            : new TrayVisibilityResult(TrayVisibilityStatus.HelperCrashed, exitCode);
    }
}

/// <summary>Pure fail-closed matcher; only the exact own path/window/id can become a candidate.</summary>
public sealed class TrayVisibilityMatch
{
    private readonly string _ownPath;
    private readonly long _window;
    private int _count, _matches;
    private uint _preference;
    private bool _limited;
    public TrayVisibilityMatch(string ownPath, long window) { _ownPath = ownPath; _window = window; }
    public bool Observe(string? executablePath, long window, uint id, uint preference, bool deadlineExpired = false)
    {
        if (_limited || deadlineExpired || ++_count > TrayVisibilityProtocol.MaximumCallbacks) { _limited = true; return false; }
        if (window != _window || id != TrayVisibilityProtocol.OwnIconId || string.IsNullOrEmpty(executablePath) ||
            executablePath!.Length >= TrayVisibilityProtocol.MaximumPathCharacters ||
            !string.Equals(executablePath, _ownPath, StringComparison.OrdinalIgnoreCase)) return false;
        _matches++;
        _preference = preference;
        return _matches == 1;
    }
    public TrayVisibilityStatus Decision => _limited ? TrayVisibilityStatus.EnumerationLimit
        : _matches == 0 ? TrayVisibilityStatus.NoMatchingIcon
        : _matches > 1 ? TrayVisibilityStatus.AmbiguousIdentity
        : _preference switch
        {
            0 => TrayVisibilityStatus.PreferenceRequested, // PREFERENCE_SHOW_WHEN_ACTIVE
            1 => TrayVisibilityStatus.UserHidden,          // PREFERENCE_SHOW_NEVER
            2 => TrayVisibilityStatus.AlreadyAlways,       // PREFERENCE_SHOW_ALWAYS
            _ => TrayVisibilityStatus.UnknownPreference
        };
}

/// <summary>The native implementation owns one private kill-on-close job and one child process handle.</summary>
public interface ITrayVisibilityChild : IDisposable
{
    bool TryGetExitCode(out int exitCode);
    void TerminateOwnedChild();
    bool WaitForExit(int milliseconds);
}

/// <summary>Testable bounded waiting and cleanup. Even success closes the job and verifies child exit.</summary>
public static class TrayVisibilityIsolation
{
    public static async Task<TrayVisibilityResult> AwaitAsync(ITrayVisibilityChild child, TimeSpan timeout, CancellationToken token)
    {
        var result = new TrayVisibilityResult(TrayVisibilityStatus.HelperTimedOut);
        var cleanupConfirmed = false;
        try
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < timeout)
            {
                if (token.IsCancellationRequested) { result = new(TrayVisibilityStatus.Cancelled); break; }
                if (child.TryGetExitCode(out var code)) { result = TrayVisibilityProtocol.FromExitCode(code); break; }
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { result = new(TrayVisibilityStatus.Cancelled); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or ObjectDisposedException)
        { result = new(TrayVisibilityStatus.HelperCrashed, exception.HResult); }
        finally
        {
            try { child.TerminateOwnedChild(); cleanupConfirmed = child.WaitForExit(1000); }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or ObjectDisposedException) { }
            finally { child.Dispose(); }
        }
        return cleanupConfirmed ? result : new TrayVisibilityResult(TrayVisibilityStatus.CleanupUnconfirmed);
    }
}
