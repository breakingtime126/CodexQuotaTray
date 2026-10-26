using System.Text;
using System.Text.Json;
using CodexQuota.Core;

static class OutputSpeedReaderTests
{
    private const string ThreadA = "11111111-1111-4111-8111-111111111111";
    private const string ThreadB = "22222222-2222-4222-8222-222222222222";
    private static readonly DateTimeOffset Epoch = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        asyncTest("output speed reader: completion-only lifecycle timestamps cannot establish 4000 TPS", async () =>
        {
            using var home = new TemporaryHome();
            // Official Codex can emit ItemStarted and ItemCompleted from OutputItemDone.
            // Their positive elapsed time then measures notification work after generation.
            // ItemStarted is transient and is not persisted in the rollout, so this shape
            // cannot distinguish a 250 ms lifecycle fallback from a streamed response.
            home.Write(ThreadA, Header(ThreadA) +
                Line(0, "turn_context", new { turn_id = "turn-fallback", model = "gpt-6.1-sol", effort = "medium" }) +
                Line(2, "event_msg", new { type = "item_completed", thread_id = ThreadA, turn_id = "turn-fallback",
                    item = new { type = "Reasoning", id = "fallback" },
                    started_at_ms = Epoch.AddMilliseconds(1750).ToUnixTimeMilliseconds(),
                    completed_at_ms = Epoch.AddSeconds(2).ToUnixTimeMilliseconds() }) +
                Line(2, "response_item", new { type = "reasoning", id = "fallback" }) +
                Usage(ThreadA, "fallback", 2, 1000));
            var result = await home.Reader().PollAsync();
            Check.True(result.Sample?.TokensPerSecond is null,
                $"Unverified lifecycle interval was exposed as {result.Sample?.TokensPerSecond} TPS.");
            Check.Equal("gpt-6.1-sol", result.Sample!.Model);
            Check.Equal("medium", result.Sample.ReasoningEffort);
            Check.Null(result.Sample.GenerationDuration);
            Check.Equal(Epoch.AddSeconds(2), result.Sample.RecordedAt);
            var view = OutputSpeedView.Create(result.Sample, Epoch.AddSeconds(3));
            Check.Equal("计时未核验", view.Value);
            Check.Equal("GPT6.1 Sol", view.CompactModel);
            Check.Equal("中", view.ReasoningLabel);
            Check.False(view.HasVerifiedTiming);
        });

        test("output speed reader: invalid limits are rejected", () =>
            Check.Throws<ArgumentOutOfRangeException>(() => new RolloutSpeedReader("unused", new()
            { MaxBytesPerPoll = 1 })));

        asyncTest("output speed reader: absent logs are unknown, never zero", async () =>
        {
            using var home = new TemporaryHome();
            var result = await home.Reader().PollAsync();
            Check.Equal(RolloutSpeedReadStatus.NoFiles, result.Status);
            Check.Null(result.Sample);
            Check.Equal(0, result.BytesRead);
        });

        asyncTest("output speed reader: CODEX_HOME is honored without config or credentials", async () =>
        {
            using var home = new TemporaryHome();
            home.Write(ThreadA, Header(ThreadA) + Response(ThreadA, "response-a", 0));
            File.WriteAllText(Path.Combine(home.Path, "auth.json"), "NOT JSON AND NEVER READ");
            File.WriteAllText(Path.Combine(home.Path, "config.toml"), "INVALID CONFIG AND NEVER READ");
            var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
            try
            {
                Environment.SetEnvironmentVariable("CODEX_HOME", home.Path);
                var result = await new RolloutSpeedReader().PollAsync();
                Check.Equal("response-a", result.Sample!.ResponseId);
                Check.Null(result.Sample.TokensPerSecond);
            }
            finally { Environment.SetEnvironmentVariable("CODEX_HOME", previous); }
        });

        asyncTest("output speed reader: complete response and idle polling use bounded metadata reads", async () =>
        {
            using var home = new TemporaryHome();
            home.Write(ThreadA, Header(ThreadA) + Response(ThreadA, "response-a", 0));
            var reader = home.Reader();
            var first = await reader.PollAsync();
            Check.Equal(RolloutSpeedReadStatus.Ready, first.Status);
            Check.Null(first.Sample!.TokensPerSecond);
            Check.False(first.PendingWork);
            var idle = await reader.PollAsync();
            Check.Equal(first.Sample, idle.Sample);
            Check.True(idle.BytesRead <= 768, "Idle files must not be parsed again.");
        });

        asyncTest("output speed reader: new unverified records never expose a historical numeric fallback", async () =>
        {
            using var home = new TemporaryHome();
            // The old parser called the first 50 TPS and the second 4000 TPS.
            // Both are completion-only lifecycle spans, regardless of their ratio.
            var path = home.Write(ThreadA, Header(ThreadA) + Response(ThreadA, "historical", 0));
            var reader = home.Reader();
            var first = await reader.PollAsync();
            Check.Equal("historical", first.Sample!.ResponseId);
            Check.Null(first.Sample.TokensPerSecond);
            await Append(path, Encoding.UTF8.GetBytes(Response(ThreadA, "new-record", 10, 8000)));
            var next = await reader.PollAsync();
            Check.Equal("new-record", next.Sample!.ResponseId);
            Check.Equal(8000L, next.Sample.OutputTokens);
            Check.Null(next.Sample.TokensPerSecond);
            Check.Equal(OutputSpeedTimingReliability.UnverifiedItemLifecycle, next.Sample.TimingReliability);
            foreach (var result in new[] { first, next, await reader.PollAsync(), await home.Reader().PollAsync() })
            {
                var view = OutputSpeedView.Create(result.Sample, Epoch.AddHours(1), unavailable: true);
                Check.Equal("计时未核验", view.Value);
                Check.False(view.HasVerifiedTiming);
                Check.False(view.AccessibleText.Contains("50.0 tok/s", StringComparison.Ordinal));
                Check.False(view.AccessibleText.Contains("4000 tok/s", StringComparison.Ordinal));
            }
        });

        asyncTest("output speed reader: split UTF8 and partial final JSON wait for newline", async () =>
        {
            using var home = new TemporaryHome();
            var path = home.Write(ThreadA, Header(ThreadA));
            var reader = home.Reader();
            await reader.PollAsync();
            var bytes = Encoding.UTF8.GetBytes(Response(ThreadA, "response-a", 0, text: "秘密é不可缓存"));
            var split = Array.IndexOf(bytes, (byte)0xc3) + 1;
            Check.True(split > 0);
            await Append(path, bytes.Take(split).ToArray());
            var partial = await reader.PollAsync();
            Check.Null(partial.Sample);
            Check.True(partial.PendingWork);
            await Append(path, bytes.Skip(split).Take(bytes.Length - split - 1).ToArray());
            Check.Null((await reader.PollAsync()).Sample);
            await Append(path, new byte[] { (byte)'\n' });
            Check.Equal("response-a", (await reader.PollAsync()).Sample!.ResponseId);
        });

        asyncTest("output speed reader: simultaneous sessions choose completion time without summing", async () =>
        {
            using var home = new TemporaryHome();
            var a = home.Write(ThreadA, Header(ThreadA) + Response(ThreadA, "response-a", 20, 200));
            var b = home.Write(ThreadB, Header(ThreadB) + Response(ThreadB, "response-b", 0, 900));
            File.SetLastWriteTimeUtc(a, Epoch.UtcDateTime);
            File.SetLastWriteTimeUtc(b, Epoch.AddHours(1).UtcDateTime);
            var result = await home.Reader().PollAsync();
            Check.Equal(ThreadA, result.Sample!.ThreadId);
            Check.Null(result.Sample.TokensPerSecond);
            Check.Equal(2, result.TrackedFileCount);
        });

        asyncTest("output speed reader: malformed future clocks cannot pin or hide later valid samples", async () =>
        {
            using var home = new TemporaryHome();
            var future = (int)(DateTimeOffset.UtcNow.AddDays(3) - Epoch).TotalSeconds;
            var path = home.Write(ThreadA, Header(ThreadA) + Response(ThreadA, "future", future));
            var reader = home.Reader();
            Check.Null((await reader.PollAsync()).Sample);
            await Append(path, Encoding.UTF8.GetBytes(Response(ThreadA, "real", 20)));
            Check.Equal("real", (await reader.PollAsync()).Sample!.ResponseId);
        });

        asyncTest("output speed reader: truncate and same-length replacement restart offsets", async () =>
        {
            using var home = new TemporaryHome();
            var path = home.Write(ThreadA, Header(ThreadA) + Response(ThreadA, "response-a", 0));
            var reader = home.Reader();
            await reader.PollAsync();
            var originalLength = new FileInfo(path).Length;
            var originalWrite = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(path, Header(ThreadA) + Response(ThreadA, "response-b", 10), new UTF8Encoding(false));
            Check.Equal(originalLength, new FileInfo(path).Length);
            // Fingerprints also detect replacement when a producer preserves timestamps.
            File.SetLastWriteTimeUtc(path, originalWrite);
            Check.Equal("response-b", (await reader.PollAsync()).Sample!.ResponseId);
            File.WriteAllText(path, Header(ThreadA), new UTF8Encoding(false));
            await reader.PollAsync();
            await Append(path, Encoding.UTF8.GetBytes(Response(ThreadA, "response-c", 20)));
            Check.Equal("response-c", (await reader.PollAsync()).Sample!.ResponseId);
        });

        asyncTest("output speed reader: archival rotation and process restart recover bounded samples", async () =>
        {
            using var home = new TemporaryHome();
            var path = home.Write(ThreadA, Header(ThreadA) + Response(ThreadA, "response-a", 0));
            var reader = home.Reader();
            var original = await reader.PollAsync();
            var archive = System.IO.Path.Combine(home.Path, "archived_sessions");
            Directory.CreateDirectory(archive);
            var moved = System.IO.Path.Combine(archive, System.IO.Path.GetFileName(path));
            File.Move(path, moved);
            Check.Equal(original.Sample, (await reader.PollAsync()).Sample);
            await Append(moved, Encoding.UTF8.GetBytes(Response(ThreadA, "response-b", 20, 120)));
            Check.Equal("response-b", (await reader.PollAsync()).Sample!.ResponseId);
            Check.Equal("response-b", (await home.Reader().PollAsync()).Sample!.ResponseId);
        });

        asyncTest("output speed reader: copied ancestor history cannot become child throughput", async () =>
        {
            using var home = new TemporaryHome();
            var path = home.Write(ThreadB, Header(ThreadA) + Response(ThreadA, "ancestor", 0));
            var reader = home.Reader();
            Check.Null((await reader.PollAsync()).Sample);
            await Append(path, Encoding.UTF8.GetBytes(Header(ThreadB) + Response(ThreadB, "child", 20)));
            Check.Equal(ThreadB, (await reader.PollAsync()).Sample!.ThreadId);
        });

        asyncTest("output speed reader: duplicates across active and archive are idempotent", async () =>
        {
            using var home = new TemporaryHome();
            var body = Header(ThreadA) + Response(ThreadA, "response-a", 0);
            home.Write(ThreadA, body);
            home.Write(ThreadA, body, archived: true);
            var result = await home.Reader().PollAsync();
            Check.Null(result.Sample!.TokensPerSecond);
            Check.Equal("response-a", result.Sample.ResponseId);
        });

        asyncTest("output speed reader: incomplete archive duplicate still invalidates conflicting usage", async () =>
        {
            foreach (var archiveNewest in new[] { false, true })
            {
                using var home = new TemporaryHome();
                var active = home.Write(ThreadA, Header(ThreadA) + Response(ThreadA, "response-a", 0));
                var archive = home.Write(ThreadA, Header(ThreadA) + Usage(ThreadA, "response-a", 2, 999), archived: true);
                File.SetLastWriteTimeUtc(active, Epoch.AddHours(archiveNewest ? 0 : 1).UtcDateTime);
                File.SetLastWriteTimeUtc(archive, Epoch.AddHours(archiveNewest ? 1 : 0).UtcDateTime);
                var result = await home.Reader().PollAsync();
                Check.Null(result.Sample);
                Check.Equal(RolloutSpeedReadStatus.NoValidSample, result.Status);
            }
        });

        asyncTest("output speed reader: a conflicting usage replay withdraws the old sample", async () =>
        {
            using var home = new TemporaryHome();
            var path = home.Write(ThreadA, Header(ThreadA) + Response(ThreadA, "response-a", 0));
            var reader = home.Reader();
            Check.True((await reader.PollAsync()).Sample is not null);
            await Append(path, Encoding.UTF8.GetBytes(Usage(ThreadA, "response-a", 2, 999)));
            var result = await reader.PollAsync();
            Check.Null(result.Sample);
            Check.Equal(RolloutSpeedReadStatus.NoValidSample, result.Status);
        });

        asyncTest("output speed reader: invalid older responses cannot reappear after latest is withdrawn", async () =>
        {
            using var home = new TemporaryHome();
            var path = home.Write(ThreadA, Header(ThreadA) + Response(ThreadA, "older", 0) +
                Response(ThreadA, "newer", 20));
            var reader = home.Reader();
            Check.Equal("newer", (await reader.PollAsync()).Sample!.ResponseId);
            await Append(path, Encoding.UTF8.GetBytes(Usage(ThreadA, "older", 30, 999)));
            Check.Equal("newer", (await reader.PollAsync()).Sample!.ResponseId);
            await Append(path, Encoding.UTF8.GetBytes(Usage(ThreadA, "newer", 32, 999)));
            Check.Null((await reader.PollAsync()).Sample);
        });

        asyncTest("output speed reader: equal-time samples are withdrawn by response identity", async () =>
        {
            using var home = new TemporaryHome();
            var path = home.Write(ThreadA, Header(ThreadA) + Response(ThreadA, "first", 0) +
                Response(ThreadA, "second", 0));
            var reader = home.Reader();
            await reader.PollAsync();
            await Append(path, Encoding.UTF8.GetBytes(Usage(ThreadA, "first", 10, 999)));
            Check.Equal("second", (await reader.PollAsync()).Sample!.ResponseId);
            await Append(path, Encoding.UTF8.GetBytes(Usage(ThreadA, "second", 12, 999)));
            Check.Null((await reader.PollAsync()).Sample);
        });

        asyncTest("output speed reader: replacement cannot retain an unverifiable old sample", async () =>
        {
            using var home = new TemporaryHome();
            var path = home.Write(ThreadA, Header(ThreadA) + Response(ThreadA, "response-a", 0));
            var reader = home.Reader();
            Check.True((await reader.PollAsync()).Sample is not null);
            File.WriteAllText(path, Header(ThreadA) + Usage(ThreadA, "response-a", 2, 999), new UTF8Encoding(false));
            Check.Null((await reader.PollAsync()).Sample);
        });

        asyncTest("output speed reader: corrupt and oversize records recover only at a fresh boundary", async () =>
        {
            using var home = new TemporaryHome();
            var path = home.Write(ThreadA, Header(ThreadA));
            var reader = home.Reader(SmallLimits());
            await reader.PollAsync();
            await Append(path, Encoding.UTF8.GetBytes(new string('x', 4000) + "\n" +
                Response(ThreadA, "response-a", 0)));
            RolloutSpeedReadResult result = await reader.PollAsync();
            for (var index = 0; index < 8 && result.PendingWork; index++) result = await reader.PollAsync();
            Check.True(result.IsHistoryLimited);
            Check.Equal("response-a", result.Sample!.ResponseId);
            await Append(path, new byte[] { 0xc3, 0x28, (byte)'\n' });
            await Append(path, Encoding.UTF8.GetBytes(Response(ThreadA, "response-b", 20)));
            Check.Equal("response-b", (await reader.PollAsync()).Sample!.ResponseId);
        });

        asyncTest("output speed reader: startup is tail bounded and poll budget is enforced", async () =>
        {
            using var home = new TemporaryHome();
            var data = Header(ThreadA) + new string('x', 100000) + "\n" + Response(ThreadA, "recent", 20);
            home.Write(ThreadA, data);
            var options = SmallLimits();
            var reader = home.Reader(options);
            var result = await reader.PollAsync();
            Check.True(result.IsHistoryLimited);
            Check.True(result.BytesRead <= options.MaxBytesPerPoll);
            Check.True(result.BytesRead < data.Length / 2, "Startup must not scan full history.");
            Check.Equal("recent", result.Sample!.ResponseId);
        });

        asyncTest("output speed reader: discovery limits and recent-file selection are explicit", async () =>
        {
            using var home = new TemporaryHome();
            home.Write(ThreadA, Header(ThreadA) + Response(ThreadA, "older", 0));
            var latest = home.Write(ThreadB, Header(ThreadB) + Response(ThreadB, "newer", 20));
            File.SetLastWriteTimeUtc(latest, DateTime.UtcNow.AddSeconds(10));
            var reader = home.Reader(new() { DiscoveryInterval = TimeSpan.Zero, MaxTrackedFiles = 1 });
            var result = await reader.PollAsync();
            Check.Equal(1, result.TrackedFileCount);
            Check.True(result.IsHistoryLimited);
            Check.Equal("newer", result.Sample!.ResponseId);
        });

        asyncTest("output speed reader: content polling does not repeat directory discovery", async () =>
        {
            using var home = new TemporaryHome();
            var path = home.Write(ThreadA, Header(ThreadA) + Response(ThreadA, "first", 0));
            var reader = home.Reader(new() { DiscoveryInterval = TimeSpan.FromHours(1) });
            await reader.PollAsync();
            home.Write(ThreadB, Header(ThreadB) + Response(ThreadB, "not-yet-discovered", 50));
            await Append(path, Encoding.UTF8.GetBytes(Response(ThreadA, "appended", 20)));
            var result = await reader.PollAsync();
            Check.Equal(1, result.TrackedFileCount);
            Check.Equal("appended", result.Sample!.ResponseId);
        });

        asyncTest("output speed reader: unavailable file preserves last sample with truthful error status", async () =>
        {
            using var home = new TemporaryHome();
            var path = home.Write(ThreadA, Header(ThreadA) + Response(ThreadA, "response-a", 0));
            var reader = home.Reader();
            var first = await reader.PollAsync();
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var unavailable = await reader.PollAsync();
                Check.Equal(RolloutSpeedReadStatus.Unavailable, unavailable.Status);
                Check.True(unavailable.HasReadErrors);
                Check.Equal(first.Sample, unavailable.Sample);
            }
            Check.Equal(RolloutSpeedReadStatus.Ready, (await reader.PollAsync()).Status);
        });

        asyncTest("output speed reader: limited poll work rotates fairly across concurrent sessions", async () =>
        {
            using var home = new TemporaryHome();
            home.Write(ThreadA, Header(ThreadA) + new string('x', 20000) + "\n" + Response(ThreadA, "first", 0));
            home.Write(ThreadB, Header(ThreadB) + new string('x', 20000) + "\n" + Response(ThreadB, "latest", 20));
            var options = SmallLimits();
            var reader = home.Reader(options);
            var result = await reader.PollAsync();
            Check.True(result.BytesRead <= options.MaxBytesPerPoll);
            Check.True(result.PendingWork);
            result = await reader.PollAsync();
            Check.True(result.BytesRead <= options.MaxBytesPerPoll);
            Check.Equal("latest", result.Sample!.ResponseId);
        });

        asyncTest("output speed reader: cancellation is bounded and concurrent polls serialize", async () =>
        {
            using var home = new TemporaryHome();
            home.Write(ThreadA, Header(ThreadA) + Response(ThreadA, "response-a", 0));
            var reader = home.Reader();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(reader.PollAsync(cancelled.Token));
            var results = await Task.WhenAll(reader.PollAsync(), reader.PollAsync(), reader.PollAsync());
            Check.True(results.All(result => result.Sample?.ResponseId == "response-a"));
        });

        asyncTest("output speed reader: exact historical model before startup tail is recovered without changing TPS", async () =>
        {
            using var home = new TemporaryHome();
            const string turn = "long-turn";
            var content = Header(ThreadA) + Line(0, "turn_context", new { turn_id = turn, model = "gpt-historical" });
            for (var i = 0; i < 150; i++) content += Line(1, "event_msg", new { type = "agent_reasoning", text = new string('x', 80) });
            content += Usage(ThreadA, "boundary", 2, 100).Replace("turn-boundary", turn, StringComparison.Ordinal);
            content += Response(ThreadA, "latest", 3).Split('\n').Skip(1).Aggregate("", (all, line) => all + line + "\n")
                .Replace("turn-latest", turn, StringComparison.Ordinal).TrimEnd('\n') + "\n";
            var path = home.Write(ThreadA, content);
            var reader = home.Reader(SmallLimits());
            var result = await reader.PollAsync();
            Check.Null(result.Sample!.TokensPerSecond);
            for (var i = 0; i < 30 && result.Sample!.Model is null; i++)
            {
                result = await reader.PollAsync();
                Check.True(result.BytesRead <= SmallLimits().MaxBytesPerPoll);
            }
            Check.Equal("gpt-historical", result.Sample!.Model);
            Check.Equal(turn, result.Sample.TurnId);
            Check.Null(result.Sample.TokensPerSecond);
            // Model recovery may finish before the separate bounded response-history pass.
            for (var i = 0; i < 100 && result.CanCatchUp; i++)
            {
                result = await reader.PollAsync();
                Check.True(result.BytesRead <= SmallLimits().MaxBytesPerPoll);
            }
            Check.False(result.CanCatchUp);
            var idle = await reader.PollAsync();
            Check.True(idle.BytesRead <= 768, "Completed model and response-history lookups must not rescan history.");
            // A later response in the same uninterrupted turn inherits only the verified
            // historical context, not a current config value or another conversation.
            var next = Response(ThreadA, "next", 6).Split('\n').Skip(1).Aggregate("", (all, line) => all + line + "\n")
                .Replace("turn-next", turn, StringComparison.Ordinal).TrimEnd('\n') + "\n";
            await Append(path, Encoding.UTF8.GetBytes(next));
            Check.Equal("gpt-historical", (await reader.PollAsync()).Sample!.Model);
        });

        asyncTest("output speed reader: known mid-turn ambiguity cannot be overwritten by recovery", async () =>
        {
            using var home = new TemporaryHome();
            var content = Header(ThreadA) + Line(0, "turn_context", new { turn_id = "turn-response", model = "initial" }) +
                Line(1, "event_msg", new { type = "thread_settings_applied", thread_id = ThreadA, thread_settings = new { model = "changed" } }) +
                Response(ThreadA, "response", 2);
            home.Write(ThreadA, content);
            var reader = home.Reader();
            for (var i = 0; i < 3; i++)
            {
                var result = await reader.PollAsync();
                Check.Null(result.Sample!.TokensPerSecond);
                Check.Null(result.Sample.Model);
            }
        });

        asyncTest("output speed reader: conflicting recovered model labels keep speed but model unknown", async () =>
        {
            using var home = new TemporaryHome();
            var content = Header(ThreadA) + Line(0, "turn_context", new { turn_id = "long-turn", model = "model-a" });
            for (var i = 0; i < 150; i++) content += Line(1, "event_msg", new { type = "agent_reasoning", text = new string('x', 80) });
            content += Usage(ThreadA, "boundary", 2, 100).Replace("turn-boundary", "long-turn", StringComparison.Ordinal);
            content += Response(ThreadA, "same", 3).Split('\n').Skip(1).Aggregate("", (all, line) => all + line + "\n")
                .Replace("turn-same", "long-turn", StringComparison.Ordinal).TrimEnd('\n') + "\n";
            home.Write(ThreadA, content);
            home.Write(ThreadA, content.Replace("model-a", "model-b", StringComparison.Ordinal), archived: true);
            var reader = home.Reader(SmallLimits());
            var result = await reader.PollAsync();
            for (var i = 0; i < 40; i++) result = await reader.PollAsync();
            Check.Null(result.Sample!.TokensPerSecond);
            Check.Null(result.Sample.Model);
            Check.True(result.BytesRead <= 1536, "Conflicting completed lookups must remain cached.");
        });

        asyncTest("output speed reader: old archive model lookup cannot relabel a replaced response identity", async () =>
        {
            using var home = new TemporaryHome();
            var content = Header(ThreadA) + Line(0, "turn_context", new { turn_id = "old-turn", model = "old-model" });
            for (var i = 0; i < 150; i++) content += Line(1, "event_msg", new { type = "agent_reasoning", text = new string('x', 80) });
            content += Usage(ThreadA, "boundary", 2, 100).Replace("turn-boundary", "old-turn", StringComparison.Ordinal);
            content += Response(ThreadA, "same", 3).Split('\n').Skip(1).Aggregate("", (all, line) => all + line + "\n")
                .Replace("turn-same", "old-turn", StringComparison.Ordinal).TrimEnd('\n') + "\n";
            var active = home.Write(ThreadA, Header(ThreadA));
            var reader = home.Reader(SmallLimits());
            await reader.PollAsync(); // Initialize the active cursor before its later replacement.
            home.Write(ThreadA, content, archived: true);
            Check.Null((await reader.PollAsync()).Sample!.Model);
            // A changed prefix makes this a real observable replacement, rather than
            // indistinguishable append-only growth of the original header.
            File.WriteAllText(active, Line(10, "session_meta", new { id = ThreadA }) + Response(ThreadA, "same", 20)
                .Replace("\"model\":\"gpt-test\"", "\"model\":null", StringComparison.Ordinal));
            RolloutSpeedReadResult result = await reader.PollAsync();
            for (var i = 0; i < 15; i++) result = await reader.PollAsync();
            Check.Equal("turn-same", result.Sample!.TurnId);
            Check.Null(result.Sample.Model);
        });
    }

    private static RolloutSpeedReaderOptions SmallLimits() => new()
    {
        DiscoveryInterval = TimeSpan.Zero, MaxLineBytes = 1024, InitialTailBytes = 4096,
        MaxBytesPerFilePerPoll = 8192, MaxBytesPerPoll = 8192
    };

    private static string Header(string thread) => Line(0, "session_meta", new { id = thread });

    private static string Response(string thread, string response, int seconds, int tokens = 100, string text = "x")
    {
        var turn = "turn-" + response;
        var item = "item-" + response;
        return Line(seconds, "turn_context", new { turn_id = turn, model = "gpt-test" }) +
            Line(seconds + 2, "response_item", new { type = "message", role = "assistant", id = item,
                content = new[] { new { type = "output_text", text } } }) +
            Line(seconds + 2, "event_msg", new { type = "item_completed", thread_id = thread, turn_id = turn,
                item = new { type = "AgentMessage", id = item },
                started_at_ms = Epoch.AddSeconds(seconds).ToUnixTimeMilliseconds(),
                completed_at_ms = Epoch.AddSeconds(seconds + 2).ToUnixTimeMilliseconds() }) +
            Usage(thread, response, seconds + 2, tokens);
    }

    private static string Usage(string thread, string response, int seconds, int tokens) =>
        Line(seconds, "token_usage_record", new { thread_id = thread, turn_id = "turn-" + response,
            response_id = response, usage = new { output_tokens = tokens } });

    private static string Line(int seconds, string type, object payload) =>
        JsonSerializer.Serialize(new { timestamp = Epoch.AddSeconds(seconds).ToString("O"), type, payload },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";

    private static async Task Append(string path, byte[] bytes)
    {
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await stream.WriteAsync(bytes);
    }

    private sealed class TemporaryHome : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quota-rollout-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryHome() => Directory.CreateDirectory(Path);
        public RolloutSpeedReader Reader(RolloutSpeedReaderOptions? options = null) =>
            new(Path, options ?? new() { DiscoveryInterval = TimeSpan.Zero });
        public string Write(string thread, string content, bool archived = false)
        {
            var directory = System.IO.Path.Combine(Path, archived ? "archived_sessions" : "sessions");
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "rollout-2026-10-02T10-00-00-" + thread + ".jsonl");
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
