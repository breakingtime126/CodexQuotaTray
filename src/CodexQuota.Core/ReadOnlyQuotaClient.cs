using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text.Json;

namespace CodexQuota.Core;

/// <summary>One owned, embedded read-only helper per refresh. Never discovers or starts the Codex CLI.</summary>
public sealed class ReadOnlyQuotaClient
{
    private readonly Func<HelperExecutableLease> _resolve;
    private readonly Action<CliDiagnostic>? _diagnostic;
    private readonly TimeSpan _timeout;

    public ReadOnlyQuotaClient(Func<HelperExecutableLease> resolve, Action<CliDiagnostic>? diagnostic = null)
        : this(resolve, diagnostic, TimeSpan.FromSeconds(40)) { }

    internal ReadOnlyQuotaClient(Func<HelperExecutableLease> resolve, Action<CliDiagnostic>? diagnostic, TimeSpan timeout)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _diagnostic = diagnostic;
        _timeout = timeout;
    }

    public async Task<QuotaRead> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_timeout);
        HelperExecutableLease? lease = null;
        OwnedProcess? process = null;
        Task stderr = Task.CompletedTask;
        Task stdout = Task.CompletedTask;
        var stage = CliStage.HelperExtract;
        QuotaRead Complete(QuotaRead read, int? code = null, CliErrorKind error = CliErrorKind.None, NetworkDiagnostic? network = null)
        {
            Publish(new(CliSource.EmbeddedReader, ReadOnlyQuotaProtocol.DiagnosticVersion, stage, read.Status, code, error,
                stage == CliStage.Start ? process?.StartOperation ?? CliStartOperation.None : CliStartOperation.None) { Network = network });
            return read;
        }
        try
        {
            // Extraction and hashing never block the tray's UI thread. Await the resolver
            // itself even if canceled, so every returned lease has an owner and is disposed.
            lease = await Task.Run(_resolve).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            stage = CliStage.Start;
            process = new OwnedProcess(CreateStartInfo(lease.ExecutablePath));
            if (!process.Start()) return Complete(new(ReadStatus.Unavailable), error: CliErrorKind.HelperProcessFailure);
            // No stdin commands, tokens, paths, endpoint overrides or account identifiers are sent.
            process.StandardInput.Close();
            stage = CliStage.AuthLoad;
            stderr = DrainStderrAsync(process.StandardError.BaseStream, deadline.Token);
            var read = ReadOnlyQuotaProtocol.ReadAsync(process.StandardOutput.BaseStream, deadline.Token);
            stdout = read;
            var first = await Task.WhenAny(read, stderr).ConfigureAwait(false);
            if (ReferenceEquals(first, stderr)) await stderr.ConfigureAwait(false);
            var response = await read.ConfigureAwait(false);
            // A stderr flood is a malformed helper, even if stdout eventually supplies JSON.
            await stderr.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            stage = response.Stage;
            return Complete(response.Read, response.Code, response.ErrorKind, response.Network);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return Complete(new(ReadStatus.Timeout), error: CliErrorKind.Timeout); }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            return Complete(new(ReadStatus.ProtocolError), error: stage == CliStage.HelperExtract
                ? CliErrorKind.HelperIntegrityFailure : CliErrorKind.HelperProtocolError);
        }
        catch (FileNotFoundException)
        { return Complete(new(ReadStatus.Unavailable), error: stage == CliStage.HelperExtract ? CliErrorKind.HelperMissing : CliErrorKind.HelperProcessFailure); }
        catch (Win32Exception e)
        { return Complete(new(ReadStatus.Unavailable), e.NativeErrorCode, stage == CliStage.HelperExtract ? CliErrorKind.HelperIntegrityFailure : CliErrorKind.HelperProcessFailure); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException or ArgumentException or NotSupportedException)
        { return Complete(new(ReadStatus.Unavailable), e.HResult, stage == CliStage.HelperExtract ? CliErrorKind.HelperIntegrityFailure : CliErrorKind.HelperProcessFailure); }
        finally
        {
            deadline.Cancel();
            if (process is not null)
            {
                // Closing the Framework kill-on-close Job covers descendants before releasing
                // the verified image lease. net8 is only used by the synthetic cross-platform tests.
                process.KillOwnedTree();
                var exited = await Task.Run(() => SafeWaitForExit(process, 2000)).ConfigureAwait(false);
                if (exited) Release(process, lease);
                else _ = RetireAsync(process, lease);
            }
            else lease?.Dispose();
            await ObserveCleanupAsync(stdout).ConfigureAwait(false);
            await ObserveCleanupAsync(stderr).ConfigureAwait(false);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath) => new()
    {
        FileName = executablePath,
        Arguments = "--read",
        WorkingDirectory = Path.GetDirectoryName(executablePath) ?? throw new InvalidDataException("Invalid reader location."),
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    private static async Task DrainStderrAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        long total = 0;
        int count;
        while ((count = await ReadOnlyQuotaProtocol.ReadChunkAsync(stream, buffer, token).ConfigureAwait(false)) != 0)
        {
            total += count;
            if (total > ReadOnlyQuotaProtocol.MaximumResponseBytes)
                throw new InvalidDataException("Embedded reader stderr exceeded its limit.");
        }
    }

    private static async Task ObserveCleanupAsync(Task pending)
    {
        try { await AsyncCompatibility.WaitAsync(pending, TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException or TimeoutException) { }
        // If synchronous Framework pipe I/O outlives the deadline, observe a later fault.
        _ = pending.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static bool SafeWaitForExit(OwnedProcess process, int milliseconds)
    {
        try { return process.WaitForExit(milliseconds); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or ObjectDisposedException or NotSupportedException)
        { return false; }
    }

    private static async Task RetireAsync(OwnedProcess process, HelperExecutableLease? lease)
    {
        // Rare OS failures cannot turn a verified, still-running image into an unlocked path.
        // This retained owner retries only cleanup of the one process it already created.
        while (true)
        {
            process.KillOwnedTree();
            if (await Task.Run(() => SafeWaitForExit(process, 2000)).ConfigureAwait(false))
            { Release(process, lease); return; }
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    private static void Release(OwnedProcess process, HelperExecutableLease? lease)
    {
        try { process.Dispose(); }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { }
        finally { lease?.Dispose(); }
    }

    private void Publish(CliDiagnostic diagnostic)
    {
        try { _diagnostic?.Invoke(diagnostic); }
        catch (Exception) { /* Diagnostics never interrupt quota reads. */ }
    }

    private sealed class OwnedProcess : IDisposable
    {
#if NETFRAMEWORK
        private readonly FrameworkCliProcess _process;
        public CliStartOperation StartOperation => _process.StartOperation;
        public OwnedProcess(ProcessStartInfo start) => _process = new(start, preferSystem32Images: true);
        public bool Start() => _process.Start();
        public bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);
        public void KillOwnedTree() => _process.KillOwnedTree();
#else
        private readonly Process _process;
        private bool _started;
        public CliStartOperation StartOperation => CliStartOperation.ManagedProcessStart;
        public OwnedProcess(ProcessStartInfo start) => _process = new() { StartInfo = start };
        public bool Start() => _started = _process.Start();
        public bool WaitForExit(int milliseconds) => !_started || _process.WaitForExit(milliseconds);
        public void KillOwnedTree()
        {
            try { if (_started && !_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
        }
#endif
        public StreamWriter StandardInput => _process.StandardInput;
        public StreamReader StandardOutput => _process.StandardOutput;
        public StreamReader StandardError => _process.StandardError;
        public void Dispose() => _process.Dispose();
    }
}
