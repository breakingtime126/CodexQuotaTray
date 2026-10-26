using System.Text;
using System.Text.Json;
using CodexQuota.Core;

static class ColdStartMetadataTests
{
    private const string Thread = "88888888-8888-4888-8888-888888888888";
    private const string Turn = "synthetic-cold-turn";
    private static readonly DateTimeOffset Epoch = new(2026, 10, 2, 7, 0, 0, TimeSpan.Zero);

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        test("cold metadata: exact-sized 146714-byte FileChange and matching settings recover nearest context", () =>
        {
            // Synthetic bodies only. These offsets and lengths reproduce the cold-start failure.
            var text = new StringBuilder(Header());
            PadTo(text, 22913);
            text.Append(Line(0, "event_msg", new { type = "task_started", thread_id = Thread, turn_id = Turn }));
            PadTo(text, 106651);
            text.Append(Context());
            PadTo(text, 4901592);
            text.Append(Sized(Context(8), 2419));
            text.Append(Sized(Settings(8), 2224));
            PadTo(text, 6513736);
            text.Append(LargeItem(146714));
            PadTo(text, 6723764);
            text.Append(Sized(Line(40, "token_usage_record", new { thread_id = Thread, turn_id = Turn,
                response_id = "synthetic-response", usage = new { output_tokens = 2275 } }), 907));
            Check.Equal(6724671, text.Length);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text.ToString()));
            var recovery = new RolloutModelRecovery(Thread, Turn, Epoch.AddSeconds(30), stream.Length);
            while (!recovery.IsComplete)
                Check.True(recovery.Scan(stream, RolloutModelRecovery.MaximumWindowBytes) <= RolloutModelRecovery.MaximumWindowBytes);
            Check.Equal("gpt-6.1-sol", recovery.ResultModel);
            Check.Equal("medium", recovery.ResultReasoningEffort);
            Check.Equal(RolloutModelMetadataStatus.Known, recovery.Status);
            Check.Equal(4901592L, recovery.Offset);
        });

        test("cold metadata: oversized output never excuses malformed or duplicate metadata", () =>
        {
            var item = LargeItem(146714);
            foreach (var bad in new[]
            {
                item.Replace("\"type\":\"event_msg\"", "\"type\":\"session_meta\",\"type\":\"event_msg\""),
                item.Replace("\"type\":\"item_completed\"", "\"type\":\"thread_settings_applied\",\"type\":\"item_completed\""),
                item.Replace("\"thread_id\":", "\"thread_id\":\"foreign\",\"thread_id\":"),
                item.Replace("\"turn_id\":", "\"turn_id\":\"foreign\",\"turn_id\":"),
                item.Replace("\"payload\":", "\"payload\":{},\"payload\":"),
                item.Replace("\"item\":", "\"item\":{},\"item\":"),
                item.Replace("\"type\":\"FileChange\"", "\"type\":\"AgentMessage\",\"type\":\"FileChange\""),
                item.Replace(Thread, "foreign"),
                item.Replace(Turn, "foreign"),
                item.TrimEnd('\n')[..^1] + "\n",
                Sized(Context(), 146714),
                Sized(Settings(), 146714),
                Sized(Header(), 146714)
            })
            {
                var recovery = Recover(Context() + bad);
                Check.Null(recovery.ResultModel);
                Check.Equal(RolloutModelMetadataStatus.Boundary, recovery.Status);
            }
        });

        test("cold metadata: tool output beyond the existing scan window stays an explicit boundary", () =>
        {
            var recovery = Recover(Context() + LargeItem(RolloutModelRecovery.MaximumWindowBytes + 2));
            Check.Null(recovery.ResultModel);
            Check.Equal(RolloutModelMetadataStatus.Boundary, recovery.Status);
        });

        test("cold metadata: matching scoped settings echo is evidence but a model change remains conflict", () =>
        {
            Check.Equal("gpt-6.1-sol", Recover(Context() + Settings()).ResultModel);
            var changed = Recover(Context() + Settings(model: "gpt-different"));
            Check.Null(changed.ResultModel);
            Check.Equal(RolloutModelMetadataStatus.Conflict, changed.Status);
            Check.Null(Recover(Context() + Settings().Replace(Thread, "foreign")).ResultModel);
            Check.Null(Recover(Context() + Settings(seconds: 40)).ResultModel);
            Check.Null(Recover(Context() + Settings().Replace("\"thread_settings\":", "\"thread_settings\":{},\"thread_settings\":")).ResultModel);
        });

        test("cold metadata: settings effort mismatch does not invent effort or erase proven model", () =>
        {
            var recovery = Recover(Context() + Settings(effort: "high"));
            Check.Equal("gpt-6.1-sol", recovery.ResultModel);
            Check.Null(recovery.ResultReasoningEffort);
            Check.Equal(RolloutModelMetadataStatus.Conflict, recovery.Status);
        });

        asyncTest("cold metadata: tiny configured file budgets terminate oversized lookups without starvation", async () =>
        {
            foreach (var toolBytes in new[] { 1500, 8000 })
            {
                var home = Path.Combine(Path.GetTempPath(), "quota-cold-small-budget-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(home, "sessions"));
                try
                {
                    var text = new StringBuilder(Header() + Context() + Settings() + LargeItem(toolBytes));
                    PadTo(text, 16000, 900);
                    text.Append(Response());
                    File.WriteAllText(Path.Combine(home, "sessions", "rollout-2026-10-02-" + Thread + ".jsonl"), text.ToString());
                    var reader = new RolloutSpeedReader(home, new()
                    { InitialTailBytes = 4096, MaxLineBytes = 1024, MaxBytesPerFilePerPoll = 4096, MaxBytesPerPoll = 4096 });
                    var result = await reader.PollAsync();
                    for (var index = 0; index < 20 && result.PendingWork; index++)
                    {
                        Check.True(result.BytesRead <= 4096);
                        result = await reader.PollAsync();
                    }
                    Check.False(result.PendingWork);
                    // The bounded streaming history replay can now validate a large
                    // tool body even when the older reverse lookup hits its window cap.
                    Check.Equal(RolloutModelMetadataStatus.Known, result.ModelMetadataStatus);
                    Check.Equal("gpt-6.1-sol", result.Sample!.Model);
                }
                finally { Directory.Delete(home, true); }
            }
        });

        asyncTest("cold metadata: displayed pending sample has priority while 24 files keep making progress", async () =>
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var home = Path.Combine(Path.GetTempPath(), "quota-cold-metadata-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(home, "sessions"));
            try
            {
                var text = new StringBuilder(Header() + Context() + Settings());
                PadTo(text, 2 * 1024 * 1024);
                text.Append(Response());
                for (var index = 0; index < 24; index++)
                {
                    var thread = index == 0 ? Thread : "88888888-8888-4888-8888-" + index.ToString("D12");
                    var content = text.ToString().Replace(Thread, thread);
                    if (index != 0) content = content.Replace(Epoch.AddSeconds(40).ToString("O"), Epoch.AddSeconds(35).ToString("O"))
                        .Replace(Epoch.AddSeconds(40).ToUnixTimeMilliseconds().ToString(), Epoch.AddSeconds(35).ToUnixTimeMilliseconds().ToString());
                    var path = Path.Combine(home, "sessions", "rollout-2026-10-02-" + thread + ".jsonl");
                    File.WriteAllText(path, content, new UTF8Encoding(false));
                    File.SetLastWriteTimeUtc(path, Epoch.AddSeconds(100 - index).UtcDateTime);
                }
                var reader = new RolloutSpeedReader(home);
                var result = await reader.PollAsync();
                Check.Equal(Thread, result.Sample!.ThreadId);
                Check.Equal(RolloutModelMetadataStatus.PendingHistory, result.ModelMetadataStatus);
                // Forward appends, exact-response replay, and model recovery share the unchanged budget.
                for (var poll = 0; poll < 12 && result.Sample!.Model is null; poll++)
                {
                    result = await reader.PollAsync();
                    Check.True(result.BytesRead <= 2 * 1024 * 1024);
                }
                Check.Equal("gpt-6.1-sol", result.Sample!.Model);
                for (var poll = 0; poll < 80 && result.PendingWork; poll++) result = await reader.PollAsync();
                Check.False(result.PendingWork, "Other files must keep a fair share after prioritizing the displayed response.");
                Check.Equal(24, result.TrackedFileCount);
                Console.WriteLine($"24-file cold history fixture: {elapsed.ElapsedMilliseconds} ms.");
            }
            finally { Directory.Delete(home, true); }
        });
    }

    private static RolloutModelRecovery Recover(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Header() + text));
        var recovery = new RolloutModelRecovery(Thread, Turn, Epoch.AddSeconds(30), stream.Length);
        while (!recovery.IsComplete) recovery.Scan(stream, RolloutModelRecovery.MaximumWindowBytes);
        return recovery;
    }

    private static string Header() => Line(0, "session_meta", new { id = Thread });
    private static string Context(int seconds = 0) => Line(seconds, "turn_context", new
    { turn_id = Turn, model = "gpt-6.1-sol", effort = "medium" });
    private static string Settings(int seconds = 0, string model = "gpt-6.1-sol", string effort = "medium") =>
        Line(seconds, "event_msg", new { type = "thread_settings_applied", thread_id = Thread,
            thread_settings = new { model, reasoning_effort = effort } });
    private static string LargeItem(int bytes) => Sized(Line(20, "event_msg", new
    { type = "item_completed", thread_id = Thread, turn_id = Turn, item = new { type = "FileChange", id = "synthetic-tool" } }), bytes);
    private static string Response() =>
        Line(25, "token_usage_record", new { thread_id = Thread, turn_id = Turn, response_id = "boundary", usage = new { output_tokens = 0 } }) +
        Line(40, "response_item", new { type = "message", role = "assistant", id = "synthetic-item" }) +
        Line(40, "event_msg", new { type = "item_completed", thread_id = Thread, turn_id = Turn,
            item = new { type = "AgentMessage", id = "synthetic-item" },
            started_at_ms = Epoch.AddSeconds(30).ToUnixTimeMilliseconds(), completed_at_ms = Epoch.AddSeconds(40).ToUnixTimeMilliseconds() }) +
        Line(40, "token_usage_record", new { thread_id = Thread, turn_id = Turn, response_id = "synthetic-response", usage = new { output_tokens = 250 } });
    private static string Line(int seconds, string type, object payload) =>
        JsonSerializer.Serialize(new { timestamp = Epoch.AddSeconds(seconds).ToString("O"), type, payload }) + "\n";
    private static string Sized(string line, int length)
    {
        const string field = ",\"synthetic_body\":\"\"";
        var padding = length - line.Length - field.Length;
        Check.True(padding >= 0);
        return line[..^2] + ",\"synthetic_body\":\"" + new string('x', padding) + "\"}\n";
    }
    private static void PadTo(StringBuilder builder, int length, int maxLine = 16000)
    {
        var plain = Line(1, "event_msg", new { type = "agent_reasoning", text = "" });
        while (builder.Length < length)
        {
            var left = length - builder.Length;
            var count = Math.Min(maxLine, left);
            if (left - count > 0 && left - count < plain.Length + 20) count -= plain.Length + 20;
            builder.Append(Sized(plain, count));
        }
        Check.Equal(length, builder.Length);
    }
}
