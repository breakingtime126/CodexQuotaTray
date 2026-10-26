using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexQuota.Core;

// The injected owner never executes an executable or script. All files are disposable local fixtures.
static class PowerShellResetFeedRunnerTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 6, 0, 0, TimeSpan.Zero);
    private static readonly ResetFeedFileRequest Request = new("01234567-89ab-cdef-0123-456789abcdef", Now, "\"fixture\"");
    private static readonly byte[] Script = Encoding.UTF8.GetBytes("# Synthetic script, never executed.\n");

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        asyncTest("reset runner: public client is JSON-only and construction/disposed fetch never launch", async () =>
        {
            var constructors = typeof(ResetFeedClient).GetConstructors();
            Check.Equal(1, constructors.Length);
            Check.Equal(0, constructors[0].GetParameters().Length);
            using var client = new ResetFeedClient();
            client.Dispose();
            var result = await client.FetchAsync();
            Check.Equal(ResetFeedReadStatus.Cancelled, result.Status);
            Check.Equal(ResetFeedTransport.PowerShell7Json, result.Diagnostic!.Transport);
            Check.Null(result.Snapshot);
        });
        asyncTest("reset runner: missing PowerShell and unavailable output directory never launch", async () =>
        {
            using var files = new TempDirectory();
            var calls = 0;
            IOwnedQuotaProcess UnexpectedStart(ProcessStartInfo _)
            {
                calls++;
                throw new TestFailure("An unavailable runner started a process.");
            }
            using (var missing = new PowerShellResetFeedRunner(files.Path, () => Script,
                () => Path.Combine(files.Path, "missing-pwsh.exe"), UnexpectedStart, () => true))
            {
                var error = await Check.ThrowsAsync<ResetFeedFileException>(missing.FetchAsync(Request, CancellationToken.None));
                Check.Equal(ResetFeedFileFailure.MissingPowerShell, error.Failure);
                Check.Equal(0, Directory.GetFiles(files.Path).Length);
            }

            var fakePwsh = Path.Combine(files.Path, "pwsh.exe");
            File.WriteAllBytes(fakePwsh, Array.Empty<byte>());
            // A regular file is a deterministic non-writable directory target, even for a privileged test user.
            var unavailable = Path.Combine(files.Path, "not-a-directory");
            File.WriteAllText(unavailable, "keep this file");
            using (var blocked = new PowerShellResetFeedRunner(unavailable, () => Script,
                () => fakePwsh, UnexpectedStart, () => true))
            {
                var error = await Check.ThrowsAsync<ResetFeedFileException>(blocked.FetchAsync(Request, CancellationToken.None));
                Check.True(error.Failure is ResetFeedFileFailure.OutputWriteFailed or ResetFeedFileFailure.OutputWriteDenied);
            }
            Check.Equal("keep this file", File.ReadAllText(unavailable));
            Check.False(File.Exists(Path.Combine(files.Path, "reset-state.json")));
            Check.Equal(0, calls);
        });

        test("reset runner: command uses a quoted file with no policy or environment changes", () =>
        {
            using var files = new TempDirectory();
            var directory = Path.Combine(files.Path, "folder with spaces");
            var pwsh = Path.Combine(directory, "pwsh.exe");
            var script = Path.Combine(directory, "Fetch-ResetState-0.2.35.ps1");
            var requestPath = Path.Combine(directory, ".reset-request." + Request.RequestId + ".json");
            var start = PowerShellResetFeedRunner.CreateStartInfo(pwsh, script, requestPath, Request.RequestId, directory);
            Check.Equal(pwsh, start.FileName);
            Check.Equal(directory, start.WorkingDirectory);
            Check.Equal("-NoLogo -NoProfile -NonInteractive -File \"" + script + "\" -RequestId " + Request.RequestId +
                " -OutputPath \"" + Path.Combine(directory, "reset-state.json") + "\" -RequestPath \"" + requestPath + "\"", start.Arguments);
            Check.False(start.Arguments.Contains("ExecutionPolicy", StringComparison.OrdinalIgnoreCase));
            Check.False(start.Arguments.Contains("-Command", StringComparison.OrdinalIgnoreCase));
            Check.False(start.UseShellExecute);
            Check.True(start.CreateNoWindow);
            Check.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
            Check.Equal(0, start.ArgumentList.Count);
            var inherited = new ProcessStartInfo(pwsh).Environment;
            Check.Equal(inherited.Count, start.Environment.Count);
            foreach (var item in inherited)
            {
                Check.True(start.Environment.TryGetValue(item.Key, out var actual));
                Check.Equal(item.Value, actual);
            }
        });

        asyncTest("reset runner: child can read retained request lease and return exact output bytes", async () =>
        {
            await using var fixture = new Fixture();
            var oldScript = Path.Combine(Path.GetDirectoryName(fixture.ScriptPath)!, "Fetch-ResetState-0.2.32.ps1");
            File.WriteAllText(oldScript, "# Existing legacy script must remain untouched.\n");
            var output = Success(Request.RequestId);
            fixture.Process.OnStart = () =>
            {
                using var reader = new FileStream(fixture.RequestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var request = JsonDocument.Parse(reader);
                Check.Equal(3, request.RootElement.EnumerateObject().Count());
                Check.Equal(1, request.RootElement.GetProperty("schemaVersion").GetInt32());
                Check.Equal(Request.RequestId, request.RootElement.GetProperty("requestId").GetString());
                Check.Equal(Request.ETag, request.RootElement.GetProperty("etag").GetString());
                Check.True(File.ReadAllBytes(fixture.ScriptPath).SequenceEqual(Script));
                File.WriteAllBytes(fixture.OutputPath, output);
            };
            var result = await fixture.FetchAsync().WaitAsync(Budget);
            Check.True(output.SequenceEqual(result));
            Check.Equal("# Existing legacy script must remain untouched.\n", File.ReadAllText(oldScript));
            Check.Equal(1, fixture.FactoryCalls);
            Check.Equal(1, fixture.Process.StartCount);
            Check.True(fixture.Process.Input.Closed);
            Check.Equal(1, fixture.Process.DisposeCount);
            Check.False(fixture.Process.DisposedBeforeCleanup);
            Check.False(File.Exists(fixture.RequestPath));
            Check.True(File.Exists(fixture.ScriptPath));
            using var releasedLock = new FileStream(fixture.LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });

        asyncTest("reset runner: unsuccessful exit cannot reuse a successful old output", async () =>
        {
            foreach (var exitCode in new[] { 2, 1 })
            {
                await using var fixture = new Fixture();
                var old = Success("fedcba98-7654-3210-fedc-ba9876543210");
                File.WriteAllBytes(fixture.OutputPath, old);
                fixture.Process.ExitCode = exitCode;
                // Exit 2 must fail before even attempting to open the output.
                using var unreadable = exitCode == 2
                    ? new FileStream(fixture.OutputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
                var error = await Check.ThrowsAsync<ResetFeedFileException>(fixture.FetchAsync());
                Check.Equal(exitCode == 2 ? ResetFeedFileFailure.OutputWriteFailed : ResetFeedFileFailure.ChildFailed, error.Failure);
                Check.Equal(exitCode, error.ChildExitCode);
                Check.Equal(1, fixture.FactoryCalls);
                Check.Equal(1, fixture.Process.DisposeCount);
                Check.False(fixture.Process.DisposedBeforeCleanup);
                unreadable?.Dispose();
                Check.True(old.SequenceEqual(File.ReadAllBytes(fixture.OutputPath)));
                Check.False(File.Exists(fixture.RequestPath));
            }
        });

        asyncTest("reset runner: cancellation retains owner and rejects overlap until tree and pipes finish", async () =>
        {
            await using var fixture = new Fixture(holdPipes: true);
            fixture.Process.AutoExitOnStart = false;
            using var cancellation = new CancellationTokenSource();
            var pending = fixture.FetchAsync(cancellation.Token);
            await fixture.Process.Started.Task.WaitAsync(Budget);
            await fixture.Process.Output.Started.Task.WaitAsync(Budget);
            await fixture.Process.Error.Started.Task.WaitAsync(Budget);
            cancellation.Cancel();
            await fixture.Process.TerminationRequested.Task.WaitAsync(Budget);
            Check.False(pending.IsCompleted);
            Check.Equal(0, fixture.Process.DisposeCount);
            Check.True(File.Exists(fixture.RequestPath));

            var overlapping = await Check.ThrowsAsync<ResetFeedFileException>(fixture.Runner.FetchAsync(
                Request with { RequestId = "fedcba98-7654-3210-fedc-ba9876543210" }, CancellationToken.None));
            Check.Equal(ResetFeedFileFailure.CleanupPending, overlapping.Failure);
            Check.Equal(1, fixture.FactoryCalls);

            fixture.Process.RootExited = true;
            await fixture.Process.RootOnlyObserved.Task.WaitAsync(Budget);
            Check.False(pending.IsCompleted);
            Check.Equal(0, fixture.Process.DisposeCount);
            fixture.Process.TreeExited = true;
            await fixture.Process.TreeExitedObserved.Task.WaitAsync(Budget);
            Check.False(pending.IsCompleted);
            Check.Equal(0, fixture.Process.DisposeCount);

            fixture.Process.Output.Finish();
            await fixture.Process.Output.Completed.Task.WaitAsync(Budget);
            Check.False(pending.IsCompleted);
            Check.Equal(0, fixture.Process.DisposeCount);
            fixture.Process.Error.Finish();
            await Check.ThrowsAsync<OperationCanceledException>(pending);
            Check.Equal(1, fixture.Process.DisposeCount);
            Check.False(fixture.Process.DisposedBeforeCleanup);
            Check.False(fixture.Process.Output.DisposedDuringRead);
            Check.False(fixture.Process.Error.DisposedDuringRead);
            Check.False(File.Exists(fixture.RequestPath));
            using var releasedLock = new FileStream(fixture.LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });

        asyncTest("reset runner: pre-existing request collision is never removed or launched", async () =>
        {
            await using var fixture = new Fixture();
            var existing = Encoding.UTF8.GetBytes("existing request belonging to another operation");
            File.WriteAllBytes(fixture.RequestPath, existing);
            var error = await Check.ThrowsAsync<ResetFeedFileException>(fixture.FetchAsync());
            Check.Equal(ResetFeedFileFailure.OutputWriteFailed, error.Failure);
            Check.Equal(0, fixture.FactoryCalls);
            Check.True(existing.SequenceEqual(File.ReadAllBytes(fixture.RequestPath)));
        });

        asyncTest("reset runner: installed script mismatch fails closed without overwrite or launch", async () =>
        {
            foreach (var differentLength in new[] { false, true })
            {
                await using var fixture = new Fixture();
                var changed = Enumerable.Repeat((byte)'x', Script.Length + (differentLength ? 1 : 0)).ToArray();
                File.WriteAllBytes(fixture.ScriptPath, changed);
                var error = await Check.ThrowsAsync<ResetFeedFileException>(fixture.FetchAsync());
                Check.Equal(ResetFeedFileFailure.ScriptMismatch, error.Failure);
                Check.Equal(0, fixture.FactoryCalls);
                Check.Equal(0, fixture.Process.StartCount);
                Check.True(changed.SequenceEqual(File.ReadAllBytes(fixture.ScriptPath)));
                Check.False(File.Exists(fixture.RequestPath));
                Check.False(File.Exists(fixture.OutputPath));
            }
        });
    }

    private static byte[] Success(string requestId) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1, requestId, status = "Success", fetchedAtUtc = Now.ToString("O"), httpStatus = 200,
        etag = (string?)null, retryAfterSeconds = (int?)null,
        data = new
        {
            latest_reset = (object?)null, scheduled_reset = (object?)null, active_watch = (object?)null,
            stats = new { total = 0, last_reset_at = (string?)null, days_since_last = (double?)null, avg_interval_days = (double?)null }
        },
        meta = new { api_version = "v1", generated_at = Now.ToString("O") }, errorCode = (string?)null
    });

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDirectory _files = new();
        private Task<byte[]>? _active;
        private int _factoryCalls;
        public FakeOwnedProcess Process { get; }
        public PowerShellResetFeedRunner Runner { get; }
        public int FactoryCalls => Volatile.Read(ref _factoryCalls);
        public string RequestPath => Path.Combine(_files.Path, ".reset-request." + Request.RequestId + ".json");
        public string ScriptPath => Path.Combine(_files.Path, "Fetch-ResetState-0.2.35.ps1");
        public string OutputPath => Path.Combine(_files.Path, "reset-state.json");
        public string LockPath => Path.Combine(_files.Path, ".reset-state.lock");

        public Fixture(bool holdPipes = false)
        {
            var fakePwsh = Path.Combine(_files.Path, "pwsh.exe");
            File.WriteAllBytes(fakePwsh, Array.Empty<byte>());
            Process = new(holdPipes);
            Runner = new(_files.Path, () => Script, () => fakePwsh, _ =>
            {
                Interlocked.Increment(ref _factoryCalls);
                return Process;
            }, () => true);
        }

        public Task<byte[]> FetchAsync(CancellationToken token = default) => _active = Runner.FetchAsync(Request, token);

        public async ValueTask DisposeAsync()
        {
            Process.ConfirmExit();
            Process.Output.Finish();
            Process.Error.Finish();
            if (_active is not null)
            {
                try { await _active.WaitAsync(Budget); }
                catch (TimeoutException)
                {
                    Runner.Dispose();
                    try { await _active.WaitAsync(Budget); }
                    catch (Exception) when (_active.IsCompleted) { }
                }
                catch (Exception) when (_active.IsCompleted) { }
            }
            Runner.Dispose();
            _files.Dispose();
        }
    }

    private sealed class FakeOwnedProcess : IOwnedQuotaProcess
    {
        private int _started, _disposed, _rootExited, _treeExited;
        public Action? OnStart { get; set; }
        public bool AutoExitOnStart { get; set; } = true;
        public int ExitCode { get; set; }
        public int StartCount => Volatile.Read(ref _started);
        public int DisposeCount => Volatile.Read(ref _disposed);
        public bool RootExited { get => Volatile.Read(ref _rootExited) != 0; set => Volatile.Write(ref _rootExited, value ? 1 : 0); }
        public bool TreeExited { get => Volatile.Read(ref _treeExited) != 0; set => Volatile.Write(ref _treeExited, value ? 1 : 0); }
        public bool DisposedBeforeCleanup { get; private set; }
        public TaskCompletionSource<bool> Started { get; } = Signal();
        public TaskCompletionSource<bool> TerminationRequested { get; } = Signal();
        public TaskCompletionSource<bool> RootOnlyObserved { get; } = Signal();
        public TaskCompletionSource<bool> TreeExitedObserved { get; } = Signal();
        public HeldReader Output { get; }
        public HeldReader Error { get; }
        public ObservedWriter Input { get; } = new();
        public bool HasOwnedProcess => StartCount != 0;
        public CliStartOperation StartOperation => CliStartOperation.ManagedProcessStart;
        public TextReader StandardOutput => Output;
        public TextReader StandardError => Error;
        public TextWriter StandardInput => Input;

        public FakeOwnedProcess(bool holdPipes) { Output = new(holdPipes); Error = new(holdPipes); }

        public bool Start()
        {
            Interlocked.Increment(ref _started);
            try { OnStart?.Invoke(); return true; }
            finally
            {
                if (AutoExitOnStart) ConfirmExit();
                Started.TrySetResult(true);
            }
        }

        public void RequestTermination() => TerminationRequested.TrySetResult(true);

        public OwnedProcessExit ObserveExit()
        {
            var root = RootExited;
            var tree = TreeExited;
            if (root && !tree) RootOnlyObserved.TrySetResult(true);
            if (root && tree) TreeExitedObserved.TrySetResult(true);
            return new(root, tree, root ? ExitCode : null);
        }

        public void ConfirmExit() { RootExited = true; TreeExited = true; }

        public void Dispose()
        {
            DisposedBeforeCleanup |= !RootExited || !TreeExited || Output.ReadActive || Error.ReadActive;
            Interlocked.Increment(ref _disposed);
            Output.Dispose();
            Error.Dispose();
            Input.Dispose();
        }
    }

    private sealed class HeldReader : TextReader
    {
        private readonly TaskCompletionSource<int> _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        public bool ReadActive => Volatile.Read(ref _active) != 0;
        public bool DisposedDuringRead { get; private set; }
        public TaskCompletionSource<bool> Started { get; } = Signal();
        public TaskCompletionSource<bool> Completed { get; } = Signal();
        public HeldReader(bool hold) { if (!hold) Finish(); }
        public void Finish() => _finish.TrySetResult(0);

        public override async Task<int> ReadAsync(char[] buffer, int index, int count)
        {
            Interlocked.Increment(ref _active);
            Started.TrySetResult(true);
            try { return await _finish.Task; }
            finally
            {
                Interlocked.Decrement(ref _active);
                Completed.TrySetResult(true);
            }
        }

        protected override void Dispose(bool disposing)
        {
            DisposedDuringRead |= ReadActive;
            base.Dispose(disposing);
        }
    }

    private sealed class ObservedWriter : StringWriter
    {
        public bool Closed { get; private set; }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
