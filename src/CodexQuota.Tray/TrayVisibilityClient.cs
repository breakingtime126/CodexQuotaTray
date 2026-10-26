using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CodexQuota.Core;
using Microsoft.Win32.SafeHandles;

namespace CodexQuota.Tray;

/// <summary>No Explorer COM is loaded in the UI process. Only this exact executable is launched.</summary>
#if !NETFRAMEWORK
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
internal static class TrayVisibilityClient
{
    public static Task<TrayVisibilityResult> TryMakeVisibleAsync(IntPtr trayWindowHandle, uint iconId, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            var deadline = Stopwatch.StartNew();
            if (cancellationToken.IsCancellationRequested) return new TrayVisibilityResult(TrayVisibilityStatus.Cancelled);
            if (!TrayVisibilityNative.IsSupportedWindows10()) return new TrayVisibilityResult(TrayVisibilityStatus.UnsupportedPlatform);
            var parent = TrayVisibilityNative.GetCurrentProcessId();
            if (trayWindowHandle == IntPtr.Zero || iconId != TrayVisibilityProtocol.OwnIconId ||
                !TrayVisibilityNative.IsOwnTrayWindow(trayWindowHandle, parent)) return new TrayVisibilityResult(TrayVisibilityStatus.InvalidRequest);
            IsolatedChild? child = null;
            try
            {
                var path = TrayVisibilityNative.ImagePath(TrayVisibilityNative.GetCurrentProcess());
                if (string.IsNullOrEmpty(path) || !path!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
                    return new TrayVisibilityResult(TrayVisibilityStatus.HelperStartFailed);
                child = new IsolatedChild();
                child.Start(path, new TrayVisibilityRequest(checked((int)parent), trayWindowHandle.ToInt64(), iconId), cancellationToken, deadline);
                var owned = child;
                child = null; // AwaitAsync now owns cleanup, including all cancellation/error paths.
                return await TrayVisibilityIsolation.AwaitAsync(owned, TimeSpan.FromSeconds(5) - deadline.Elapsed, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            { return StopAfterFailedStart(child, TrayVisibilityStatus.Cancelled); }
            catch (TimeoutException)
            { return StopAfterFailedStart(child, TrayVisibilityStatus.HelperTimedOut); }
            catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or
                InvalidOperationException or ArgumentException or System.Security.SecurityException)
            {
                return StopAfterFailedStart(child, TrayVisibilityStatus.HelperStartFailed,
                    exception is Win32Exception native ? native.NativeErrorCode : exception.HResult);
            }
            finally { child?.Dispose(); }
        });

    private static TrayVisibilityResult StopAfterFailedStart(IsolatedChild? child, TrayVisibilityStatus status, int? code = null)
    {
        if (child is not null)
        {
            try { child.TerminateOwnedChild(); if (!child.WaitForExit(1000)) return new(TrayVisibilityStatus.CleanupUnconfirmed); }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ObjectDisposedException)
            { return new(TrayVisibilityStatus.CleanupUnconfirmed); }
        }
        return new(status, code);
    }

    private sealed class IsolatedChild : ITrayVisibilityChild
    {
        private SafeKernelHandle? _job, _process;
        public void Start(string executable, TrayVisibilityRequest request, CancellationToken token, Stopwatch deadline)
        {
            IntPtr attributes = IntPtr.Zero, jobValue = IntPtr.Zero;
            var initialized = false;
            var jobPinned = false;
            CancellationTokenRegistration cancellation = default;
            try
            {
                _job = Native.CreateJobObject(IntPtr.Zero, null);
                if (_job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                // Cancellation may mark the job closed, but may not recycle its raw handle while
                // JOB_LIST/CreateProcess still references it. A suspended child cannot run late.
                _job.DangerousAddRef(ref jobPinned);
                cancellation = token.Register(() => _job.Dispose());
                token.ThrowIfCancellationRequested();
                var limits = new Native.JobExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
                if (!Native.SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<Native.JobExtendedLimitInformation>()))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                IntPtr bytes = IntPtr.Zero;
                Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
                attributes = Marshal.AllocHGlobal(bytes);
                if (!Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
                initialized = true;
                jobValue = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(jobValue, _job.DangerousGetHandle());
                // Atomically assigned before child code runs. Failure is closed: no unmanaged fallback launch.
                if (!Native.UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x0002000d, jobValue, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var startup = new Native.StartupInfoEx { AttributeList = attributes };
                startup.StartupInfo.Size = Marshal.SizeOf<Native.StartupInfoEx>();
                var command = new StringBuilder(StartupCommand.QuoteArgument(executable) + " " + TrayVisibilityProtocol.Arguments(request));
                token.ThrowIfCancellationRequested();
                if (deadline.ElapsedMilliseconds >= 5000) throw new TimeoutException();
                if (!Native.CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false,
                    0x08000000 | 0x00080000 | 0x00000004, IntPtr.Zero, Path.GetDirectoryName(executable)!, ref startup, out var info))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                _process = new SafeKernelHandle(info.Process);
                using var thread = new SafeKernelHandle(info.Thread);
                token.ThrowIfCancellationRequested();
                if (deadline.ElapsedMilliseconds >= 5000) throw new TimeoutException();
                if (Native.ResumeThread(thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                if (initialized) Native.DeleteProcThreadAttributeList(attributes);
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                if (jobValue != IntPtr.Zero) Marshal.FreeHGlobal(jobValue);
                cancellation.Dispose();
                if (jobPinned) _job!.DangerousRelease();
            }
        }
        public bool TryGetExitCode(out int exitCode)
        {
            exitCode = 0;
            if (_process is null || !WaitForExit(0)) return false;
            if (!Native.GetExitCodeProcess(_process, out var code)) throw new Win32Exception(Marshal.GetLastWin32Error());
            exitCode = unchecked((int)code);
            return true;
        }
        public bool WaitForExit(int milliseconds) => _process is null || TrayVisibilityNative.Wait(_process, milliseconds);
        public void TerminateOwnedChild() => _job?.Dispose();
        public void Dispose() { TerminateOwnedChild(); _process?.Dispose(); }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct JobBasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct JobExtendedLimitInformation
        {
            public JobBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            public int Size;
            public IntPtr Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public ushort ShowWindow, ReservedByteCount;
            public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeKernelHandle CreateJobObject(IntPtr security, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(SafeKernelHandle job, int informationClass, ref JobExtendedLimitInformation information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnedSize);
        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processSecurity, IntPtr threadSecurity,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string workingDirectory, ref StartupInfoEx startupInfo, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(SafeKernelHandle process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(SafeKernelHandle thread);
    }
}

internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeKernelHandle() : base(true) { }
    public SafeKernelHandle(IntPtr value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() => TrayVisibilityNative.CloseHandle(handle);
}

internal static class TrayVisibilityNative
{
    public static bool IsSupportedWindows10()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
        var version = new OsVersion { Size = (uint)Marshal.SizeOf<OsVersion>(), ServicePack = "" };
        return RtlGetVersion(ref version) == 0 && TrayVisibilityProtocol.SupportsOs(version.Major, version.Minor, version.Build);
    }
    public static bool IsOwnTrayWindow(IntPtr window, uint owner)
    {
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var actualOwner) == 0 || actualOwner != owner) return false;
        var caption = new StringBuilder(64);
        return GetWindowText(window, caption, caption.Capacity) > 0 && caption.ToString() == "CodexQuotaTray.MessageWindow";
    }
    public static string? ImagePath(IntPtr process)
    {
        var path = new StringBuilder(TrayVisibilityProtocol.MaximumPathCharacters);
        var count = path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref count) || count <= 0 || count >= path.Capacity) return null;
        return Path.GetFullPath(path.ToString());
    }
    public static bool Wait(SafeKernelHandle process, int milliseconds)
    {
        var result = WaitForSingleObject(process, checked((uint)milliseconds));
        if (result == 0) return true;
        if (result == 0x102) return false;
        throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersion { public uint Size, Major, Minor, Build, Platform; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack; }
    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref OsVersion version);
    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeKernelHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int characters);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
}
