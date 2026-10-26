using CodexQuota.Core;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class TrayVisibilityHelperTests
{
    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        test("tray helper: actual production ABI layout GUIDs and CCW visibility match pinned contract", () =>
        {
            var assembly = Assembly.GetExecutingAssembly();
            var item = assembly.GetType("CodexQuota.Tray.TrayVisibilityHelper+NotifyItem", true)!;
            Check.Equal(IntPtr.Size == 8 ? 56 : 40, Marshal.SizeOf(item));
            var fields = new[] { "Executable", "Tip", "Icon", "Window", "Preference", "Id", "Guid" };
            var offsets = IntPtr.Size == 8 ? new[] { 0, 8, 16, 24, 32, 36, 40 } : new[] { 0, 4, 8, 12, 16, 20, 24 };
            for (var i = 0; i < fields.Length; i++) Check.Equal(offsets[i], Marshal.OffsetOf(item, fields[i]).ToInt32());
            var callback = assembly.GetType("CodexQuota.Tray.TrayVisibilityHelper+OwnIconCallback", true)!;
            var callbackInterface = assembly.GetType("CodexQuota.Tray.TrayVisibilityHelper+INotificationCallback", true)!;
            var trayInterface = assembly.GetType("CodexQuota.Tray.TrayVisibilityHelper+ITrayNotifyWin8", true)!;
            Check.True(callback.IsVisible && callbackInterface.IsVisible && trayInterface.IsVisible);
            Check.Equal(new Guid("D782CCBA-AFB0-43F1-94DB-FDA3779EACCB"), callbackInterface.GUID);
            Check.Equal(new Guid("D133CE13-3537-48BA-93A7-AFCD5D2053B4"), trayInterface.GUID);
            var unregister = trayInterface.GetMethod("UnregisterCallback")!;
            Check.Equal(typeof(uint).MakeByRefType(), unregister.GetParameters().Single().ParameterType);
        });
        test("tray helper: numeric request round-trips and arbitrary paths or ids fail closed", () =>
        {
            var request = new TrayVisibilityRequest(123, 456, 1);
            Check.True(TrayVisibilityProtocol.TryParse(TrayVisibilityProtocol.Arguments(request).Split(' '), out var actual));
            Check.Equal(request, actual);
            foreach (var args in new[]
            {
                new[] { TrayVisibilityProtocol.HelperArgument },
                new[] { TrayVisibilityProtocol.HelperArgument, "123", "456", "2" },
                new[] { TrayVisibilityProtocol.HelperArgument, "123", "0", "1" },
                new[] { TrayVisibilityProtocol.HelperArgument, "0", "456", "1" },
                new[] { TrayVisibilityProtocol.HelperArgument, "123", "456", "1", @"C:\other.exe" },
                new[] { TrayVisibilityProtocol.HelperArgument, @"C:\other.exe", "456", "1" },
                new[] { TrayVisibilityProtocol.HelperArgument, "123", "18446744073709551616", "1" }
            }) Check.False(TrayVisibilityProtocol.TryParse(args, out _));
            Check.True(TrayVisibilityProtocol.TryParse(new[] { TrayVisibilityProtocol.HelperArgument, "123", "-2", "1" }, out actual));
            Check.Equal(-2L, actual.Window);
        });
        test("tray helper: reliable version gate excludes Windows 11 and other families", () =>
        {
            Check.True(TrayVisibilityProtocol.SupportsOs(10, 0, 19045));
            foreach (var version in new[] { (10u, 0u, 22000u), (10u, 0u, 26100u), (6u, 3u, 9600u), (11u, 0u, 30000u), (10u, 0u, 0u) })
                Check.False(TrayVisibilityProtocol.SupportsOs(version.Item1, version.Item2, version.Item3));
        });
        test("tray helper: exact complete own path window and id are all required", () =>
        {
            var match = new TrayVisibilityMatch(@"C:\Apps\CodexQuotaTray.exe", 42);
            Check.False(match.Observe(@"C:\Other\CodexQuotaTray.exe", 42, 1, 0));
            Check.False(match.Observe(@"C:\Apps\CodexQuotaTray.exe", 43, 1, 0));
            Check.False(match.Observe(@"C:\Apps\CodexQuotaTray.exe", 42, 2, 0));
            Check.False(match.Observe(@"CodexQuotaTray.exe", 42, 1, 0));
            Check.False(match.Observe(null, 42, 1, 0));
            Check.Equal(TrayVisibilityStatus.NoMatchingIcon, match.Decision);
            Check.True(match.Observe(@"c:\APPS\CodexQuotaTray.exe", 42, 1, 0));
            Check.Equal(TrayVisibilityStatus.PreferenceRequested, match.Decision);
        });
        test("tray helper: explicit hidden and unknown preferences are never changed", () =>
        {
            foreach (var pair in new[] { (1u, TrayVisibilityStatus.UserHidden), (2u, TrayVisibilityStatus.AlreadyAlways),
                (3u, TrayVisibilityStatus.UnknownPreference), (uint.MaxValue, TrayVisibilityStatus.UnknownPreference) })
            {
                var match = new TrayVisibilityMatch(@"C:\own.exe", 42);
                match.Observe(@"C:\own.exe", 42, 1, pair.Item1);
                Check.Equal(pair.Item2, match.Decision);
            }
        });
        test("tray helper: duplicate exact identity, callback flood and deadline abort mutation", () =>
        {
            var duplicate = new TrayVisibilityMatch(@"C:\own.exe", 42);
            duplicate.Observe(@"C:\own.exe", 42, 1, 0);
            duplicate.Observe(@"C:\own.exe", 42, 1, 0);
            Check.Equal(TrayVisibilityStatus.AmbiguousIdentity, duplicate.Decision);
            var flood = new TrayVisibilityMatch(@"C:\own.exe", 42);
            for (var i = 0; i <= TrayVisibilityProtocol.MaximumCallbacks; i++) flood.Observe(null, 0, 0, 0);
            Check.Equal(TrayVisibilityStatus.EnumerationLimit, flood.Decision);
            var expired = new TrayVisibilityMatch(@"C:\own.exe", 42);
            expired.Observe(@"C:\own.exe", 42, 1, 0, deadlineExpired: true);
            Check.Equal(TrayVisibilityStatus.EnumerationLimit, expired.Decision);
        });
        test("tray helper: closed result framing distinguishes native crash without leaking text", () =>
        {
            foreach (TrayVisibilityStatus status in Enum.GetValues(typeof(TrayVisibilityStatus)))
                Check.Equal(status, TrayVisibilityProtocol.FromExitCode(TrayVisibilityProtocol.ExitCode(status)).Status);
            var crash = TrayVisibilityProtocol.FromExitCode(unchecked((int)0xC0000005));
            Check.Equal(TrayVisibilityStatus.HelperCrashed, crash.Status);
            Check.Equal<int?>(unchecked((int)0xC0000005), crash.NativeCode);
            Check.False(crash.ToSafeLogCode().Contains("\\"));
            Check.Equal(TrayVisibilityStatus.HelperCrashed, TrayVisibilityProtocol.FromExitCode(0).Status);
        });
        asyncTest("tray helper isolation: finite timeout kills and verifies only its owned child", async () =>
        {
            var child = new FakeChild();
            var result = await TrayVisibilityIsolation.AwaitAsync(child, TimeSpan.FromMilliseconds(1), CancellationToken.None);
            Check.Equal(TrayVisibilityStatus.HelperTimedOut, result.Status);
            Check.True(child.Killed && child.Disposed && child.CleanupWaited);
        });
        asyncTest("tray helper isolation: cancellation kills and verifies owned child", async () =>
        {
            var child = new FakeChild();
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            var result = await TrayVisibilityIsolation.AwaitAsync(child, TimeSpan.FromSeconds(1), stop.Token);
            Check.Equal(TrayVisibilityStatus.Cancelled, result.Status);
            Check.True(child.Killed && child.Disposed && child.CleanupWaited);
        });
        asyncTest("tray helper isolation: success and native crash both clean up job", async () =>
        {
            foreach (var code in new[] { TrayVisibilityProtocol.ExitCode(TrayVisibilityStatus.PreferenceRequested), unchecked((int)0xC0000005) })
            {
                var child = new FakeChild { ExitCode = code };
                var result = await TrayVisibilityIsolation.AwaitAsync(child, TimeSpan.FromSeconds(1), CancellationToken.None);
                Check.Equal(TrayVisibilityProtocol.FromExitCode(code).Status, result.Status);
                Check.True(child.Killed && child.Disposed && child.CleanupWaited);
            }
        });
        asyncTest("tray helper isolation: unconfirmed cleanup overrides apparent success", async () =>
        {
            var child = new FakeChild { ExitCode = TrayVisibilityProtocol.ExitCode(TrayVisibilityStatus.PreferenceRequested), ConfirmExit = false };
            var result = await TrayVisibilityIsolation.AwaitAsync(child, TimeSpan.FromSeconds(1), CancellationToken.None);
            Check.Equal(TrayVisibilityStatus.CleanupUnconfirmed, result.Status);
            Check.True(child.Killed && child.Disposed);
        });
    }

    private sealed class FakeChild : ITrayVisibilityChild
    {
        public int? ExitCode;
        public bool Killed, Disposed, CleanupWaited, ConfirmExit = true;
        public bool TryGetExitCode(out int exitCode) { exitCode = ExitCode ?? 0; return ExitCode.HasValue; }
        public void TerminateOwnedChild() => Killed = true;
        public bool WaitForExit(int milliseconds) { Check.Equal(1000, milliseconds); CleanupWaited = true; return ConfirmExit && Killed; }
        public void Dispose() => Disposed = true;
    }
}
