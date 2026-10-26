using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CodexQuota.Core;
using Microsoft.Win32.SafeHandles;

namespace CodexQuota.Tray;

/// <summary>
/// One ordinary-user instance per Windows session. Only a cooperative, verified peer exits
/// itself. No process enumeration, window-close guessing, process killing or disk replacement.
/// Keep both mutexes on the calling STA thread; async work never owns/releases them.
/// </summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _instance;
    private readonly Mutex _launch;
    private readonly string _pipeName;
    private readonly string _userSid;
    private readonly string _logonSid;
    private readonly int _session;
    private readonly Version _version;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _listenerLock = new();
    private bool _ownsInstance, _ownsLaunch, _disposed;
    private NamedPipeServerStream? _listener;
    private Task? _server;

    private SingleInstanceCoordinator(out HandoffStartupStage stage)
    {
        stage = HandoffStartupStage.Identity;
        using var identity = WindowsIdentity.GetCurrent();
        _userSid = identity.User?.Value ?? throw new SecurityException("Missing user identity.");
        _logonSid = LogonSessionIdentity.Read(identity);
        using var process = Process.GetCurrentProcess();
        _session = process.SessionId;
        _version = Assembly.GetExecutingAssembly().GetName().Version ?? throw new InvalidDataException("Missing release version.");
        stage = HandoffStartupStage.Coordination;
        _instance = new Mutex(false, $"Local\\CodexQuotaTray-{_userSid}"); // Also excludes all pre-protocol releases.
        _launch = new Mutex(false, $"Local\\CodexQuotaTray-Launch-{_userSid}");
        _pipeName = $"CodexQuotaTray-Handoff1-{_userSid}-{_session}";
    }

    public static SingleInstanceCoordinator? Enter(string? explicitCliPath, out string? effectiveCliPath, out string? warning)
    {
        effectiveCliPath = explicitCliPath;
        warning = null;
        SingleInstanceCoordinator? result = null;
        var stage = HandoffStartupStage.Identity;
        try
        {
            result = new SingleInstanceCoordinator(out stage);
            if (!Acquire(result._launch, 15000)) { warning = "另一个启动请求正在交接版本，请稍后再试。"; result.Dispose(); return null; }
            result._ownsLaunch = true;
            if (Acquire(result._instance, 0)) { result._ownsInstance = true; return result; }
            stage = HandoffStartupStage.ExistingInstance;
            var outcome = result.RequestHandoffAsync().GetAwaiter().GetResult();
            if (outcome.Disposition == HandoffDisposition.KeepExisting)
            {
                if (outcome.ExistingVersion > result._version) warning = "已有更高版本在运行，已保留当前运行的版本。";
                result.Dispose();
                return null;
            }
            // An acknowledgement means accepted, not exited. Wait on the exact verified OS
            // process handle, then acquire the same mutex. Never start a second tray on timeout.
            using (outcome.Peer)
            {
                if (outcome.Peer is null || Native.WaitForSingleObject(outcome.Peer.Handle, 15000) != 0 || !Acquire(result._instance, 1000))
                    throw new TimeoutException();
                result._ownsInstance = true;
            }
            effectiveCliPath = InstanceHandoffProtocol.SelectCliPath(explicitCliPath, outcome.CliPath);
            return result;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            result?.Dispose();
            warning = HandoffStartupFailure.Message(stage);
            return null;
        }
    }

    public void StartServer(string? cliPath, Action requestExit)
    {
        // Create the secured endpoint before letting the next launch leave its gate. No
        // window-message endpoint exists, and a foreign endpoint is never overwritten.
        _listener = CreatePipe();
        _server = ServeAsync(cliPath, requestExit);
        ReleaseLaunch();
    }

    private async Task<(HandoffDisposition Disposition, Version ExistingVersion, VerifiedPeer? Peer, string? CliPath)> RequestHandoffAsync()
    {
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var cancel = deadline.Token.Register(() => pipe.Dispose());
        await pipe.ConnectAsync(2000, deadline.Token).ConfigureAwait(false);
        var peer = VerifyPeer(pipe.SafePipeHandle, server: true);
        try
        {
            var nonce = Guid.NewGuid().ToString("N");
            await InstanceHandoffProtocol.WriteAsync(pipe, InstanceHandoffProtocol.Message("hello", _version, nonce), deadline.Token).ConfigureAwait(false);
            var response = await InstanceHandoffProtocol.ReadAsync(pipe, deadline.Token).ConfigureAwait(false);
            if (peer.Version >= _version && InstanceHandoffProtocol.IsReply(response, "keep", peer.Version, nonce))
            {
                await InstanceHandoffProtocol.WriteAsync(pipe, InstanceHandoffProtocol.Message("received", _version, nonce), deadline.Token).ConfigureAwait(false);
                peer.Dispose();
                return (HandoffDisposition.KeepExisting, peer.Version, null, null);
            }
            if (peer.Version >= _version || !InstanceHandoffProtocol.IsReply(response, "ready", peer.Version, nonce))
                throw new InvalidDataException("Unexpected handoff response.");
            await InstanceHandoffProtocol.WriteAsync(pipe, InstanceHandoffProtocol.Message("commit", _version, nonce), deadline.Token).ConfigureAwait(false);
            var closing = await InstanceHandoffProtocol.ReadAsync(pipe, deadline.Token).ConfigureAwait(false);
            if (!InstanceHandoffProtocol.IsReply(closing, "closing", peer.Version, nonce) || closing.CliPath != response.CliPath)
                throw new InvalidDataException("Missing close acknowledgement.");
            // The server must know its acknowledgement was actually read before closing
            // the pipe; merely writing into a pipe buffer does not establish delivery.
            await InstanceHandoffProtocol.WriteAsync(pipe, InstanceHandoffProtocol.Message("received", _version, nonce), deadline.Token).ConfigureAwait(false);
            return (HandoffDisposition.Prepare, peer.Version, peer, response.CliPath);
        }
        catch { peer.Dispose(); throw; }
    }

    private async Task ServeAsync(string? cliPath, Action requestExit)
    {
        while (!_stop.IsCancellationRequested)
        {
            var pipe = _listener;
            if (pipe is null) return;
            var closeAccepted = false;
            var reusePipe = false;
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(6));
                using var cancel = deadline.Token.Register(() => pipe.Dispose());
                using var peer = VerifyPeer(pipe.SafePipeHandle, server: false);
                var request = await InstanceHandoffProtocol.ReadAsync(pipe, deadline.Token).ConfigureAwait(false);
                var disposition = InstanceHandoffProtocol.Evaluate(_version, peer.Version, request);
                if (disposition == HandoffDisposition.KeepExisting)
                {
                    await InstanceHandoffProtocol.WriteAsync(pipe, InstanceHandoffProtocol.Message("keep", _version, request.Nonce), deadline.Token).ConfigureAwait(false);
                    var receipt = await InstanceHandoffProtocol.ReadAsync(pipe, deadline.Token).ConfigureAwait(false);
                    if (!InstanceHandoffProtocol.IsReply(receipt, "received", peer.Version, request.Nonce) || receipt.CliPath is not null)
                        throw new InvalidDataException("Unconfirmed response.");
                    reusePipe = true;
                }
                else if (disposition == HandoffDisposition.Prepare)
                {
                    var gate = new HandoffCommitGate();
                    if (!gate.Prepare(_version, peer.Version, request)) throw new InvalidDataException();
                    await InstanceHandoffProtocol.WriteAsync(pipe, InstanceHandoffProtocol.Message("ready", _version, request.Nonce, cliPath), deadline.Token).ConfigureAwait(false);
                    var commit = await InstanceHandoffProtocol.ReadAsync(pipe, deadline.Token).ConfigureAwait(false);
                    if (!gate.Commit(commit)) throw new InvalidDataException();
                    await InstanceHandoffProtocol.WriteAsync(pipe, InstanceHandoffProtocol.Message("closing", _version, request.Nonce, cliPath), deadline.Token).ConfigureAwait(false);
                    var receipt = await InstanceHandoffProtocol.ReadAsync(pipe, deadline.Token).ConfigureAwait(false);
                    if (!InstanceHandoffProtocol.IsReply(receipt, "received", peer.Version, request.Nonce) || receipt.CliPath is not null)
                        throw new InvalidDataException("Unconfirmed close acknowledgement.");
                    closeAccepted = true;
                }
            }
            catch (Exception exception) when (IsExpected(exception)) { /* A malformed/timed-out peer cannot close this instance. */ }
            finally
            {
                if (!closeAccepted && !_stop.IsCancellationRequested)
                {
                    // Reuse the secured server instance even after a rejected/malformed
                    // request; closing/recreating while a rejected client still has its
                    // handle open can otherwise lose the first-instance endpoint.
                    try { pipe.Disconnect(); reusePipe = true; }
                    catch (Exception exception) when (IsExpected(exception)) { reusePipe = false; }
                }
                if (!reusePipe) pipe.Dispose();
            }
            if (closeAccepted)
            {
                requestExit(); // Program marshals this onto the existing WinForms thread.
                return;
            }
            if (_stop.IsCancellationRequested) return;
            if (reusePipe) continue;
            if (!await RecreateListenerAsync().ConfigureAwait(false)) return;
        }
    }

    private async Task<bool> RecreateListenerAsync()
    {
        var elapsed = Stopwatch.StartNew();
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                lock (_listenerLock)
                {
                    if (_stop.IsCancellationRequested) return false;
                    _listener = CreatePipe();
                    return true;
                }
            }
            // A timed-out request closes the async pipe to bound stuck I/O. Its client
            // handle can briefly outlive that close. Retry only this bounded drain race;
            // never drop FIRST_PIPE_INSTANCE to attach to an unknown endpoint.
            catch (Win32Exception exception) when (exception.NativeErrorCode is 5 or 231 && elapsed.Elapsed < TimeSpan.FromSeconds(3)) { }
            catch (Exception exception) when (IsExpected(exception)) { return false; }
            try { await Task.Delay(100, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private NamedPipeServerStream CreatePipe()
    {
        // Explicit current-logon ACL plus PIPE_REJECT_REMOTE_CLIENTS. The default pipe ACL
        // is broader, and naming a pipe after a SID is not itself access control.
        if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;;GA;;;{_logonSid})", 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var security = new Native.SecurityAttributes { Length = Marshal.SizeOf(typeof(Native.SecurityAttributes)), Descriptor = descriptor };
            var handle = Native.CreateNamedPipe($"\\\\.\\pipe\\{_pipeName}", 0x40080003, 0x8, 1, 8192, 8192, 0, ref security);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { Native.LocalFree(descriptor); }
    }

    private VerifiedPeer VerifyPeer(SafePipeHandle pipe, bool server)
    {
        uint pid;
        if (!(server ? Native.GetNamedPipeServerProcessId(pipe, out pid) : Native.GetNamedPipeClientProcessId(pipe, out pid)) ||
            !Native.ProcessIdToSessionId(pid, out var session) || session != (uint)_session)
            throw new SecurityException("Different session.");
        var handle = Native.OpenProcess(0x00101000, false, pid); // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION.
        if (handle.IsInvalid) { handle.Dispose(); throw new SecurityException("Unavailable peer."); }
        try
        {
            string? userSid, logonSid;
            if (!Native.OpenProcessToken(handle, 0x0008, out var token)) throw new SecurityException("Unavailable peer identity.");
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            {
                userSid = identity.User?.Value;
                logonSid = LogonSessionIdentity.Read(identity);
            }
            var path = new StringBuilder(32768);
            var length = (uint)path.Capacity;
            if (!Native.QueryFullProcessImageName(handle, 0, path, ref length)) throw new SecurityException("Unavailable peer image.");
            var metadata = FileVersionInfo.GetVersionInfo(path.ToString());
            var peerIdentity = new HandoffPeerIdentity(userSid, logonSid, (int)session, metadata.ProductName, metadata.OriginalFilename, metadata.FileVersion);
            if (!InstanceHandoffProtocol.VerifyIdentity(peerIdentity, _userSid, _logonSid, _session, out var version))
                throw new SecurityException("Different application.");
            // These portable binaries are unsigned. Metadata+local protocol avoids accidental
            // matches, but is not an authenticity boundary against malicious same-user code.
            return new VerifiedPeer(handle, version);
        }
        catch { handle.Dispose(); throw; }
    }

    private static bool Acquire(Mutex mutex, int milliseconds)
    {
        try { return mutex.WaitOne(milliseconds); }
        catch (AbandonedMutexException) { return true; } // Ownership is granted when its former process died.
    }

    private static bool IsExpected(Exception exception) => exception is IOException or UnauthorizedAccessException or
        SecurityException or Win32Exception or TimeoutException or OperationCanceledException or ObjectDisposedException or
        InvalidOperationException or ArgumentException or JsonException;

    private void ReleaseLaunch()
    {
        if (!_ownsLaunch) return;
        _launch.ReleaseMutex();
        _ownsLaunch = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        lock (_listenerLock) { _listener?.Dispose(); }
        if (_server is not null)
            _ = _server.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (_ownsInstance) { _instance.ReleaseMutex(); _ownsInstance = false; }
        ReleaseLaunch();
        _instance.Dispose();
        _launch.Dispose();
        // ServeAsync can still observe the token after cancellation; dispose the source only
        // after that task finishes rather than racing its final loop iteration.
        if (_server is null) _stop.Dispose();
        else _ = _server.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }

    private sealed class VerifiedPeer(SafeProcessHandle handle, Version version) : IDisposable
    {
        public SafeProcessHandle Handle { get; } = handle;
        public Version Version { get; } = version;
        public void Dispose() => Handle.Dispose();
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
        [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateNamedPipeW")]
        internal static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outSize, uint inSize, uint timeout, ref SecurityAttributes attributes);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeFileHandle token);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    }
}
