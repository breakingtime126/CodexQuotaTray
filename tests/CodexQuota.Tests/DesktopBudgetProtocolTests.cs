using System.Diagnostics;
using CodexQuota.Core;

internal static class DesktopBudgetProtocolTests
{
    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        foreach (var (limit, text) in new[]
        {
            (DesktopEvidenceLimit.HashTime, "累计读取/哈希时间预算"),
            (DesktopEvidenceLimit.VerificationTime, "总时间截止"),
            (DesktopEvidenceLimit.ImageBytes, "单文件验证大小上限"),
            (DesktopEvidenceLimit.TotalHashBytes, "累计文件字节上限"),
            (DesktopEvidenceLimit.DirectoryCount, "目录扫描条目数量"),
            (DesktopEvidenceLimit.None, "资源预算")
        })
            test("Desktop budget explanation identifies limit " + limit, () =>
            {
                var diagnostic = new CliDiagnostic(CliSource.None, null, CliStage.Discovery, ReadStatus.Unavailable,
                    ErrorKind: CliErrorKind.DesktopEvidenceLimit) { Desktop = Detail(limit) };
                Check.Contains(text, diagnostic.Explanation!);
                Check.False(diagnostic.Explanation!.Contains("未通过当前注册包", StringComparison.Ordinal));
            });
        asyncTest("Desktop budget discovery is a resource limit, not an identity mismatch", async () =>
        {
            var diagnostics = new List<CliDiagnostic>(); var traces = new List<SourceBackendDiagnostic>();
            var read = await new CodexQuotaClient(QuotaDataSource.Desktop, () => Failure(DesktopEvidenceLimit.HashTime),
                diagnostics.Add, traces.Add).ReadAsync(CancellationToken.None);
            Check.Equal(ReadStatus.Unavailable, read.Status);
            Check.Equal(CliErrorKind.DesktopEvidenceLimit, diagnostics.Single().ErrorKind);
            Check.Equal(DesktopEvidenceLimit.HashTime, diagnostics.Single().Desktop!.Limit);
            Check.Equal(SourceBackendStage.Discover, traces.Single().Stage);
            Check.Equal(SourceBackendExit.NotStarted, traces.Single().Exit);
        });
        asyncTest("Desktop discovery receives the actual client session deadline", async () =>
        {
            var traces = new List<SourceBackendDiagnostic>(); var entered = false;
            var read = await new CodexQuotaClient(QuotaDataSource.Desktop, token =>
            {
                entered = true; Check.True(token.CanBeCanceled);
                Check.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(3)));
                Check.True(token.IsCancellationRequested);
                return Failure(DesktopEvidenceLimit.VerificationTime);
            }, null, traces.Add, TimeSpan.FromMilliseconds(40)).ReadAsync(CancellationToken.None);
            Check.True(entered); Check.Equal(ReadStatus.Timeout, read.Status);
            Check.Equal(SourceBackendTimeout.Session, traces.Single().Timeout);
            Check.Equal(SourceBackendExit.NotStarted, traces.Single().Exit);
        });
        asyncTest("Desktop caller cancellation waits for synchronous discovery completion", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            using var entered = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim();
            var returned = false;
            var read = new CodexQuotaClient(QuotaDataSource.Desktop, token =>
            {
                entered.Set();
                Check.True(finish.Wait(TimeSpan.FromSeconds(3)));
                Check.True(token.IsCancellationRequested); returned = true;
                return Failure(DesktopEvidenceLimit.VerificationTime);
            }, null, null).ReadAsync(cancellation.Token);
            Check.True(entered.Wait(TimeSpan.FromSeconds(3))); cancellation.Cancel();
            Check.False(read.IsCompleted); finish.Set();
            try { await read; throw new Exception("Expected caller cancellation"); }
            catch (OperationCanceledException) { }
            Check.True(returned);
        });
        asyncTest("Desktop prelaunch cancellation retains its cancelled trace and unavailable cleanup", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var traces = new List<SourceBackendDiagnostic>();
            var candidate = new CliCandidate(CliSource.Desktop, new ProcessStartInfo("synthetic-must-not-launch.exe"))
            {
                AcquireLaunchLease = () =>
                {
                    cancellation.Cancel();
                    throw new DesktopRuntimeValidationException(SourceBackendExclusion.DesktopEvidenceLimit,
                        desktop: Detail(DesktopEvidenceLimit.VerificationTime));
                }
            };
            try
            {
                await new CodexQuotaClient(QuotaDataSource.Desktop, () => new SourceBackendDiscovery(candidate, 2),
                    null, traces.Add).ReadAsync(cancellation.Token);
                throw new Exception("Expected caller cancellation");
            }
            catch (OperationCanceledException) { }
            var start = traces.Single(item => item.Stage == SourceBackendStage.Start);
            Check.Equal(SourceBackendOutcome.Cancelled, start.Outcome);
            Check.Equal(ReadStatus.Unavailable, start.Status);
            Check.Equal(ReadStatus.Unavailable, traces.Single(item => item.Stage == SourceBackendStage.Cleanup).Status);
            Check.True(traces.All(item => item.Exit == SourceBackendExit.NotStarted));
        });
        asyncTest("Desktop prelaunch budget failure does not start the discovered component", async () =>
        {
            var diagnostics = new List<CliDiagnostic>(); var traces = new List<SourceBackendDiagnostic>();
            var candidate = new CliCandidate(CliSource.Desktop, new ProcessStartInfo("synthetic-must-not-launch.exe"))
            {
                Location = SourceBackendLocation.DesktopExistingRuntime,
                AcquireLaunchLease = () => throw new DesktopRuntimeValidationException(SourceBackendExclusion.DesktopEvidenceLimit,
                    desktop: Detail(DesktopEvidenceLimit.HashTime))
            };
            var read = await new CodexQuotaClient(QuotaDataSource.Desktop, () => new SourceBackendDiscovery(candidate, 2),
                diagnostics.Add, traces.Add).ReadAsync(CancellationToken.None);
            Check.Equal(ReadStatus.Unavailable, read.Status);
            Check.Equal(CliErrorKind.DesktopEvidenceLimit, diagnostics.Single().ErrorKind);
            Check.Equal(CliStartOperation.ValidateDesktopRuntime, diagnostics.Single().StartOperation);
            Check.True(traces.All(item => item.Exit == SourceBackendExit.NotStarted));
        });
    }

    private static DesktopEvidenceDiagnostic Detail(DesktopEvidenceLimit limit) =>
        new(DesktopEvidenceStep.ImageHash, DesktopEvidenceLocation.RuntimeImage) { Limit = limit };
    private static SourceBackendDiscovery Failure(DesktopEvidenceLimit limit) =>
        new(null, Examined: 2, Excluded: 1, Exclusions: SourceBackendExclusion.DesktopEvidenceLimit) { Desktop = Detail(limit) };
}
