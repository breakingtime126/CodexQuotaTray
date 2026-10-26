using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexQuota.Core;

// Exercise the production client/coordinator/RPC lifetime with synthetic byte streams only.
// No executable, user account, credentials, filesystem discovery, or network is involved.
internal static class BackendCleanupTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);
    private static readonly BackendCleanupOptions Fast = new(TimeSpan.FromMilliseconds(30),
        TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(40));

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        test("owned command: native launch preserves net8 app-server argument", () =>
        {
            var start = new ProcessStartInfo(@"C:\Program Files\Codex\codex.exe");
            start.ArgumentList.Add("app-server");
            Check.Equal("\"C:\\Program Files\\Codex\\codex.exe\" \"app-server\"", FrameworkCliProcess.BuildCommandLine(start));
        });
        test("owned command: npm script and special arguments retain CRT quoting without a shell", () =>
        {
            var start = new ProcessStartInfo(@"C:\Program Files\nodejs\node.exe");
            start.ArgumentList.Add(@"C:\Users\Synthetic User\AppData\npm\codex.js");
            start.ArgumentList.Add("app-server");
            start.ArgumentList.Add("space & caret^ percent% quote\" tail\\");
            start.ArgumentList.Add("");
            Check.Equal("\"C:\\Program Files\\nodejs\\node.exe\" " +
                "\"C:\\Users\\Synthetic User\\AppData\\npm\\codex.js\" \"app-server\" " +
                "\"space & caret^ percent% quote\\\" tail\\\\\" \"\"", FrameworkCliProcess.BuildCommandLine(start));
        });
        test("owned command: raw arguments stay intact and mixed argument representations are rejected", () =>
        {
            var start = new ProcessStartInfo(@"C:\Codex\codex.exe", "app-server --synthetic=\"a b\"");
            Check.Equal("\"C:\\Codex\\codex.exe\" app-server --synthetic=\"a b\"", FrameworkCliProcess.BuildCommandLine(start));
            start.ArgumentList.Add("app-server");
            Check.Throws<InvalidOperationException>(() => FrameworkCliProcess.BuildCommandLine(start));
        });

        asyncTest("cleanup retry: successful quota and original read time survive delayed owned exit", async () =>
        {
            await using var fixture = new Fixture();
            using var gate = new SourceRequestGate(QuotaDataSource.Cli);
            Check.True(gate.TryBeginRead(out var lease));
            var before = DateTimeOffset.UtcNow;
            var read = fixture.ReadWithinLeaseAsync(gate, lease!);
            await fixture.Process.ReplyQuotaAsync();
            var pending = await fixture.Pending.Task.WaitAsync(Budget);
            Check.Equal(BackendCleanupState.Pending, pending.State);
            Check.Equal(QuotaDataSource.Cli, pending.Source);
            Check.Equal(ReadStatus.Ready, pending.Read!.Status);
            Check.Quota(pending.Read.Quota, 73, 73);
            Check.True(pending.ReadAt >= before && pending.ReadAt <= DateTimeOffset.UtcNow);
            Check.True(pending.ElapsedMilliseconds >= Fast.PendingAfter.TotalMilliseconds);
            var state = QuotaState.Initial.Apply(pending.Read, pending.ReadAt!.Value);
            await Check.StillPendingAsync(read);
            Check.False(gate.TryBeginRead(out _));
            Check.Equal(0, fixture.Process.DisposeCount);
            Check.Equal(0, fixture.LaunchLease.DisposeCount);
            fixture.Process.ConfirmExit();
            var result = await read.WaitAsync(Budget);
            Check.True(ReferenceEquals(pending.Read, result));
            var confirmed = fixture.Progress.Last();
            Check.Equal(BackendCleanupState.Confirmed, confirmed.State);
            Check.Equal(pending.ReadAt, confirmed.ReadAt);
            Check.True(ReferenceEquals(pending.Read, confirmed.Read));
            state = state.Apply(confirmed.Read!, confirmed.ReadAt!.Value);
            Check.Equal(pending.ReadAt, state.LastSuccess);
            Check.Equal(2, fixture.Progress.Count);
            Check.Equal(1, fixture.Process.DisposeCount);
            Check.Equal(1, fixture.LaunchLease.DisposeCount);
            Check.False(fixture.Process.DisposedBeforeExit);
            Check.True(gate.TryBeginRead(out var resumed));
            resumed!.Dispose();
            var cleanup = fixture.Trace.Where(item => item.Stage == SourceBackendStage.Cleanup).ToArray();
            Check.Equal(2, cleanup.Length);
            Check.Equal(SourceBackendOutcome.Pending, cleanup[0].Outcome);
            Check.Equal(SourceBackendOutcome.Succeeded, cleanup[1].Outcome);
            Check.Equal(SourceBackendExit.OwnedTreeTerminationRequested, cleanup[1].Exit);
            Check.Equal(true, cleanup[1].RootExitConfirmed);
            Check.Equal(true, cleanup[1].TreeExitConfirmed);
            Check.Equal(true, cleanup[1].PipesCompleted);
        });

        asyncTest("cleanup retry: root exit does not release a nonempty owned tree", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Process.RootExited = true;
            var read = fixture.ReadAsync();
            await fixture.Process.ReplyQuotaAsync();
            await fixture.Pending.Task.WaitAsync(Budget);
            await Check.StillPendingAsync(read);
            Check.Equal(0, fixture.Process.DisposeCount);
            var pending = fixture.Trace.Last(item => item.Stage == SourceBackendStage.Cleanup);
            Check.Equal(true, pending.RootExitConfirmed);
            Check.Equal(false, pending.TreeExitConfirmed);
            fixture.Process.TreeExited = true;
            Check.Equal(ReadStatus.Ready, (await read.WaitAsync(Budget)).Status);
            Check.Equal(1, fixture.Process.DisposeCount);
        });

        asyncTest("cleanup retry: exit observation errors fail closed and recover on the same owner", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Process.ObserveFailure = new Win32Exception(5, "SYNTHETIC_PRIVATE_ERROR");
            var read = fixture.ReadAsync();
            await fixture.Process.ReplyQuotaAsync();
            await fixture.Pending.Task.WaitAsync(Budget);
            var probes = fixture.Process.ObserveCount;
            await Task.Delay(100);
            Check.True(fixture.Process.ObserveCount > probes);
            Check.False(read.IsCompleted);
            Check.Equal(0, fixture.Process.DisposeCount);
            Check.Equal(1, fixture.FactoryCalls);
            var pending = fixture.Trace.Single(item => item.Stage == SourceBackendStage.Cleanup);
            Check.Equal(5, pending.Code);
            Check.False(pending.ToSafeLogCode().Contains("SYNTHETIC_PRIVATE_ERROR", StringComparison.Ordinal));
            fixture.Process.ObserveFailure = null;
            fixture.Process.ConfirmExit();
            Check.Equal(ReadStatus.Ready, (await read.WaitAsync(Budget)).Status);
        });

        asyncTest("cleanup retry: termination errors retry without restarting or disposing the owner", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Process.TerminationFailure = new IOException("synthetic termination failure");
            var read = fixture.ReadAsync();
            await fixture.Process.ReplyQuotaAsync();
            await fixture.Pending.Task.WaitAsync(Budget);
            var attempts = fixture.Process.TerminationCount;
            await Task.Delay(100);
            Check.True(fixture.Process.TerminationCount > attempts);
            Check.False(read.IsCompleted);
            Check.Equal(1, fixture.FactoryCalls);
            Check.Equal(0, fixture.Process.DisposeCount);
            fixture.Process.TerminationFailure = null;
            fixture.Process.ConfirmExit();
            Check.Equal(ReadStatus.Ready, (await read.WaitAsync(Budget)).Status);
        });

        asyncTest("cleanup retry: independent confirmed exit recovers despite a failing termination request", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Process.TerminationFailure = new Win32Exception(5);
            var read = fixture.ReadAsync();
            await fixture.Process.ReplyQuotaAsync();
            await fixture.Pending.Task.WaitAsync(Budget);
            Check.Equal(0, fixture.Process.DisposeCount);
            fixture.Process.ConfirmExit();
            Check.Equal(ReadStatus.Ready, (await read.WaitAsync(Budget)).Status);
            Check.Equal(BackendCleanupState.Confirmed, fixture.Progress.Last().State);
            Check.Equal(1, fixture.FactoryCalls);
        });

        asyncTest("cleanup retry: pending stderr drain retains owner after root and tree exit", async () =>
        {
            await using var fixture = new Fixture(holdStderr: true);
            fixture.Process.ConfirmExit();
            var read = fixture.ReadAsync();
            await fixture.Process.ReplyQuotaAsync();
            await fixture.Pending.Task.WaitAsync(Budget);
            await Check.StillPendingAsync(read);
            Check.Equal(0, fixture.Process.DisposeCount);
            Check.False(fixture.Process.ErrorReader.WasDisposed);
            var pending = fixture.Trace.Single(item => item.Stage == SourceBackendStage.Cleanup);
            Check.Equal(true, pending.RootExitConfirmed);
            Check.Equal(true, pending.TreeExitConfirmed);
            Check.Equal(false, pending.PipesCompleted);
            fixture.Process.ErrorReader.Finish();
            Check.Equal(ReadStatus.Ready, (await read.WaitAsync(Budget)).Status);
            Check.False(fixture.Process.ErrorReader.DisposedDuringRead);
        });

        asyncTest("cleanup retry: actual stdout read ignoring cancellation prevents premature completion", async () =>
        {
            await using var fixture = new Fixture(ignoreStdoutCancellation: true);
            var read = fixture.ReadAsync();
            await fixture.Process.ReplyQuotaAsync();
            fixture.Process.ConfirmExit();
            await fixture.Pending.Task.WaitAsync(Budget);
            // Hold beyond DisposeAsync's two-second pump join timeout.
            await Task.Delay(2200);
            Check.False(read.IsCompleted, "A disposal wait timeout was mistaken for actual stdout completion.");
            Check.Equal(0, fixture.Process.DisposeCount);
            Check.False(fixture.Process.OutputReader.WasDisposed);
            fixture.Process.CloseOutput();
            Check.Equal(ReadStatus.Ready, (await read.WaitAsync(Budget)).Status);
            Check.False(fixture.Process.OutputReader.DisposedDuringRead);
        });

        asyncTest("cleanup retry: actual RPC write ignoring cancellation retains streams and owner", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Process.InputWriter.HoldServerResponse = true;
            var read = fixture.ReadAsync();
            await fixture.Process.ReplyQuotaAsync(serverRequest: true);
            await fixture.Process.InputWriter.Blocked.Task.WaitAsync(Budget);
            fixture.Process.ConfirmExit();
            await fixture.Pending.Task.WaitAsync(Budget);
            // Hold beyond both two-second pump/write-gate disposal waits.
            await Task.Delay(4200);
            Check.False(read.IsCompleted, "Expired disposal waits released an actual pending RPC write.");
            Check.Equal(0, fixture.Process.DisposeCount);
            Check.False(fixture.Process.InputWriter.WasDisposed);
            fixture.Process.InputWriter.Release.TrySetResult();
            Check.Equal(ReadStatus.Ready, (await read.WaitAsync(Budget)).Status);
            Check.False(fixture.Process.InputWriter.DisposedDuringWrite);
        });

        asyncTest("cleanup retry: source cancellation waits for confirmed cleanup before latest selection resumes", async () =>
        {
            await using var fixture = new Fixture();
            using var gate = new SourceRequestGate(QuotaDataSource.Cli);
            Check.True(gate.TryBeginRead(out var lease));
            var read = fixture.ReadWithinLeaseAsync(gate, lease!);
            await fixture.Process.ReadQuotaRequestAsync();
            var oldSwitch = gate.BeginSwitch(QuotaDataSource.Desktop);
            var latestSwitch = gate.BeginSwitch(QuotaDataSource.VsCode);
            await fixture.Pending.Task.WaitAsync(Budget);
            await Check.StillPendingAsync(read);
            Check.False(oldSwitch.Cleanup.IsCompleted);
            Check.False(latestSwitch.Cleanup.IsCompleted);
            Check.False(gate.TryBeginRead(out _));
            Check.False(gate.CompleteSwitch(latestSwitch));
            Check.False(lease!.TryApply(() => throw new TestFailure("Canceled lease applied stale data.")));
            Check.Equal(0, fixture.Process.DisposeCount);
            fixture.Process.ConfirmExit();
            await Check.ThrowsAsync<OperationCanceledException>(read);
            await latestSwitch.Cleanup.WaitAsync(Budget);
            Check.False(gate.CompleteSwitch(oldSwitch));
            Check.True(gate.CompleteSwitch(latestSwitch));
            Check.True(gate.TryBeginRead(out var resumed));
            Check.Equal(QuotaDataSource.VsCode, resumed!.Source);
            resumed.Dispose();
        });

        asyncTest("cleanup retry: Auto cannot overlap or fall back until failed backend is confirmed gone", async () =>
        {
            await using var fixture = new Fixture();
            var router = new AutoQuotaSourceClient();
            var sources = new ConcurrentQueue<QuotaDataSource>();
            Task<QuotaRead> ReadSource(QuotaDataSource source, CancellationToken token)
            {
                sources.Enqueue(source);
                return source == QuotaDataSource.Cli ? fixture.ReadAsync(token) :
                    Task.FromResult(new QuotaRead(ReadStatus.Ready, new QuotaWindow(88, null)));
            }
            var read = router.ReadAsync(ReadSource, CancellationToken.None);
            await fixture.Process.ReplyQuotaAsync(error: true);
            await fixture.Pending.Task.WaitAsync(Budget);
            await Check.StillPendingAsync(read);
            Check.Equal(1, sources.Count);
            Check.Null(fixture.Progress.Single().Read);
            Check.Null(fixture.Progress.Single().ReadAt);
            await Check.ThrowsAsync<InvalidOperationException>(router.ReadAsync(ReadSource, CancellationToken.None));
            Check.Equal(1, sources.Count);
            fixture.Process.ConfirmExit();
            Check.Quota((await read.WaitAsync(Budget)).Quota, 88, 88);
            Check.Equal(2, sources.Count);
            Check.Equal(QuotaDataSource.VsCode, sources.Last());
            Check.False(router.CleanupBlocked);
        });

        asyncTest("cleanup retry: application shutdown is bounded and latches future reads closed", async () =>
        {
            await using var fixture = new Fixture();
            using var gate = new SourceRequestGate(QuotaDataSource.Auto);
            var router = new AutoQuotaSourceClient();
            Check.True(gate.TryBeginRead(out var lease));
            async Task<QuotaRead> ReadOwnedAsync()
            {
                using (lease)
                {
                    try { return await router.ReadAsync((_, token) => fixture.ReadAsync(token), lease!.Token); }
                    catch (BackendCleanupUnconfirmedException) { gate.BlockFurtherReads(); throw; }
                }
            }
            var read = ReadOwnedAsync();
            await fixture.Process.ReplyQuotaAsync();
            await fixture.Pending.Task.WaitAsync(Budget);
            var watch = Stopwatch.StartNew();
            fixture.Stop.Cancel();
            await Check.ThrowsAsync<BackendCleanupUnconfirmedException>(read);
            Check.True(watch.Elapsed < TimeSpan.FromSeconds(1), "Application exit waited for the normal cleanup retry interval.");
            Check.True(router.CleanupBlocked);
            Check.True(gate.ReadsBlocked);
            Check.Equal(1, fixture.Progress.Count);
            Check.False(fixture.Progress.Any(item => item.State == BackendCleanupState.Confirmed));
            var change = gate.BeginSwitch(QuotaDataSource.Desktop);
            await change.Cleanup.WaitAsync(Budget);
            Check.True(gate.CompleteSwitch(change));
            Check.False(gate.TryBeginRead(out _));
            await Check.ThrowsAsync<BackendCleanupUnconfirmedException>(router.ReadAsync((_, _) =>
                throw new TestFailure("Shutdown cleanup restarted an unconfirmed owner."), CancellationToken.None));
            Check.Equal(1, fixture.FactoryCalls);
        });

        asyncTest("cleanup retry: owned start exception still retains ownership until cleanup confirms", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Process.StartFailure = new Win32Exception(5);
            var read = fixture.ReadAsync();
            await fixture.Pending.Task.WaitAsync(Budget);
            await Check.StillPendingAsync(read);
            Check.Equal(0, fixture.Process.DisposeCount);
            fixture.Process.ConfirmExit();
            Check.Equal(ReadStatus.Unavailable, (await read.WaitAsync(Budget)).Status);
            Check.Equal(1, fixture.Process.DisposeCount);
        });

        asyncTest("cleanup retry: owner disposal exception is an unconfirmed failure, never a safe fallback", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Process.DisposeFailure = new IOException("synthetic owner disposal failure");
            fixture.Process.ConfirmExit();
            var router = new AutoQuotaSourceClient();
            var starts = 0;
            var read = router.ReadAsync((_, token) =>
            {
                Interlocked.Increment(ref starts);
                return fixture.ReadAsync(token);
            }, CancellationToken.None);
            await fixture.Process.ReplyQuotaAsync();
            await Check.ThrowsAsync<BackendCleanupUnconfirmedException>(read);
            Check.True(router.CleanupBlocked);
            Check.False(fixture.Progress.Any(item => item.State == BackendCleanupState.Confirmed));
            Check.False(fixture.Trace.Any(item => item.Stage == SourceBackendStage.Cleanup &&
                item.Outcome == SourceBackendOutcome.Succeeded));
            Check.Equal(1, starts);
        });

        asyncTest("cleanup retry: launch lease disposal exception keeps the source policy fail closed", async () =>
        {
            await using var fixture = new Fixture();
            fixture.LaunchLease.Failure = new IOException("synthetic executable lease disposal failure");
            fixture.Process.ConfirmExit();
            var router = new AutoQuotaSourceClient();
            var read = router.ReadAsync((_, token) => fixture.ReadAsync(token), CancellationToken.None);
            await fixture.Process.ReplyQuotaAsync();
            await Check.ThrowsAsync<BackendCleanupUnconfirmedException>(read);
            Check.True(router.CleanupBlocked);
            Check.False(fixture.Progress.Any(item => item.State == BackendCleanupState.Confirmed));
            Check.False(fixture.Trace.Any(item => item.Stage == SourceBackendStage.Cleanup &&
                item.Outcome == SourceBackendOutcome.Succeeded));
        });

        asyncTest("cleanup retry: repeated checks are bounded and publish one pending event only", async () =>
        {
            await using var fixture = new Fixture();
            fixture.ThrowProgress = true;
            var read = fixture.ReadAsync();
            await fixture.Process.ReplyQuotaAsync();
            await fixture.Pending.Task.WaitAsync(Budget);
            var before = fixture.Process.TerminationCount;
            var watch = Stopwatch.StartNew();
            await Task.Delay(180);
            var elapsed = watch.Elapsed.TotalMilliseconds;
            var probes = fixture.Process.TerminationCount - before;
            Check.True(probes >= 2, "Cleanup stopped retrying after the initial fast window.");
            Check.True(probes <= Math.Ceiling(elapsed / Fast.MaximumPoll.TotalMilliseconds) + 2,
                $"Cleanup busy-polled {probes} times in {elapsed:0} ms.");
            Check.Equal(1, fixture.Progress.Count);
            Check.Equal(1, fixture.Trace.Count(item => item.Stage == SourceBackendStage.Cleanup));
            Check.False(read.IsCompleted);
            Check.Equal(0, fixture.Process.DisposeCount);
            fixture.Process.ConfirmExit();
            Check.Equal(ReadStatus.Ready, (await read.WaitAsync(Budget)).Status);
            Check.Equal(2, fixture.Progress.Count);
            Check.Equal(1, fixture.FactoryCalls);
        });
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly CodexQuotaClient _client;
        private Task<QuotaRead>? _read;
        private int _factoryCalls;
        public FakeOwnedProcess Process { get; }
        public FakeLaunchLease LaunchLease { get; } = new();
        public CancellationTokenSource Stop { get; } = new();
        public ConcurrentQueue<BackendCleanupProgress> Progress { get; } = new();
        public ConcurrentQueue<SourceBackendDiagnostic> Trace { get; } = new();
        public TaskCompletionSource<BackendCleanupProgress> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ThrowProgress { get; set; }
        public int FactoryCalls => Volatile.Read(ref _factoryCalls);

        public Fixture(bool holdStderr = false, bool ignoreStdoutCancellation = false)
        {
            Process = new(holdStderr, ignoreStdoutCancellation);
            var candidate = new CliCandidate(CliSource.Explicit, new ProcessStartInfo("synthetic-never-executed"))
            { AcquireLaunchLease = () => LaunchLease };
            _client = new(QuotaDataSource.Cli, _ => new SourceBackendDiscovery(candidate, 1), null, Trace.Enqueue,
                sessionTimeout: TimeSpan.FromSeconds(5), processFactory: _ =>
                {
                    Interlocked.Increment(ref _factoryCalls);
                    return Process;
                }, cleanupProgress: progress =>
                {
                    Progress.Enqueue(progress);
                    if (progress.State == BackendCleanupState.Pending) Pending.TrySetResult(progress);
                    if (ThrowProgress) throw new InvalidOperationException("synthetic observer failure");
                }, cleanupStop: Stop.Token, cleanupOptions: Fast);
        }
        public Task<QuotaRead> ReadAsync(CancellationToken token = default) => _read = _client.ReadAsync(token);
        public async Task<QuotaRead> ReadWithinLeaseAsync(SourceRequestGate gate, SourceReadLease lease)
        {
            using (lease)
            {
                try { return await ReadAsync(lease.Token); }
                catch (BackendCleanupUnconfirmedException) { gate.BlockFurtherReads(); throw; }
            }
        }
        public async ValueTask DisposeAsync()
        {
            Process.TerminationFailure = null;
            Process.ObserveFailure = null;
            Process.DisposeFailure = null;
            LaunchLease.Failure = null;
            Process.ConfirmExit();
            Process.ErrorReader.Finish();
            Process.InputWriter.Release.TrySetResult();
            Process.CloseOutput();
            if (_read is not null)
            {
                try { await _read.WaitAsync(Budget); }
                catch (Exception) { Stop.Cancel(); }
            }
            Stop.Cancel();
            Process.ReleaseTransport();
            Stop.Dispose();
        }
    }

    private sealed class FakeLaunchLease : IDisposable
    {
        public int DisposeCount;
        public Exception? Failure;
        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCount);
            if (Failure is { } failure) throw failure;
        }
    }

    private sealed class FakeOwnedProcess : IOwnedQuotaProcess
    {
        private readonly MemoryDuplexStream _client;
        private readonly MemoryDuplexStream _server;
        private readonly StreamReader _serverReader;
        private readonly StreamWriter _serverWriter;
        private int _rootExited, _treeExited, _started, _disposed;
        private int _observeCount, _terminationCount;
        public ObservedReader OutputReader { get; }
        public HeldReader ErrorReader { get; }
        public HeldWriter InputWriter { get; }
        public Exception? ObserveFailure;
        public Exception? TerminationFailure;
        public Exception? StartFailure;
        public Exception? DisposeFailure;
        public bool DisposedBeforeExit { get; private set; }
        public int ObserveCount => Volatile.Read(ref _observeCount);
        public int TerminationCount => Volatile.Read(ref _terminationCount);
        public int DisposeCount => Volatile.Read(ref _disposed);
        public bool RootExited { get => Volatile.Read(ref _rootExited) != 0; set => Volatile.Write(ref _rootExited, value ? 1 : 0); }
        public bool TreeExited { get => Volatile.Read(ref _treeExited) != 0; set => Volatile.Write(ref _treeExited, value ? 1 : 0); }
        public FakeOwnedProcess(bool holdStderr, bool ignoreStdoutCancellation)
        {
            (_client, _server) = MemoryDuplexStream.CreatePair();
            OutputReader = new(new StreamReader(_client, Encoding.UTF8, false, 1024, true), ignoreStdoutCancellation);
            InputWriter = new(new StreamWriter(_client, new UTF8Encoding(false), 1024, true));
            ErrorReader = new(holdStderr);
            _serverReader = new(_server, Encoding.UTF8, false, 1024, true);
            _serverWriter = new(_server, new UTF8Encoding(false), 1024, true);
        }
        public bool Start()
        {
            Interlocked.Exchange(ref _started, 1);
            if (StartFailure is { } failure) throw failure;
            return true;
        }
        public bool HasOwnedProcess => Volatile.Read(ref _started) != 0;
        public CliStartOperation StartOperation => CliStartOperation.ManagedProcessStart;
        public TextReader StandardOutput => OutputReader;
        public TextReader StandardError => ErrorReader;
        public TextWriter StandardInput => InputWriter;
        public void RequestTermination()
        {
            Interlocked.Increment(ref _terminationCount);
            if (TerminationFailure is { } failure) throw failure;
        }
        public OwnedProcessExit ObserveExit()
        {
            Interlocked.Increment(ref _observeCount);
            if (ObserveFailure is { } failure) throw failure;
            return new(RootExited, TreeExited, RootExited ? 0 : null);
        }
        public void ConfirmExit() { RootExited = true; TreeExited = true; }
        public void CloseOutput() => _server.CompleteWrites();
        public async Task<JsonElement> ReadQuotaRequestAsync()
        {
            var initialize = await ReadMessageAsync();
            Check.Method(initialize, "initialize");
            await SendAsync(new { id = initialize.GetProperty("id").GetInt64(), result = new { userAgent = "synthetic/0.2.28" } });
            Check.Method(await ReadMessageAsync(), "initialized");
            var quota = await ReadMessageAsync();
            Check.Method(quota, "account/rateLimits/read");
            return quota;
        }
        public async Task ReplyQuotaAsync(bool error = false, bool serverRequest = false)
        {
            var request = await ReadQuotaRequestAsync();
            var id = request.GetProperty("id").GetInt64();
            var line = error ? JsonSerializer.Serialize(new { id, error = new { code = -32603, message = "synthetic failure" } }) :
                JsonSerializer.Serialize(new { id, result = new { rateLimits = new { primary = new { usedPercent = 27, windowDurationMins = 10080 } } } });
            if (serverRequest) line += "\n" + JsonSerializer.Serialize(new { id = 987654, method = "synthetic/unsupported", @params = new { } });
            await _serverWriter.WriteLineAsync(line);
            await _serverWriter.FlushAsync();
        }
        private async Task<JsonElement> ReadMessageAsync()
        {
            var line = await _serverReader.ReadLineAsync().WaitAsync(Budget);
            Check.True(line is not null, "Synthetic client closed before protocol request.");
            using var document = JsonDocument.Parse(line!);
            return document.RootElement.Clone();
        }
        private async Task SendAsync(object message)
        {
            await _serverWriter.WriteLineAsync(JsonSerializer.Serialize(message));
            await _serverWriter.FlushAsync();
        }
        public void Dispose()
        {
            Interlocked.Increment(ref _disposed);
            DisposedBeforeExit |= !(RootExited && TreeExited);
            if (DisposeFailure is { } failure) throw failure;
            ErrorReader.Dispose();
        }
        public void ReleaseTransport()
        {
            _client.Dispose();
            _server.Dispose();
            _serverReader.Dispose();
            _serverWriter.Dispose();
        }
    }

    private sealed class HeldReader : TextReader
    {
        private readonly TaskCompletionSource<int> _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        public bool WasDisposed { get; private set; }
        public bool DisposedDuringRead { get; private set; }
        public HeldReader(bool held) { if (!held) _finish.TrySetResult(0); }
        public void Finish() => _finish.TrySetResult(0);
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _active);
            try { return await _finish.Task; }
            finally { Interlocked.Decrement(ref _active); }
        }
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            DisposedDuringRead |= Volatile.Read(ref _active) != 0;
            base.Dispose(disposing);
        }
    }

    private sealed class ObservedReader(TextReader inner, bool ignoreCancellation) : TextReader
    {
        private int _active;
        public bool WasDisposed { get; private set; }
        public bool DisposedDuringRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _active);
            try { return await inner.ReadAsync(buffer, ignoreCancellation ? CancellationToken.None : cancellationToken); }
            finally { Interlocked.Decrement(ref _active); }
        }
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            DisposedDuringRead |= Volatile.Read(ref _active) != 0;
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class HeldWriter(TextWriter inner) : TextWriter
    {
        private int _active;
        public override Encoding Encoding => inner.Encoding;
        public bool HoldServerResponse { get; set; }
        public bool WasDisposed { get; private set; }
        public bool DisposedDuringWrite { get; private set; }
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _active);
            try
            {
                if (HoldServerResponse && buffer.Span.IndexOf("\"error\"".AsSpan()) >= 0)
                {
                    Blocked.TrySetResult();
                    await Release.Task; // Simulates uninterruptible Framework pipe I/O.
                    cancellationToken.ThrowIfCancellationRequested();
                }
                await inner.WriteLineAsync(buffer, cancellationToken);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            DisposedDuringWrite |= Volatile.Read(ref _active) != 0;
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
