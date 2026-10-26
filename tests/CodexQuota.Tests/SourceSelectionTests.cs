using System.Text.Json;
using CodexQuota.Core;

static class SourceSelectionTests
{
    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        test("source selection: choices have exact stable labels and default to Auto", () =>
        {
            using var gate = new SourceRequestGate();
            Check.Equal(QuotaDataSource.Auto, gate.Source);
            Check.Equal("自动", QuotaDataSource.Auto.DisplayName());
            Check.Equal("Codex桌面端", QuotaDataSource.Desktop.DisplayName());
            Check.Equal("Codex CLI", QuotaDataSource.Cli.DisplayName());
            Check.Equal("VSCode插件", QuotaDataSource.VsCode.DisplayName());
            Check.False(QuotaDataSourceNames.IsValid((QuotaDataSource)700));
            Check.Throws<ArgumentOutOfRangeException>(() => new SourceRequestGate((QuotaDataSource)700));
        });

        test("source settings: missing settings default to Auto without creating a file", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "not-created", "settings.json");
            var result = new SourcePreferencesStore(path).Load();
            Check.Equal(QuotaDataSource.Auto, result.Source);
            Check.Equal(SourcePreferenceStatus.Default, result.Status);
            Check.False(result.IsWarning);
            Check.Contains("persistedSource=None", result.ToSafeLogCode());
            Check.False(File.Exists(path));
            Check.False(Directory.Exists(Path.GetDirectoryName(path)));
        }));

        test("source settings: all selections round trip and atomically replace existing settings", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new SourcePreferencesStore(path);
            foreach (var source in new[] { QuotaDataSource.Auto, QuotaDataSource.Desktop, QuotaDataSource.Cli, QuotaDataSource.VsCode, QuotaDataSource.Auto })
            {
                Check.Equal(new SourcePreferenceResult(source, SourcePreferenceStatus.Saved), store.Save(source));
                Check.Equal(new SourcePreferenceResult(source, SourcePreferenceStatus.Loaded), new SourcePreferencesStore(path).Load());
                Check.True(new FileInfo(path).Length < SourcePreferencesStore.MaximumBytes);
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                Check.Equal(2, document.RootElement.EnumerateObject().Count());
                Check.Equal(2, document.RootElement.GetProperty("version").GetInt32());
                Check.Equal(JsonValueKind.String, document.RootElement.GetProperty("dataSource").ValueKind);
                Check.Equal(1, Directory.GetFiles(directory).Length);
            }
        }));

        test("source settings: saved desktop and extension selections ignore a legacy CLI override", () => WithDirectory(directory =>
        {
            var store = new SourcePreferencesStore(Path.Combine(directory, "settings.json"));
            const string syntheticPath = "synthetic-legacy-cli-path";
            foreach (var source in new[] { QuotaDataSource.Desktop, QuotaDataSource.VsCode })
            {
                Check.Equal(SourcePreferenceStatus.Saved, store.Save(source).Status);
                Check.Null(store.Load().Source.CliOverridePath(syntheticPath));
            }
            Check.Equal(syntheticPath, QuotaDataSource.Cli.CliOverridePath(syntheticPath));
        }));

        foreach (var json in new[]
        {
            "", "{", "null", "[]", "123", "{}", "{\"version\":1}",
            "{\"version\":1,\"dataSource\":\"unsupported-private-value\"}",
            "{\"version\":1,\"dataSource\":0}", "{\"version\":1,\"dataSource\":null}",
            "{\"version\":1,\"dataSource\":\"CLI\"}", "{\"version\":3,\"dataSource\":\"desktop\"}", "{\"version\":1,\"dataSource\":\"auto\"}",
            "{\"version\":\"1\",\"dataSource\":\"desktop\"}", "{\"version\":null,\"dataSource\":\"desktop\"}",
            "{\"version\":1.5,\"dataSource\":\"desktop\"}", "{\"version\":1e99,\"dataSource\":\"desktop\"}",
            "{\"version\":1,\"dataSource\":\"cli\",\"dataSource\":\"desktop\"}",
            "{\"version\":1,\"dataSource\":\"desktop\",\"token\":\"PRIVATE_SENTINEL\"}",
            "{\"version\":1,\"dataSource\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":1}}}}}"
        })
        {
            var invalid = json;
            test("source settings: malformed/unsupported settings fail safely: " + json, () => WithDirectory(directory =>
            {
                var path = Path.Combine(directory, "settings.json");
                File.WriteAllText(path, invalid);
                var result = new SourcePreferencesStore(path).Load();
                Check.Equal(QuotaDataSource.Auto, result.Source);
                Check.Equal(SourcePreferenceStatus.Invalid, result.Status);
                Check.True(result.IsWarning);
                Check.Contains("persistedSource=Unknown", result.ToSafeLogCode());
                Check.False(result.ToSafeLogCode().Contains("PRIVATE_SENTINEL", StringComparison.Ordinal));
                Check.False(result.ToSafeLogCode().Contains(directory, StringComparison.Ordinal));
                Check.Equal(invalid, File.ReadAllText(path)); // Invalid settings are never repaired or rewritten.
            }));
        }

        test("source settings: oversized input is rejected before unbounded parsing", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, new string(' ', SourcePreferencesStore.MaximumBytes + 1));
            var result = new SourcePreferencesStore(path).Load();
            Check.Equal(SourcePreferenceStatus.Invalid, result.Status);
            Check.Equal(QuotaDataSource.Auto, result.Source);
        }));

        test("source settings: read failures have explicit safe status", () => WithDirectory(directory =>
        {
            var result = new SourcePreferencesStore(directory).Load(); // A directory is not a settings file.
            Check.Equal(SourcePreferenceStatus.ReadFailed, result.Status);
            Check.Equal(QuotaDataSource.Auto, result.Source);
            Check.True(result.IsWarning);
            Check.Contains("ReadFailed", result.ToSafeLogCode());
            Check.False(result.ToSafeLogCode().Contains(directory, StringComparison.Ordinal));
        }));

        test("source settings: write failure keeps selected source and cleans temporary file", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            Directory.CreateDirectory(path); // Stable failure even when tests run with elevated Unix permissions.
            var result = new SourcePreferencesStore(path).Save(QuotaDataSource.VsCode);
            Check.Equal(QuotaDataSource.VsCode, result.Source);
            Check.Equal(SourcePreferenceStatus.SaveFailed, result.Status);
            Check.True(result.IsWarning);
            Check.Contains("本次选择仍有效", result.Explanation);
            Check.Contains("selectedSource=VsCode", result.ToSafeLogCode());
            Check.Contains("persistedSource=Unknown", result.ToSafeLogCode());
            Check.Equal(0, Directory.GetFiles(directory).Length);
            Check.True(Directory.Exists(path));
        }));

        test("source settings: invalid enum cannot alter an existing preference", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new SourcePreferencesStore(path);
            Check.Equal(SourcePreferenceStatus.Saved, store.Save(QuotaDataSource.Desktop).Status);
            var before = File.ReadAllText(path);
            Check.Equal(SourcePreferenceStatus.SaveFailed, store.Save((QuotaDataSource)900).Status);
            Check.Equal(before, File.ReadAllText(path));
        }));

        test("source settings: only app preferences are written; unrelated synthetic auth file stays unchanged", () => WithDirectory(directory =>
        {
            var auth = Path.Combine(directory, "auth.json");
            const string sentinel = "SYNTHETIC_UNRELATED_FILE";
            File.WriteAllText(auth, sentinel);
            var store = new SourcePreferencesStore(Path.Combine(directory, "settings.json"));
            _ = store.Load();
            _ = store.Save(QuotaDataSource.Desktop);
            _ = store.Load();
            Check.Equal(sentinel, File.ReadAllText(auth));
        }));

        test("source gate: single in-flight request and completed request callbacks are rejected", () =>
        {
            using var gate = new SourceRequestGate();
            Check.True(gate.TryBeginRead(out var read));
            var applied = 0;
            Check.True(read!.TryApply(() => applied++));
            Check.False(gate.TryBeginRead(out _));
            read.Dispose();
            read.Dispose();
            Check.False(read.TryApply(() => applied++));
            Check.Equal(1, applied);
            Check.True(gate.TryBeginRead(out var next));
            Check.False(read.TryApply(() => applied++));
            next!.Dispose();
        });

        test("source gate: invalidation happens before synchronous cancellation callbacks", () =>
        {
            using var gate = new SourceRequestGate();
            Check.True(gate.TryBeginRead(out var read));
            var cancellationObserved = false;
            var oldMutation = false;
            using var registration = read!.Token.Register(() =>
            {
                cancellationObserved = true;
                oldMutation = read.TryApply(() => throw new TestFailure("Old source updated during cancellation."));
                Check.Equal(QuotaDataSource.Desktop, gate.Source);
            });
            var change = gate.BeginSwitch(QuotaDataSource.Desktop);
            Check.True(cancellationObserved);
            Check.False(oldMutation);
            Check.True(read.Token.IsCancellationRequested);
            Check.False(change.Cleanup.IsCompleted);
            read.Dispose();
        });

        asyncTest("source gate: canceled late result, diagnostics, and finally cannot mutate new selection", async () =>
        {
            using var gate = new SourceRequestGate();
            var now = DateTimeOffset.UtcNow;
            var oldPolicy = new RefreshPolicy();
            Check.True(oldPolicy.TryBegin(now, RefreshTrigger.Manual));
            Check.True(gate.TryBeginRead(out var old));
            var state = QuotaState.Initial.Apply(new(ReadStatus.Ready, new(84, now.AddHours(1))), now);
            var resultCount = 0;
            var diagnosticCount = 0;
            var finallyCount = 0;
            var change = gate.BeginSwitch(QuotaDataSource.Desktop);
            state = QuotaState.Initial;
            var newPolicy = new RefreshPolicy();
            Check.False(old!.TryApply(() => { state = state.Apply(new(ReadStatus.Ready, new(33, null)), now); resultCount++; }));
            Check.False(old.TryApply(() => { state = state.Apply(new(ReadStatus.Unavailable), now); resultCount++; }));
            Check.False(old.TryApply(() => diagnosticCount++));
            Check.False(old.TryApply(() => { oldPolicy.Complete(ReadStatus.RateLimited, now); finallyCount++; }));
            Check.False(gate.TryBeginRead(out _));
            Check.False(gate.CompleteSwitch(change));
            Check.False(change.Cleanup.IsCompleted);
            old.Dispose();
            await change.Cleanup;
            Check.True(gate.CompleteSwitch(change));
            Check.True(gate.TryBeginRead(out var current));
            Check.True(current!.TryApply(() => state = state.Apply(new(ReadStatus.Ready, new(91, null)), now)));
            Check.False(old.TryApply(() => state = state.Apply(new(ReadStatus.Ready, new(1, null)), now)));
            Check.Quota(state.Quota, 91, 91);
            Check.Equal(0, resultCount);
            Check.Equal(0, diagnosticCount);
            Check.Equal(0, finallyCount);
            Check.True(newPolicy.CanRequest(now));
            Check.Equal(DateTimeOffset.MinValue, newPolicy.RateLimitCooldownUntil);
            current.Dispose();
        });

        asyncTest("source gate: rapid switching starts only newest source after cleanup", async () =>
        {
            using var gate = new SourceRequestGate();
            Check.True(gate.TryBeginRead(out var original));
            var first = gate.BeginSwitch(QuotaDataSource.Desktop);
            var middle = gate.BeginSwitch(QuotaDataSource.VsCode);
            var newest = gate.BeginSwitch(QuotaDataSource.Auto); // Same policy as original, different generation.
            Check.True(ReferenceEquals(first.Cleanup, middle.Cleanup));
            Check.True(ReferenceEquals(middle.Cleanup, newest.Cleanup));
            Check.False(original!.TryApply(() => throw new TestFailure("Re-selected source revived an old generation.")));
            var starts = new List<QuotaDataSource>();
            async Task ResumeAsync(SourceSwitch selection)
            {
                await selection.Cleanup;
                if (!gate.CompleteSwitch(selection)) return;
                Check.True(gate.TryBeginRead(out var active));
                starts.Add(active!.Source);
                active.Dispose();
            }
            var switches = new[] { ResumeAsync(first), ResumeAsync(middle), ResumeAsync(newest) };
            Check.Equal(0, starts.Count);
            Check.False(gate.TryBeginRead(out _));
            original.Dispose();
            await Task.WhenAll(switches);
            Check.Equal(1, starts.Count);
            Check.Equal(QuotaDataSource.Auto, starts[0]);
            Check.False(gate.SwitchPending);
        });

        test("source gate: another gate's selection cannot complete this transition", () =>
        {
            using var first = new SourceRequestGate();
            using var second = new SourceRequestGate();
            var firstChange = first.BeginSwitch(QuotaDataSource.Desktop);
            var secondChange = second.BeginSwitch(QuotaDataSource.Desktop);
            Check.False(first.CompleteSwitch(secondChange));
            Check.True(first.CompleteSwitch(firstChange));
        });

        asyncTest("source gate: shutdown cancels owned request and permanently rejects callbacks and reads", async () =>
        {
            var gate = new SourceRequestGate();
            Check.True(gate.TryBeginRead(out var old));
            var change = gate.BeginSwitch(QuotaDataSource.Desktop);
            gate.Dispose();
            gate.Dispose();
            Check.True(old!.Token.IsCancellationRequested);
            Check.False(old.TryApply(() => throw new TestFailure("Closed gate accepted callback.")));
            old.Dispose();
            await change.Cleanup;
            Check.False(gate.CompleteSwitch(change));
            Check.False(gate.TryBeginRead(out _));
            Check.Throws<ObjectDisposedException>(() => gate.BeginSwitch(QuotaDataSource.VsCode));
        });
    }

    private static void WithDirectory(Action<string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), "quota-source-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try { action(path); }
        finally { Directory.Delete(path, recursive: true); }
    }
}
