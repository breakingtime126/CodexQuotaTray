using System.Text.Json;
using CodexQuota.Core;

// Pure policy delegates and isolated synthetic settings only. Never discovers an installation,
// starts an official component, reads real credentials or accesses the network.
static class AutoSourcePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        asyncTest("auto source: CLI real quota wins without probing another source", async () =>
        {
            var router = Create();
            var calls = new List<QuotaDataSource>();
            var result = await router.ReadAsync((source, _) =>
            {
                calls.Add(source);
                return Task.FromResult(Ready(0)); // Real zero is success, never an unknown sentinel.
            }, CancellationToken.None);
            Sources(calls, QuotaDataSource.Cli);
            Check.Equal(ReadStatus.Ready, result.Status);
            Check.Quota(result.Quota, 0, 0);
            Check.Equal<QuotaDataSource?>(QuotaDataSource.Cli, router.ActiveSource);
            Check.Equal<QuotaDataSource?>(QuotaDataSource.Cli, router.LastAttemptedSource);
            Check.Equal(QuotaDataSource.Auto, router.SelectedSource);
        });

        asyncTest("auto source: failed CLI advances to VSCode and stops on real success", async () =>
        {
            var router = Create();
            var calls = new List<QuotaDataSource>();
            var result = await router.ReadAsync((source, _) =>
            {
                calls.Add(source);
                return Task.FromResult(source == QuotaDataSource.Cli ? new QuotaRead(ReadStatus.Unavailable) : Ready());
            }, CancellationToken.None);
            Sources(calls, QuotaDataSource.Cli, QuotaDataSource.VsCode);
            Check.Equal(ReadStatus.Ready, result.Status);
            Check.Equal<QuotaDataSource?>(QuotaDataSource.VsCode, router.ActiveSource);
        });

        asyncTest("auto source: initial search uses CLI VSCode Desktop order with one attempt each", async () =>
        {
            var router = Create();
            var calls = new List<QuotaDataSource>();
            var events = new List<AutoSourceDiagnostic>();
            var result = await router.ReadAsync((source, _) =>
            {
                calls.Add(source);
                return Task.FromResult(new QuotaRead(ReadStatus.Timeout));
            }, CancellationToken.None, events.Add);
            Sources(calls, QuotaDataSource.Cli, QuotaDataSource.VsCode, QuotaDataSource.Desktop);
            Check.Equal(ReadStatus.Timeout, result.Status);
            Check.Null(router.ActiveSource);
            Check.Equal<QuotaDataSource?>(QuotaDataSource.Desktop, router.LastAttemptedSource);
            Check.Equal(AutoSourceStage.Exhausted, events[^1].Stage);
            Check.True(events.Count <= AutoQuotaSourceClient.MaximumPolicyEvents);
            Check.Equal(3, events[^1].Order);
            Check.Equal(1, events[^1].Attempt);
        });

        asyncTest("auto source: discovery or empty success never counts as quota success", async () =>
        {
            foreach (var first in new[]
            {
                new QuotaRead(ReadStatus.Connecting), new QuotaRead(ReadStatus.Ready),
                new QuotaRead(ReadStatus.NoQuotaWindow), new QuotaRead(ReadStatus.NoWeeklyWindow),
                new QuotaRead(ReadStatus.Ready, new(double.NaN, null)),
                new QuotaRead(ReadStatus.Ready, new(double.PositiveInfinity, null)),
                new QuotaRead(ReadStatus.Ready, new(-1, null)),
                new QuotaRead(ReadStatus.Ready, new(101, null))
            })
            {
                var router = Create();
                var calls = new List<QuotaDataSource>();
                var result = await router.ReadAsync((source, _) =>
                {
                    calls.Add(source);
                    return Task.FromResult(source == QuotaDataSource.Cli ? first : Ready());
                }, CancellationToken.None);
                Sources(calls, QuotaDataSource.Cli, QuotaDataSource.VsCode);
                Check.Equal(ReadStatus.Ready, result.Status);
                Check.Equal<QuotaDataSource?>(QuotaDataSource.VsCode, router.ActiveSource);
            }
        });

        asyncTest("auto source: winner remains first on future refreshes", async () =>
        {
            foreach (var winner in new[] { QuotaDataSource.Cli, QuotaDataSource.VsCode, QuotaDataSource.Desktop })
            {
                var router = Create();
                await PickWinner(router, winner);
                var calls = new List<QuotaDataSource>();
                for (var refresh = 0; refresh < 3; refresh++)
                    await router.ReadAsync((source, _) => { calls.Add(source); return Task.FromResult(Ready()); }, CancellationToken.None);
                Sources(calls, winner, winner, winner);
                Check.Equal<QuotaDataSource?>(winner, router.ActiveSource);
                Check.Equal(QuotaDataSource.Auto, router.SelectedSource);
            }
        });

        asyncTest("auto source: transient active failure retries once after backoff then selects an alternative", async () =>
        {
            foreach (var failure in new[] { ReadStatus.Timeout, ReadStatus.Unavailable })
            {
                var delays = new List<TimeSpan>();
                var router = Create(delay: (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
                await PickWinner(router, QuotaDataSource.VsCode);
                var calls = new List<QuotaDataSource>();
                var events = new List<AutoSourceDiagnostic>();
                var result = await router.ReadAsync((source, _) =>
                {
                    calls.Add(source);
                    return Task.FromResult(source == QuotaDataSource.VsCode ? new QuotaRead(failure) : Ready());
                }, CancellationToken.None, events.Add);
                Sources(calls, QuotaDataSource.VsCode, QuotaDataSource.VsCode, QuotaDataSource.Cli);
                Check.Equal(ReadStatus.Ready, result.Status);
                Check.Equal(1, delays.Count);
                Check.Equal(TimeSpan.FromSeconds(1), delays[0]);
                Check.Equal(1, events.Count(item => item.Stage == AutoSourceStage.RetryScheduled));
                Check.Equal<QuotaDataSource?>(QuotaDataSource.Cli, router.ActiveSource);
                Check.True(events.Count <= AutoQuotaSourceClient.MaximumPolicyEvents);
            }
        });

        asyncTest("auto source: active retry success retains winner and does not probe alternatives", async () =>
        {
            var router = Create();
            await PickWinner(router, QuotaDataSource.Desktop);
            var calls = new List<QuotaDataSource>();
            await router.ReadAsync((source, _) =>
            {
                calls.Add(source);
                return Task.FromResult(calls.Count == 1 ? new QuotaRead(ReadStatus.Timeout) : Ready());
            }, CancellationToken.None);
            Sources(calls, QuotaDataSource.Desktop, QuotaDataSource.Desktop);
            Check.Equal<QuotaDataSource?>(QuotaDataSource.Desktop, router.ActiveSource);
        });

        asyncTest("auto source: worst cycle is four sequential attempts and one backoff", async () =>
        {
            var delays = 0;
            var router = Create(delay: (_, _) => { ++delays; return Task.CompletedTask; });
            await PickWinner(router, QuotaDataSource.Desktop);
            var calls = new List<QuotaDataSource>();
            var events = new List<AutoSourceDiagnostic>();
            var result = await router.ReadAsync((source, _) =>
            {
                calls.Add(source);
                return Task.FromResult(new QuotaRead(ReadStatus.Unavailable));
            }, CancellationToken.None, events.Add);
            Sources(calls, QuotaDataSource.Desktop, QuotaDataSource.Desktop, QuotaDataSource.Cli, QuotaDataSource.VsCode);
            Check.Equal(ReadStatus.Unavailable, result.Status);
            Check.Equal(1, delays);
            Check.Equal(10, events.Count);
            Check.True(events.Count <= AutoQuotaSourceClient.MaximumPolicyEvents);
            Check.Equal(AutoSourceStage.Exhausted, events[^1].Stage);
            // Last known working source remains attached to a stale snapshot, never a failure.
            Check.Equal<QuotaDataSource?>(QuotaDataSource.Desktop, router.ActiveSource);
            Check.Equal<QuotaDataSource?>(QuotaDataSource.VsCode, router.LastAttemptedSource);
        });

        asyncTest("auto source: nontransient active failure advances without retry or delay", async () =>
        {
            foreach (var failure in new[] { ReadStatus.CliMissing, ReadStatus.NotSignedIn, ReadStatus.ApiKey,
                ReadStatus.UnsupportedAccount, ReadStatus.ProtocolError, ReadStatus.NoQuotaWindow,
                ReadStatus.CredentialsExpired, ReadStatus.CredentialsUnavailable, ReadStatus.AuthConfigurationUnsupported })
            {
                var router = Create(delay: (_, _) => throw new TestFailure("Nontransient failure requested retry."));
                await PickWinner(router, QuotaDataSource.Cli);
                var calls = new List<QuotaDataSource>();
                await router.ReadAsync((source, _) =>
                {
                    calls.Add(source);
                    return Task.FromResult(source == QuotaDataSource.Cli ? new QuotaRead(failure) : Ready());
                }, CancellationToken.None);
                Sources(calls, QuotaDataSource.Cli, QuotaDataSource.VsCode);
            }
        });

        asyncTest("auto source: all manual choices stay strict without fallback or retry", async () =>
        {
            foreach (var selected in new[] { QuotaDataSource.Cli, QuotaDataSource.VsCode, QuotaDataSource.Desktop })
            foreach (var failure in new[] { ReadStatus.CliMissing, ReadStatus.Timeout, ReadStatus.Unavailable,
                ReadStatus.ProtocolError, ReadStatus.NotSignedIn, ReadStatus.RateLimited })
            {
                var router = Create(selected, delay: (_, _) => throw new TestFailure("Manual choice retried."));
                await PickWinner(router, selected);
                var calls = new List<QuotaDataSource>();
                var result = await router.ReadAsync((source, _) =>
                {
                    calls.Add(source);
                    return Task.FromResult(new QuotaRead(failure));
                }, CancellationToken.None);
                Sources(calls, selected);
                Check.Equal(failure, result.Status);
                Check.Equal(selected, router.SelectedSource);
                Check.Equal<QuotaDataSource?>(selected, router.ActiveSource);
            }
        });

        asyncTest("auto source: rate limiting stops shared-provider fallback and honors five-minute cooldown", async () =>
        {
            var now = Now;
            var router = Create(now: () => now, delay: (_, _) => throw new TestFailure("Rate limit retried."));
            var calls = new List<QuotaDataSource>();
            var events = new List<AutoSourceDiagnostic>();
            Task<QuotaRead> Limited(QuotaDataSource source, CancellationToken _)
            { calls.Add(source); return Task.FromResult(new QuotaRead(ReadStatus.RateLimited)); }
            Check.Equal(ReadStatus.RateLimited, (await router.ReadAsync(Limited, CancellationToken.None, events.Add)).Status);
            Sources(calls, QuotaDataSource.Cli);
            Check.Equal(Now.AddMinutes(5), router.RateLimitCooldownUntil);
            Check.Equal(AutoSourceStage.RateLimitCooldown, events[^1].Stage);
            now = Now.AddMinutes(5).AddTicks(-1);
            Check.Equal(ReadStatus.RateLimited, (await router.ReadAsync(Limited, CancellationToken.None)).Status);
            Sources(calls, QuotaDataSource.Cli);
            now = Now.AddMinutes(5);
            await router.ReadAsync((source, _) => { calls.Add(source); return Task.FromResult(Ready()); }, CancellationToken.None);
            Sources(calls, QuotaDataSource.Cli, QuotaDataSource.Cli);
            Check.Equal(DateTimeOffset.MinValue, router.RateLimitCooldownUntil);
        });

        asyncTest("auto source: a rate-limited active retry cannot continue to another installation", async () =>
        {
            var router = Create();
            await PickWinner(router, QuotaDataSource.VsCode);
            var calls = new List<QuotaDataSource>();
            var result = await router.ReadAsync((source, _) =>
            {
                calls.Add(source);
                return Task.FromResult(new QuotaRead(calls.Count == 1 ? ReadStatus.Timeout : ReadStatus.RateLimited));
            }, CancellationToken.None);
            Check.Equal(ReadStatus.RateLimited, result.Status);
            Sources(calls, QuotaDataSource.VsCode, QuotaDataSource.VsCode);
            Check.Equal<QuotaDataSource?>(QuotaDataSource.VsCode, router.ActiveSource);
        });

        asyncTest("auto source: pre-cancelled read starts no source", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var router = Create();
            await Check.ThrowsAsync<OperationCanceledException>(router.ReadAsync((_, _) =>
                throw new TestFailure("Cancelled policy started a backend."), cancellation.Token));
            Check.Null(router.ActiveSource);
            Check.Null(router.LastAttemptedSource);
        });

        asyncTest("auto source: cancellation discards a late success and never starts the next source", async () =>
        {
            foreach (var late in new[] { Ready(), new QuotaRead(ReadStatus.Unavailable) })
            {
                using var cancellation = new CancellationTokenSource();
                var router = Create();
                var calls = new List<QuotaDataSource>();
                await Check.ThrowsAsync<OperationCanceledException>(router.ReadAsync((source, _) =>
                {
                    calls.Add(source);
                    cancellation.Cancel();
                    return Task.FromResult(late); // Deliberately emulates a cancellation-ignoring backend.
                }, cancellation.Token));
                Sources(calls, QuotaDataSource.Cli);
                Check.Null(router.ActiveSource);
            }
        });

        asyncTest("auto source: cancellation during retry backoff starts neither retry nor alternative", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var delayStarted = Signal();
            var router = Create(delay: async (_, token) =>
            {
                delayStarted.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
            });
            await PickWinner(router, QuotaDataSource.Cli);
            var calls = new List<QuotaDataSource>();
            var pending = router.ReadAsync((source, _) =>
            {
                calls.Add(source);
                return Task.FromResult(new QuotaRead(ReadStatus.Timeout));
            }, cancellation.Token);
            await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(pending);
            Sources(calls, QuotaDataSource.Cli);
        });

        asyncTest("auto source: attempts await prior cleanup and reject concurrent refresh", async () =>
        {
            var router = Create();
            var cleanup = new TaskCompletionSource<QuotaRead>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = Signal();
            var calls = new List<QuotaDataSource>();
            var pending = router.ReadAsync((source, _) =>
            {
                calls.Add(source);
                if (source == QuotaDataSource.Cli) { started.TrySetResult(true); return cleanup.Task; }
                return Task.FromResult(Ready());
            }, CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Sources(calls, QuotaDataSource.Cli);
            await Check.ThrowsAsync<InvalidOperationException>(router.ReadAsync((_, _) =>
                throw new TestFailure("Concurrent refresh started another backend."), CancellationToken.None));
            Sources(calls, QuotaDataSource.Cli);
            cleanup.SetResult(new(ReadStatus.CliMissing));
            Check.Equal(ReadStatus.Ready, (await pending).Status);
            Sources(calls, QuotaDataSource.Cli, QuotaDataSource.VsCode);
        });

        asyncTest("auto source: unconfirmed cleanup blocks fallback and every later read", async () =>
        {
            var router = Create();
            var calls = new List<QuotaDataSource>();
            var events = new List<AutoSourceDiagnostic>();
            await Check.ThrowsAsync<BackendCleanupUnconfirmedException>(router.ReadAsync((source, _) =>
            {
                calls.Add(source);
                throw new BackendCleanupUnconfirmedException();
            }, CancellationToken.None, events.Add));
            Sources(calls, QuotaDataSource.Cli);
            Check.True(router.CleanupBlocked);
            Check.Null(router.ActiveSource);
            Check.Equal(AutoSourceStage.CleanupUnconfirmed, events[^1].Stage);
            Check.Equal<QuotaDataSource?>(QuotaDataSource.Cli, events[^1].Source);
            await Check.ThrowsAsync<BackendCleanupUnconfirmedException>(router.ReadAsync((_, _) =>
                throw new TestFailure("Unconfirmed cleanup allowed a later backend."), CancellationToken.None));
            Check.True(router.CleanupBlocked);
        });

        asyncTest("auto source: active-source cleanup failure cannot retry or inspect alternatives", async () =>
        {
            var router = Create(delay: (_, _) => throw new TestFailure("Unconfirmed cleanup scheduled a retry."));
            await PickWinner(router, QuotaDataSource.VsCode);
            var calls = new List<QuotaDataSource>();
            await Check.ThrowsAsync<BackendCleanupUnconfirmedException>(router.ReadAsync((source, _) =>
            {
                calls.Add(source);
                throw new BackendCleanupUnconfirmedException();
            }, CancellationToken.None));
            Sources(calls, QuotaDataSource.VsCode);
            Check.True(router.CleanupBlocked);
            Check.Equal<QuotaDataSource?>(QuotaDataSource.VsCode, router.ActiveSource);
        });

        asyncTest("auto source: unconfirmed cleanup remains blocking even when selection was cancelled", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var router = Create();
            var calls = new List<QuotaDataSource>();
            var events = new List<AutoSourceDiagnostic>();
            await Check.ThrowsAsync<BackendCleanupUnconfirmedException>(router.ReadAsync((source, _) =>
            {
                calls.Add(source);
                cancellation.Cancel();
                throw new BackendCleanupUnconfirmedException();
            }, cancellation.Token, events.Add));
            Sources(calls, QuotaDataSource.Cli);
            Check.True(router.CleanupBlocked);
            Check.Equal(AutoSourceStage.CleanupUnconfirmed, events[^1].Stage);
            Check.False(events.Any(item => item.Stage == AutoSourceStage.Cancelled));
            await Check.ThrowsAsync<BackendCleanupUnconfirmedException>(router.ReadAsync((_, _) =>
                throw new TestFailure("Cancelled cleanup failure allowed another backend."), cancellation.Token));
            Check.Null(router.ActiveSource);
        });

        asyncTest("source gate: permanent cleanup block survives selection completion and later switches", async () =>
        {
            using var gate = new SourceRequestGate();
            Check.False(gate.ReadsBlocked);
            Check.True(gate.TryBeginRead(out var old));
            var change = gate.BeginSwitch(QuotaDataSource.Desktop);
            Check.False(old!.TryApply(() => throw new TestFailure("A stale result was applied.")));
            // Cleanup failure belongs to process ownership even if its result generation is stale.
            gate.BlockFurtherReads();
            Check.True(gate.ReadsBlocked);
            Check.False(gate.TryBeginRead(out _));
            old.Dispose();
            await change.Cleanup;
            Check.True(gate.CompleteSwitch(change));
            Check.True(gate.ReadsBlocked);
            Check.False(gate.TryBeginRead(out _));
            foreach (var source in new[] { QuotaDataSource.Cli, QuotaDataSource.VsCode, QuotaDataSource.Auto })
            {
                var next = gate.BeginSwitch(source);
                await next.Cleanup;
                Check.True(gate.CompleteSwitch(next));
                Check.True(gate.ReadsBlocked);
                Check.False(gate.TryBeginRead(out _));
            }
            gate.BlockFurtherReads(); // Idempotent; no policy selection or successful cleanup clears it.
            Check.True(gate.ReadsBlocked);
        });

        test("source gate: cleanup block prevents a first read without requiring an active lease", () =>
        {
            using var gate = new SourceRequestGate();
            gate.BlockFurtherReads();
            Check.True(gate.ReadsBlocked);
            Check.False(gate.TryBeginRead(out _));
        });

        asyncTest("auto source: new generation cannot inherit a cancelled router winner or cooldown", async () =>
        {
            using var gate = new SourceRequestGate();
            var oldRouter = Create();
            Check.True(gate.TryBeginRead(out var oldLease));
            var cleanup = new TaskCompletionSource<QuotaRead>(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldRead = oldRouter.ReadAsync((_, _) => cleanup.Task, oldLease!.Token);
            var change = gate.BeginSwitch(QuotaDataSource.Desktop);
            Check.False(change.Cleanup.IsCompleted);
            cleanup.SetResult(Ready());
            await Check.ThrowsAsync<OperationCanceledException>(oldRead);
            Check.Null(oldRouter.ActiveSource);
            oldLease.Dispose();
            await change.Cleanup;
            Check.True(gate.CompleteSwitch(change));
            var next = Create(QuotaDataSource.Desktop);
            Check.Null(next.ActiveSource);
            Check.Equal(DateTimeOffset.MinValue, next.RateLimitCooldownUntil);
            Check.True(gate.TryBeginRead(out var currentLease));
            var result = await next.ReadAsync((_, _) => Task.FromResult(Ready()), currentLease!.Token);
            Check.True(currentLease.TryApply(() => Check.Equal(ReadStatus.Ready, result.Status)));
            Check.False(oldLease.TryApply(() => throw new TestFailure("Old generation changed the new policy.")));
            currentLease.Dispose();
        });

        asyncTest("auto source: throwing diagnostics do not alter selection or retry", async () =>
        {
            var router = Create();
            var calls = new List<QuotaDataSource>();
            var result = await router.ReadAsync((source, _) =>
            {
                calls.Add(source);
                return Task.FromResult(source == QuotaDataSource.Cli ? new QuotaRead(ReadStatus.CliMissing) : Ready());
            }, CancellationToken.None, _ => throw new InvalidOperationException("SYNTHETIC_PRIVATE_TEXT"));
            Check.Equal(ReadStatus.Ready, result.Status);
            Sources(calls, QuotaDataSource.Cli, QuotaDataSource.VsCode);
        });

        test("auto source: safe policy trace formats only bounded numbers and known labels", () =>
        {
            var trace = new AutoSourceDiagnostic((QuotaDataSource)600, (QuotaDataSource)700,
                (AutoSourceStage)800, (ReadStatus)900, int.MaxValue, -1).FormatSafe();
            Check.Contains("selectedSource=Unknown", trace);
            Check.Contains("source=Unknown", trace);
            Check.Contains("stage=Unknown", trace);
            Check.Contains("status=Unknown", trace);
            Check.Contains("order=3", trace);
            Check.Contains("attempt=0", trace);
            Check.True(trace.Length < 200);
        });

        test("auto source: invalid selected policy is rejected", () =>
            Check.Throws<ArgumentOutOfRangeException>(() => new AutoQuotaSourceClient((QuotaDataSource)999)));

        foreach (var legacy in new[] { "cli", "vscode", "desktop" })
        {
            var label = legacy;
            test("source settings: valid v1 " + label + " migrates to v2 Auto exactly once", () => WithDirectory(directory =>
            {
                var path = Path.Combine(directory, "settings.json");
                File.WriteAllText(path, "{\"version\":1,\"dataSource\":\"" + label + "\"}");
                var store = new SourcePreferencesStore(path);
                var migrated = store.Load();
                Check.Equal(QuotaDataSource.Auto, migrated.Source);
                Check.Equal(SourcePreferenceStatus.Migrated, migrated.Status);
                Check.False(migrated.IsWarning);
                Check.Contains("旧版", migrated.Explanation);
                Check.Contains("自动", migrated.Explanation);
                Check.Contains("persistedSource=Auto", migrated.ToSafeLogCode());
                using var saved = JsonDocument.Parse(File.ReadAllBytes(path));
                Check.Equal(2, saved.RootElement.GetProperty("version").GetInt32());
                Check.Equal("auto", saved.RootElement.GetProperty("dataSource").GetString());
                var written = File.ReadAllText(path);
                Check.Equal(new SourcePreferenceResult(QuotaDataSource.Auto, SourcePreferenceStatus.Loaded), store.Load());
                Check.Equal(written, File.ReadAllText(path));
                Check.Equal(1, Directory.GetFiles(directory).Length);
                // A newly chosen v2 manual source remains manual on subsequent restarts.
                store.Save(QuotaDataSource.Desktop);
                Check.Equal(new SourcePreferenceResult(QuotaDataSource.Desktop, SourcePreferenceStatus.Loaded), store.Load());
            }));
        }

        asyncTest("source settings: Auto winner never becomes a persisted manual selection", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "quota-auto-policy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "settings.json");
                var store = new SourcePreferencesStore(path);
                store.Save(QuotaDataSource.Auto);
                var before = File.ReadAllText(path);
                var router = Create(store.Load().Source);
                await PickWinner(router, QuotaDataSource.VsCode);
                Check.Equal(QuotaDataSource.Auto, router.SelectedSource);
                Check.Equal<QuotaDataSource?>(QuotaDataSource.VsCode, router.ActiveSource);
                Check.Equal(before, File.ReadAllText(path));
                Check.Equal(QuotaDataSource.Auto, store.Load().Source);
                Check.False(before.Contains("activeSource", StringComparison.Ordinal));
                Check.Equal(1, Directory.GetFiles(directory).Length);
            }
            finally { Directory.Delete(directory, recursive: true); }
        });
    }

    private static AutoQuotaSourceClient Create(QuotaDataSource selected = QuotaDataSource.Auto,
        Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(selected, now ?? (() => Now), delay ?? ((_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }));
    private static QuotaRead Ready(double remaining = 72) => new(ReadStatus.Ready, new(remaining, null));
    private static Task PickWinner(AutoQuotaSourceClient router, QuotaDataSource winner) =>
        router.ReadAsync((source, _) => Task.FromResult(source == winner ? Ready() : new QuotaRead(ReadStatus.CliMissing)), CancellationToken.None);
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Sources(IReadOnlyList<QuotaDataSource> actual, params QuotaDataSource[] expected)
    {
        Check.Equal(expected.Length, actual.Count);
        for (var index = 0; index < expected.Length; index++) Check.Equal(expected[index], actual[index]);
    }
    private static void WithDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "quota-auto-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
