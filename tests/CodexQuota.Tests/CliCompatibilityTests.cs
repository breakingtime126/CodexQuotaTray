using System.Diagnostics;
using System.Text.Json;
using CodexQuota.Core;

internal static class CliCompatibilityTests
{
    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        test("CLI candidates: source-agnostic compatibility entry is CLI-only", () =>
        {
            using var files = new TempDirectory();
            var path = files.Touch(Path.Combine("path", "codex.exe"));
            var candidates = CliLocator.FindCandidates(null, Path.GetDirectoryName(path), files.Path, true,
                NeverDiscover, NeverDiscover).ToArray();
            Check.Equal(1, candidates.Length);
            Check.Equal(CliSource.Path, candidates[0].Source);
            Check.Equal(path, candidates[0].StartInfo.FileName);
        });

        test("CLI candidates: explicit override never enumerates another source", () =>
        {
            using var files = new TempDirectory();
            var path = files.Touch("chosen.exe");
            var result = CliLocator.FindCandidates(path, files.Path, files.Path, true,
                NeverDiscover, NeverDiscover).ToArray();
            Check.Equal(1, result.Length);
            Check.Equal(CliSource.Explicit, result[0].Source);
            Check.Equal(0, CliLocator.FindCandidates(Path.Combine(files.Path, "missing.exe"), files.Path, files.Path, true,
                NeverDiscover, NeverDiscover).Count());
        });

        test("CLI diagnostics: only numeric backend version is retained", () =>
        {
            foreach (var product in new[] { "codex_cli_rs", "codex_app_server", "codex_quota_tray" })
            {
                var version = VersionFrom(product + "/0.159.0-alpha.2 (private-device; private-account)");
                Check.Equal(new Version(0, 159, 0), version);
                var diagnostic = new CliDiagnostic(CliSource.VsCode, version, CliStage.RateLimitsRead, ReadStatus.Unavailable, -32603);
                var text = diagnostic.ToSafeLogCode();
                Check.Contains("version=0.159.0", text);
                Check.Contains("code=-32603", text);
                Check.False(text.Contains("private", StringComparison.Ordinal));
                Check.False(text.Contains("alpha", StringComparison.Ordinal));
            }
            foreach (var input in new[] { "private/0.159.0", "codex_cli_rs/private", "codex_cli_rs/0.159.0private", "codex_cli_rs/0.159", new string('x', 1025) })
                Check.Null(VersionFrom(input));
            Check.Contains("source=Unknown", new CliDiagnostic((CliSource)999, null, CliStage.Start, ReadStatus.Unavailable).ToSafeLogCode());
        });

        test("CLI isolation: no status permits another candidate", () =>
        {
            foreach (var status in Enum.GetValues<ReadStatus>())
                Check.False(CodexQuotaClient.CanTryNext(status));
        });

        test("CLI diagnostics: startup operation and ordinals are allowlisted without raw messages", () =>
        {
            var text = new CliDiagnostic(CliSource.Desktop, null, CliStage.Start, ReadStatus.Unavailable, 5,
                StartOperation: CliStartOperation.CreateProcess, CandidateIndex: 2, AttemptIndex: 1).ToSafeLogCode();
            Check.Contains("startOperation=CreateProcess", text);
            Check.Contains("candidate=2 attempt=1", text);
            Check.Contains("code=5", text);
            Check.Contains("startOperation=Unknown", new CliDiagnostic(CliSource.Desktop, null, CliStage.Start,
                ReadStatus.Unavailable, StartOperation: (CliStartOperation)999).ToSafeLogCode());
        });

        foreach (var count in new[] { 1, 2 })
            asyncTest("CLI diagnostics: only one Desktop candidate starts out of " + count + " available", async () =>
            {
                using var files = new TempDirectory();
                var diagnostics = new List<CliDiagnostic>();
                var candidates = Enumerable.Range(1, count).Select(index => new CliCandidate(CliSource.Desktop,
                    new ProcessStartInfo(Path.Combine(files.Path, "missing-private-binary-" + index))
                    { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true })).ToArray();
                var result = await new CodexQuotaClient(QuotaDataSource.Desktop, () => candidates, diagnostics.Add).ReadAsync(CancellationToken.None);
                Check.Equal(ReadStatus.Unavailable, result.Status);
                Check.Equal(1, diagnostics.Count);
                Check.Equal(1, diagnostics[0].CandidateIndex);
                Check.Equal(1, diagnostics[0].AttemptIndex);
                Check.True(diagnostics.All(item => item.Stage == CliStage.Start &&
                    item.StartOperation == CliStartOperation.ManagedProcessStart && item.Code is not null));
                Check.False(diagnostics.Any(item => item.ToSafeLogCode().Contains("private", StringComparison.Ordinal)));
            });

        asyncTest("CLI isolation: unusable first binary never enumerates or launches a second", async () =>
        {
            using var files = new TempDirectory();
            var diagnostics = new List<CliDiagnostic>();
            IEnumerable<CliCandidate> Candidates()
            {
                yield return new(CliSource.Path, new ProcessStartInfo(Path.Combine(files.Path, "missing-private-path"))
                { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true });
                throw new TestFailure("Second candidate must not be enumerated.");
            }
            var result = await new CodexQuotaClient(Candidates, diagnostics.Add).ReadAsync(CancellationToken.None);
            Check.Equal(ReadStatus.Unavailable, result.Status);
            Check.Equal(1, diagnostics.Count);
            Check.Equal(CliStage.Start, diagnostics[0].Stage);
            Check.True(diagnostics[0].Code is not null);
            Check.Equal(1, diagnostics[0].CandidateIndex);
            Check.Equal(1, diagnostics[0].AttemptIndex);
            Check.False(diagnostics[0].ToSafeLogCode().Contains("private", StringComparison.Ordinal));
        });

        asyncTest("CLI isolation: session deadline cleans the only hung process", async () =>
        {
            using var environment = new FakeChildEnvironment("hang");
            var diagnostics = new List<CliDiagnostic>();
            var start = CliLocator.Find(environment.Executable)!;
            var client = new CodexQuotaClient(() => new[]
            {
                new CliCandidate(CliSource.Path, start), new CliCandidate(CliSource.Desktop, start)
            }, diagnostics.Add, TimeSpan.FromMilliseconds(500));
            var watch = Stopwatch.StartNew();
            var result = await client.ReadAsync(CancellationToken.None);
            Check.Equal(ReadStatus.Timeout, result.Status);
            Check.True(watch.Elapsed < TimeSpan.FromSeconds(4));
            Check.Equal(1, diagnostics.Count);
            Check.Equal(CliStage.Initialize, diagnostics[0].Stage);
            await environment.AssertChildrenExitedAsync(1);
        });

        asyncTest("CLI diagnostics: observer failure never breaks a successful read", async () =>
        {
            using var environment = new FakeChildEnvironment("success");
            var result = await new CodexQuotaClient(environment.Executable, _ => throw new IOException("private error"))
                .ReadAsync(CancellationToken.None);
            Check.Equal(ReadStatus.Ready, result.Status);
            await environment.AssertChildrenExitedAsync(1);
        });

        asyncTest("CLI protocol diagnostics: stage and numeric version survive a rate-limit method error", async () =>
        {
            await using var peer = new FakePeer();
            var stages = new List<(CliStage Stage, Version? Version)>();
            var read = CodexQuotaProtocol.ReadAsync(peer.Rpc, TimeSpan.FromSeconds(2), CancellationToken.None,
                (stage, version) => stages.Add((stage, version)));
            var initialize = await peer.ReadAsync();
            await peer.RespondAsync(initialize, new { userAgent = "codex_quota_tray/0.159.0 (private-device)" });
            Check.Method(await peer.ReadAsync(), "initialized");
            var quota = await peer.ReadAsync();
            await peer.SendAsync(new { id = quota.GetProperty("id").GetInt64(), error = new { code = -32601, message = "private account error" } });
            var error = await Check.ThrowsAsync<RpcException>(read);
            Check.Equal(-32601, error.Code);
            Check.Equal(CliStage.RateLimitsRead, stages.Last().Stage);
            Check.Equal(new Version(0, 159, 0), stages.Last().Version);
            Check.False(error.Message.Contains("private", StringComparison.Ordinal));
        });

        asyncTest("CLI isolation: pre-canceled read does not even discover installations", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var client = new CodexQuotaClient(() => throw new TestFailure("Discovery must not run."));
            await Check.ThrowsAsync<OperationCanceledException>(client.ReadAsync(cancellation.Token));
        });

        asyncTest("CLI diagnostics: expired discovery deadline is timeout rather than missing CLI", async () =>
        {
            var diagnostics = new List<CliDiagnostic>();
            var client = new CodexQuotaClient(() =>
            {
                Thread.Sleep(150);
                return Array.Empty<CliCandidate>();
            }, diagnostics.Add, TimeSpan.FromMilliseconds(25));
            var result = await client.ReadAsync(CancellationToken.None);
            Check.Equal(ReadStatus.Timeout, result.Status);
            Check.Equal(1, diagnostics.Count);
            Check.Equal(CliStage.Discovery, diagnostics[0].Stage);
            Check.Equal(ReadStatus.Timeout, diagnostics[0].Status);
        });

        asyncTest("CLI isolation: cancellation during empty discovery propagates", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var client = new CodexQuotaClient(() =>
            {
                cancellation.Cancel();
                return Array.Empty<CliCandidate>();
            });
            await Check.ThrowsAsync<OperationCanceledException>(client.ReadAsync(cancellation.Token));
        });
    }

    private static IEnumerable<string> NeverDiscover() => throw new TestFailure("Fallback discovery must remain deferred.");

    private static Version? VersionFrom(string userAgent)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { userAgent }));
        return CliDiagnostic.ReadVersion(json.RootElement);
    }
}
