using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexQuota.Core;

/// <summary>
/// Windows 10+ process ownership shared by both Windows target frameworks. CreateProcess assigns a private
/// kill-on-close job atomically, before any CLI code runs. Only the three stdio pipe ends
/// are inherited; no process-name lookup, external killer, or other user's CLI is involved.
/// </summary>
internal sealed class FrameworkCliProcess(ProcessStartInfo start, bool preferSystem32Images = false) : IDisposable
{
    private SafeJobHandle? _job;
    private SafeProcessWaitHandle? _process;
    private SafeFileHandle? _inputHandle;
    private SafeFileHandle? _outputHandle;
    private SafeFileHandle? _errorHandle;
    private bool _terminationRequested;
    private bool _hadOwnedProcess;
    private bool _rootExited;
    private int _rootExitCode;
    public CliStartOperation StartOperation { get; private set; }
    internal bool HasOwnedProcess => _hadOwnedProcess;
    public StreamWriter StandardInput { get; private set; } = null!;
    public StreamReader StandardOutput { get; private set; } = null!;
    public StreamReader StandardError { get; private set; } = null!;

    public bool Start()
    {
        StartOperation = CliStartOperation.ValidateConfiguration;
        if (_job is not null) throw new InvalidOperationException("Process already started.");
        if (start.UseShellExecute || !start.RedirectStandardInput || !start.RedirectStandardOutput || !start.RedirectStandardError)
            throw new InvalidOperationException("Redirected stdio is required.");
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null, errorRead = null, errorWrite = null;
        IntPtr attributes = IntPtr.Zero, inheritedHandles = IntPtr.Zero, jobList = IntPtr.Zero, mitigation = IntPtr.Zero;
        bool initializedAttributes = false;
        try
        {
            StartOperation = CliStartOperation.CreateJob;
            _job = Native.CreateJobObject(IntPtr.Zero, null);
            if (_job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new Native.JobExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
            StartOperation = CliStartOperation.ConfigureJob;
            if (!Native.SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf(typeof(Native.JobExtendedLimitInformation))))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var security = new Native.SecurityAttributes { Length = Marshal.SizeOf(typeof(Native.SecurityAttributes)), InheritHandle = true };
            StartOperation = CliStartOperation.CreateStdinPipe;
            CreatePipe(out inputRead, out inputWrite, ref security);
            StartOperation = CliStartOperation.CreateStdoutPipe;
            CreatePipe(out outputRead, out outputWrite, ref security);
            StartOperation = CliStartOperation.CreateStderrPipe;
            CreatePipe(out errorRead, out errorWrite, ref security);
            StartOperation = CliStartOperation.ProtectStdinHandle;
            MakePrivate(inputWrite);
            StartOperation = CliStartOperation.ProtectStdoutHandle;
            MakePrivate(outputRead);
            StartOperation = CliStartOperation.ProtectStderrHandle;
            MakePrivate(errorRead);

            StartOperation = CliStartOperation.InitializeAttributes;
            IntPtr size = IntPtr.Zero;
            var attributeCount = preferSystem32Images ? 3 : 2;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!Native.InitializeProcThreadAttributeList(attributes, attributeCount, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            initializedAttributes = true;
            inheritedHandles = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(inheritedHandles, 0, inputRead.DangerousGetHandle());
            Marshal.WriteIntPtr(inheritedHandles, IntPtr.Size, outputWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(inheritedHandles, IntPtr.Size * 2, errorWrite.DangerousGetHandle());
            StartOperation = CliStartOperation.SetHandleList;
            SetAttribute(attributes, 0x00020002, inheritedHandles, IntPtr.Size * 3); // HANDLE_LIST.
            jobList = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobList, _job.DangerousGetHandle());
            StartOperation = CliStartOperation.SetJobList;
            SetAttribute(attributes, 0x0002000d, jobList, IntPtr.Size); // JOB_LIST (Windows 10+).
            if (preferSystem32Images)
            {
                // The embedded reader imports only Windows system libraries. Prefer System32
                // at process creation, before any loader activity in the user cache directory.
                mitigation = Marshal.AllocHGlobal(sizeof(long));
                Marshal.WriteInt64(mitigation, 1L << 60);
                StartOperation = CliStartOperation.SetMitigationPolicy;
                SetAttribute(attributes, 0x00020007, mitigation, sizeof(long));
            }

            var startup = new Native.StartupInfoEx { AttributeList = attributes };
            startup.StartupInfo.Size = Marshal.SizeOf(typeof(Native.StartupInfoEx));
            startup.StartupInfo.Flags = 0x100; // STARTF_USESTDHANDLES.
            startup.StartupInfo.StandardInput = inputRead.DangerousGetHandle();
            startup.StartupInfo.StandardOutput = outputWrite.DangerousGetHandle();
            startup.StartupInfo.StandardError = errorWrite.DangerousGetHandle();
            // The executable is explicit and the argument string uses Win32 quoting in CliLocator.
            // A null environment block inherits the complete current environment, including CODEX_HOME.
            var command = new StringBuilder(BuildCommandLine(start));
            StartOperation = CliStartOperation.CreateProcess;
            if (!Native.CreateProcess(start.FileName, command, IntPtr.Zero, IntPtr.Zero, true,
                0x08000000 | 0x00080000, IntPtr.Zero, start.WorkingDirectory, ref startup, out var process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Native.CloseHandle(process.Thread);
            _process = new SafeProcessWaitHandle(process.Process);
            _hadOwnedProcess = true;

            // Anonymous pipes are synchronous Windows handles. Framework's ReadAsync/WriteAsync
            // use worker threads; our cancellation wrapper bounds waiting and closing the job
            // closes the peer pipe ends so outstanding I/O can finish.
            StartOperation = CliStartOperation.OpenStdioStreams;
            _inputHandle = inputWrite;
            var inputPipe = new FileStream(inputWrite, FileAccess.Write, 4096, isAsync: false); inputWrite = null;
            _outputHandle = outputRead;
            var outputPipe = new FileStream(outputRead, FileAccess.Read, 4096, isAsync: false); outputRead = null;
            _errorHandle = errorRead;
            var errorPipe = new FileStream(errorRead, FileAccess.Read, 4096, isAsync: false); errorRead = null;
            StandardInput = new StreamWriter(inputPipe, new UTF8Encoding(false));
            StandardOutput = new StreamReader(outputPipe, start.StandardOutputEncoding ?? Encoding.UTF8);
            StandardError = new StreamReader(errorPipe, start.StandardErrorEncoding ?? Encoding.UTF8);
            return true;
        }
        catch
        {
            // Keep the Job available for later whole-tree confirmation, including failures
            // after CreateProcess succeeded but redirected stream construction failed.
            try { RequestOwnedTreeTermination(); }
            catch (Win32Exception) { /* The lifetime owner will retry this same Job. */ }
            ClosePipeHandles();
            throw;
        }
        finally
        {
            // The parent's duplicate child pipe ends must close, otherwise EOF never arrives.
            inputRead?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose(); outputWrite?.Dispose(); errorRead?.Dispose(); errorWrite?.Dispose();
            if (initializedAttributes) Native.DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (inheritedHandles != IntPtr.Zero) Marshal.FreeHGlobal(inheritedHandles);
            if (jobList != IntPtr.Zero) Marshal.FreeHGlobal(jobList);
            if (mitigation != IntPtr.Zero) Marshal.FreeHGlobal(mitigation);
        }
    }

    public void KillOwnedTree()
    {
        // The noninherited job handle is the only open job handle, so closing it also kills
        // descendants even if the initial app-server process has already exited.
        _job?.Dispose();
    }

    internal static string BuildCommandLine(ProcessStartInfo start)
    {
        var arguments = start.Arguments;
#if !NETFRAMEWORK
        if (start.ArgumentList.Count > 0)
        {
            if (!string.IsNullOrEmpty(arguments)) throw new InvalidOperationException("Ambiguous process arguments.");
            arguments = string.Join(" ", start.ArgumentList.Select(StartupCommand.QuoteArgument));
        }
#endif
        return StartupCommand.QuoteArgument(start.FileName) + " " + arguments;
    }

    public void RequestOwnedTreeTermination()
    {
        if (!_hadOwnedProcess || _terminationRequested) return;
        if (_job is null || _job.IsClosed || _job.IsInvalid)
            throw new InvalidOperationException("Owned Job is unavailable.");
        if (!Native.TerminateJobObject(_job, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        _terminationRequested = true;
    }

    public OwnedProcessExit ObserveOwnedTreeExit()
    {
        if (!_hadOwnedProcess) return new(true, true);
        var rootExited = TryGetExitCode(out var code);
        if (rootExited)
        {
            // Job accounting can retain terminated processes while their handles remain open.
            // Cache the signaled-handle evidence before releasing this handle; never look it up
            // later by PID, which could identify a different process after reuse.
            _rootExited = true;
            _rootExitCode = code;
            _process?.Dispose();
            _process = null;
        }
        if (_job is null || _job.IsClosed || _job.IsInvalid)
            throw new InvalidOperationException("Owned Job is unavailable.");
        if (!Native.QueryInformationJobObject(_job, 1, out var accounting,
            (uint)Marshal.SizeOf(typeof(Native.JobBasicAccountingInformation)), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(rootExited, accounting.ActiveProcesses == 0, rootExited ? code : null);
    }

    public void Dispose()
    {
        KillOwnedTree();
        ClosePipeHandles();
        _process?.Dispose();
    }

    public bool WaitForExit(int milliseconds)
    {
        if (milliseconds < -1) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        var process = _process;
        if (process is null) return true;
        var result = Native.WaitForSingleObject(process, unchecked((uint)milliseconds));
        if (result == 0) return true;
        if (result == 0x102) return false;
        throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    // Read only our owned process handle. STILL_ACTIVE alone is not enough: a process
    // can legitimately exit with that numeric code, so check the handle's signal first.
    public bool TryGetExitCode(out int code)
    {
        code = 0;
        if (_rootExited) { code = _rootExitCode; return true; }
        if (_process is null || !WaitForExit(0)) return false;
        if (!Native.GetExitCodeProcess(_process, out var nativeCode))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        code = unchecked((int)nativeCode);
        return true;
    }

    private void ClosePipeHandles()
    {
        // Close raw handles before streams: never let StreamWriter.Dispose flush into a
        // nonreading child and block Exit. The connection owns its managed reader/writer.
        _inputHandle?.Dispose();
        _outputHandle?.Dispose();
        _errorHandle?.Dispose();
    }

    private static void CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref Native.SecurityAttributes security)
    {
        if (!Native.CreatePipe(out read, out write, ref security, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private static void MakePrivate(SafeFileHandle handle)
    {
        if (!Native.SetHandleInformation(handle, 1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private static void SetAttribute(IntPtr list, int key, IntPtr value, int bytes)
    {
        if (!Native.UpdateProcThreadAttribute(list, 0, (IntPtr)key, value, (IntPtr)bytes, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(true) { }
        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }

    private sealed class SafeProcessWaitHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeProcessWaitHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
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
        [StructLayout(LayoutKind.Sequential)]
        internal struct JobBasicAccountingInformation
        {
            public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(SafeProcessWaitHandle process, out uint exitCode);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeJobHandle CreateJobObject(IntPtr security, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass, ref JobExtendedLimitInformation information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(SafeJobHandle job, int informationClass,
            out JobBasicAccountingInformation information, uint length, IntPtr returnLength);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, ref SecurityAttributes security, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
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
        internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(SafeProcessWaitHandle handle, uint milliseconds);
    }
}
