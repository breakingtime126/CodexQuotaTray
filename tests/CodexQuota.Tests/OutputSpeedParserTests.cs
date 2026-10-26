using System.Text.Json;
using CodexQuota.Core;

internal static class OutputSpeedParserTests
{
    private const string Owner = "019abcde-1234-5678-9012-345678901234";
    private const string Turn = "turn-one";
    private const long Epoch = 1767225600000;

    public static void Register(Action<string, Action> test)
    {
        foreach (var kind in new[] { "Reasoning", "AgentMessage" })
        foreach (var earlier in new[] { true, false })
            test($"speed parser: unmatched {kind} timing cannot shorten the {(earlier ? "start" : "end")}", () =>
            {
                var parser = Ready();
                if (earlier) parser.Feed(Timing("orphan", 1000, 9000, kind: kind));
                parser.Feed(Timing("message", earlier ? 9000 : 1000, earlier ? 10000 : 2000, kind: "AgentMessage"));
                parser.Feed(Output("message", "message", earlier ? 10010 : 2010));
                if (!earlier) parser.Feed(Timing("orphan", 3000, 10000, kind: kind));
                Check.Null(parser.Feed(Usage("incomplete-response", 730, 10020, reasoningTokens: 650)));
                Check.Null(parser.LatestSample);
            });
        test("speed parser: incomplete timing join preserves the previous valid sample", () =>
        {
            var parser = Ready(); Reasoning(parser);
            var previous = parser.Feed(Usage("previous", 120, 3000));
            parser.Feed(Timing("unmatched", 4000, 9000));
            parser.Feed(Timing("message", 9000, 10000, kind: "AgentMessage"));
            parser.Feed(Output("message", "message", 10010));
            Check.Null(parser.Feed(Usage("incomplete", 730, 10020, reasoningTokens: 650)));
            Check.Equal(previous, parser.LatestSample);
        });
        test("speed parser: actual schema joins item.id without response_id and counts reasoning once", () =>
        {
            var parser = Ready();
            parser.Feed(Timing("reason", 1000, 2000)); // Persisted before response_item.
            parser.Feed(Output("reason", "reasoning", 2010));
            var sample = parser.Feed(Usage("response", 120, 2020, reasoningTokens: 80));
            AssertSample(sample, 120, 1, "response");
            Check.Equal("gpt-5.4", sample!.Model);
            Check.Equal(At(2000), sample.CompletedAt);
            Check.Equal(At(1000), sample.StartedAt);
        });
        test("speed parser: reasoning plus assistant includes intervening output gap", () =>
        {
            var parser = Ready();
            parser.Feed(Output("reason", "reasoning", 2010));
            parser.Feed(Timing("reason", 1000, 2000));
            parser.Feed(Output("message", "message", 4010));
            parser.Feed(Timing("message", 3000, 4000, kind: "AgentMessage"));
            AssertSample(parser.Feed(Usage("response", 240, 4020)), 240, 3, "response");
        });
        foreach (var kind in new[] { "function_call", "custom_tool_call" })
            test($"speed parser: {kind} completion counts but tool execution and late usage do not", () =>
            {
                var parser = Ready();
                Reasoning(parser);
                parser.Feed(Output("tool", kind, 3000));
                parser.Feed(Record("event_msg", new { type = "item_completed", thread_id = Owner, turn_id = Turn,
                    item = new { id = "tool", type = "McpToolCall" }, started_at_ms = Epoch + 3000,
                    completed_at_ms = Epoch + 30000 }, 30010));
                parser.Feed(Record("response_item", new { type = kind + "_output", call_id = "call", output = "private tool body" }, 30020));
                AssertSample(parser.Feed(Usage("response", 120, 30100)), 120, 2, "response");
            });
        foreach (var kind in new[] { "function_call", "custom_tool_call" })
            test($"speed parser: {kind} first never borrows a later reasoning start", () =>
            {
                var parser = Ready();
                parser.Feed(Output("tool", kind, 1000));
                Reasoning(parser);
                Check.Null(parser.Feed(Usage("response", 120, 3000)));
                Check.Null(parser.LatestSample);
            });
        test("speed parser: missing first timing cannot borrow a later assistant boundary", () =>
        {
            var parser = Ready();
            parser.Feed(Output("reason", "reasoning", 2000));
            parser.Feed(Timing("message", 3000, 4000, kind: "AgentMessage"));
            parser.Feed(Output("message", "message", 4010));
            Check.Null(parser.Feed(Usage("response", 240, 5000)));
        });
        foreach (var (start, end) in new (object?, object?)[]
        {
            (Epoch + 2000, Epoch + 2000), (0, Epoch + 2000), (-1, Epoch + 2000),
            (Epoch + 2001, Epoch + 2000), ("1767225601000", Epoch + 2000),
            (null, Epoch + 2000), (Epoch + 1000, null), (Epoch + 1000, 1e30)
        })
            test($"speed parser: no guessed speed from invalid first timing {start}/{end}", () =>
            {
                var parser = Ready();
                parser.Feed(Record("event_msg", new { type = "item_completed", thread_id = Owner, turn_id = Turn,
                    item = new { id = "reason", type = "Reasoning" }, started_at_ms = start, completed_at_ms = end }, 2010));
                parser.Feed(Output("reason", "reasoning", 2020));
                Check.Null(parser.Feed(Usage("response", 120, 3000)));
            });
        test("speed parser: positive 1 ms lifecycle remains unverified without a speed cutoff", () =>
        {
            var parser = Ready();
            parser.Feed(Timing("reason", 1000, 1001));
            parser.Feed(Output("reason", "reasoning", 1002));
            AssertSample(parser.Feed(Usage("response", 1000, 1010)), 1000, .001, "response");
        });
        test("speed parser: zero output count survives without inventing generation speed", () =>
        {
            var parser = Ready(); Reasoning(parser);
            AssertSample(parser.Feed(Usage("response", 0, 3000, reasoningTokens: 0)), 0, 1, "response");
        });
        foreach (var reasoning in new object[] { 121, -1, 1.5 })
            test($"speed parser: contradictory reasoning breakdown {reasoning} invalidates usage", () =>
            {
                var parser = Ready(); Reasoning(parser);
                Check.Null(parser.Feed(Record("token_usage_record", new { thread_id = Owner, turn_id = Turn, response_id = "response",
                    usage = new { output_tokens = 120, reasoning_output_tokens = reasoning } }, 3000)));
                Check.Null(parser.LatestSample);
                Check.Null(parser.DrainUsageObservations()[0].OutputTokens);
            });
        foreach (var value in new object?[] { -1, 1.5, "120", null, true, 1e30 })
            test($"speed parser: invalid output count {value ?? "null"} stays unavailable", () =>
            {
                var parser = Ready(); Reasoning(parser);
                Check.Null(parser.Feed(Usage("response", value, 3000)));
            });
        test("speed parser: duplicate output and timing records are idempotent", () =>
        {
            var parser = Ready(); Reasoning(parser); Reasoning(parser);
            var first = parser.Feed(Usage("response", 120, 3000));
            AssertSample(first, 120, 1, "response");
            Check.Null(parser.Feed(Usage("response", 120, 3100)));
            Check.Equal(first, parser.LatestSample);
            Reasoning(parser);
            Check.Null(parser.Feed(Usage("response", 120, 3200)));
            Check.Equal(first, parser.LatestSample);
        });
        test("speed parser: conflicting output IDs and conflicting timing reject the response", () =>
        {
            foreach (var conflict in new[] { Output("reason", "reasoning", 2100), Timing("reason", 1100, 2000) })
            {
                var parser = Ready(); Reasoning(parser); parser.Feed(conflict);
                Check.Null(parser.Feed(Usage("response", 120, 3000)));
            }
        });
        test("speed parser: conflicting repeated usage withdraws latest and falls back", () =>
        {
            var parser = Ready(); Reasoning(parser);
            var first = parser.Feed(Usage("first", 120, 3000));
            Reasoning(parser, "second-item", 4000, 5000);
            AssertSample(parser.Feed(Usage("second", 100, 6000)), 100, 1, "second");
            Check.Null(parser.Feed(Usage("second", 101, 6010)));
            Check.Equal(first, parser.LatestSample);
            Check.Null(parser.Feed(Usage("first", 121, 6020)));
            Check.Null(parser.LatestSample);
            Check.Null(parser.Feed(Usage("second", 100, 6030)));
            Check.Null(parser.LatestSample);
        });
        test("speed parser: older and equal-time conflicts report every invalidated identity", () =>
        {
            var parser = Ready(); Reasoning(parser);
            parser.Feed(Usage("first", 120, 3000));
            Reasoning(parser, "second-item", 4000, 5000);
            var latest = parser.Feed(Usage("second", 100, 6000));
            parser.Feed(Usage("first", 121, 6100));
            Check.Equal(latest, parser.LatestSample);
            Check.True(parser.DrainInvalidatedResponseIds().SequenceEqual(new[] { "first" }));
            Check.Equal(0, parser.DrainInvalidatedResponseIds().Count);
            parser.Feed(Usage("second", 101, 6200));
            Check.Null(parser.LatestSample);
            Check.True(parser.DrainInvalidatedResponseIds().SequenceEqual(new[] { "second" }));
            parser = Ready(); Reasoning(parser);
            parser.Feed(Usage("tie-first", 120, 3000));
            Reasoning(parser, "tie-item");
            parser.Feed(Usage("tie-second", 120, 3100));
            parser.Feed(Usage("tie-first", 121, 3200));
            Check.Equal("tie-second", parser.LatestSample!.ResponseId);
            Check.True(parser.DrainInvalidatedResponseIds().SequenceEqual(new[] { "tie-first" }));
        });
        foreach (var status in new[] { "cancelled", "canceled", "failed", "incomplete" })
            test($"speed parser: {status} usage replay withdraws even with identical counts", () =>
            {
                var parser = Ready(); Reasoning(parser);
                parser.Feed(Usage("response", 120, 3000));
                parser.Feed(Record("token_usage_record", new { thread_id = Owner, turn_id = Turn, response_id = "response",
                    status, usage = new { output_tokens = 120 } }, 3100));
                Check.Null(parser.LatestSample);
                Check.True(parser.DrainInvalidatedResponseIds().SequenceEqual(new[] { "response" }));
            });
        test("speed parser: malformed and contradictory replayed output withdraws prior sample", () =>
        {
            foreach (var replay in new[]
            {
                Timing("reason", 2000, 1000), Timing("reason", 2000, 2000),
                Timing("reason", 1000, 4000), Output("reason", "function_call", 2000),
                Output("reason", "reasoning", 2000).Replace("\"id\":\"reason\"", "\"id\":null", StringComparison.Ordinal)
            })
            {
                var parser = Ready(); Reasoning(parser);
                parser.Feed(Usage("response", 120, 3000));
                parser.Feed(replay);
                if (!replay.Contains("response_item", StringComparison.Ordinal)) parser.Feed(Output("reason", "reasoning", 2010));
                parser.Feed(Usage("response", 120, 3100));
                Check.Null(parser.LatestSample);
                Check.True(parser.DrainInvalidatedResponseIds().SequenceEqual(new[] { "response" }));
            }
        });
        test("speed parser: incomplete timing still exports scoped usage conflict evidence", () =>
        {
            var parser = Ready();
            parser.Feed(Output("no-timing", "reasoning", 2000));
            Check.Null(parser.Feed(Usage("response", 999, 3000)));
            var observations = parser.DrainUsageObservations();
            Check.Equal(1, observations.Count);
            Check.Equal(new OutputSpeedUsageObservation(Owner, "response", Turn, 999, false) { ReasoningOutputTokens = 80 }, observations[0]);
            Check.Equal(0, parser.DrainUsageObservations().Count);
            parser.Feed(Usage("foreign", 100, 4000, thread: "ancestor"));
            Check.Equal(0, parser.DrainUsageObservations().Count);
        });
        test("speed parser: duplicate JSON token count fields are rejected", () =>
        {
            var parser = Ready(); Reasoning(parser);
            Check.Null(parser.Feed(Usage("response", 120, 3000).Replace("\"output_tokens\":120", "\"output_tokens\":120,\"output_tokens\":121", StringComparison.Ordinal)));
        });
        test("speed parser: malformed generated identity cannot silently remove the first item", () =>
        {
            foreach (var broken in new[]
            {
                Output("first", "reasoning", 1000).Replace("\"type\":\"reasoning\"", "\"type\":\"reasoning\",\"type\":\"message\"", StringComparison.Ordinal),
                Output("first", "message", 1000).Replace("\"role\":\"assistant\"", "\"role\":null", StringComparison.Ordinal)
            })
            {
                var parser = Ready(); parser.Feed(broken); Reasoning(parser);
                Check.Null(parser.Feed(Usage("response", 120, 3000)));
            }
        });
        foreach (var failure in new[] { "turn_aborted", "task_failed", "error", "stream_error" })
            test($"speed parser: {failure} cannot turn partial output into a completed sample", () =>
            {
                var parser = Ready(); Reasoning(parser);
                parser.Feed(Record("event_msg", new { type = failure, turn_id = Turn }, 2100));
                Check.Null(parser.Feed(Usage("response", 120, 3000)));
                Reasoning(parser, "late", 4000, 5000);
                Check.Null(parser.Feed(Usage("late-response", 100, 6000)));
                parser.Feed(Context("turn-two", 7000));
                Reasoning(parser, "fresh", 8000, 9000, "turn-two");
                AssertSample(parser.Feed(Usage("fresh-response", 100, 10000, turn: "turn-two")), 100, 1, "fresh-response");
            });
        test("speed parser: cancellation of a later partial response preserves earlier completion", () =>
        {
            var parser = Ready(); Reasoning(parser);
            var first = parser.Feed(Usage("first", 120, 3000));
            Reasoning(parser, "partial", 4000, 5000);
            parser.Feed(Record("event_msg", new { type = "turn_aborted", turn_id = Turn }, 5100));
            Check.Equal(first, parser.LatestSample);
        });
        test("speed parser: legacy snapshots alone never supply response identity", () =>
        {
            var parser = Ready(); Reasoning(parser);
            var legacy = Record("event_msg", new { type = "token_count", info = new { last_token_usage = new { output_tokens = 120 } } }, 2100);
            Check.Null(parser.Feed(legacy)); Check.Null(parser.LatestSample);
            // Modern logs can still include this notification before explicit usage.
            AssertSample(parser.Feed(Usage("response", 120, 3000)), 120, 1, "response");
        });
        test("speed parser: a legacy boundary cannot merge two unrelated responses", () =>
        {
            var parser = Ready(); Reasoning(parser);
            parser.Feed(Record("event_msg", new { type = "token_count" }, 2100));
            Reasoning(parser, "second", 4000, 5000);
            Check.Null(parser.Feed(Usage("response", 120, 6000)));
        });
        test("speed parser: owner must be established independently from inherited records", () =>
        {
            var parser = new RolloutSpeedParser(Owner);
            parser.Feed(Context()); Reasoning(parser);
            Check.Null(parser.Feed(Usage("response", 120, 3000)));
            parser.Feed(Record("session_meta", new { id = "ancestor" }, 0));
            parser.Feed(Context()); Reasoning(parser);
            Check.Null(parser.Feed(Usage("response", 120, 3000, thread: "ancestor")));
            parser.Feed(Record("event_msg", new { type = "thread_settings_applied", thread_id = Owner }, 4000));
            parser.Feed(Context("own-turn", 4500));
            Reasoning(parser, "own", 5000, 6000, "own-turn");
            AssertSample(parser.Feed(Usage("response", 120, 7000, turn: "own-turn")), 120, 1, "response");
        });
        test("speed parser: own session metadata also ends copied ancestor history", () =>
        {
            var parser = new RolloutSpeedParser(Owner);
            parser.Feed(Record("session_meta", new { id = "ancestor" }, 0));
            parser.Feed(Context()); Reasoning(parser);
            parser.Feed(Usage("ancestor-response", 120, 3000, thread: "ancestor"));
            parser.Feed(Record("session_meta", new { id = Owner }, 4000));
            parser.Feed(Context("own", 4500, null));
            Reasoning(parser, "own", 5000, 6000, "own");
            var sample = parser.Feed(Usage("own-response", 120, 7000, turn: "own"));
            AssertSample(sample, 120, 1, "own-response"); Check.Null(sample!.Model);
        });
        foreach (var corrupt in new[] { "{", "null", "[]", "", "{\"type\":\"response_item\",\"payload\":{}}" })
            test($"speed parser: malformed record {corrupt} breaks active matching", () =>
            {
                var parser = Ready(); Reasoning(parser); parser.Feed(corrupt);
                Check.Null(parser.Feed(Usage("broken", 120, 3000)));
                Reasoning(parser, "fresh", 4000, 5000);
                var sample = parser.Feed(Usage("fresh", 100, 6000));
                AssertSample(sample, 100, 1, "fresh"); Check.Null(sample!.Model);
            });
        test("speed parser: skipped tail discards partial state without losing verified ownership", () =>
        {
            var parser = Ready(); Reasoning(parser);
            var original = parser.Feed(Usage("first", 120, 3000));
            Reasoning(parser, "partial", 4000, 5000);
            parser.ResetPending(); Check.Equal(original, parser.LatestSample);
            Check.Null(parser.Feed(Usage("partial", 100, 6000)));
            Reasoning(parser, "new", 7000, 8000);
            var sample = parser.Feed(Usage("new", 100, 9000));
            AssertSample(sample, 100, 1, "new"); Check.Null(sample!.Model);
            parser.Reset(); Check.Null(parser.LatestSample);
            parser.Feed(Context()); Reasoning(parser);
            Check.Null(parser.Feed(Usage("no-owner", 120, 10000)));
        });
        test("speed parser: wrong thread and turn identities cannot join valid pieces", () =>
        {
            foreach (var wrong in new[] { "timing-thread", "timing-turn", "usage-thread", "usage-turn", "item-turn" })
            {
                var parser = Ready();
                parser.Feed(Timing("reason", 1000, 2000, thread: wrong == "timing-thread" ? "other" : Owner,
                    turn: wrong == "timing-turn" ? "other" : Turn));
                parser.Feed(Output("reason", "reasoning", 2010, wrong == "item-turn" ? "other" : Turn));
                Check.Null(parser.Feed(Usage("response", 120, 3000,
                    thread: wrong == "usage-thread" ? "other" : Owner, turn: wrong == "usage-turn" ? "other" : Turn)));
            }
        });
        test("speed parser: timestamps require a zone and preserve the same instant across offsets", () =>
        {
            var parser = Ready();
            parser.Feed(Timing("reason", 1000, 2000));
            parser.Feed(WithTimestamp(Output("reason", "reasoning", 2010), "2026-01-01T08:00:02.010+08:00"));
            AssertSample(parser.Feed(Usage("response", 120, 3000)), 120, 1, "response");
            var noZone = Ready(); noZone.Feed(Timing("reason", 1000, 2000));
            noZone.Feed(WithTimestamp(Output("reason", "reasoning", 2010), "2026-01-01T00:00:02.010"));
            Check.Null(noZone.Feed(Usage("response", 120, 3000)));
        });
        test("speed parser: contradictory completion and generation order is rejected", () =>
        {
            var parser = Ready(); parser.Feed(Timing("reason", 1000, 2500));
            parser.Feed(Output("reason", "reasoning", 2000));
            Check.Null(parser.Feed(Usage("response", 120, 3000)));
            parser = Ready(); Reasoning(parser);
            parser.Feed(Output("tool", "function_call", 1900));
            Check.Null(parser.Feed(Usage("response", 120, 3000)));
            parser = Ready(); parser.Feed(Timing("reason", 1000, 2000));
            parser.Feed(Output("reason", "reasoning", 5000));
            Check.Null(parser.Feed(Usage("response", 120, 3000)));
        });
        foreach (var kind in new[] { "turn_context", "task_started" })
            test($"speed parser: foreign explicit thread on {kind} cannot establish ownership", () =>
            {
                var parser = Ready();
                parser.Feed(Record(kind == "task_started" ? "event_msg" : kind,
                    new { type = kind, thread_id = "other", turn_id = Turn, model = "foreign-model" }, 600));
                Reasoning(parser);
                Check.Null(parser.Feed(Usage("response", 120, 3000)));
            });
        test("speed parser: source strings are bounded and missing model is not invented", () =>
        {
            Check.Throws<ArgumentException>(() => new RolloutSpeedParser(new string('x', 161)));
            var parser = Ready(new string('m', 129)); Reasoning(parser);
            var sample = parser.Feed(Usage("response", 120, 3000));
            AssertSample(sample, 120, 1, "response"); Check.Null(sample!.Model);
            parser = Ready(); parser.Feed(Timing(new string('i', 161), 1000, 2000));
            parser.Feed(Output(new string('i', 161), "reasoning", 2010));
            Check.Null(parser.Feed(Usage("response", 120, 3000)));
        });
        test("speed parser: raw prompt and arguments are never copied into the sample", () =>
        {
            var parser = Ready();
            parser.Feed(Record("response_item", new { type = "message", role = "user", content = "PRIVATE PROMPT" }, 600));
            Reasoning(parser);
            parser.Feed(Output("tool", "function_call", 3000));
            var sample = parser.Feed(Usage("response", 120, 4000));
            AssertSample(sample, 120, 2, "response");
            var serialized = JsonSerializer.Serialize(sample);
            Check.False(serialized.Contains("PRIVATE", StringComparison.Ordinal));
            Check.False(serialized.Contains("arguments", StringComparison.Ordinal));
        });
        test("speed parser: restored model is scoped to exact unchanged turn and leaves TPS untouched", () =>
        {
            var parser = Ready(); parser.ResetPending();
            parser.Feed(Usage("boundary", 100, 500));
            Reasoning(parser);
            var first = parser.Feed(Usage("first", 120, 3000));
            Check.Null(first!.Model); Check.Equal(Turn, first.TurnId);
            var epoch = parser.ModelEpoch;
            Check.False(parser.TryRestoreModel("different-turn", "gpt-recovered", epoch));
            Check.False(parser.TryRestoreModel(Turn, "gpt-recovered", epoch - 1));
            Check.True(parser.TryRestoreModel(Turn, "gpt-recovered", epoch));
            Reasoning(parser, "second-item", 4000, 5000);
            var second = parser.Feed(Usage("second", 120, 6000));
            Check.Equal("gpt-recovered", second!.Model); Check.Null(second.TokensPerSecond);
            Check.True(first.IsCompatibleWith(first with { Model = "gpt-recovered" }));
            Check.False((first with { Model = "a" }).IsCompatibleWith(first with { Model = "b" }));
        });
        test("speed parser: mid-turn settings cannot substitute current model for frozen history", () =>
        {
            var parser = Ready(); var epoch = parser.ModelEpoch;
            parser.Feed(Record("event_msg", new { type = "thread_settings_applied", thread_id = Owner,
                thread_settings = new { model = "different-model" } }, 600));
            Check.False(parser.TryRestoreModel(Turn, "gpt-5.4", epoch));
            Reasoning(parser);
            parser.Feed(Context(at: 2500)); // A later frozen initial-turn snapshot cannot undo ambiguity.
            var sample = parser.Feed(Usage("response", 120, 3000));
            Check.Null(sample!.Model); Check.Null(sample.TokensPerSecond);
        });
    }

    private static RolloutSpeedParser Ready(string? model = "gpt-5.4")
    {
        var parser = new RolloutSpeedParser(Owner);
        parser.Feed(Record("session_meta", new { id = Owner }, 0));
        parser.Feed(Context(model: model));
        return parser;
    }
    private static string Context(string turn = Turn, long at = 500, string? model = "gpt-5.4") =>
        Record("turn_context", new { turn_id = turn, model }, at);
    private static string Output(string id, string kind, long at, string turn = Turn) =>
        Record("response_item", new { type = kind, id, role = "assistant", content = "PRIVATE CONTENT", arguments = "PRIVATE ARGUMENTS",
            internal_chat_message_metadata_passthrough = new { turn_id = turn } }, at);
    private static string Timing(string id, long start, long end, string kind = "Reasoning", string thread = Owner, string turn = Turn) =>
        Record("event_msg", new { type = "item_completed", thread_id = thread, turn_id = turn, item = new { id, type = kind },
            started_at_ms = Epoch + start, completed_at_ms = Epoch + end }, end + 10);
    private static string Usage(string response, object? tokens, long at, string thread = Owner, string turn = Turn, long reasoningTokens = 80) =>
        Record("token_usage_record", new { thread_id = thread, turn_id = turn, response_id = response,
            usage = new { output_tokens = tokens, reasoning_output_tokens = reasoningTokens } }, at);
    private static string Record(string type, object payload, long at) => JsonSerializer.Serialize(new
    {
        timestamp = At(at).ToString("O"), type, payload
    });
    private static DateTimeOffset At(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(Epoch + milliseconds);
    private static string WithTimestamp(string line, string timestamp)
    {
        using var input = JsonDocument.Parse(line);
        var changed = JsonSerializer.Serialize(new { timestamp, type = input.RootElement.GetProperty("type").GetString(),
            payload = input.RootElement.GetProperty("payload") });
        using var check = JsonDocument.Parse(changed);
        Check.Equal(timestamp, check.RootElement.GetProperty("timestamp").GetString());
        Check.False(input.RootElement.GetProperty("timestamp").GetString() == timestamp);
        return changed;
    }
    private static void Reasoning(RolloutSpeedParser parser, string id = "reason", long start = 1000, long end = 2000, string turn = Turn)
    {
        parser.Feed(Timing(id, start, end, turn: turn));
        parser.Feed(Output(id, "reasoning", end + 10, turn));
    }
    private static void AssertSample(OutputSpeedSample? sample, long outputTokens, double seconds, string response)
    {
        Check.True(sample is not null, "Expected consistent completed-response metadata.");
        Check.Equal(Owner, sample!.ThreadId); Check.Equal(response, sample.ResponseId);
        Check.Equal(seconds, sample.ObservedInterval.TotalSeconds); Check.Null(sample.TokensPerSecond);
        Check.Null(sample.GenerationDuration);
        Check.Equal(OutputSpeedTimingReliability.UnverifiedItemLifecycle, sample.TimingReliability);
        Check.Equal(outputTokens, sample.OutputTokens);
    }
}
