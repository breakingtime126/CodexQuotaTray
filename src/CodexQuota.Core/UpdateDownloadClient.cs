using System.Diagnostics;
using System.Text;

namespace CodexQuota.Core;

/// <summary>Fixed public update transport. It never decides whether downloaded code is trusted.</summary>
public sealed class UpdateDownloadClient : IDisposable
{
    public const int MaximumManifestBytes = 32768;
    public const int MaximumAssetBytes = 30 * 1024 * 1024;
    private readonly string _stagingRoot;
    private readonly Func<string> _powerShell;
    private readonly Func<byte[]> _script;
    private readonly Func<ProcessStartInfo, IOwnedQuotaProcess> _processFactory;
    private readonly Func<bool> _supported;
    private readonly CancellationTokenSource _stop = new();
    private int _inFlight, _disposed;

    public UpdateDownloadClient(string stagingRoot)
        : this(stagingRoot, () => SelectPowerShell(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.System), File.Exists),
            ReadEmbeddedScript, start => new OwnedQuotaProcess(start), () => Environment.OSVersion.Platform == PlatformID.Win32NT) { }

    internal UpdateDownloadClient(string stagingRoot, Func<string> powerShell, Func<byte[]> script,
        Func<ProcessStartInfo, IOwnedQuotaProcess> processFactory, Func<bool> supported)
    {
        _stagingRoot = Path.GetFullPath(stagingRoot);
        _powerShell = powerShell; _script = script; _processFactory = processFactory; _supported = supported;
    }

    internal static string SelectPowerShell(string programFiles, string systemDirectory, Func<string, bool> exists)
    {
        if (!Path.IsPathRooted(programFiles) || !Path.IsPathRooted(systemDirectory))
            throw new InvalidDataException("PowerShell roots must be absolute.");
        var modern = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        if (exists(modern)) return modern;
        var inbox = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (exists(inbox)) return inbox;
        throw new FileNotFoundException("Neither installed PowerShell 7 nor Windows PowerShell 5.1 was found.");
    }

    public async Task<byte[]> FetchManifestAsync(CancellationToken cancellationToken)
    {
        var path = await FetchAsync(null, cancellationToken).ConfigureAwait(false);
        try { return ReadBounded(path, MaximumManifestBytes); }
        finally { DeleteOwnedDownload(path); }
    }

    /// <summary>Returns fresh, untrusted bytes staged in a unique child folder. Caller must verify signed manifest/hash before use.</summary>
    public Task<string> FetchAssetAsync(Version version, CancellationToken cancellationToken)
    {
        if (version is null || version.Major < 0 || version.Minor < 0 || version.Build < 0 || version.Revision < 0)
            throw new ArgumentException("A four-component update version is required.", nameof(version));
        return FetchAsync(version, cancellationToken);
    }

    private Task<string> FetchAsync(Version? version, CancellationToken cancellationToken) =>
        Task.Run(() => FetchCoreAsync(version, cancellationToken), cancellationToken);

    private async Task<string> FetchCoreAsync(Version? version, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) throw new InvalidOperationException("Update download already running.");
        string? folder = null;
        IOwnedQuotaProcess? process = null;
        Task pipes = Task.CompletedTask;
        FileStream? scriptLease = null;
        var succeeded = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(150));
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0) throw new OperationCanceledException(deadline.Token);
            if (!_supported()) throw new PlatformNotSupportedException("Update transport requires Windows PowerShell 5.1 or PowerShell 7.");
            var pwsh = _powerShell();
            if (!Path.IsPathRooted(pwsh)) throw new InvalidDataException("PowerShell path must be absolute.");
            RejectReparseAncestors(pwsh);
            if (!File.Exists(pwsh)) throw new FileNotFoundException("Windows PowerShell 5.1 or PowerShell 7 is required for automatic updates.");
            RejectReparseAncestors(_stagingRoot);
            Directory.CreateDirectory(_stagingRoot);
            RejectReparseAncestors(_stagingRoot);
            folder = Path.Combine(_stagingRoot, "fetch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            RejectReparseAncestors(folder);
            var scriptPath = Path.Combine(folder, "Fetch-Update.ps1");
            scriptLease = PrepareScript(scriptPath, _script());
            var start = new ProcessStartInfo(pwsh)
            {
                Arguments = "-NoLogo -NoProfile -NonInteractive -File " + StartupCommand.QuoteArgument(scriptPath) +
                    " -Operation " + (version is null ? "manifest" : "asset") +
                    (version is null ? "" : " -Version " + version.ToString(4)),
                WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                // Preserve environment, policy, proxy, credentials and TLS settings.
            };
            process = _processFactory(start);
            if (!process.Start()) throw new IOException("Could not start update transport.");
            process.StandardInput.Close();
            pipes = Task.WhenAll(DiscardAsync(process.StandardOutput), DiscardAsync(process.StandardError));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var exit = process.ObserveExit();
                if (exit.RootExited && exit.TreeExited && exit.ExitCode is { } code)
                {
                    if (code != 0) throw new IOException("Update download failed; current version was not changed.");
                    break;
                }
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
            var output = Path.Combine(folder, "download.bin");
            RejectReparseAncestors(output);
            using (var stream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var limit = version is null ? MaximumManifestBytes : MaximumAssetBytes;
                if (stream.Length is <= 0 || stream.Length > limit) throw new InvalidDataException("Update size is invalid.");
            }
            deadline.Token.ThrowIfCancellationRequested();
            succeeded = true;
            return output;
        }
        finally
        {
            try
            {
                if (process is not null && process.HasOwnedProcess)
                {
                    process.RequestTermination(); // Only this retained process/job, never a name or caller-supplied PID.
                    var until = DateTime.UtcNow.AddSeconds(5);
                    while (DateTime.UtcNow < until)
                    {
                        var exit = process.ObserveExit();
                        if (exit.RootExited && exit.TreeExited) break;
                        await Task.Delay(50).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                process?.Dispose(); // Private kill-on-close job ensures no owned transport is left running.
                scriptLease?.Dispose();
                await Task.WhenAny(pipes, Task.Delay(1000)).ConfigureAwait(false);
                if (folder is not null)
                {
                    DeleteFileSafely(Path.Combine(folder, "Fetch-Update.ps1"));
                    DeleteFileSafely(Path.Combine(folder, "download.partial"));
                    if (!succeeded) DeleteFileSafely(Path.Combine(folder, "download.bin"));
                    if (!succeeded) DeleteDirectoryIfEmpty(folder);
                }
                Interlocked.Exchange(ref _inFlight, 0);
            }
        }
    }

    private static byte[] ReadEmbeddedScript()
    {
        using var resource = typeof(UpdateDownloadClient).Assembly.GetManifestResourceStream("CodexQuota.Update.ps1")
            ?? throw new InvalidDataException("Embedded update transport is missing.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static FileStream PrepareScript(string path, byte[] expected)
    {
        if (expected.Length is 0 or > 32768) throw new InvalidDataException("Embedded update transport is invalid.");
        RejectReparseAncestors(path);
        using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { writer.Write(expected, 0, expected.Length); writer.Flush(true); }
        var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (lease.Length != expected.Length) throw new InvalidDataException("Update transport changed.");
            for (var i = 0; i < expected.Length; i++)
                if (lease.ReadByte() != expected[i]) throw new InvalidDataException("Update transport changed.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static byte[] ReadBounded(string path, int limit)
    {
        RejectReparseAncestors(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 || input.Length > limit) throw new InvalidDataException("Update response is invalid.");
        var bytes = new byte[(int)input.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = input.Read(bytes, offset, bytes.Length - offset);
            if (count == 0) throw new InvalidDataException("Update response is truncated.");
            offset += count;
        }
        if (input.ReadByte() != -1) throw new InvalidDataException("Update response changed.");
        return bytes;
    }

    public static void RejectReparseAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update paths cannot contain reparse points.");
        }
    }

    /// <summary>Only delete a fetch directory returned by this instance, with fixed known filenames.</summary>
    public void DeleteOwnedDownload(string path)
    {
        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full);
        if (folder is null || !string.Equals(Path.GetDirectoryName(folder), _stagingRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(folder).StartsWith("fetch-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(folder).Substring(6), "N", out _) ||
            !string.Equals(Path.GetFileName(full), "download.bin", StringComparison.Ordinal)) return;
        DeleteFileSafely(full);
        DeleteDirectoryIfEmpty(folder);
    }

    private static void DeleteFileSafely(string path)
    {
        try { RejectReparseAncestors(path); File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }
    private static void DeleteDirectoryIfEmpty(string path)
    {
        try { RejectReparseAncestors(path); Directory.Delete(path, recursive: false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }
    private static async Task DiscardAsync(TextReader reader)
    {
        var buffer = new char[512];
        try { while (await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) != 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _stop.Cancel(); }
}
