using System.Text;
using System.Text.Json;
using CodexQuota.Core;

static class LongTurnMetadataTests
{
    private const string Thread = "77777777-7777-4777-8777-777777777777";
    private const string Turn = "evidence-shaped-long-turn";
    private static readonly DateTimeOffset Epoch = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        asyncTest("long-turn metadata: 0.2.7 evidence-shaped 4,311,873-byte context distance recovers incrementally", async () =>
        {
            using var home = new TemporaryHome();
            var content = EvidenceTurn(4_311_873);
            var path = home.Write(content);
            var reader = new RolloutSpeedReader(home.Path);
            var result = await reader.PollAsync();
            Check.Null(result.Sample!.TokensPerSecond);
            Check.Null(result.Sample.Model);
            Check.True(result.BytesRead <= 512 * 1024);
            // The measured response stays selected while later harmless records arrive.
            // Its context is now 4.62 MiB behind EOF, as in the diagnostic evidence.
            var headerBytes = Encoding.UTF8.GetByteCount(Header());
            File.AppendAllText(path, Padding(4_844_422 - (content.Length - headerBytes)), new UTF8Encoding(false));
            Check.Equal(4_844_422L, new FileInfo(path).Length - headerBytes);
            for (var index = 0; index < 64; index++)
            {
                result = await reader.PollAsync();
                Check.True(result.BytesRead <= 512 * 1024, "Keep the existing per-file read budget, including rereads.");
                if (result.Sample!.TokensPerSecond is { } responseRate)
                    Check.Equal(303 / 11d, responseRate); // usage boundary at 1, completion record at 12.
                if (result.Sample.Model is not null) break;
            }
            Check.Equal("gpt-evidence", result.Sample!.Model);
            Check.Equal("xhigh", result.Sample.ReasoningEffort);
            Check.Equal(RolloutModelMetadataStatus.Known, result.ModelMetadataStatus);
        });

        asyncTest("long-turn metadata: model and response history each progress once within unchanged per-poll budgets", async () =>
        {
            using var home = new TemporaryHome();
            var content = EvidenceTurn(12 * 1024 * 1024);
            home.Write(content);
            var reader = new RolloutSpeedReader(home.Path);
            var result = await reader.PollAsync();
            Check.True(result.PendingWork);
            Check.Equal(RolloutModelMetadataStatus.PendingHistory, result.ModelMetadataStatus);
            long bytes = result.BytesRead;
            var polls = 1;
            while (result.ModelMetadataStatus == RolloutModelMetadataStatus.PendingHistory || result.CanCatchUp)
            {
                Check.True(++polls < 100);
                result = await reader.PollAsync();
                bytes += result.BytesRead;
                Check.True(result.BytesRead <= 512 * 1024);
                if (result.Sample!.TokensPerSecond is { } responseRate)
                    Check.Equal(303 / 11d, responseRate);
            }
            Check.Equal(303 / 11d, result.Sample!.TokensPerSecond!.Value);
            Check.True(polls > 16, "The lookup must not silently raise a per-poll budget.");
            Check.Equal("gpt-evidence", result.Sample!.Model);
            Check.Equal("xhigh", result.Sample.ReasoningEffort);
            Check.True(bytes < content.Length * 2.3, "One reverse metadata pass and one forward response-history pass may run; neither may restart or read unbounded history.");
            for (var index = 0; index < 3; index++)
            {
                var idle = await reader.PollAsync();
                Check.False(idle.PendingWork);
                Check.True(idle.BytesRead <= 768);
                Check.Equal(result.Sample, idle.Sample);
            }
        });

        asyncTest("long-turn metadata: configurable ceiling is explicit and terminal misses do not restart", async () =>
        {
            using var home = new TemporaryHome();
            var path = home.Write(EvidenceTurn(2 * 1024 * 1024));
            var reader = new RolloutSpeedReader(home.Path, new() { MaxModelHistoryBytes = 512 * 1024 });
            var result = await PollUntilSettled(reader);
            Check.Equal(RolloutModelMetadataStatus.HistoryLimit, result.ModelMetadataStatus);
            Check.Null(result.Sample!.Model);
            Check.Null(result.Sample.ReasoningEffort);
            Check.Null(result.Sample.TokensPerSecond);
            var idle = await reader.PollAsync();
            Check.False(idle.PendingWork);
            Check.True(idle.BytesRead <= 768);
            File.AppendAllText(path, Response("after-limit", 20), new UTF8Encoding(false));
            var next = await reader.PollAsync();
            Check.Equal("after-limit", next.Sample!.ResponseId);
            Check.Equal(RolloutModelMetadataStatus.HistoryLimit, next.ModelMetadataStatus);
            Check.True(next.BytesRead <= 4096, "A later same-turn sample reuses the cached terminal miss.");
            Check.False(next.PendingWork);
        });

        asyncTest("long-turn metadata: cancellation preserves pending progress and later recovery", async () =>
        {
            using var home = new TemporaryHome();
            home.Write(EvidenceTurn(2 * 1024 * 1024));
            var reader = new RolloutSpeedReader(home.Path);
            Check.True((await reader.PollAsync()).PendingWork);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(reader.PollAsync(cancelled.Token));
            var result = await PollUntilSettled(reader);
            Check.Equal("gpt-evidence", result.Sample!.Model);
            Check.Equal("xhigh", result.Sample.ReasoningEffort);
        });

        asyncTest("long-turn metadata: concurrent files retain round-robin fairness and global read cap", async () =>
        {
            using var home = new TemporaryHome();
            var content = EvidenceTurn(2 * 1024 * 1024);
            for (var index = 0; index < 6; index++)
            {
                var thread = "77777777-7777-4777-8777-77777777777" + index;
                // Distinct completion times keep this a fairness/recovery test.
                // Equal-time cross-thread ambiguity is covered by TurnAverageTests.
                home.Write((content + Response("latest-" + index, 20 + index)).Replace(Thread, thread, StringComparison.Ordinal), thread);
            }
            var reader = new RolloutSpeedReader(home.Path);
            var result = await reader.PollAsync();
            for (var index = 0; index < 40 && result.PendingWork; index++)
            {
                Check.True(result.BytesRead <= 2 * 1024 * 1024);
                result = await reader.PollAsync();
            }
            Check.Equal(6, result.TrackedFileCount);
            Check.False(result.PendingWork);
            Check.Equal("gpt-evidence", result.Sample!.Model);
            Check.Equal("xhigh", result.Sample.ReasoningEffort);
            Check.True((await reader.PollAsync()).BytesRead <= 6 * 768);
        });

        asyncTest("long-turn metadata: pending lookup is replaced after truncation and reader restart", async () =>
        {
            using var home = new TemporaryHome();
            var path = home.Write(EvidenceTurn(2 * 1024 * 1024));
            var reader = new RolloutSpeedReader(home.Path);
            Check.True((await reader.PollAsync()).PendingWork);
            var replacement = Header() + Line(0, "turn_context", new
                { turn_id = Turn, model = "replacement-model", effort = "low" }) + Response("replacement", 20);
            File.WriteAllText(path, replacement, new UTF8Encoding(false));
            var result = await PollUntilSettled(reader);
            Check.Equal("replacement", result.Sample!.ResponseId);
            Check.Equal("replacement-model", result.Sample.Model);
            Check.Equal("low", result.Sample.ReasoningEffort);
            var restarted = await new RolloutSpeedReader(home.Path).PollAsync();
            Check.Equal(result.Sample, restarted.Sample);
            Check.Equal(RolloutModelMetadataStatus.Known, restarted.ModelMetadataStatus);
        });

        asyncTest("long-turn metadata: historical peers survive later turn changes without restoring live parser", async () =>
        {
            using var home = new TemporaryHome();
            home.Write(EvidenceTurn(2 * 1024 * 1024) + Response("latest-peer", 20) +
                Line(40, "turn_context", new { turn_id = "next-turn", model = "next-model", effort = "low" }));
            var result = await PollUntilSettled(new RolloutSpeedReader(home.Path));
            Check.Equal("latest-peer", result.Sample!.ResponseId);
            Check.Equal("gpt-evidence", result.Sample.Model);
            Check.Equal("xhigh", result.Sample.ReasoningEffort);
            Check.Equal(303 / 18d, result.Sample.TokensPerSecond!.Value);
        });

        asyncTest("long-turn metadata: direct and recovered effort conflicts cannot relabel the speed", async () =>
        {
            using var home = new TemporaryHome();
            var content = EvidenceTurn(2 * 1024 * 1024);
            home.Write(content);
            home.Write(content.Replace("xhigh", "low", StringComparison.Ordinal), archived: true);
            var result = await PollUntilSettled(new RolloutSpeedReader(home.Path));
            Check.Equal("gpt-evidence", result.Sample!.Model);
            Check.Null(result.Sample.ReasoningEffort);
            Check.Equal(RolloutModelMetadataStatus.Conflict, result.ModelMetadataStatus);
            Check.Equal(303 / 11d, result.Sample.TokensPerSecond!.Value);
            Check.True((await new RolloutSpeedReader(home.Path).PollAsync()).PendingWork);
        });

        asyncTest("response history: old lifecycle timestamps cannot override the exact previous-completion boundary", async () =>
        {
            using var home = new TemporaryHome();
            home.Write(EvidenceTurn(2 * 1024 * 1024) + Response("earlier-start-peer", -1, duration: 40));
            var peer = await PollUntilSettled(new RolloutSpeedReader(home.Path));
            Check.Equal("earlier-start-peer", peer.Sample!.ResponseId);
            Check.Equal("gpt-evidence", peer.Sample.Model);
            Check.Equal("xhigh", peer.Sample.ReasoningEffort);
            Check.Equal(RolloutModelMetadataStatus.Known, peer.ModelMetadataStatus);
            Check.Equal(303 / 27d, peer.Sample.TokensPerSecond!.Value); // exact usage 12→39; lifecycle -1 is irrelevant.

            using var laterHome = new TemporaryHome();
            var path = laterHome.Write(EvidenceTurn(2 * 1024 * 1024));
            var reader = new RolloutSpeedReader(laterHome.Path);
            Check.Equal("gpt-evidence", (await PollUntilSettled(reader)).Sample!.Model);
            File.AppendAllText(path, Response("earlier-start-later", -1, duration: 50), new UTF8Encoding(false));
            var later = await PollUntilSettled(reader);
            Check.Equal("earlier-start-later", later.Sample!.ResponseId);
            Check.Equal("gpt-evidence", later.Sample.Model);
            Check.Equal("xhigh", later.Sample.ReasoningEffort);
            Check.Equal(RolloutModelMetadataStatus.Known, later.ModelMetadataStatus);
            Check.Equal(303 / 37d, later.Sample.TokensPerSecond!.Value); // exact usage 12→49.
        });

        asyncTest("long-turn metadata: damaged context and absent context have distinct cached terminal reasons", async () =>
        {
            foreach (var absent in new[] { false, true })
            {
                using var home = new TemporaryHome();
                var content = EvidenceTurn(2 * 1024 * 1024);
                var context = content.Split('\n')[1] + "\n";
                home.Write(content.Replace(context, absent ? Padding(context.Length) : "{broken}\n", StringComparison.Ordinal));
                var reader = new RolloutSpeedReader(home.Path);
                var result = await PollUntilSettled(reader);
                Check.Equal(absent ? RolloutModelMetadataStatus.NotFound : RolloutModelMetadataStatus.Boundary,
                    result.ModelMetadataStatus);
                Check.Null(result.Sample!.TokensPerSecond);
                Check.Null(result.Sample.Model);
                Check.True((await reader.PollAsync()).BytesRead <= 768);
            }
        });

        asyncTest("long-turn metadata: continuously appended records cannot starve historical progress", async () =>
        {
            using var home = new TemporaryHome();
            var path = home.Write(EvidenceTurn(2 * 1024 * 1024));
            var reader = new RolloutSpeedReader(home.Path);
            var result = await reader.PollAsync();
            for (var index = 0; index < 20 && result.Sample!.Model is null; index++)
            {
                File.AppendAllText(path, Padding(512 * 1024), new UTF8Encoding(false));
                result = await reader.PollAsync();
                Check.True(result.BytesRead <= 512 * 1024);
            }
            Check.Equal("gpt-evidence", result.Sample!.Model);
            Check.Equal("xhigh", result.Sample.ReasoningEffort);
            Check.True(result.PendingWork, "Forward backlog must remain visible after history recovers.");
        });

        asyncTest("long-turn metadata: direct known copy cannot inherit another copy's stale missing status", async () =>
        {
            using var home = new TemporaryHome();
            var content = EvidenceTurn(2 * 1024 * 1024);
            var context = content.Split('\n')[1] + "\n";
            home.Write(content.Replace(context, "{broken}\n", StringComparison.Ordinal));
            var reader = new RolloutSpeedReader(home.Path, new() { DiscoveryInterval = TimeSpan.Zero });
            Check.Equal(RolloutModelMetadataStatus.PendingHistory, (await reader.PollAsync()).ModelMetadataStatus);
            home.Write(Header() + context + Response("measured", 2), archived: true);
            var result = await PollUntilSettled(reader);
            Check.Equal("gpt-evidence", result.Sample!.Model);
            Check.Equal("xhigh", result.Sample.ReasoningEffort);
            Check.Equal(RolloutModelMetadataStatus.Known, result.ModelMetadataStatus);
            Check.Null(result.Sample.TokensPerSecond);
        });

        asyncTest("long-turn metadata: conflicting direct effort cannot be revived by a later incomplete copy", async () =>
        {
            using var home = new TemporaryHome();
            home.Write(EvidenceTurn(2 * 1024 * 1024));
            var reader = new RolloutSpeedReader(home.Path, new() { DiscoveryInterval = TimeSpan.Zero });
            Check.Equal("xhigh", (await PollUntilSettled(reader)).Sample!.ReasoningEffort);
            var direct = Header() + Line(0, "turn_context", new { turn_id = Turn, model = "gpt-evidence", effort = "low" }) + Response("measured", 2);
            home.Write(direct, archived: true);
            Check.Equal(RolloutModelMetadataStatus.Conflict, (await PollUntilSettled(reader)).ModelMetadataStatus);
            home.Write(Header() + Line(0, "turn_context", new { turn_id = Turn, model = "gpt-evidence" }) + Response("measured", 2), prefix: "copy-three");
            var result = await PollUntilSettled(reader);
            Check.Equal("gpt-evidence", result.Sample!.Model);
            Check.Null(result.Sample.ReasoningEffort);
            Check.Equal(RolloutModelMetadataStatus.Conflict, result.ModelMetadataStatus);
            Check.Equal(303 / 11d, result.Sample.TokensPerSecond!.Value);
        });

        asyncTest("long-turn metadata: incomplete direct copy preserves a verified effort for the exact response", async () =>
        {
            using var home = new TemporaryHome();
            home.Write(Header() + Line(0, "turn_context", new { turn_id = Turn, model = "gpt-evidence", effort = "high" }) + Response("measured", 2));
            var reader = new RolloutSpeedReader(home.Path, new() { DiscoveryInterval = TimeSpan.Zero });
            Check.Equal("high", (await reader.PollAsync()).Sample!.ReasoningEffort);
            home.Write(Header() + Line(0, "turn_context", new { turn_id = Turn, model = "gpt-evidence" }) + Response("measured", 2), archived: true);
            var result = await PollUntilSettled(reader);
            Check.Equal("high", result.Sample!.ReasoningEffort);
            Check.Equal(RolloutModelMetadataStatus.Known, result.ModelMetadataStatus);
        });

        asyncTest("long-turn metadata: same-response effort replay clears only effort permanently", async () =>
        {
            using var home = new TemporaryHome();
            var path = home.Write(Header() + Line(0, "turn_context", new { turn_id = Turn, model = "gpt-evidence", effort = "high" }) + Response("measured", 2));
            var reader = new RolloutSpeedReader(home.Path);
            var original = (await reader.PollAsync()).Sample!;
            foreach (var effort in new[] { "low", "high" })
            {
                File.AppendAllText(path, Line(0, "turn_context", new { turn_id = Turn, model = "gpt-evidence", effort }) + Response("measured", 2), new UTF8Encoding(false));
                var result = await PollUntilSettled(reader);
                Check.Equal(original.OutputTokens, result.Sample!.OutputTokens);
                Check.Equal(original.StartedAt, result.Sample.StartedAt);
                Check.Equal(original.CompletedAt, result.Sample.CompletedAt);
                Check.Null(result.Sample.TokensPerSecond);
                Check.Equal("gpt-evidence", result.Sample.Model);
                Check.Null(result.Sample.ReasoningEffort);
                Check.Equal(RolloutModelMetadataStatus.Conflict, result.ModelMetadataStatus);
            }
        });

        asyncTest("long-turn metadata: contradictory historical models cannot be revived by a direct copy", async () =>
        {
            foreach (var badFirst in new[] { true, false })
            {
                using var home = new TemporaryHome();
                var context = Line(0, "turn_context", new { turn_id = Turn, model = "gpt-evidence", effort = "high" });
                var direct = Header() + context + Response("measured", 2);
                var conflicting = Header() + context + Padding(1024 * 1024) +
                    Line(1, "turn_context", new { turn_id = Turn, model = "contradictory-model", effort = "high" }) +
                    Padding(1024 * 1024) + Usage("boundary", 1, 0) + Response("measured", 2);
                home.Write(badFirst ? conflicting : direct);
                var reader = new RolloutSpeedReader(home.Path, new() { DiscoveryInterval = TimeSpan.Zero });
                await PollUntilSettled(reader);
                home.Write(badFirst ? direct : conflicting, archived: true);
                var result = await PollUntilSettled(reader);
                Check.Null(result.Sample!.Model);
                Check.Null(result.Sample.ReasoningEffort);
                Check.Equal(RolloutModelMetadataStatus.Conflict, result.ModelMetadataStatus);
                Check.Null(result.Sample.TokensPerSecond);
            }
        });

        test("long-turn metadata: invalid history ceilings are rejected", () =>
        {
            foreach (var limit in new[] { 0L, -1L, 256L * 1024 * 1024 + 1, long.MaxValue })
                Check.Throws<ArgumentOutOfRangeException>(() => new RolloutSpeedReader("unused", new() { MaxModelHistoryBytes = limit }));
        });
    }

    private static string EvidenceTurn(int contextToSealBytes)
    {
        var context = Line(0, "turn_context", new { turn_id = Turn, thread_id = Thread, model = "gpt-evidence", effort = "xhigh",
            collaboration_mode = new { settings = new { reasoning_effort = "xhigh" } } });
        var start = Line(0, "event_msg", new { type = "task_started", turn_id = Turn, thread_id = Thread });
        var boundary = Usage("boundary", 1, 0);
        var output = Response("measured", 2);
        const int contextToStartBytes = 118_374;
        var text = Header() + context + Padding(contextToStartBytes - context.Length) + start +
            Padding(contextToSealBytes - contextToStartBytes - start.Length - boundary.Length - output.Length) + boundary + output;
        Check.Equal(contextToSealBytes, Encoding.UTF8.GetByteCount(text) - Encoding.UTF8.GetByteCount(Header()));
        return text;
    }

    private static string Response(string response, int seconds, int duration = 10) =>
        Line(seconds + duration, "response_item", new { type = "message", role = "assistant", id = response + "-item" }) +
        Line(seconds + duration, "event_msg", new { type = "item_completed", thread_id = Thread, turn_id = Turn,
            item = new { type = "AgentMessage", id = response + "-item" },
            started_at_ms = Epoch.AddSeconds(seconds).ToUnixTimeMilliseconds(),
            completed_at_ms = Epoch.AddSeconds(seconds + duration).ToUnixTimeMilliseconds() }) + Usage(response, seconds + duration, 303);

    private static async Task<RolloutSpeedReadResult> PollUntilSettled(RolloutSpeedReader reader)
    {
        var result = await reader.PollAsync();
        for (var index = 0; index < 100 && result.PendingWork; index++)
        {
            Check.True(result.BytesRead <= 2 * 1024 * 1024);
            result = await reader.PollAsync();
        }
        Check.False(result.PendingWork, "Bounded synthetic histories must reach a terminal status.");
        return result;
    }

    private static string Padding(int length)
    {
        var builder = new StringBuilder(length);
        var plain = Line(1, "event_msg", new { type = "agent_reasoning", text = "" });
        while (length > 0)
        {
            var size = Math.Min(16384, length);
            if (length - size > 0 && length - size < plain.Length) size -= plain.Length;
            Check.True(size >= plain.Length, "Padding must contain whole small valid timestamped records.");
            builder.Append(Line(1, "event_msg", new { type = "agent_reasoning", text = new string('x', size - plain.Length) }));
            length -= size;
        }
        return builder.ToString();
    }

    private static string Header() => Line(0, "session_meta", new { id = Thread });
    private static string Usage(string response, int seconds, int tokens) => Line(seconds, "token_usage_record", new
    { thread_id = Thread, turn_id = Turn, response_id = response, usage = new { output_tokens = tokens } });
    private static string Line(int seconds, string type, object payload) =>
        JsonSerializer.Serialize(new { timestamp = Epoch.AddSeconds(seconds).ToString("O"), type, payload }) + "\n";

    private sealed class TemporaryHome : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quota-long-turn-tests-" + Guid.NewGuid().ToString("N"));
        public string Write(string content, string thread = Thread, bool archived = false, string prefix = "2026-10-02T10-00-00")
        {
            var directory = System.IO.Path.Combine(Path, archived ? "archived_sessions" : "sessions");
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "rollout-" + prefix + "-" + thread + ".jsonl");
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
