using System.ComponentModel;
using System.Diagnostics;
using CodexQuota.Core;

internal static class SelectedBackendTests
{
    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        test("selected source: invalid selection is rejected before any provider", () =>
        {
            Check.Throws<ArgumentOutOfRangeException>(() => new CodexQuotaClient((QuotaDataSource)999));
            Check.Throws<ArgumentOutOfRangeException>(() => CliLocator.Discover((QuotaDataSource)999, null,
                NeverString, NeverString, true, NeverDiscover, NeverDiscover));
        });
        test("selected source: failed CLI discovery never reads Desktop or VS Code providers", () =>
        {
            using var files = new TempDirectory();
            var result = CliLocator.Discover(QuotaDataSource.Cli, null, () => "", () => files.Path,
                true, NeverDiscover, NeverDiscover);
            Check.Null(result.Candidate);
            Check.Equal(1, result.Examined);
            Check.Equal(SourceBackendExclusion.Missing, result.Exclusions);
        });
        foreach (var source in new[] { QuotaDataSource.Desktop, QuotaDataSource.VsCode })
            test("selected source: " + source + " absence never reads CLI or foreign provider", () =>
            {
                var result = CliLocator.Discover(source, null, NeverString, NeverString, true,
                    source == QuotaDataSource.Desktop ? () => Array.Empty<string>() : NeverDiscover,
                    source == QuotaDataSource.VsCode ? () => Array.Empty<string>() : NeverDiscover);
                Check.Null(result.Candidate);
                Check.Equal(0, result.Examined);
            });
        test("selected source: explicit CLI override is never used for Desktop", () =>
        {
            var result = CliLocator.Discover(QuotaDataSource.Desktop, "private-path", NeverString, NeverString,
                true, NeverDiscover, NeverDiscover);
            Check.Null(result.Candidate);
            Check.Equal(SourceBackendExclusion.SourceMismatch, result.Exclusions);
        });
        foreach (var (path, reason) in new[]
        {
            (Path.Combine("package", "app", "resources", "codex.exe"), SourceBackendExclusion.DesktopComponent),
            (Path.Combine("extensions", "openai.chatgpt-26.10.100-win32-x64", "bin", "windows-x86_64", "codex.exe"), SourceBackendExclusion.VsCodeComponent),
            (Path.Combine("Microsoft", "WindowsApps", "codex.exe"), SourceBackendExclusion.WindowsAppsAlias)
        })
            test("selected source: CLI rejects known foreign location " + reason, () =>
            {
                using var files = new TempDirectory();
                var binary = files.Touch(path);
                var result = CliLocator.Discover(QuotaDataSource.Cli, binary, () => "", NeverString,
                    true, NeverDiscover, NeverDiscover);
                Check.Null(result.Candidate);
                Check.Equal(1, result.Examined);
                Check.Equal(1, result.Excluded);
                Check.Equal(reason, result.Exclusions);
            });
        test("selected source: PATH desktop alias is excluded and later standalone CLI wins", () =>
        {
            using var files = new TempDirectory();
            var foreign = files.Touch(Path.Combine("package", "app", "resources", "codex.exe"));
            var cli = files.Touch(Path.Combine("standalone", "codex.exe"));
            var path = string.Join(Path.PathSeparator, Path.GetDirectoryName(foreign), Path.GetDirectoryName(cli));
            var result = CliLocator.Discover(QuotaDataSource.Cli, null, () => path, NeverString,
                true, NeverDiscover, NeverDiscover);
            Check.Equal(cli, result.Candidate!.StartInfo.FileName);
            Check.True((result.Exclusions & SourceBackendExclusion.DesktopComponent) != 0);
            Check.Equal(SourceBackendLocation.CliPath, result.Candidate.Location);
        });
        test("selected source: official local VS Code numeric version ordering rejects lookalikes", () =>
        {
            using var files = new TempDirectory();
            string Add(string folder) => files.Touch(Path.Combine("extensions", folder, "bin", "windows-x86_64", "codex.exe"));
            Add("openai.chatgpt-26.9.900-win32-x64");
            var newest = Add("openai.chatgpt-26.10.100-win32-x64");
            Add("openai.chatgpt-26.11.100-linux-x64");
            Add("openai.chatgpt-26.11.100-malicious");
            Add("other.chatgpt-99.1.1-win32-x64");
            Add("openai.chatgpt-malicious-99.1.1-win32-x64");
            var result = CliLocator.Discover(QuotaDataSource.VsCode, null, NeverString, NeverString,
                true, NeverDiscover, () => new[] { Path.Combine(files.Path, "extensions") });
            Check.Equal(newest, result.Candidate!.StartInfo.FileName);
            Check.Equal(SourceBackendComponent.VsCodeBundledCodex, result.Candidate.Component);
            Check.Equal(1, result.Examined);
        });
        test("selected source: provider access denial retains native code without private text", () =>
        {
            var result = CliLocator.Discover(QuotaDataSource.Desktop, null, NeverString, NeverString,
                true, () => throw new Win32Exception(5, "private-user private-path secret-token"), NeverDiscover);
            Check.Null(result.Candidate);
            Check.Equal(5, result.Code);
            Check.Equal(SourceBackendExclusion.Inaccessible, result.Exclusions);
        });
        test("selected source: provider unavailability differs from an absent installation", () =>
        {
            var result = CliLocator.Discover(QuotaDataSource.Desktop, null, NeverString, NeverString,
                true, () => throw new EntryPointNotFoundException("private details"), NeverDiscover);
            Check.Null(result.Candidate);
            Check.Equal(SourceBackendExclusion.ProviderUnavailable, result.Exclusions);
            Check.True(result.Code is not null);
        });
        test("source diagnostics: labels and numbers are bounded even for invalid enum values", () =>
        {
            var record = new SourceBackendDiagnostic((QuotaDataSource)999, (SourceBackendStage)999,
                (SourceBackendOutcome)999, (ReadStatus)999, long.MaxValue,
                (SourceBackendLocation)999, (SourceBackendComponent)999, new Version(0, 159, 2),
                (SourceBackendVersionEvidence)999, int.MaxValue, -5, int.MaxValue,
                SourceBackendExclusion.WindowsAppsAlias | (SourceBackendExclusion)4096, int.MinValue,
                (CliErrorKind)999, (CliStartOperation)999, (SourceBackendExit)999, (SourceBackendTimeout)999, int.MaxValue);
            var text = record.FormatSafe();
            Check.True(text.Length < 1000);
            Check.Contains("source=Unknown", text);
            Check.Contains("elapsedMs=86400000", text);
            Check.Contains("examined=4096 candidates=0 excluded=4096", text);
            Check.Contains("exclusions=WindowsAppsAlias,Unknown", text);
            Check.Contains("version=0.159.2", text);
            Check.False(text.Contains("999", StringComparison.Ordinal));
            Check.Equal(text, record.ToSafeLogCode());
            Check.False(typeof(SourceBackendDiagnostic).GetProperties().Any(property => property.PropertyType == typeof(string)));
        });
        foreach (var (source, origin) in new[]
        {
            (QuotaDataSource.Cli, CliSource.Explicit), (QuotaDataSource.Desktop, CliSource.Desktop),
            (QuotaDataSource.VsCode, CliSource.VsCode)
        })
            asyncTest("source diagnostics: successful synthetic " + source + " read reports all stages once", async () =>
            {
                using var environment = new FakeChildEnvironment("success");
                var traces = new List<SourceBackendDiagnostic>();
                var diagnostics = new List<CliDiagnostic>();
                var start = CliLocator.Find(environment.Executable)!;
                var result = await new CodexQuotaClient(source, () => new[] { new CliCandidate(origin, start) },
                    diagnostics.Add, traces.Add).ReadAsync(CancellationToken.None);
                Check.Equal(ReadStatus.Ready, result.Status);
                Check.True(traces.Select(item => item.Stage).SequenceEqual(new[]
                {
                    SourceBackendStage.Discover, SourceBackendStage.Start, SourceBackendStage.Initialize,
                    SourceBackendStage.QuotaRead, SourceBackendStage.Cleanup
                }));
                Check.True(traces.All(item => item.Source == source && item.Outcome == SourceBackendOutcome.Succeeded));
                Check.True(traces.All(item => item.ElapsedMilliseconds >= 0 && item.FormatSafe().Length < 1000));
                Check.Null(traces[0].Version);
                Check.Null(traces[1].Version);
                Check.Equal(1, diagnostics.Count);
                Check.Equal(SourceBackendExit.OwnedTreeTerminationRequested, traces.Last().Exit);
                await environment.AssertChildrenExitedAsync(1);
            });
        asyncTest("source diagnostics: inaccessible discovery is unavailable, not missing installation", async () =>
        {
            var traces = new List<SourceBackendDiagnostic>();
            var result = await new CodexQuotaClient(QuotaDataSource.Desktop,
                () => new SourceBackendDiscovery(null, Excluded: 1, Exclusions: SourceBackendExclusion.Inaccessible, Code: 5),
                null, traces.Add).ReadAsync(CancellationToken.None);
            Check.Equal(ReadStatus.Unavailable, result.Status);
            Check.Equal(1, traces.Count);
            Check.Equal(5, traces[0].Code);
            Check.Equal(SourceBackendExclusion.Inaccessible, traces[0].Exclusions);
        });
        asyncTest("source diagnostics: mismatched injected candidate fails before process creation", async () =>
        {
            var traces = new List<SourceBackendDiagnostic>();
            var diagnostics = new List<CliDiagnostic>();
            var result = await new CodexQuotaClient(QuotaDataSource.Desktop,
                () => new[] { new CliCandidate(CliSource.Path, new ProcessStartInfo("must-never-launch")) },
                diagnostics.Add, traces.Add).ReadAsync(CancellationToken.None);
            Check.Equal(ReadStatus.Unavailable, result.Status);
            Check.Equal(1, traces.Count);
            Check.Equal(SourceBackendStage.Discover, traces[0].Stage);
            Check.Equal(CliErrorKind.SourceMismatch, traces[0].ErrorKind);
            Check.Equal(SourceBackendExit.NotStarted, traces[0].Exit);
        });
        asyncTest("source diagnostics: start failure retains code and unverified version without retry", async () =>
        {
            using var files = new TempDirectory();
            var traces = new List<SourceBackendDiagnostic>();
            IEnumerable<CliCandidate> Candidates()
            {
                yield return new(CliSource.Desktop, new ProcessStartInfo(Path.Combine(files.Path, "private-user-secret-path"))
                { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true });
                throw new TestFailure("No alternate candidate may be enumerated.");
            }
            var result = await new CodexQuotaClient(QuotaDataSource.Desktop, Candidates, trace: traces.Add)
                .ReadAsync(CancellationToken.None);
            Check.Equal(ReadStatus.Unavailable, result.Status);
            var failed = traces.Single(item => item.Stage == SourceBackendStage.Start);
            Check.Equal(SourceBackendOutcome.Failed, failed.Outcome);
            Check.Equal(CliStartOperation.ManagedProcessStart, failed.StartOperation);
            Check.True(failed.Code is not null);
            Check.Null(failed.Version);
            Check.Equal(SourceBackendVersionEvidence.Unknown, failed.VersionEvidence);
            Check.True(traces.All(item => !item.FormatSafe().Contains("private", StringComparison.Ordinal)));
        });
        asyncTest("source diagnostics: synthetic timeout is bounded and names its stage and scope", async () =>
        {
            using var environment = new FakeChildEnvironment("hang");
            var traces = new List<SourceBackendDiagnostic>();
            var result = await new CodexQuotaClient(QuotaDataSource.VsCode,
                () => new[] { new CliCandidate(CliSource.VsCode, CliLocator.Find(environment.Executable)!) },
                trace: traces.Add, sessionTimeout: TimeSpan.FromMilliseconds(500)).ReadAsync(CancellationToken.None);
            Check.Equal(ReadStatus.Timeout, result.Status);
            var failed = traces.Single(item => item.Outcome == SourceBackendOutcome.Failed);
            Check.Equal(SourceBackendStage.Initialize, failed.Stage);
            Check.Equal(SourceBackendTimeout.Session, failed.Timeout);
            Check.Equal(SourceBackendStage.Cleanup, traces.Last().Stage);
            await environment.AssertChildrenExitedAsync(1);
        });
        asyncTest("source lifecycle: blocked discovery returns promptly and holds switch until real completion", async () =>
        {
            using var gate = new SourceRequestGate(QuotaDataSource.Cli);
            Check.True(gate.TryBeginRead(out var lease));
            using var ownedLease = lease!;
            using var releaseDiscovery = new ManualResetEventSlim(false);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var returned = new TaskCompletionSource<Task<QuotaRead>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var traces = new List<SourceBackendDiagnostic>();
            var client = new CodexQuotaClient(QuotaDataSource.Cli, () =>
            {
                entered.TrySetResult(true);
                if (!releaseDiscovery.Wait(TimeSpan.FromSeconds(5)))
                    throw new TestFailure("Controlled discovery was never released.");
                return new[] { new CliCandidate(CliSource.Path, new ProcessStartInfo("must-never-start")) };
            }, trace: traces.Add);
            // A dedicated caller represents the UI. The test remains recoverable even if
            // a regression blocks that caller before ReadAsync returns its task.
            var caller = new Thread(() =>
            {
                try { returned.TrySetResult(client.ReadAsync(ownedLease.Token)); }
                catch (Exception error) { returned.TrySetException(error); }
            }) { IsBackground = true };
            caller.Start();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var read = await returned.Task.WaitAsync(TimeSpan.FromSeconds(1));
                Check.False(read.IsCompleted, "ReadAsync must return while discovery is still blocked.");
                async Task ReleaseAfterReadAsync()
                {
                    try { await read; }
                    finally { ownedLease.Dispose(); }
                }
                var lifetime = ReleaseAfterReadAsync();
                var change = gate.BeginSwitch(QuotaDataSource.Desktop);
                await Task.Delay(40);
                Check.True(ownedLease.Token.IsCancellationRequested);
                Check.False(read.IsCompleted, "Cancellation must not abandon blocked discovery.");
                Check.False(change.Cleanup.IsCompleted);
                Check.False(gate.TryBeginRead(out _));
                Check.False(gate.CompleteSwitch(change));
                releaseDiscovery.Set();
                await Check.ThrowsAsync<OperationCanceledException>(lifetime);
                await change.Cleanup.WaitAsync(TimeSpan.FromSeconds(2));
                Check.True(gate.CompleteSwitch(change));
                Check.True(gate.TryBeginRead(out var next));
                next!.Dispose();
                Check.False(traces.Any(item => item.Stage == SourceBackendStage.Start),
                    "A cancelled discovery must not launch its discovered process.");
            }
            finally
            {
                releaseDiscovery.Set();
                Check.True(caller.Join(TimeSpan.FromSeconds(2)), "The simulated UI caller did not return.");
            }
        });
        asyncTest("source diagnostics: trace observer failure cannot break a synthetic read", async () =>
        {
            using var environment = new FakeChildEnvironment("success");
            var result = await new CodexQuotaClient(QuotaDataSource.Cli, environment.Executable,
                trace: _ => throw new IOException("private-observer-error")).ReadAsync(CancellationToken.None);
            Check.Equal(ReadStatus.Ready, result.Status);
            await environment.AssertChildrenExitedAsync(1);
        });
    }
    private static string NeverString() => throw new TestFailure("Unselected provider was evaluated.");
    private static IEnumerable<string> NeverDiscover() => throw new TestFailure("Unselected provider was evaluated.");
}
