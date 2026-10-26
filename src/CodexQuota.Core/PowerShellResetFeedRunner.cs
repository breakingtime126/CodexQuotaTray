using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace CodexQuota.Core;

/// <summary>
/// Runs only the installed PowerShell 7, once, with the embedded fixed-endpoint script.
/// All files stay beside the tray executable. A denied directory or process never triggers elevation,
/// a different output directory, a second executable, or a direct network fallback.
/// </summary>
public sealed class PowerShellResetFeedRunner : IDisposable
{
    private readonly string _directory;
    private readonly Func<byte[]> _script;
    private readonly Func<string> _powerShell;
    private readonly Func<ProcessStartInfo, IOwnedQuotaProcess> _processFactory;
    private readonly Func<bool> _supported;
    private readonly CancellationTokenSource _stop = new();
    private int _inFlight, _disposed;

    public PowerShellResetFeedRunner(string directory)
        : this(directory, ReadEmbeddedScript,
            () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            start => new OwnedQuotaProcess(start), () => Environment.OSVersion.Platform == PlatformID.Win32NT) { }

    internal PowerShellResetFeedRunner(string directory, Func<byte[]> script, Func<string> powerShell,
        Func<ProcessStartInfo, IOwnedQuotaProcess> processFactory, Func<bool> supported)
    {
        _directory = Path.GetFullPath(directory);
        _script = script;
        _powerShell = powerShell;
        _processFactory = processFactory;
        _supported = supported;
    }

    // File I/O and native process creation must never block the WinForms message thread.
    public Task<byte[]> FetchAsync(ResetFeedFileRequest request, CancellationToken cancellationToken) =>
        Task.Run(() => FetchCoreAsync(request, cancellationToken), cancellationToken);

    private async Task<byte[]> FetchCoreAsync(ResetFeedFileRequest request, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            throw new ResetFeedFileException(ResetFeedFileFailure.CleanupPending);
        string? requestPath = null;
        var ownsRequest = false;
        IOwnedQuotaProcess? process = null;
        Task pipes = Task.CompletedTask;
        FileStream? lockLease = null, scriptLease = null, requestLease = null;
        var phase = ResetFeedFileFailure.OutputWriteFailed;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0) throw new OperationCanceledException(deadline.Token);
            if (!_supported()) throw new ResetFeedFileException(ResetFeedFileFailure.MissingPowerShell);
            if (!Guid.TryParseExact(request.RequestId, "D", out var id) || id.ToString("D") != request.RequestId)
                throw new ResetFeedFileException(ResetFeedFileFailure.OutputInvalid);
            var pwsh = _powerShell();
            if (!Path.IsPathRooted(pwsh) || !File.Exists(pwsh))
                throw new ResetFeedFileException(ResetFeedFileFailure.MissingPowerShell);
            // Shared with the small validator; two owners cannot publish the same output concurrently.
            var lockPath = Path.Combine(_directory, ".reset-state.lock");
            RejectReparsePoint(lockPath);
            lockLease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var scriptPath = Path.Combine(_directory, "Fetch-ResetState-0.2.35.ps1");
            scriptLease = PrepareScript(scriptPath, _script());
            requestPath = Path.Combine(_directory, ".reset-request." + request.RequestId + ".json");
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, requestId = request.RequestId, etag = request.ETag });
            if (requestBytes.Length > 2048) throw new ResetFeedFileException(ResetFeedFileFailure.OutputInvalid);
            using (var writer = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsRequest = true;
                writer.Write(requestBytes, 0, requestBytes.Length);
                writer.Flush(true);
            }
            // Windows share flags are bilateral: retaining a write handle would prevent the child
            // from opening its own read-only/ShareRead handle. Hold only a read lease during execution.
            requestLease = new FileStream(requestPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            deadline.Token.ThrowIfCancellationRequested();
            phase = ResetFeedFileFailure.LaunchFailed;
            var start = CreateStartInfo(pwsh, scriptPath, requestPath, request.RequestId, _directory);
            process = _processFactory(start);
            if (!process.Start()) throw new ResetFeedFileException(ResetFeedFileFailure.LaunchFailed);
            // Drain bounded chunks, discard all text. Neither stdout nor stderr is diagnostic input.
            pipes = Task.WhenAll(DiscardAsync(process.StandardOutput), DiscardAsync(process.StandardError));
            process.StandardInput.Close();
            phase = ResetFeedFileFailure.RequestFailed;
            int exitCode;
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var exit = process.ObserveExit();
                if (exit.RootExited && exit.TreeExited && exit.ExitCode is { } code) { exitCode = code; break; }
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            }
            if (exitCode is not (0 or 1))
                throw new ResetFeedFileException(exitCode == 2 ? ResetFeedFileFailure.OutputWriteFailed :
                    ResetFeedFileFailure.ChildFailed, childExitCode: exitCode);
            phase = ResetFeedFileFailure.OutputMissing;
            var result = ReadBounded(Path.Combine(_directory, "reset-state.json"));
            // A failed process may only contribute a correlated error envelope, never a successful old file.
            using (var document = JsonDocument.Parse(result, new JsonDocumentOptions { MaxDepth = 14 }))
            {
                if (!document.RootElement.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
                    (exitCode == 1) != (status.GetString() == "Error"))
                    throw new ResetFeedFileException(ResetFeedFileFailure.ChildFailed, childExitCode: exitCode);
            }
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (ResetFeedFileException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or
            InvalidOperationException or ArgumentException or NotSupportedException or JsonException)
        {
            throw new ResetFeedFileException(error is JsonException ? ResetFeedFileFailure.OutputInvalid :
                phase == ResetFeedFileFailure.OutputWriteFailed && error is UnauthorizedAccessException ? ResetFeedFileFailure.OutputWriteDenied :
                phase == ResetFeedFileFailure.LaunchFailed && error is UnauthorizedAccessException ? ResetFeedFileFailure.LaunchDenied : phase,
                ResetFeedDiagnostic.NumericCode(error));
        }
        finally
        {
            try
            {
                if (process is not null && process.HasOwnedProcess)
                    await BackendCleanupCoordinator.WaitAsync(process, null, pipes, _stop.Token,
                        BackendCleanupOptions.Default, (_, _) => { }).ConfigureAwait(false);
            }
            catch (BackendCleanupUnconfirmedException) when (_stop.IsCancellationRequested)
            { /* Application shutdown closes only this retained kill-on-close Job below. */ }
            finally
            {
                process?.Dispose();
                requestLease?.Dispose();
                scriptLease?.Dispose();
                if (ownsRequest && requestPath is not null)
                    try { File.Delete(requestPath); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                lockLease?.Dispose();
                Interlocked.Exchange(ref _inFlight, 0);
            }
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string pwsh, string script, string requestPath, string requestId, string directory) =>
        new(pwsh)
        {
            Arguments = "-NoLogo -NoProfile -NonInteractive -File " + StartupCommand.QuoteArgument(script) +
                " -RequestId " + requestId + " -OutputPath " + StartupCommand.QuoteArgument(Path.Combine(directory, "reset-state.json")) +
                " -RequestPath " + StartupCommand.QuoteArgument(requestPath),
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            // No environment, proxy, TLS, ExecutionPolicy, user name or credential changes.
        };

    private static FileStream PrepareScript(string path, byte[] expected)
    {
        if (expected.Length is 0 or > 65536) throw new ResetFeedFileException(ResetFeedFileFailure.OutputInvalid);
        RejectReparsePoint(path);
        if (!File.Exists(path))
        {
            using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created.Write(expected, 0, expected.Length);
            created.Flush(true);
        }
        var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (lease.Length != expected.Length) throw new ResetFeedFileException(ResetFeedFileFailure.ScriptMismatch);
            for (var i = 0; i < expected.Length; i++)
                if (lease.ReadByte() != expected[i]) throw new ResetFeedFileException(ResetFeedFileFailure.ScriptMismatch);
            return lease; // Prevent alteration until the child and its entire owned Job have exited.
        }
        catch { lease.Dispose(); throw; }
    }

    internal static byte[] ReadBounded(string path)
    {
        RejectReparsePoint(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length == 0) throw new ResetFeedFileException(ResetFeedFileFailure.OutputInvalid);
        if (stream.Length > ResetFeedFileProtocol.MaximumEnvelopeBytes)
            throw new ResetFeedFileException(ResetFeedFileFailure.OutputTooLarge);
        var bytes = new byte[(int)stream.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = stream.Read(bytes, offset, bytes.Length - offset);
            if (count == 0) throw new ResetFeedFileException(ResetFeedFileFailure.OutputInvalid);
            offset += count;
        }
        if (stream.ReadByte() != -1) throw new ResetFeedFileException(ResetFeedFileFailure.OutputTooLarge);
        return bytes;
    }

    private static void RejectReparsePoint(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ResetFeedFileException(ResetFeedFileFailure.OutputInvalid);
    }

    private static async Task DiscardAsync(TextReader reader)
    {
        var buffer = new char[512];
        try { while (await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) != 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private static byte[] ReadEmbeddedScript()
    {
        using var stream = typeof(PowerShellResetFeedRunner).Assembly.GetManifestResourceStream("CodexQuota.ResetFeed.ps1")
            ?? throw new ResetFeedFileException(ResetFeedFileFailure.ScriptMismatch);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _stop.Cancel();
        // Active lifetimes retain ownership until cleanup; never dispose their handles from another thread.
    }
}
