using System.Diagnostics;
using System.Runtime.InteropServices;
using CodexQuota.Tray;

namespace CodexQuota.FrameworkSmoke;

internal static class TrayVisibilityAbiSmoke
{
    public static void Run(Action<bool, string> check)
    {
        // Synthetic managed data only. No Explorer activation, callbacks, tray or registry mutation.
        check(Marshal.SizeOf(typeof(TrayVisibilityHelper.NotifyItem)) == 56, "tray helper production x64 NOTIFYITEM size");
        check(Marshal.OffsetOf(typeof(TrayVisibilityHelper.NotifyItem), "Guid").ToInt32() == 40, "tray helper GUID offset");
        check(typeof(TrayVisibilityHelper.OwnIconCallback).IsVisible && typeof(TrayVisibilityHelper.INotificationCallback).IsVisible,
            "tray helper callback and interface publicly visible");
        check(Marshal.IsTypeVisibleFromCom(typeof(TrayVisibilityHelper.OwnIconCallback)) &&
            Marshal.IsTypeVisibleFromCom(typeof(TrayVisibilityHelper.INotificationCallback)), "tray helper CCW types visible to COM");
        using var callback = new TrayVisibilityHelper.OwnIconCallback(@"C:\Synthetic\Own.exe", new IntPtr(42), Stopwatch.StartNew());
        var pointer = Marshal.GetComInterfaceForObject(callback, typeof(TrayVisibilityHelper.INotificationCallback));
        try { check(pointer != IntPtr.Zero, "tray helper acquires own callback interface without Explorer activation"); }
        finally { if (pointer != IntPtr.Zero) Marshal.Release(pointer); }
    }
}
