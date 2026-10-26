using System.Diagnostics;
using System.Text;
using CodexQuota.Core;

static class UpdateDownloadTests
{
    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        test("update transport: prefer installed PS7 and fall back only to system PS5.1", () =>
        {
            using var files = new TempDirectory();
            var programFiles = Path.Combine(files.Path, "Program Files");
            var system = Path.Combine(files.Path, "Windows", "System32");
            var modern = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
            var inbox = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
            Check.Equal(modern, UpdateDownloadClient.SelectPowerShell(programFiles, system, _ => true));
            Check.Equal(inbox, UpdateDownloadClient.SelectPowerShell(programFiles, system, path => path == inbox));
            Check.Throws<FileNotFoundException>(() => UpdateDownloadClient.SelectPowerShell(programFiles, system, _ => false));
            Check.Throws<InvalidDataException>(() => UpdateDownloadClient.SelectPowerShell("relative", system, _ => true));
        });
        test("update transport: embedded script uses PS5.1-compatible body API and scoped TLS", () =>
        {
            using var stream = typeof(UpdateDownloadClient).Assembly.GetManifestResourceStream("CodexQuota.Update.ps1")!;
            Check.True(stream is not null);
            using var reader = new StreamReader(stream!);
            var script = reader.ReadToEnd();
            Check.True(script.Contains("Add-Type -AssemblyName System.Net.Http", StringComparison.Ordinal));
            Check.True(script.Contains("ReadAsStreamAsync()", StringComparison.Ordinal));
            Check.False(script.Contains("ReadAsStreamAsync($deadline.Token)", StringComparison.Ordinal));
            Check.True(script.Contains("[Net.WebRequest]::DefaultWebProxy", StringComparison.Ordinal));
            Check.True(script.Contains("$handler.SslProtocols = [Security.Authentication.SslProtocols]::Tls12", StringComparison.Ordinal));
            Check.False(script.Contains("ServerCertificateCustomValidationCallback", StringComparison.Ordinal));
            Check.False(script.Contains("::SecurityProtocol =", StringComparison.Ordinal));
            Check.False(script.Contains("Authorization", StringComparison.Ordinal));
        });
        asyncTest("update transport: fresh bounded manifest and command keep the approved PS policy", async () =>
        {
            using var files = new TempDirectory();
            var pwsh = Path.Combine(files.Path, "pwsh.exe"); File.WriteAllText(pwsh, "fixture");
            var launches = 0;
            using var client = new UpdateDownloadClient(Path.Combine(files.Path, "staging"), () => pwsh,
                () => Encoding.UTF8.GetBytes("# Never executed."), start =>
                {
                    launches++;
                    Check.False(start.UseShellExecute);
                    Check.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
                    Check.True(start.Arguments.Contains(" -Operation manifest", StringComparison.Ordinal));
                    Check.False(start.Arguments.Contains("ExecutionPolicy", StringComparison.OrdinalIgnoreCase));
                    Check.False(start.Arguments.Contains("-Command", StringComparison.OrdinalIgnoreCase));
                    Check.Equal(new ProcessStartInfo(pwsh).Environment.Count, start.Environment.Count);
                    File.WriteAllText(Path.Combine(start.WorkingDirectory, "download.bin"), "manifest fixture");
                    return new FakeProcess();
                }, () => true);
            Check.Equal("manifest fixture", Encoding.UTF8.GetString(await client.FetchManifestAsync(CancellationToken.None)));
            Check.Equal(1, launches);
            Check.Equal(0, Directory.GetDirectories(Path.Combine(files.Path, "staging")).Length);
        });
        asyncTest("update transport: failed child never accepts stale-looking output", async () =>
        {
            using var files = new TempDirectory();
            var pwsh = Path.Combine(files.Path, "pwsh.exe"); File.WriteAllText(pwsh, "fixture");
            using var client = new UpdateDownloadClient(Path.Combine(files.Path, "staging"), () => pwsh,
                () => Encoding.UTF8.GetBytes("# Never executed."), start =>
                {
                    File.WriteAllText(Path.Combine(start.WorkingDirectory, "download.bin"), "stale");
                    return new FakeProcess { ExitCode = 1 };
                }, () => true);
            await Check.ThrowsAsync<IOException>(client.FetchManifestAsync(CancellationToken.None));
            Check.Equal(0, Directory.GetDirectories(Path.Combine(files.Path, "staging")).Length);
        });
        asyncTest("update transport: oversize manifest rejected and asset uses four-part release tag", async () =>
        {
            using var files = new TempDirectory();
            var pwsh = Path.Combine(files.Path, "pwsh.exe"); File.WriteAllText(pwsh, "fixture");
            var isAsset = false;
            using var client = new UpdateDownloadClient(Path.Combine(files.Path, "staging"), () => pwsh,
                () => Encoding.UTF8.GetBytes("# Never executed."), start =>
                {
                    isAsset = start.Arguments.EndsWith(" -Operation asset -Version 0.2.34.0", StringComparison.Ordinal);
                    File.WriteAllBytes(Path.Combine(start.WorkingDirectory, "download.bin"), new byte[isAsset ? 2 : UpdateDownloadClient.MaximumManifestBytes + 1]);
                    return new FakeProcess();
                }, () => true);
            await Check.ThrowsAsync<InvalidDataException>(client.FetchManifestAsync(CancellationToken.None));
            var asset = await client.FetchAssetAsync(new Version(0, 2, 34, 0), CancellationToken.None);
            Check.True(isAsset); Check.True(File.Exists(asset));
            client.DeleteOwnedDownload(asset);
            Check.False(File.Exists(asset));
        });
        asyncTest("update transport: cancellation stops only its retained owner and rejects concurrent run", async () =>
        {
            using var files = new TempDirectory();
            var pwsh = Path.Combine(files.Path, "pwsh.exe"); File.WriteAllText(pwsh, "fixture");
            var process = new FakeProcess { Exited = false };
            using var cancellation = new CancellationTokenSource();
            using var client = new UpdateDownloadClient(Path.Combine(files.Path, "staging"), () => pwsh,
                () => Encoding.UTF8.GetBytes("# Never executed."), _ => process, () => true);
            var pending = client.FetchManifestAsync(cancellation.Token);
            await process.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Check.ThrowsAsync<InvalidOperationException>(client.FetchManifestAsync(CancellationToken.None));
            cancellation.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(pending);
            Check.True(process.Terminated); Check.True(process.Disposed);
        });
        test("update transport: missing version component and cleanup traversal rejected", () =>
        {
            using var files = new TempDirectory();
            using var client = new UpdateDownloadClient(files.Path);
            Check.Throws<ArgumentException>(() => client.FetchAssetAsync(new Version(0, 2, 34), CancellationToken.None));
            var unrelated = Path.Combine(files.Path, "do-not-delete"); File.WriteAllText(unrelated, "keep");
            client.DeleteOwnedDownload(unrelated);
            Check.True(File.Exists(unrelated));
        });
    }
    private sealed class FakeProcess : IOwnedQuotaProcess
    {
        public int ExitCode { get; set; }
        public bool Exited { get; set; } = true;
        public bool Terminated { get; private set; }
        public bool Disposed { get; private set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Start() { Started.TrySetResult(true); return true; }
        public bool HasOwnedProcess => Started.Task.IsCompleted;
        public CliStartOperation StartOperation => CliStartOperation.ManagedProcessStart;
        public TextReader StandardOutput { get; } = new StringReader("");
        public TextReader StandardError { get; } = new StringReader("");
        public TextWriter StandardInput { get; } = new StringWriter();
        public void RequestTermination() { Terminated = true; Exited = true; }
        public OwnedProcessExit ObserveExit() => new(Exited, Exited, Exited ? ExitCode : null);
        public void Dispose() { Disposed = true; StandardOutput.Dispose(); StandardError.Dispose(); StandardInput.Dispose(); }
    }
}
