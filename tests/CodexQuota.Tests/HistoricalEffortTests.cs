using System.Text;
using System.Text.Json;
using CodexQuota.Core;

internal static class HistoricalEffortTests
{
    private const string Owner = "88888888-8888-4888-8888-888888888888";
    private const string OtherOwner = "99999999-9999-4999-8999-999999999999";
    private const string Turn = "historical-effort-turn";
    private const string Model = "gpt-historical";
    private static readonly DateTimeOffset Epoch = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    // Program.cs registers this suite with HistoricalEffortTests.Register(Test).
    public static void Register(Action<string, Action> test)
    {
        foreach (var effort in new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra", "persistent", "custom" })
            test($"historical effort: supported {effort} is exact in both schema locations", () =>
            {
                foreach (var fields in new[] { TopEffort(effort), NestedEffort(effort), BothEfforts(effort) })
                {
                    var sample = Measure(Ready(Context(fields)));
                    AssertSample(sample, "response", Model, effort);
                    var recovered = Recover(Context(fields) + Usage("response", 12000));
                    Check.Equal(Model, recovered.ResultModel);
                    Check.Equal(effort, recovered.ResultReasoningEffort);
                    Check.Equal(At(500), recovered.ContextAt);
                }
            });

        test("historical effort: missing effort never invents a medium default", () =>
        {
            foreach (var fields in new[]
            {
                "", "\"collaboration_mode\":{}", "\"collaboration_mode\":{\"settings\":{}}",
                "\"settings\":{\"reasoning_effort\":\"medium\"}"
            })
            {
                AssertSample(Measure(Ready(Context(fields))), "response", Model, null);
                var recovered = Recover(Context(fields) + Usage("response", 12000));
                Check.Equal(Model, recovered.ResultModel);
                Check.Null(recovered.ResultReasoningEffort);
            }
        });

        test("historical effort: disagreement makes only effort unknown", () =>
        {
            foreach (var fields in new[]
            {
                TopEffort("low") + "," + NestedEffort("xhigh"),
                TopEffort("none") + "," + NestedEffort("medium")
            }) AssertUnknownEffort(fields);
        });

        foreach (var invalid in new[] { "null", "42", "true", "[]", "{}", "\"\"", "\"HIGH\"", "\" high \"", "\"superhigh\"", "\"high\\n\"" })
            test($"historical effort: invalid effort value {invalid} never spoofs a valid sibling", () =>
            {
                AssertUnknownEffort("\"effort\":" + invalid);
                AssertUnknownEffort("\"collaboration_mode\":{\"settings\":{\"reasoning_effort\":" + invalid + "}}");
                AssertUnknownEffort("\"effort\":" + invalid + "," + NestedEffort("high"));
                AssertUnknownEffort(TopEffort("high") + ",\"collaboration_mode\":{\"settings\":{\"reasoning_effort\":" + invalid + "}}");
            });

        test("historical effort: malformed collaboration containers never borrow top-level effort", () =>
        {
            foreach (var invalid in new[] { "null", "42", "true", "[]", "\"xhigh\"" })
            {
                AssertUnknownEffort(TopEffort("xhigh") + ",\"collaboration_mode\":" + invalid);
                AssertUnknownEffort(TopEffort("xhigh") + ",\"collaboration_mode\":{\"settings\":" + invalid + "}");
            }
        });

        test("historical effort: duplicate evidence at every nesting level is unknown", () =>
        {
            foreach (var fields in new[]
            {
                "\"effort\":\"high\",\"effort\":\"high\"",
                "\"effort\":\"low\",\"effort\":\"high\"",
                "\"collaboration_mode\":{\"settings\":{\"reasoning_effort\":\"high\",\"reasoning_effort\":\"high\"}}",
                "\"collaboration_mode\":{\"settings\":{\"reasoning_effort\":\"low\",\"reasoning_effort\":\"high\"}}",
                "\"collaboration_mode\":{\"settings\":{},\"settings\":{\"reasoning_effort\":\"high\"}}",
                "\"collaboration_mode\":{},\"collaboration_mode\":{\"settings\":{\"reasoning_effort\":\"high\"}}",
                TopEffort("high") + ",\"collaboration_mode\":{},\"collaboration_mode\":{}"
            }) AssertUnknownEffort(fields);
        });

        test("historical effort: recovery restores a model-effort pair for the next same-turn sample", () =>
        {
            var parser = Ready();
            parser.ResetPending();
            parser.Feed(Usage("tail-boundary", 500));
            var original = Measure(parser);
            AssertSample(original, "response", null, null);
            var recovered = Recover(Context(BothEfforts("xhigh")) + Usage("response", 12000));
            Check.Equal(Model, recovered.ResultModel);
            Check.Equal("xhigh", recovered.ResultReasoningEffort);

            var epoch = parser.ModelEpoch;
            Check.False(parser.TryRestoreModel("another-turn", Model, epoch, recovered.ResultReasoningEffort));
            Check.False(parser.TryRestoreModel(Turn, Model, epoch - 1, recovered.ResultReasoningEffort));
            Check.True(parser.TryRestoreModel(Turn, recovered.ResultModel!, epoch, recovered.ResultReasoningEffort));
            var next = Measure(parser, "next-response", start: 20000);
            AssertSample(next, "next-response", Model, "xhigh", start: 20000);
            AssertSample(original, "response", null, null);
        });

        test("historical effort: legacy model-only restore remains compatible without invented effort", () =>
        {
            var parser = Ready();
            parser.ResetPending();
            parser.Feed(Usage("tail-boundary", 500));
            AssertSample(Measure(parser), "response", null, null);
            Check.True(parser.TryRestoreModel(Turn, Model, parser.ModelEpoch));
            AssertSample(Measure(parser, "next-response", start: 20000), "next-response", Model, null, start: 20000);
        });

        test("historical effort: same-model live settings change cannot be undone by frozen contexts", () =>
        {
            foreach (var effortField in new[] { "reasoning_effort", "effort" })
            {
                var parser = Ready(Context(BothEfforts("low")));
                var original = Measure(parser);
                AssertSample(original, "response", Model, "low");
                var oldEpoch = parser.ModelEpoch;
                parser.Feed(Settings(OtherOwner, "high", at: 13000, effortField: effortField));
                parser.Feed(Settings(Owner, "high", at: 14000, effortField: effortField));
                parser.Feed(Context(BothEfforts("low"), at: 15000));
                parser.Feed(Context(BothEfforts("low"), at: 16000));
                Check.False(parser.TryRestoreModel(Turn, Model, oldEpoch, "low"));
                var next = Measure(parser, "after-settings", start: 20000);
                AssertSample(next, "after-settings", Model, null, start: 20000);
                AssertSample(original, "response", Model, "low");

                parser.Feed(Context(BothEfforts("high"), turn: "new-turn", at: 25000));
                AssertSample(Measure(parser, "new-turn-response", start: 30000, turn: "new-turn"),
                    "new-turn-response", Model, "high", start: 30000, turn: "new-turn");
            }
        });

        test("historical effort: foreign settings do not rewrite an owned historical label", () =>
        {
            var parser = Ready(Context(BothEfforts("low")));
            parser.Feed(Settings(OtherOwner, "high", at: 600));
            AssertSample(Measure(parser), "response", Model, "low");
        });

        test("historical effort: repeated contexts must agree before recovery publishes effort", () =>
        {
            var equal = Recover(Context(BothEfforts("xhigh")) + Context(BothEfforts("xhigh"), at: 700) + Usage("response", 12000));
            Check.Equal(Model, equal.ResultModel);
            Check.Equal("xhigh", equal.ResultReasoningEffort);
            Check.Equal(At(700), equal.ContextAt);

            foreach (var older in new[] { TopEffort("low"), "", "\"effort\":42" })
            {
                var result = Recover(Context(older) + Context(BothEfforts("xhigh"), at: 700) + Usage("response", 12000));
                Check.Equal(Model, result.ResultModel);
                Check.Null(result.ResultReasoningEffort);
            }
        });

        test("historical effort: same-model settings preserve model but conflicting effort and foreign scope stay unknown", () =>
        {
            var own = Recover(Context(BothEfforts("low")) + Settings(Owner, "high", 600) +
                Context(BothEfforts("low"), at: 700) + Usage("response", 12000));
            Check.Equal(Model, own.ResultModel);
            Check.Null(own.ResultReasoningEffort);
            Check.Equal(RolloutModelMetadataStatus.Conflict, own.Status);
            foreach (var barrier in new[]
            {
                Settings(OtherOwner, "high", 600),
                Record("event_msg", new { type = "thread_settings_applied", thread_settings = new { model = Model, reasoning_effort = "high" } }, 600)
            })
            {
                var result = Recover(Context(BothEfforts("low")) + barrier + Context(BothEfforts("low"), at: 700) + Usage("response", 12000));
                Check.Null(result.ResultModel);
                Check.Null(result.ResultReasoningEffort);
                Check.Null(result.ContextAt);
            }
        });

        test("historical effort: future contexts cannot supply or alter a completed sample", () =>
        {
            var parser = Ready(Context(BothEfforts("low")));
            var original = Measure(parser);
            parser.Feed(Context(BothEfforts("high"), at: 15000));
            AssertSample(original, "response", Model, "low");
            AssertSample(parser.LatestSample, "response", Model, "low");

            var reordered = Ready(Context(BothEfforts("low")));
            reordered.Feed(Context(BothEfforts("high"), at: 11000));
            AssertSample(Measure(reordered), "response", null, null);

            var historical = Header(Owner) + Context(BothEfforts("low")) + Usage("response", 12000);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(historical + Context(BothEfforts("high"), at: 15000)));
            var recovered = new RolloutModelRecovery(Owner, Turn, At(10000), Encoding.UTF8.GetByteCount(historical));
            ScanAll(recovered, stream);
            Check.Equal(Model, recovered.ResultModel);
            Check.Equal("low", recovered.ResultReasoningEffort);

            var future = Recover(Context(BothEfforts("low")) + Context(BothEfforts("high"), at: 11000) + Usage("response", 12000));
            Check.Null(future.ResultModel);
            Check.Null(future.ResultReasoningEffort);
        });

        test("historical effort: wrong turn or thread never labels an owned sample", () =>
        {
            foreach (var context in new[]
            {
                Context(BothEfforts("high"), thread: OtherOwner),
                Context(BothEfforts("high"), turn: "wrong-turn")
            })
            {
                var parser = Ready(context);
                parser.Feed(Usage("owned-boundary", 5000));
                AssertSample(Measure(parser), "response", null, null);
                var recovered = Recover(context + Usage("response", 12000));
                Check.Null(recovered.ResultModel);
                Check.Null(recovered.ResultReasoningEffort);
            }
        });

        test("historical effort: malformed records and session ownership boundaries block recovery", () =>
        {
            foreach (var barrier in new[]
            {
                "{\n", "null\n", "[]\n", Header(OtherOwner),
                Record("event_msg", new { type = "task_started", thread_id = OtherOwner, turn_id = Turn }, 600),
                Record("event_msg", new { type = "task_started", thread_id = Owner, turn_id = "wrong-turn" }, 600)
            })
            {
                var recovered = Recover(Context(BothEfforts("high")) + barrier + Usage("response", 12000));
                Check.Null(recovered.ResultModel);
                Check.Null(recovered.ResultReasoningEffort);
            }
        });

        test("historical effort: malformed input clears effort as well as model for later samples", () =>
        {
            foreach (var malformed in new[] { "{", "null", "[]" })
            {
                var parser = Ready(Context(BothEfforts("high")));
                parser.Feed(malformed);
                parser.Feed(Usage("fresh-boundary", 5000));
                AssertSample(Measure(parser), "response", null, null);
            }
        });

        test("historical effort: recovery without a proven owner boundary never publishes metadata", () =>
        {
            foreach (var prefix in new[] { "", Header(OtherOwner) })
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(prefix + Context(BothEfforts("high")) + Usage("response", 12000)));
                var result = new RolloutModelRecovery(Owner, Turn, At(10000), stream.Length);
                ScanAll(result, stream);
                Check.Null(result.ResultModel);
                Check.Null(result.ResultReasoningEffort);
            }
        });
    }

    private static void AssertUnknownEffort(string fields)
    {
        AssertSample(Measure(Ready(Context(fields))), "response", Model, null);
        var recovered = Recover(Context(fields) + Usage("response", 12000));
        Check.Equal(Model, recovered.ResultModel);
        Check.Null(recovered.ResultReasoningEffort);
    }

    private static RolloutSpeedParser Ready(string? context = null)
    {
        var parser = new RolloutSpeedParser(Owner);
        parser.Feed(Header(Owner));
        if (context is not null) parser.Feed(context);
        return parser;
    }

    private static OutputSpeedSample? Measure(RolloutSpeedParser parser, string response = "response", int start = 10000, string turn = Turn)
    {
        var id = response + "-item";
        parser.Feed(Record("event_msg", new { type = "item_completed", thread_id = Owner, turn_id = turn,
            item = new { id, type = "Reasoning" }, started_at_ms = At(start).ToUnixTimeMilliseconds(),
            completed_at_ms = At(start + 1000).ToUnixTimeMilliseconds() }, start + 1010));
        parser.Feed(Record("response_item", new { type = "reasoning", id,
            internal_chat_message_metadata_passthrough = new { turn_id = turn } }, start + 1010));
        return parser.Feed(Usage(response, start + 2000, turn));
    }

    private static void AssertSample(OutputSpeedSample? sample, string response, string? model, string? effort,
        int start = 10000, string turn = Turn)
    {
        Check.True(sample is not null, "Expected the existing measured output sample to survive metadata handling.");
        Check.Equal(Owner, sample!.ThreadId);
        Check.Equal(turn, sample.TurnId);
        Check.Equal(response, sample.ResponseId);
        Check.Equal(model, sample.Model);
        Check.Equal(effort, sample.ReasoningEffort);
        Check.Equal(120L, sample.OutputTokens);
        Check.Equal(At(start), sample.StartedAt);
        Check.Equal(At(start + 1000), sample.CompletedAt);
        Check.Equal(1d, sample.ObservedInterval.TotalSeconds);
        Check.Null(sample.TokensPerSecond);
    }

    private static RolloutModelRecovery Recover(string records)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Header(Owner) + records));
        var result = new RolloutModelRecovery(Owner, Turn, At(10000), stream.Length);
        ScanAll(result, stream);
        return result;
    }

    private static void ScanAll(RolloutModelRecovery result, Stream stream)
    {
        var calls = 0;
        while (!result.IsComplete)
        {
            Check.True(++calls <= 100, "Synthetic recovery must terminate within a bounded number of scans.");
            var offset = result.Offset;
            var used = result.Scan(stream, 1024);
            Check.True(used <= 1024);
            Check.True(result.Offset <= offset);
            Check.True(used > 0 || result.IsComplete, "Synthetic lines must fit the scan budget.");
        }
        Check.Equal(0, result.Scan(stream, 1024));
    }

    private static string TopEffort(string effort) => "\"effort\":" + JsonSerializer.Serialize(effort);
    private static string NestedEffort(string effort) => "\"collaboration_mode\":{\"settings\":{\"reasoning_effort\":" + JsonSerializer.Serialize(effort) + "}}";
    private static string BothEfforts(string effort) => TopEffort(effort) + "," + NestedEffort(effort);
    private static string Header(string owner) => Record("session_meta", new { id = owner }, 0);
    private static string Context(string fields = "", string thread = Owner, string turn = Turn, int at = 500) =>
        "{\"timestamp\":" + JsonSerializer.Serialize(At(at).ToString("O")) + ",\"type\":\"turn_context\",\"payload\":{\"thread_id\":" +
        JsonSerializer.Serialize(thread) + ",\"turn_id\":" + JsonSerializer.Serialize(turn) + ",\"model\":" + JsonSerializer.Serialize(Model) +
        (fields.Length == 0 ? "" : "," + fields) + "}}\n";
    private static string Settings(string thread, string effort, int at, string effortField = "reasoning_effort") =>
        Record("event_msg", new { type = "thread_settings_applied", thread_id = thread,
            thread_settings = new Dictionary<string, string> { ["model"] = Model, [effortField] = effort } }, at);
    private static string Usage(string response, int at, string turn = Turn) =>
        Record("token_usage_record", new { thread_id = Owner, turn_id = turn, response_id = response,
            usage = new { output_tokens = 120, reasoning_output_tokens = 80 } }, at);
    private static string Record(string type, object payload, int at) =>
        JsonSerializer.Serialize(new { timestamp = At(at).ToString("O"), type, payload }) + "\n";
    private static DateTimeOffset At(int milliseconds) => Epoch.AddMilliseconds(milliseconds);
}
