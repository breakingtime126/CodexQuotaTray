using System.Text.Json;
using CodexQuota.Core;

internal static class ResponseRateTests
{
    private const string Thread = "77777777-7777-4777-8777-777777777777";
    private const string Turn = "synthetic-response-turn";
    private static readonly DateTimeOffset Epoch = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    public static void Register(Action<string, Action> test)
    {
        test("response rate: exact completion publishes immediately including reasoning and small outputs", () =>
        {
            var parser = Ready();
            var sample = parser.Feed(Usage("a", 100, 2, reasoning: 80));
            Rate(sample, 50);
            Check.Equal(100L, sample!.OutputTokens);
            Check.Equal(OutputSpeedTimingReliability.ResponseRateEstimate, sample.TimingReliability);
            Check.Null(sample.GenerationDuration);
            Check.Equal(ResponseRateStartBoundary.TaskStarted, sample.ResponseRate!.StartBoundary);
            Check.Equal(ResponseRateEndBoundary.TokenUsageRecord, sample.ResponseRate.EndBoundary);
            Check.Equal(ResponseRateReason.Ready, parser.Diagnostic.Reason);
        });
        test("response rate: two 100 token responses exclude ten seconds of external tool waiting", () =>
        {
            var parser = Ready();
            Rate(parser.Feed(Usage("a", 100, 2)), 50);
            parser.Feed(Item(12, "function_call_output"));
            parser.Feed(Item(13, "reasoning"));
            var second = parser.Feed(Usage("b", 100, 14));
            Rate(second, 50);
            Check.Equal(ResponseRateStartBoundary.ToolResult, second!.ResponseRate!.StartBoundary);
        });
        test("response rate: late duplicate cannot clear a locked window or advance its start", () =>
        {
            var parser = Ready();
            Rate(parser.Feed(Usage("a", 100, 2)), 50);
            parser.Feed(Item(12, "function_call_output"));
            parser.Feed(Item(13, "reasoning"));
            Check.Null(parser.Feed(Usage("a", 100, 13.5)));
            Rate(parser.Feed(Usage("b", 100, 14)), 50);
            Check.Equal(2, parser.Diagnostic.ResponseCount);
        });
        test("response rate: parallel tool output freezes this response and only seeds the next", () =>
        {
            var parser = Ready();
            parser.Feed(Item(1, "reasoning"));
            parser.Feed(Item(1.5, "function_call_output"));
            Rate(parser.Feed(Usage("a", 100, 2)), 50);
            Rate(parser.Feed(Usage("b", 100, 4)), 50);
        });
        test("response rate: no content items required and previous completion recovers next response", () =>
        {
            var parser = new ResponseRateParser(Thread);
            parser.Feed(Header()); parser.Feed(Context());
            Check.Null(parser.Feed(Usage("a", 100, 2)));
            Check.Equal(ResponseRateReason.MissingStart, parser.Diagnostic.Reason);
            Rate(parser.Feed(Usage("b", 100, 4)), 50);
        });
        test("response rate: arbitrary long or fast responses have no silent numeric filter", () =>
        {
            Rate(Ready().Feed(Usage("long", 10, 700)), 10d / 700);
            Rate(Ready().Feed(Usage("fast", 3000, 1)), 3000);
            Rate(Ready().Feed(Usage("zero", 0, 1)), 0);
        });
        test("response rate: conflicting replay retracts only that response and never moves current window", () =>
        {
            var parser = Ready();
            Rate(parser.Feed(Usage("a", 100, 2)), 50);
            parser.Feed(Item(12, "function_call_output")); parser.Feed(Item(13, "reasoning"));
            Check.Null(parser.Feed(Usage("a", 101, 13.5)));
            Check.Null(parser.LatestSample);
            Check.Equal("a", parser.DrainInvalidatedResponseIds().Single());
            Rate(parser.Feed(Usage("b", 100, 14)), 50);
            Check.Null(parser.Feed(Usage("a", 100, 15)));
            Check.Equal("b", parser.LatestSample!.ResponseId);
        });
        test("response rate: later turn abort preserves completed responses and rejects pending work", () =>
        {
            var parser = Ready();
            Rate(parser.Feed(Usage("a", 100, 2)), 50);
            parser.Feed(Event(3, new { type = "turn_aborted", turn_id = Turn }));
            Check.Equal("a", parser.LatestSample!.ResponseId);
            Check.Null(parser.Feed(Usage("b", 100, 4)));
            Check.Equal(0, parser.DrainInvalidatedResponseIds().Count);
        });
        test("response rate: corrupt region requires a fresh boundary but does not poison whole turn", () =>
        {
            var parser = Ready(); parser.Feed("{broken}");
            Check.Null(parser.Feed(Usage("a", 100, 2)));
            parser.Feed(Context(2));
            Rate(parser.Feed(Usage("b", 100, 4)), 50);
        });
        test("response rate: transient stream errors discard one request and recover within the turn", () =>
        {
            var parser = Ready(); parser.Feed(Event(1, new { type = "stream_error", turn_id = Turn }));
            Check.Null(parser.Feed(Usage("damaged", 100, 2)));
            Rate(parser.Feed(Usage("recovered", 100, 4)), 50);
        });
        test("response rate: foreign owner turn and inherited headers cannot attribute samples", () =>
        {
            var parser = new ResponseRateParser(Thread);
            parser.Feed(Header("foreign")); parser.Feed(Start()); parser.Feed(Context());
            Check.Null(parser.Feed(Usage("foreign", 100, 2)));
            parser.Feed(Header()); parser.Feed(Start()); parser.Feed(Context());
            Check.Null(parser.Feed(Usage("foreign", 100, 1).Replace(Thread, "other", StringComparison.Ordinal)));
            Check.Null(parser.Feed(Usage("wrong-turn", 100, 1).Replace(Turn, "other-turn", StringComparison.Ordinal)));
            Rate(parser.Feed(Usage("a", 100, 2)), 50);
        });
        test("response rate: malformed identities negative timing and invalid counts recover after completion", () =>
        {
            foreach (var bad in new[] { Usage("bad", -1, 2), Usage("bad", 100, 2, reasoning: 101), Usage("bad", 100, 0) })
            {
                var parser = Ready(); Check.Null(parser.Feed(bad));
                parser.Feed(Item(3, "function_call_output"));
                Rate(parser.Feed(Usage("good", 100, 5)), 50);
            }
            var damagedIdentity = Ready();
            damagedIdentity.Feed(Usage("bad", 100, 2).Replace("\"response_id\":\"bad\"", "\"response_id\":null", StringComparison.Ordinal));
            damagedIdentity.Feed(Item(3, "function_call_output"));
            Check.Null(damagedIdentity.Feed(Usage("synchronize", 100, 5)));
            Rate(damagedIdentity.Feed(Usage("good", 100, 7)), 50);
        });
        test("response rate: model labels are atomic and no current-model rewrite reaches history", () =>
        {
            var parser = Ready(); var first = parser.Feed(Usage("a", 100, 2)); Rate(first, 50);
            parser.Feed(Context(2, "model-b", "high"));
            var second = parser.Feed(Usage("b", 100, 4)); Rate(second, 50);
            Check.Equal("model-a", first!.Model); Check.Equal("medium", first.ReasoningEffort);
            Check.Equal("model-b", second!.Model); Check.Equal("high", second.ReasoningEffort);
            parser.Feed(Item(5, "reasoning")); parser.Feed(Context(5.5, "model-c", "low"));
            Check.Null(parser.Feed(Usage("mixed", 100, 6)));
            Rate(parser.Feed(Usage("next", 100, 8)), 50);
        });
        test("response rate: Fast is completion-time snapshot and malformed settings clear it", () =>
        {
            var parser = Ready();
            parser.Feed(Event(1, new { type = "thread_settings_applied", thread_id = Thread,
                thread_settings = new { service_tier = "priority" } }));
            var first = parser.Feed(Usage("a", 100, 2)); Rate(first, 50);
            parser.Feed(Event(3, new { type = "thread_settings_applied", thread_id = Thread,
                thread_settings = new { service_tier = "default" } }));
            Check.Equal(ThreadRequestedSetting.Fast, first!.ThreadRequestedSetting);
            Check.Equal(ThreadRequestedSetting.Standard, parser.Feed(Usage("b", 100, 4))!.ThreadRequestedSetting);
        });
        test("response rate: legacy token count and item lifecycle cannot invent a second response", () =>
        {
            var parser = Ready();
            parser.Feed(Event(1, new { type = "item_completed", thread_id = Thread, turn_id = Turn,
                started_at_ms = Epoch.ToUnixTimeMilliseconds(), completed_at_ms = Epoch.AddSeconds(1).ToUnixTimeMilliseconds(),
                item = new { type = "Reasoning", id = "item" } }));
            Check.Null(parser.Feed(Event(2, new { type = "token_count", info = new { last_token_usage = new { output_tokens = 100 } } })));
            Check.Equal(ResponseRateReason.LegacyUsageUnsupported, parser.Diagnostic.Reason);
            Rate(parser.Feed(Usage("a", 100, 2)), 50);
            Check.Null(parser.Feed(Event(12, new { type = "token_count", info = new { last_token_usage = new { output_tokens = 100 } } })));
            Rate(parser.Feed(Usage("b", 100, 4)), 50);
        });
        test("response rate: bounded replay cache supports more than 512 responses", () =>
        {
            var parser = Ready();
            for (var i = 1; i <= 600; i++) Rate(parser.Feed(Usage("r" + i, 100, i * 2)), 50);
            Check.True(parser.Diagnostic.ResponseCount <= 512);
            Check.Null(parser.Feed(Usage("r1", 100, 2)));
            Check.Equal("r600", parser.LatestSample!.ResponseId);
            Rate(parser.Feed(Usage("last", 100, 1202)), 50);
        });
        test("response rate: duplicated JSON identity or usage fields are rejected", () =>
        {
            var parser = Ready();
            Check.Null(parser.Feed(Usage("bad", 100, 2).Replace("\"output_tokens\":100", "\"output_tokens\":100,\"output_tokens\":200", StringComparison.Ordinal)));
            Check.Null(parser.LatestSample);
            parser.Feed(Item(3, "tool_search_output")); Rate(parser.Feed(Usage("good", 100, 5)), 50);
        });
        test("response rate: backward completion never establishes a start before the turn", () =>
        {
            var parser = new ResponseRateParser(Thread); parser.Feed(Header()); parser.Feed(Start(10)); parser.Feed(Context(10));
            Check.Null(parser.Feed(Usage("rollback", 100, 2)));
            Check.Null(parser.Feed(Usage("baseline", 100, 12)));
            Rate(parser.Feed(Usage("recovered", 100, 14)), 50);
        });
        test("response rate: fresh explicit turn is not pinned by an earlier bad future clock", () =>
        {
            var parser = Ready(); parser.Feed(Usage("future", 100, 10000));
            parser.Feed(Start(20).Replace(Turn, "next-turn", StringComparison.Ordinal));
            parser.Feed(Context(20).Replace(Turn, "next-turn", StringComparison.Ordinal));
            Rate(parser.Feed(Usage("good", 100, 22).Replace(Turn, "next-turn", StringComparison.Ordinal)), 50);
        });
        test("response rate: replay may enrich missing breakdown but contradictory checkpoints retract", () =>
        {
            var parser = Ready();
            Rate(parser.Feed(Usage("a", 100, 2).Replace(",\"reasoning_output_tokens\":0", "", StringComparison.Ordinal)), 50);
            Check.Null(parser.Feed(Usage("a", 100, 3, reasoning: 80)));
            Check.Equal(0, parser.DrainInvalidatedResponseIds().Count);
            Rate(parser.LatestSample, 50);
            var checkpoint = Line(3, "token_usage_record", new { thread_id = Thread, turn_id = Turn, response_id = "a",
                usage = new { output_tokens = 100, reasoning_output_tokens = 80 }, turn_token_usage = new { output_tokens = 100 } });
            parser.Feed(checkpoint);
            Check.Equal(0, parser.DrainInvalidatedResponseIds().Count);
            parser.Feed(checkpoint.Replace("\"turn_token_usage\":{\"output_tokens\":100}", "\"turn_token_usage\":{\"output_tokens\":101}", StringComparison.Ordinal));
            Check.Null(parser.LatestSample); Check.Equal("a", parser.DrainInvalidatedResponseIds().Single());
        });
        test("response rate: thread preference cannot overwrite stronger scoped turn model", () =>
        {
            var parser = Ready(); var first = parser.Feed(Usage("a", 100, 2)); Rate(first, 50);
            parser.Feed(Event(3, new { type = "thread_settings_applied", thread_id = Thread,
                thread_settings = new { service_tier = "priority", model = "model-b", reasoning_effort = "high" } }));
            var scoped = parser.Feed(Usage("scoped-model", 100, 4)); Rate(scoped, 50);
            Check.Equal("model-a", scoped!.Model); Check.Equal("medium", scoped.ReasoningEffort);
            Check.Equal("model-a", first!.Model);
            parser.Feed(Context(4, "model-b", "high"));
            var next = parser.Feed(Usage("known-model", 100, 6)); Rate(next, 50);
            Check.Equal("model-b", next!.Model); Check.Equal("high", next.ReasoningEffort);
        });
        test("response rate: pristine same-turn pre-start context preserves labels but never supplies time", () =>
        {
            var parser = new ResponseRateParser(Thread); parser.Feed(Header()); parser.Feed(Context(0)); parser.Feed(Start(10));
            var sample = parser.Feed(Usage("a", 100, 12)); Rate(sample, 50);
            Check.Equal(Epoch.AddSeconds(10), sample!.StartedAt);
            Check.Equal("model-a", sample.Model); Check.Equal("medium", sample.ReasoningEffort);
        });
        test("response rate: pre-start context from another turn or a later clock is never inherited", () =>
        {
            foreach (var context in new[] { Context(0).Replace(Turn, "foreign-turn", StringComparison.Ordinal), Context(11) })
            {
                var parser = new ResponseRateParser(Thread); parser.Feed(Header()); parser.Feed(context); parser.Feed(Start(10));
                Check.Null(parser.Feed(Usage("a", 100, 12))); Check.Equal(ResponseRateReason.MissingModel, parser.Diagnostic.Reason);
            }
        });
        test("response rate: damaged or used pre-start context cannot be reused at a later start", () =>
        {
            foreach (var interruption in new[] { "{broken}", Item(1, "reasoning"), Usage("earlier", 100, 2),
                Event(1, new { type = "stream_error", turn_id = Turn }), Event(1, new { type = "error", turn_id = Turn }) })
            {
                var parser = new ResponseRateParser(Thread); parser.Feed(Header()); parser.Feed(Context(0)); parser.Feed(interruption); parser.Feed(Start(10));
                Check.Null(parser.Feed(Usage("a", 100, 12))); Check.Equal(ResponseRateReason.MissingModel, parser.Diagnostic.Reason);
            }
        });
        test("response rate: only new input or output marks a completed response as awaiting another usage", () =>
        {
            var completed = Ready(); completed.Feed(Usage("a", 100, 2));
            completed.Feed(Event(10, new { type = "task_complete", turn_id = Turn, duration_ms = 10000 }));
            Check.Equal(ResponseRateReason.Ready, completed.Diagnostic.Reason);
            foreach (var activity in new[] { Item(3, "function_call_output"), Item(3, "reasoning"),
                Event(3, new { type = "user_message" }) })
            {
                var parser = Ready(); parser.Feed(Usage("a", 100, 2)); parser.Feed(activity);
                Check.Equal(ResponseRateReason.AwaitingUsage, parser.Diagnostic.Reason);
                Rate(parser.LatestSample, 50);
            }
        });
        test("response rate: a skipped region cannot turn a parallel input into a fabricated request start", () =>
        {
            foreach (var input in new[] { Item(12, "function_call_output"),
                Line(12, "response_item", new { type = "message", role = "user" }) })
            {
                var parser = Ready(); parser.ResetPending(); parser.Feed(Context(10)); parser.Feed(input);
                Check.Null(parser.Feed(Usage("synchronize", 100, 14)));
                Check.Equal(ResponseRateReason.MissingStart, parser.Diagnostic.Reason);
                Rate(parser.Feed(Usage("next", 100, 16)), 50);
            }
            var restarted = Ready(); restarted.ResetPending(); restarted.Feed(Start(10)); restarted.Feed(Context(10));
            Rate(restarted.Feed(Usage("explicit", 100, 12)), 50);
            var retry = Ready(); retry.Feed(Event(1, new { type = "stream_error", turn_id = Turn }));
            retry.Feed(Item(3, "function_call_output"));
            Check.Null(retry.Feed(Usage("retry-synchronize", 100, 5)));
            Rate(retry.Feed(Usage("retry-next", 100, 7)), 50);
        });
        test("response rate: nested response-item turn scope cannot supply or freeze another turn's boundary", () =>
        {
            var input = Ready();
            input.Feed(Line(9, "response_item", new { type = "function_call_output",
                internal_chat_message_metadata_passthrough = new { turn_id = "foreign-turn" } }));
            Rate(input.Feed(Usage("own", 100, 10)), 10);
            var output = Ready();
            output.Feed(Line(1, "response_item", new { type = "reasoning",
                internal_chat_message_metadata_passthrough = new { turn_id = "foreign-turn" } }));
            output.Feed(Item(8, "function_call_output"));
            Rate(output.Feed(Usage("own", 100, 10)), 50);
        });
        test("response rate: invalid usage cannot hide a backward timestamp when synchronizing the next response", () =>
        {
            var parser = new ResponseRateParser(Thread); parser.Feed(Header()); parser.Feed(Start(10)); parser.Feed(Context(10));
            Check.Null(parser.Feed(Usage("bad", -1, 2)));
            Check.Equal(ResponseRateReason.InvalidUsage, parser.Diagnostic.Reason);
            parser.Feed(Item(11, "function_call_output"));
            Check.Null(parser.Feed(Usage("synchronize", 100, 12)));
            Rate(parser.Feed(Usage("good", 100, 14)), 50);
        });
        test("response rate: a duplicate nested turn key ending in null cannot bypass scope validation", () =>
        {
            foreach (var first in new[] { "\"foreign-turn\"", "null" })
            {
                var parser = Ready();
                var input = Line(9, "response_item", new { type = "function_call_output",
                    internal_chat_message_metadata_passthrough = new { turn_id = "foreign-turn" } });
                parser.Feed(input.Replace("\"turn_id\":\"foreign-turn\"", "\"turn_id\":" + first + ",\"turn_id\":null", StringComparison.Ordinal));
                Check.Null(parser.Feed(Usage("synchronize", 100, 10)));
                parser.Feed(Context(10)); Rate(parser.Feed(Usage("next", 100, 12)), 50);
            }
        });
    }

    private static ResponseRateParser Ready() { var parser = new ResponseRateParser(Thread); parser.Feed(Header()); parser.Feed(Start()); parser.Feed(Context()); return parser; }
    private static void Rate(OutputSpeedSample? sample, double expected) => Check.True(sample?.TokensPerSecond is { } rate && Math.Abs(rate - expected) < 1e-9, "Expected response rate " + expected + ", got " + sample?.TokensPerSecond);
    private static string Header(string thread = Thread) => Line(0, "session_meta", new { id = thread });
    private static string Start(double at = 0) => Event(at, new { type = "task_started", turn_id = Turn });
    private static string Context(double at = 0, string model = "model-a", string effort = "medium") => Line(at, "turn_context", new { turn_id = Turn, model, effort });
    private static string Item(double at, string type) => Line(at, "response_item", new { type });
    private static string Event(double at, object payload) => Line(at, "event_msg", payload);
    private static string Usage(string id, long count, double at, long? reasoning = null) => Line(at, "token_usage_record", new {
        thread_id = Thread, turn_id = Turn, response_id = id, session_id = "different-process-session",
        usage = new { output_tokens = count, reasoning_output_tokens = reasoning ?? 0 } });
    private static string Line(double at, string type, object payload) => JsonSerializer.Serialize(new { timestamp = Epoch.AddSeconds(at), type, payload });
}
