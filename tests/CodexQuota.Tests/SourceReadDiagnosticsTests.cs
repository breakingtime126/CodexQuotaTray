using CodexQuota.Core;

static class SourceReadDiagnosticsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        test("diagnostic snapshot: starts Desktop evidence before any backend or protocol event", () =>
        {
            using var gate = new SourceRequestGate(QuotaDataSource.Desktop);
            var diagnostics = new SourceReadDiagnostics(gate);
            Check.True(gate.TryBeginRead(out var request));
            using var lease = request!;
            Check.True(diagnostics.Begin(lease, Now));
            Check.True(diagnostics.Record(lease, Attempt(QuotaDataSource.Desktop)));
            var snapshot = diagnostics.Capture();
            Check.Equal(QuotaDataSource.Desktop, snapshot.Current!.LastAttemptedSource);
            Check.Equal(ReadStatus.Connecting, snapshot.Current.Status);
            Check.Equal(SourceReadOutcome.InProgress, snapshot.Current.Outcome);
            Check.Equal(1, snapshot.Current.Policy.Count);
            Check.Equal(0, snapshot.Current.Backend.Count);
            Check.Equal(0, snapshot.Current.Protocol.Count);
            Check.Null(snapshot.LastCompleted);
            var text = SourceReadDiagnosticText.Format(snapshot);
            Check.Contains("本轮最近尝试来源：Codex桌面端", text);
            Check.Contains("本轮状态：正在读取", text);
            Check.Contains("不能据此认定已完成握手", text);
            Check.False(text.Contains("本轮最近尝试来源：尚未开始", StringComparison.Ordinal));
        });

        test("diagnostic snapshot: retry separates old failed status from new in-progress evidence", () =>
        {
            using var gate = new SourceRequestGate(QuotaDataSource.Desktop);
            var diagnostics = new SourceReadDiagnostics(gate);
            Check.True(gate.TryBeginRead(out var first));
            Check.True(diagnostics.Begin(first!, Now));
            diagnostics.Record(first!, Attempt(QuotaDataSource.Desktop));
            diagnostics.Record(first!, new SourceBackendDiagnostic(QuotaDataSource.Desktop,
                SourceBackendStage.Start, SourceBackendOutcome.Failed, ReadStatus.Unavailable, Code: 5));
            Check.True(diagnostics.Complete(first!, ReadStatus.Unavailable, SourceReadOutcome.Failed, Now.AddSeconds(1)));
            var completed = diagnostics.Capture();
            first!.Dispose();
            Check.Null(completed.Current);
            Check.Equal(SourceReadOutcome.Failed, completed.LastCompleted!.Outcome);

            Check.True(gate.TryBeginRead(out var second));
            using var lease = second!;
            Check.True(diagnostics.Begin(lease, Now.AddSeconds(31)));
            diagnostics.Record(lease, Attempt(QuotaDataSource.Desktop));
            var retry = diagnostics.Capture();
            Check.Equal(ReadStatus.Connecting, retry.Current!.Status);
            Check.Equal(ReadStatus.Unavailable, retry.LastCompleted!.Status);
            Check.True(retry.Current.Round > retry.LastCompleted.Round);
            Check.Equal(0, retry.Current.Backend.Count);
            Check.Equal(1, retry.LastCompleted.Backend.Count);
            Check.True(ReferenceEquals(completed.LastCompleted, retry.LastCompleted));
            Check.Null(completed.Current); // Previously captured snapshots do not change.
            var text = SourceReadDiagnosticText.Format(retry);
            var sections = text.Split(new[] { "最近完成轮：" }, StringSplitOptions.None);
            Check.Contains("outcome=InProgress status=Connecting", sections[0]);
            Check.False(sections[0].Contains("status=Unavailable", StringComparison.Ordinal));
            Check.Contains("outcome=Failed status=Unavailable", sections[1]);
        });

        test("diagnostic snapshot: host exception after AttemptStarted has a terminal outcome without invented backend phase", () =>
        {
            using var gate = new SourceRequestGate(QuotaDataSource.Desktop);
            var diagnostics = new SourceReadDiagnostics(gate);
            Check.True(gate.TryBeginRead(out var request));
            using var lease = request!;
            diagnostics.Begin(lease, Now);
            diagnostics.Record(lease, Attempt(QuotaDataSource.Desktop));
            var failure = SourceReadFailureDiagnostic.FromException(new InvalidOperationException("PRIVATE_PATH_TOKEN_JSON"));
            Check.True(diagnostics.Complete(lease, ReadStatus.Unavailable, SourceReadOutcome.UnexpectedFailure,
                Now.AddMilliseconds(5), failure: failure));
            var snapshot = diagnostics.Capture();
            Check.Null(snapshot.Current);
            Check.Equal(SourceReadOutcome.UnexpectedFailure, snapshot.LastCompleted!.Outcome);
            Check.Equal(QuotaDataSource.Desktop, snapshot.LastCompleted.LastAttemptedSource);
            Check.Equal(0, snapshot.LastCompleted.Backend.Count);
            Check.Equal(0, snapshot.LastCompleted.Protocol.Count);
            var text = SourceReadDiagnosticText.Format(snapshot);
            Check.Contains("outcome=UnexpectedFailure status=Unavailable", text);
            Check.Contains("exceptionKind=InvalidOperation", text);
            Check.False(text.Contains("PRIVATE_PATH_TOKEN_JSON", StringComparison.Ordinal));
            Check.False(text.Contains("stage=QuotaRead", StringComparison.Ordinal));
            Check.False(text.Contains("请等待当前查询", StringComparison.Ordinal));
        });

        test("diagnostic snapshot: exception before policy callback reports no attempted backend", () =>
        {
            using var gate = new SourceRequestGate();
            var diagnostics = new SourceReadDiagnostics(gate);
            Check.True(gate.TryBeginRead(out var request));
            using var lease = request!;
            diagnostics.Begin(lease, Now);
            diagnostics.Complete(lease, ReadStatus.Unavailable, SourceReadOutcome.UnexpectedFailure, Now,
                failure: SourceReadFailureDiagnostic.FromException(new IOException("SENSITIVE")));
            var completed = diagnostics.Capture().LastCompleted!;
            Check.Null(completed.LastAttemptedSource);
            Check.Null(completed.SuccessfulSource);
            Check.Equal(0, completed.Policy.Count);
            Check.Contains("exceptionKind=Io", completed.ToSafeLogCode());
        });

        asyncTest("diagnostic snapshot: fake policy exception before backend diagnostics completes the host round", async () =>
        {
            using var gate = new SourceRequestGate(QuotaDataSource.Desktop);
            var diagnostics = new SourceReadDiagnostics(gate);
            var router = new AutoQuotaSourceClient(QuotaDataSource.Desktop);
            Check.True(gate.TryBeginRead(out var request));
            using var lease = request!;
            diagnostics.Begin(lease, Now);
            try
            {
                await router.ReadAsync((_, _) => throw new InvalidOperationException("PRIVATE_BACKEND_PAYLOAD"),
                    lease.Token, item => diagnostics.Record(lease, item));
                throw new TestFailure("The fake backend should fail before emitting a backend event.");
            }
            catch (InvalidOperationException error)
            {
                Check.True(diagnostics.Complete(lease, ReadStatus.Unavailable, SourceReadOutcome.UnexpectedFailure,
                    Now.AddMilliseconds(1), failure: SourceReadFailureDiagnostic.FromException(error)));
            }
            var snapshot = diagnostics.Capture();
            Check.Null(snapshot.Current);
            Check.Equal(SourceReadOutcome.UnexpectedFailure, snapshot.LastCompleted!.Outcome);
            Check.Equal(QuotaDataSource.Desktop, snapshot.LastCompleted.LastAttemptedSource);
            Check.Equal(AutoSourceStage.AttemptStarted, snapshot.LastCompleted.Policy.Single().Stage);
            Check.Equal(0, snapshot.LastCompleted.Backend.Count);
            Check.Equal(0, snapshot.LastCompleted.Protocol.Count);
            Check.False(SourceReadDiagnosticText.Format(snapshot).Contains("PRIVATE_BACKEND_PAYLOAD", StringComparison.Ordinal));
        });

        test("diagnostic snapshot: success is recorded only in its completed round", () =>
        {
            using var gate = new SourceRequestGate();
            var diagnostics = new SourceReadDiagnostics(gate);
            Check.True(gate.TryBeginRead(out var request));
            using var lease = request!;
            diagnostics.Begin(lease, Now);
            diagnostics.Record(lease, Attempt(QuotaDataSource.Cli, QuotaDataSource.Auto));
            Check.Null(diagnostics.Capture().Current!.SuccessfulSource);
            Check.True(diagnostics.Complete(lease, ReadStatus.Ready, SourceReadOutcome.Succeeded,
                Now.AddSeconds(1), QuotaDataSource.Cli));
            var completed = diagnostics.Capture().LastCompleted!;
            Check.Equal(QuotaDataSource.Auto, completed.SelectedSource);
            Check.Equal(QuotaDataSource.Cli, completed.SuccessfulSource);
            Check.Equal(Now.AddSeconds(1), completed.FinishedAt);
            Check.False(diagnostics.Record(lease, Attempt(QuotaDataSource.Desktop)));
            Check.False(diagnostics.Complete(lease, ReadStatus.Unavailable, SourceReadOutcome.UnexpectedFailure, Now));
            Check.True(ReferenceEquals(completed, diagnostics.Capture().LastCompleted));
        });

        asyncTest("diagnostic snapshot: source switch rejects old callbacks, terminals and repeated-source generations", async () =>
        {
            using var gate = new SourceRequestGate();
            var diagnostics = new SourceReadDiagnostics(gate);
            Check.True(gate.TryBeginRead(out var original));
            diagnostics.Begin(original!, Now);
            diagnostics.Record(original!, Attempt(QuotaDataSource.Cli, QuotaDataSource.Auto));
            var before = diagnostics.Capture();
            var firstSwitch = gate.BeginSwitch(QuotaDataSource.Desktop);
            diagnostics.BeginSelection(firstSwitch);
            var newest = gate.BeginSwitch(QuotaDataSource.Auto);
            diagnostics.BeginSelection(newest);
            var pending = diagnostics.Capture();
            Check.True(pending.SwitchPending);
            Check.Null(pending.Current);
            Check.Null(pending.LastCompleted); // Requested cancellation is not a completed read.
            Check.Equal(2L, pending.Generation);
            Check.Equal(QuotaDataSource.Cli, before.Current!.LastAttemptedSource);
            Check.False(diagnostics.Record(original!, Attempt(QuotaDataSource.Desktop)));
            Check.False(diagnostics.Complete(original!, ReadStatus.Unavailable, SourceReadOutcome.Cancelled, Now));
            original!.Dispose();
            await newest.Cleanup;
            Check.True(gate.CompleteSwitch(newest));
            diagnostics.CompleteSelection(firstSwitch);
            Check.True(diagnostics.Capture().SwitchPending);
            diagnostics.CompleteSelection(newest);
            Check.False(diagnostics.Capture().SwitchPending);
            Check.True(gate.TryBeginRead(out var current));
            using var lease = current!;
            diagnostics.Begin(lease, Now.AddSeconds(1));
            var after = diagnostics.Capture();
            Check.Equal(2L, after.Current!.Generation);
            Check.True(after.Current.Round > before.Current.Round);
            Check.False(diagnostics.Record(original, new CliDiagnostic(CliSource.Desktop, null, CliStage.Start, ReadStatus.Unavailable)));
            Check.True(ReferenceEquals(after, diagnostics.Capture()));
        });

        test("diagnostic snapshot: completed prior selection stays explicitly attributed when selection changes", () =>
        {
            using var gate = new SourceRequestGate(QuotaDataSource.Cli);
            var diagnostics = new SourceReadDiagnostics(gate);
            Check.True(gate.TryBeginRead(out var first));
            diagnostics.Begin(first!, Now);
            diagnostics.Complete(first!, ReadStatus.Ready, SourceReadOutcome.Succeeded, Now, QuotaDataSource.Cli);
            first!.Dispose();
            var change = gate.BeginSwitch(QuotaDataSource.Desktop);
            diagnostics.BeginSelection(change);
            Check.True(gate.CompleteSwitch(change));
            diagnostics.CompleteSelection(change);
            Check.True(gate.TryBeginRead(out var second));
            using var lease = second!;
            diagnostics.Begin(lease, Now.AddSeconds(1));
            var snapshot = diagnostics.Capture();
            Check.Equal(QuotaDataSource.Desktop, snapshot.Current!.SelectedSource);
            Check.Equal(QuotaDataSource.Cli, snapshot.LastCompleted!.SelectedSource);
            Check.Equal(0L, snapshot.LastCompleted.Generation);
            Check.Equal(1L, snapshot.Current.Generation);
            Check.Null(snapshot.Current.SuccessfulSource);
        });

        test("diagnostic snapshot: superseded cleanup failure blocks globally without replacing current selection's evidence", () =>
        {
            using var gate = new SourceRequestGate(QuotaDataSource.Desktop);
            var diagnostics = new SourceReadDiagnostics(gate);
            Check.True(gate.TryBeginRead(out var original));
            diagnostics.Begin(original!, Now);
            diagnostics.Record(original!, Attempt(QuotaDataSource.Desktop));
            var change = gate.BeginSwitch(QuotaDataSource.Cli);
            diagnostics.BeginSelection(change);
            diagnostics.BlockFurtherReads(original!, Now);
            Check.False(diagnostics.Complete(original!, ReadStatus.Unavailable, SourceReadOutcome.CleanupUnconfirmed, Now));
            var snapshot = diagnostics.Capture();
            Check.True(snapshot.ReadsBlocked);
            Check.True(gate.ReadsBlocked);
            Check.Equal(QuotaDataSource.Cli, snapshot.SelectedSource);
            Check.Equal(1L, snapshot.Generation);
            Check.Equal(0L, snapshot.GateBlock!.Generation);
            Check.Equal(QuotaDataSource.Desktop, snapshot.GateBlock.SelectedSource);
            Check.Null(snapshot.Current);
            Check.Null(snapshot.LastCompleted);
            original!.Dispose();
            Check.True(gate.CompleteSwitch(change));
            diagnostics.CompleteSelection(change);
            Check.False(gate.TryBeginRead(out _));
            Check.Contains("gateBlockOriginSource=Desktop", SourceReadDiagnosticText.Format(snapshot));
        });

        test("diagnostic snapshot: current cleanup failure publishes terminal and gate block together", () =>
        {
            using var gate = new SourceRequestGate(QuotaDataSource.Desktop);
            var diagnostics = new SourceReadDiagnostics(gate);
            Check.True(gate.TryBeginRead(out var request));
            using var lease = request!;
            diagnostics.Begin(lease, Now);
            diagnostics.Record(lease, Attempt(QuotaDataSource.Desktop));
            var before = diagnostics.Capture();
            Check.True(diagnostics.BlockFurtherReads(lease, Now.AddSeconds(1)));
            var after = diagnostics.Capture();
            Check.False(before.ReadsBlocked);
            Check.Equal(SourceReadOutcome.InProgress, before.Current!.Outcome);
            Check.True(after.ReadsBlocked);
            Check.Null(after.Current);
            Check.Equal(SourceReadOutcome.CleanupUnconfirmed, after.LastCompleted!.Outcome);
            Check.Equal(before.Current.Round, after.LastCompleted.Round);
            Check.Equal(QuotaDataSource.Desktop, after.LastCompleted.LastAttemptedSource);
            Check.False(diagnostics.Record(lease, Attempt(QuotaDataSource.Cli)));
        });

        test("diagnostic snapshot: event collections are immutable and bounded", () =>
        {
            using var gate = new SourceRequestGate(QuotaDataSource.Desktop);
            var diagnostics = new SourceReadDiagnostics(gate);
            Check.True(gate.TryBeginRead(out var request));
            using var lease = request!;
            diagnostics.Begin(lease, Now);
            for (var i = 0; i < 100; ++i)
            {
                diagnostics.Record(lease, Attempt(QuotaDataSource.Desktop));
                diagnostics.Record(lease, new SourceBackendDiagnostic(QuotaDataSource.Desktop, SourceBackendStage.Discover,
                    SourceBackendOutcome.Failed, ReadStatus.Unavailable, Code: i));
                diagnostics.Record(lease, new CliDiagnostic(CliSource.Desktop, null, CliStage.Discovery, ReadStatus.Unavailable, Code: i));
            }
            var round = diagnostics.Capture().Current!;
            Check.Equal(AutoQuotaSourceClient.MaximumPolicyEvents, round.Policy.Count);
            Check.Equal(SourceReadDiagnostics.MaximumBackendEvents, round.Backend.Count);
            Check.Equal(SourceReadDiagnostics.MaximumProtocolEvents, round.Protocol.Count);
            Check.Equal(99, round.Backend.Last().Code);
            Check.Throws<NotSupportedException>(() => ((IList<AutoSourceDiagnostic>)round.Policy).Clear());
            Check.Throws<NotSupportedException>(() => ((IList<SourceBackendDiagnostic>)round.Backend)[0] = round.Backend[1]);
            Check.Throws<NotSupportedException>(() => ((IList<CliDiagnostic>)round.Protocol).Add(round.Protocol[0]));
            diagnostics.Record(lease, Attempt(QuotaDataSource.Cli));
            Check.Equal(QuotaDataSource.Desktop, round.LastAttemptedSource);
        });

        asyncTest("diagnostic snapshot: concurrent captures cannot mix source, round or terminal fields", async () =>
        {
            using var gate = new SourceRequestGate(QuotaDataSource.Desktop);
            var diagnostics = new SourceReadDiagnostics(gate);
            var writer = Task.Run(() =>
            {
                for (var i = 0; i < 500; ++i)
                {
                    Check.True(gate.TryBeginRead(out var request));
                    using var lease = request!;
                    Check.True(diagnostics.Begin(lease, Now.AddSeconds(i)));
                    diagnostics.Record(lease, Attempt(QuotaDataSource.Desktop));
                    diagnostics.Record(lease, new SourceBackendDiagnostic(QuotaDataSource.Desktop, SourceBackendStage.Start,
                        SourceBackendOutcome.Failed, ReadStatus.Unavailable));
                    Check.True(diagnostics.Complete(lease, ReadStatus.Unavailable, SourceReadOutcome.Failed, Now.AddSeconds(i + 1)));
                }
            });
            do
            {
                var captured = diagnostics.Capture();
                if (captured.Current is { } current)
                {
                    Check.Equal(SourceReadOutcome.InProgress, current.Outcome);
                    Check.Equal(ReadStatus.Connecting, current.Status);
                    Check.Null(current.FinishedAt);
                    Check.Equal(captured.Generation, current.Generation);
                    Check.Equal(captured.SelectedSource, current.SelectedSource);
                    if (current.Policy.Count != 0) Check.Equal(QuotaDataSource.Desktop, current.LastAttemptedSource);
                    if (captured.LastCompleted is { } previous) Check.True(current.Round > previous.Round);
                }
                if (captured.LastCompleted is { } completed)
                {
                    Check.Equal(SourceReadOutcome.Failed, completed.Outcome);
                    Check.Equal(ReadStatus.Unavailable, completed.Status);
                    Check.True(completed.FinishedAt is not null);
                    Check.Equal(1, completed.Policy.Count);
                    Check.Equal(1, completed.Backend.Count);
                }
                await Task.Yield();
            } while (!writer.IsCompleted);
            await writer;
            Check.Equal(500L, diagnostics.Capture().LastCompleted!.Round);
        });

        test("diagnostic snapshot: other gate and invalid late callbacks cannot write evidence", () =>
        {
            using var owner = new SourceRequestGate();
            using var foreign = new SourceRequestGate();
            var diagnostics = new SourceReadDiagnostics(owner);
            Check.True(foreign.TryBeginRead(out var request));
            using var lease = request!;
            Check.False(diagnostics.Begin(lease, Now));
            Check.False(diagnostics.Record(lease, Attempt(QuotaDataSource.Cli, QuotaDataSource.Auto)));
            Check.False(diagnostics.BlockFurtherReads(lease, Now));
            Check.False(owner.ReadsBlocked);
            var change = foreign.BeginSwitch(QuotaDataSource.Desktop);
            diagnostics.BeginSelection(change);
            Check.Equal(QuotaDataSource.Auto, diagnostics.Capture().SelectedSource);
            Check.Equal(0L, diagnostics.Capture().Generation);
        });

        test("diagnostic snapshot: exception labels and native codes are closed and payload-free", () =>
        {
            const string secret = "TOKEN_PATH_SERVER_JSON_SENTINEL";
            foreach (var error in new Exception[]
            {
                new IOException(secret), new UnauthorizedAccessException(secret), new InvalidOperationException(secret),
                new ArgumentException(secret), new PlatformNotSupportedException(secret), new NotSupportedException(secret),
                new TimeoutException(secret), new OperationCanceledException(secret), new System.ComponentModel.Win32Exception(5, secret),
                new Exception(secret, new Exception(secret))
            })
            {
                error.Data[secret] = secret;
                var safe = SourceReadFailureDiagnostic.FromException(error).FormatSafe();
                Check.False(safe.Contains(secret, StringComparison.Ordinal));
                Check.Contains("exceptionKind=", safe);
                Check.Contains("exceptionCode=", safe);
            }
            Check.Contains("exceptionKind=Win32 exceptionCode=5",
                SourceReadFailureDiagnostic.FromException(new System.ComponentModel.Win32Exception(5, secret)).FormatSafe());
            Check.Contains("exceptionKind=Unknown", new SourceReadFailureDiagnostic((SourceReadExceptionKind)777, 5).FormatSafe());
        });
    }

    private static AutoSourceDiagnostic Attempt(QuotaDataSource source, QuotaDataSource? selected = null) =>
        new(selected ?? source, source, AutoSourceStage.AttemptStarted, ReadStatus.Connecting, 1, 1);
}
