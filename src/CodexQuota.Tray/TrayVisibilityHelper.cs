using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CodexQuota.Core;

namespace CodexQuota.Tray;

/// <summary>Undocumented Explorer ABI is used only in this disposable child, never in the UI.</summary>
#if !NETFRAMEWORK
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
public static class TrayVisibilityHelper
{
    public static bool TryRun(string[] args)
    {
        if (!TrayVisibilityProtocol.IsHelperInvocation(args)) return false;
        SetErrorMode(0x8003); // Do not show error/GP-fault dialogs from this best-effort helper.
        var result = TrayVisibilityStatus.InvalidRequest;
        try
        {
            if (TrayVisibilityProtocol.TryParse(args, out var request)) result = Run(request);
        }
        catch (Exception exception) when (exception is COMException or Win32Exception or ArgumentException or
            InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { result = TrayVisibilityStatus.EnumerationFailed; }
        Environment.ExitCode = TrayVisibilityProtocol.ExitCode(result);
        return true;
    }

    private static TrayVisibilityStatus Run(TrayVisibilityRequest request)
    {
        if (!TrayVisibilityNative.IsSupportedWindows10()) return TrayVisibilityStatus.UnsupportedPlatform;
        if (IntPtr.Size == 4 && (request.Window < int.MinValue || request.Window > uint.MaxValue)) return TrayVisibilityStatus.InvalidRequest;
        var window = IntPtr.Size == 4 ? new IntPtr(unchecked((int)request.Window)) : new IntPtr(request.Window);
        // The caller may not nominate a sibling process, even another copy of this same executable.
        if (NtQueryInformationProcess(TrayVisibilityNative.GetCurrentProcess(), 0, out var basic,
            Marshal.SizeOf<ProcessBasicInformation>(), out _) < 0 || basic.ParentProcessId.ToInt64() != request.ParentProcessId)
            return TrayVisibilityStatus.InvalidRequest;
        using var parent = OpenProcess(0x00101000, false, (uint)request.ParentProcessId); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION.
        if (parent.IsInvalid || TrayVisibilityNative.Wait(parent, 0)) return TrayVisibilityStatus.ParentUnavailable;
        var ownPath = TrayVisibilityNative.ImagePath(TrayVisibilityNative.GetCurrentProcess());
        var parentPath = TrayVisibilityNative.ImagePath(parent.DangerousGetHandle());
        if (string.IsNullOrEmpty(ownPath) || !string.Equals(ownPath, parentPath, StringComparison.OrdinalIgnoreCase) ||
            !TrayVisibilityNative.IsOwnTrayWindow(window, (uint)request.ParentProcessId)) return TrayVisibilityStatus.InvalidRequest;

        var deadline = Stopwatch.StartNew();
        using var watchdog = new System.Threading.Timer(_ =>
        {
            try
            {
                if (deadline.ElapsedMilliseconds >= 4000 || TrayVisibilityNative.Wait(parent, 0))
                    TerminateProcess(TrayVisibilityNative.GetCurrentProcess(), (uint)TrayVisibilityProtocol.ExitCode(TrayVisibilityStatus.HelperTimedOut));
            }
            catch (ObjectDisposedException) { }
            catch (Win32Exception) { TerminateProcess(TrayVisibilityNative.GetCurrentProcess(), (uint)TrayVisibilityProtocol.ExitCode(TrayVisibilityStatus.ParentUnavailable)); }
        }, null, 100, 100);
        var initialized = CoInitializeEx(IntPtr.Zero, 2); // COINIT_APARTMENTTHREADED, matching the STA entry point.
        if (initialized < 0) return TrayVisibilityStatus.ComUnavailable;
        ITrayNotifyWin8? tray = null;
        using var callback = new OwnIconCallback(ownPath!, window, deadline);
        try
        {
            var classId = new Guid("25DEAD04-1EAC-4911-9E3A-AD0A4AB560FD");
            var interfaceId = typeof(ITrayNotifyWin8).GUID;
            // Chromium uses CLSCTX_ALL; activation remains confined to this isolated helper.
            // No legacy interface fallback and no COM activation in the parent UI process.
            if (CoCreateInstance(ref classId, IntPtr.Zero, 23, ref interfaceId, out tray) < 0 || tray is null)
                return TrayVisibilityStatus.ComUnavailable;
            uint registration = 0;
            int registered, unregistered;
            try { registered = tray.RegisterCallback(callback, out registration); }
            finally
            {
                // Chromium observes synchronous enumeration. Always unregister before using the copy,
                // including failed registration; only a successful unregister permits SetPreference.
                unregistered = tray.UnregisterCallback(ref registration);
                GC.KeepAlive(callback);
            }
            if (registered < 0 || unregistered < 0 || callback.Failed) return TrayVisibilityStatus.EnumerationFailed;
            var decision = callback.Match.Decision;
            if (decision != TrayVisibilityStatus.PreferenceRequested) return decision;
            if (deadline.ElapsedMilliseconds >= 3000) return TrayVisibilityStatus.HelperTimedOut;
            if (TrayVisibilityNative.Wait(parent, 0) || !TrayVisibilityNative.IsOwnTrayWindow(window, (uint)request.ParentProcessId))
                return TrayVisibilityStatus.ParentUnavailable;
            var item = callback.OwnedItem;
            item.Preference = 2;
            return tray.SetPreference(ref item) >= 0 ? TrayVisibilityStatus.PreferenceRequested : TrayVisibilityStatus.PreferenceFailed;
        }
        finally
        {
            // The RCW belongs only to this short-lived STA. Unregistration is above, before release.
            if (tray is not null && Marshal.IsComObject(tray)) Marshal.FinalReleaseComObject(tray);
            CoUninitialize();
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class OwnIconCallback : INotificationCallback, IDisposable
    {
        private readonly IntPtr _window;
        private readonly Stopwatch _deadline;
        private int _callbacks;
        public TrayVisibilityMatch Match { get; }
        public bool Failed { get; private set; }
        internal NotifyItem OwnedItem;
        public OwnIconCallback(string path, IntPtr window, Stopwatch deadline)
        { Match = new TrayVisibilityMatch(path, window.ToInt64()); _window = window; _deadline = deadline; }
        public int Notify(uint eventId, IntPtr itemPointer)
        {
            const int abort = unchecked((int)0x80004004);
            if (++_callbacks > TrayVisibilityProtocol.MaximumCallbacks || _deadline.ElapsedMilliseconds >= 2500)
            {
                Match.Observe(null, 0, 0, 0, deadlineExpired: true);
                return abort;
            }
            if (Failed || itemPointer == IntPtr.Zero) { Failed = true; return abort; }
            try
            {
                var item = Marshal.PtrToStructure<NotifyItem>(itemPointer);
                // Never decode or retain other icons' paths or tooltips.
                if (item.Window != _window || item.Id != TrayVisibilityProtocol.OwnIconId) return 0;
                var path = ReadBoundedString(item.Executable, TrayVisibilityProtocol.MaximumPathCharacters);
                if (!Match.Observe(path, item.Window.ToInt64(), item.Id, item.Preference)) return 0;
                if (item.Preference != 0) return 0; // No copy or change is needed for hidden, already-visible, or unknown preferences.
                // Callback pointers may be ephemeral. Own both strings and an HICON copy until SetPreference returns.
                OwnedItem = item;
                OwnedItem.Executable = OwnedItem.Tip = OwnedItem.Icon = IntPtr.Zero;
                OwnedItem.Executable = Marshal.StringToCoTaskMemUni(path);
                var tip = ReadBoundedString(item.Tip, 4096);
                if (tip is null) { Failed = true; return abort; }
                OwnedItem.Tip = Marshal.StringToCoTaskMemUni(tip);
                if (item.Icon != IntPtr.Zero)
                {
                    OwnedItem.Icon = CopyIcon(item.Icon);
                    if (OwnedItem.Icon == IntPtr.Zero) { Failed = true; return abort; }
                }
                return 0;
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException or InvalidOperationException)
            { Failed = true; return abort; }
        }
        private static string? ReadBoundedString(IntPtr pointer, int limit)
        {
            if (pointer == IntPtr.Zero) return null;
            for (var length = 0; length < limit; length++)
                if (Marshal.ReadInt16(pointer, checked(length * sizeof(char))) == 0)
                    return Marshal.PtrToStringUni(pointer, length);
            return null;
        }
        public void Dispose()
        {
            if (OwnedItem.Executable != IntPtr.Zero) Marshal.FreeCoTaskMem(OwnedItem.Executable);
            if (OwnedItem.Tip != IntPtr.Zero) Marshal.FreeCoTaskMem(OwnedItem.Tip);
            if (OwnedItem.Icon != IntPtr.Zero) DestroyIcon(OwnedItem.Icon);
            OwnedItem = default;
        }
    }

    // ABI facts verified against pinned Chromium sources; implementation and isolation are ours.
    // https://chromium.googlesource.com/chromium/src/+/a4993e2d04893594d4e5b3f4fa5ed1c51d13f824/chrome/browser/ui/views/status_icons/status_tray_state_changer_win.cc
    // https://chromium.googlesource.com/chromium/src/+/4a8573cb240df29b0e4d9820303538fb28e31d84/chrome/browser/ui/views/status_icons/status_tray_state_changer_win.h
    // Copyright 2014 The Chromium Authors. See third_party/Chromium-BSD-3-Clause.txt.
    [StructLayout(LayoutKind.Sequential)]
    public struct NotifyItem
    {
        public IntPtr Executable, Tip, Icon, Window;
        public uint Preference, Id;
        public Guid Guid;
    }
    [ComImport, Guid("D133CE13-3537-48BA-93A7-AFCD5D2053B4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITrayNotifyWin8
    {
        [PreserveSig] int RegisterCallback([MarshalAs(UnmanagedType.Interface)] INotificationCallback callback, out uint registration);
        [PreserveSig] int UnregisterCallback(ref uint registration);
        [PreserveSig] int SetPreference([In] ref NotifyItem item);
        [PreserveSig] int EnableAutoTray([MarshalAs(UnmanagedType.Bool)] bool enabled); // ABI slot only; never called.
        [PreserveSig] int DoAction([MarshalAs(UnmanagedType.Bool)] bool enabled); // ABI slot only; never called.
    }
    [ComVisible(true), Guid("D782CCBA-AFB0-43F1-94DB-FDA3779EACCB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface INotificationCallback
    { [PreserveSig] int Notify(uint eventId, IntPtr item); }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    { public IntPtr Reserved1, Peb, Reserved2, Reserved3, ProcessId, ParentProcessId; }
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, out ProcessBasicInformation information, int length, out int returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeKernelHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint id);
    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint context, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out ITrayNotifyWin8? instance);
    [DllImport("user32.dll")]
    private static extern IntPtr CopyIcon(IntPtr icon);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
