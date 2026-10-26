using System.Text.Json;
using CodexQuota.Core;

internal static class TurnAverageTests
{
    private const string Thread = "66666666-6666-4666-8666-666666666666";
    private const string Turn = "synthetic-average-turn";
    private static readonly DateTimeOffset Epoch = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        asyncTest("turn diagnostics: damaged incomplete archive does not change existing withdrawal timing", async () =>
        {
            using var home = new Home(); home.Write(Valid());
            home.Archive(Header() + Start() + Context() + "{broken}\n");
            Check.Equal(151d, (await home.Reader().PollAsync()).Sample?.TokensPerSecond ?? -1);
            var parser = new TurnAverageParser(Thread);
            Feed(parser, Header() + Start() + Context() + "{broken}\n");
            Check.Equal(TurnAverageReason.InvalidRecord, parser.Diagnostic.Reason);
            Check.Equal(0, parser.DrainInvalidatedTurnIds().Count);
            Feed(parser, Complete(10000, 10));
            Check.Equal(TurnAverageReason.InvalidRecord, parser.Diagnostic.Reason);
            Check.Equal(Turn, parser.DrainInvalidatedTurnIds().Single());
        });
        test("turn diagnostics: foreign thread or turn records cannot supply observation flags", () =>
        {
            foreach (var records in new[] {
                (Usage("a", 302, 2) + Complete(10000, 10)).Replace(Thread, "foreign-thread", StringComparison.Ordinal),
                (Usage("a", 302, 2) + Complete(10000, 10)).Replace(Turn, "foreign-turn", StringComparison.Ordinal) })
            {
                var parser = new TurnAverageParser(Thread); Feed(parser, Header() + Start() + Context() + records);
                Check.False(parser.Diagnostic.UsageSeen || parser.Diagnostic.CompletionSeen || parser.Diagnostic.DurationPresent);
                Check.Equal(0, parser.Diagnostic.ResponseCount);
            }
        });
        test("turn diagnostics: missing start usage checkpoint and duration have distinct fixed reasons", () =>
        {
            var noCheckpoint = Line(2, "token_usage_record", new { thread_id = Thread, turn_id = Turn, response_id = "a", usage = new { output_tokens = 302 } });
            var noDuration = Line(10, "event_msg", new { type = "task_complete", turn_id = Turn });
            foreach (var (records, expected) in new[] {
                (Header() + Context() + Usage("a", 302, 2) + Complete(10000, 10), TurnAverageReason.MissingStart),
                (Header() + Start() + Context() + Complete(10000, 10), TurnAverageReason.MissingUsage),
                (Header() + Start() + Context() + noCheckpoint + Complete(10000, 10), TurnAverageReason.MissingCumulativeUsage),
                (Header() + Start() + Context() + Usage("a", 302, 2) + noDuration, TurnAverageReason.MissingDuration),
                (Header() + Start() + Context() + Usage("a", 302, 2) + Complete(0, 10), TurnAverageReason.InvalidDuration),
                (Header() + Start() + Context() + Usage("a", 302, 2, cumulative: 999) + Complete(10000, 10), TurnAverageReason.UsageMismatch) })
            {
                var parser = new TurnAverageParser(Thread); Feed(parser, records);
                Check.Equal(expected, parser.Diagnostic.Reason);
                Check.True(parser.Diagnostic.CompletionSeen);
                Check.True(parser.Diagnostic.ResponseCount <= 512);
            }
        });
        test("turn diagnostics: in-progress booleans and completed response count require no raw content", () =>
        {
            var parser = new TurnAverageParser(Thread);
            Feed(parser, Header() + Start() + Context() + Usage("a", 302, 2));
            Check.Equal(TurnAverageReason.AwaitingCompletion, parser.Diagnostic.Reason);
            Check.True(parser.Diagnostic.StartSeen && parser.Diagnostic.ModelSeen && parser.Diagnostic.UsageSeen);
            Check.False(parser.Diagnostic.CompletionSeen || parser.Diagnostic.DurationPresent);
            Check.Equal(1, parser.Diagnostic.ResponseCount);
            Feed(parser, Complete(10000, 10));
            Check.Equal(TurnAverageReason.Ready, parser.Diagnostic.Reason);
            Check.True(parser.Diagnostic.DurationPresent);
        });
        test("turn diagnostics: real cancellation and unrecognized item failures remain rejected", () =>
        {
            foreach (var (type, status) in new[] { ("DynamicToolCall", "declined"), ("DynamicToolCall", "in_progress"),
                ("McpToolCall", "inProgress"), ("UnknownTool", "failed"), ("AgentMessage", "failed") })
            {
                var parser = new TurnAverageParser(Thread);
                Feed(parser, Header() + Start() + Context() + Usage("a", 302, 2) +
                    Line(4, "event_msg", new { type = "item_completed", thread_id = Thread, turn_id = Turn, item = new { id = "x", type, status } }) + Complete(10000, 10));
                Check.Equal(TurnAverageReason.ItemStatusRejected, parser.Diagnostic.Reason);
                Check.Equal(TurnAverageReason.ItemStatusRejected, parser.Diagnostic.LastRejectReason);
            }
            var cancelled = new TurnAverageParser(Thread);
            Feed(cancelled, Header() + Start() + Context() + Line(4, "event_msg", new { type = "turn_aborted", turn_id = Turn }) + Complete(10000, 10));
            Check.Equal(TurnAverageReason.TurnCancelled, cancelled.Diagnostic.Reason);
        });
        asyncTest("response diagnostics reader: large bodies retain boundaries and cold tails recover them", async () =>
        {
            using var home = new Home();
            home.Write(Header() + Start() + Context() + Line(1, "response_item", new { type = "function_call_output", call_id = "x", output = new string('x', 140 * 1024) }) +
                Lifecycle(Turn, "a", 302, 9) + Complete(10000, 10));
            var oversized = await home.Reader().PollAsync();
            Check.Equal(37.75, oversized.Sample!.TokensPerSecond!.Value);
            Check.Equal(ResponseRateStartBoundary.ToolResult, oversized.Sample.ResponseRate!.StartBoundary);
            Check.Equal(TurnAverageReason.Ready, oversized.TurnAverageDiagnostic.Reason);
            Check.True(oversized.TurnAverageDiagnostic.CompletionSeen);
            using var tailReader = new RolloutSpeedReader(home.Path, new() { InitialTailBytes = 1024 });
            var tail = await CatchUp(tailReader);
            Check.Equal(37.75, tail.Sample!.TokensPerSecond!.Value);
            Check.Equal(ResponseRateReason.Ready, tail.ResponseRateDiagnostic.Reason);
            Check.Contains("responseRateReason=Ready", OutputMetadataDiagnostic.Format(tail));
        });
        test("turn average: failed or declined tool steps do not fail a later successful complete turn", () =>
        {
            // Paginated official rollouts persist these tool ItemCompleted records.
            // Their status describes the tool attempt, not TurnComplete.error.
            foreach (var (type, status) in new[] { ("CommandExecution", "failed"), ("CommandExecution", "declined"),
                ("DynamicToolCall", "failed"), ("McpToolCall", "failed"), ("FileChange", "failed"), ("FileChange", "declined") })
            {
                var tool = Line(4, "event_msg", new { type = "item_completed", thread_id = Thread, turn_id = Turn,
                    item = new { id = "tool-attempt", type, status, exit_code = 1 } });
                var sample = Parse(Header(), Start(), Context(), Output("a", 2), Usage("a", 200, 2), tool,
                    Output("b", 9), Usage("b", 102, 9, cumulative: 302), Complete(10000, 10));
                Check.Equal(30.2, sample?.TokensPerSecond ?? -1);
            }
        });
        test("turn average: tool failure exemption never covers failed generation or failed whole turns", () =>
        {
            foreach (var failure in new[] {
                Line(4, "event_msg", new { type = "item_completed", thread_id = Thread, turn_id = Turn, item = new { type = "AgentMessage", id = "bad", status = "failed" } }),
                Line(4, "event_msg", new { type = "turn_aborted", thread_id = Thread, turn_id = Turn }),
                Line(4, "event_msg", new { type = "task_failed", thread_id = Thread, turn_id = Turn }) })
                Check.Null(Parse(Header(), Start(), Context(), Output("a", 2), Usage("a", 200, 2), failure,
                    Output("b", 9), Usage("b", 102, 9, cumulative: 302), Complete(10000, 10)));
        });
        test("turn retention: cross-year single-line history time preserves the year", () =>
        {
            var sample = Parse(Valid())! with { IsRetainedTurnAverage = true };
            var view = OutputSpeedView.Create(sample, Epoch.AddYears(1));
            Check.Equal("上次" + sample.CompletedAt.ToLocalTime().ToString("yyyy/M/dd HH:mm") + " ", view.HistoryPrefix);
            Check.Contains("≈ 30.2 tok/s", view.CompactLine);
        });
        asyncTest("response retention: a new turn preserves history until its first response completes", async () =>
        {
            using var home = new Home();
            home.Write(Header() + Setting("priority", 0) + Valid().Substring(Header().Length));
            var reader = home.Reader();
            Check.Equal(151d, (await reader.PollAsync()).Sample!.TokensPerSecond!.Value);
            home.Append(Setting("default", 11) + StartAt("next", 11, "gpt-6.2-sol", "high"));
            var retained = (await reader.PollAsync()).Sample!;
            Check.Equal(151d, retained.TokensPerSecond!.Value);
            Check.Equal("gpt-6.1-sol", retained.Model);
            Check.Equal("medium", retained.ReasoningEffort);
            Check.Equal(ThreadRequestedSetting.Fast, retained.ThreadRequestedSetting);
            var view = OutputSpeedView.Create(retained, Epoch.AddSeconds(21));
            Check.True(view.CompactLine.StartsWith("上次" + Epoch.AddSeconds(2).ToLocalTime().ToString("HH:mm") + " ", StringComparison.Ordinal));
            Check.Contains("⚡ GPT6.1 Sol 中  ≈ 151.0 tok/s", view.CompactLine);
            var diagnostic = OutputMetadataDiagnostic.Format(new(retained, RolloutSpeedReadStatus.Ready));
            Check.Contains("rateAvailable=true sampleSelection=SameThreadCompletedTurn fastSettingScope=CompletionSnapshot", diagnostic);
            Check.Contains("≈ 151.0 tok/s", OutputSpeedView.Create(retained, Epoch.AddHours(1), unavailable: true, pendingWork: true).CompactLine);
            home.Append(Lifecycle("next", "b", 100, 20));
            var next = (await reader.PollAsync()).Sample!;
            Check.Equal("gpt-6.2-sol", next.Model);
            Check.Equal("high", next.ReasoningEffort);
            Check.Equal(100 / 9d, next.TokensPerSecond!.Value);
            Check.Equal(ThreadRequestedSetting.Standard, next.ThreadRequestedSetting);
            Check.False(OutputSpeedView.Create(next, Epoch.AddSeconds(31)).CompactLine.StartsWith("上次", StringComparison.Ordinal));
            home.Append(Complete(19000, 30).Replace(Turn, "next", StringComparison.Ordinal));
            Check.True(next.IsCompatibleWith((await reader.PollAsync()).Sample!));
        });
        asyncTest("turn retention: returning activity to an older thread never rolls back the latest complete history", async () =>
        {
            using var home = new Home(); home.Write(Valid());
            const string other = "77777777-7777-4777-8777-777777777777";
            home.WriteOther(other, (Header() + StartAt("other", 20, "gpt-6.2-sol", "high") +
                Usage("b", 100, 29).Replace(Turn, "other", StringComparison.Ordinal) +
                Complete(10000, 30).Replace(Turn, "other", StringComparison.Ordinal)).Replace(Thread, other, StringComparison.Ordinal));
            var reader = home.Reader(); Check.Equal(other, (await reader.PollAsync()).Sample!.ThreadId);
            home.Append(StartAt("returned", 40));
            var historical = await reader.PollAsync();
            Check.Equal(other, historical.Sample!.ThreadId);
            Check.Equal(100 / 9d, historical.Sample.TokensPerSecond!.Value);
            Check.Equal("gpt-6.2-sol", historical.Sample.Model);
            Check.Contains("sampleSelection=HistoricalOtherThread", OutputMetadataDiagnostic.Format(historical));
            Check.True(OutputSpeedView.Create(historical.Sample, Epoch.AddSeconds(41)).CompactLine.StartsWith("上次", StringComparison.Ordinal));
        });
        asyncTest("turn retention: another active thread keeps a clearly labelled complete historical group", async () =>
        {
            using var home = new Home(); home.Write(Valid());
            var reader = home.Reader(); await reader.PollAsync();
            const string other = "77777777-7777-4777-8777-777777777777";
            home.WriteOther(other, (Header() + StartAt("other", 20)).Replace(Thread, other, StringComparison.Ordinal));
            var history = await reader.PollAsync();
            Check.Equal(Thread, history.Sample!.ThreadId);
            Check.Equal(151d, history.Sample.TokensPerSecond!.Value);
            Check.Contains("sampleSelection=HistoricalOtherThread", OutputMetadataDiagnostic.Format(history));
            Check.True(OutputSpeedView.Create(history.Sample, Epoch.AddSeconds(21)).CompactLine.StartsWith("上次", StringComparison.Ordinal));
            Check.False(history.TurnAverageDiagnostic.UsageSeen || history.TurnAverageDiagnostic.CompletionSeen);
            home.WriteOther(other, (Header() + StartAt("other", 20, "gpt-6.2-sol", "high") + Lifecycle("other", "other-response", 20, 25)).Replace(Thread, other, StringComparison.Ordinal));
            var current = (await reader.PollAsync()).Sample;
            Check.Equal(other, current!.ThreadId);
            Check.Equal("gpt-6.2-sol", current.Model);
            Check.Equal("high", current.ReasoningEffort);
            Check.Equal(4d, current.TokensPerSecond!.Value);
        });
        asyncTest("response retention: contradictory newest usage permits only independently valid history", async () =>
        {
            using var home = new Home(); home.Write(Valid());
            const string other = "77777777-7777-4777-8777-777777777777";
            var otherTurn = Header() + StartAt("other", 20) + Usage("b", 100, 29).Replace(Turn, "other", StringComparison.Ordinal) +
                Complete(10000, 30).Replace(Turn, "other", StringComparison.Ordinal);
            home.WriteOther(other, otherTurn.Replace(Thread, other, StringComparison.Ordinal));
            var reader = home.Reader();
            Check.Equal(other, (await reader.PollAsync()).Sample!.ThreadId);
            home.WriteOther(other, (otherTurn + Usage("b", 101, 31).Replace(Turn, "other", StringComparison.Ordinal)).Replace(Thread, other, StringComparison.Ordinal));
            var history = await reader.PollAsync();
            Check.Equal(Thread, history.Sample!.ThreadId);
            Check.Equal(151d, history.Sample.TokensPerSecond!.Value);
            Check.Contains("sampleSelection=HistoricalOtherThread", OutputMetadataDiagnostic.Format(history));
            home.Append(Usage("a", 303, 32));
            Check.Null((await reader.PollAsync()).Sample?.TokensPerSecond);
        });
        asyncTest("turn retention: equal-time cross-thread starts or completions remain ambiguous until newer evidence", async () =>
        {
            foreach (var otherRecords in new[] { Header() + StartAt("other", 10), Valid() })
            {
                using var home = new Home(); home.Write(Valid());
                const string other = "77777777-7777-4777-8777-777777777777";
                home.WriteOther(other, otherRecords.Replace(Thread, other, StringComparison.Ordinal));
                var reader = home.Reader(); var ambiguous = await reader.PollAsync();
                Check.Equal(151d, ambiguous.Sample!.TokensPerSecond!.Value);
                Check.True(ambiguous.Sample.IsRetainedTurnAverage);
                Check.Contains("sampleSelection=HistoricalOtherThread", OutputMetadataDiagnostic.Format(ambiguous));
                Check.True(ambiguous.ActivityAmbiguous);
                Check.Contains("activityAmbiguous=true", OutputMetadataDiagnostic.Format(ambiguous));
                home.WriteOther(other, (Header() + StartAt("later", 20) + Usage("c", 100, 29).Replace(Turn, "later", StringComparison.Ordinal) +
                    Complete(10000, 30).Replace(Turn, "later", StringComparison.Ordinal)).Replace(Thread, other, StringComparison.Ordinal));
                var resolved = await reader.PollAsync();
                Check.False(resolved.ActivityAmbiguous);
                Check.Equal(other, resolved.Sample!.ThreadId);
                Check.Equal(100 / 9d, resolved.Sample.TokensPerSecond!.Value);
            }
        });
        asyncTest("turn retention: foreign sections and far future starts cannot steal activity ownership", async () =>
        {
            foreach (var otherRecords in new[] { Header() + StartAt("foreign", 20),
                (Header() + StartAt("future", (DateTimeOffset.UtcNow.AddDays(1) - Epoch).TotalSeconds)).Replace(Thread, "77777777-7777-4777-8777-777777777777", StringComparison.Ordinal) })
            {
                using var home = new Home(); home.Write(Valid());
                home.WriteOther("77777777-7777-4777-8777-777777777777", otherRecords);
                Check.Equal(Thread, (await home.Reader().PollAsync()).Sample!.ThreadId);
            }
        });
        asyncTest("response retention: later damage or cancellation never undoes a completed response", async () =>
        {
            foreach (var ending in new[] { "{broken}\n", Line(25, "event_msg", new { type = "turn_aborted", turn_id = "next" }) })
            {
                using var home = new Home(); home.Write(Valid()); var reader = home.Reader(); await reader.PollAsync();
                home.Append(StartAt("next", 11) + Lifecycle("next", "b", 100, 20) + ending);
                var completed = (await reader.PollAsync()).Sample!;
                Check.Equal(100 / 9d, completed.TokensPerSecond!.Value);
                Check.Equal("b", completed.ResponseId);
                home.Append(Line(5, "event_msg", new { type = "turn_aborted", thread_id = Thread, turn_id = Turn }));
                Check.True(completed.IsCompatibleWith((await reader.PollAsync()).Sample!));
            }
        });
        asyncTest("response retention: archived later context or cancellation does not rewrite completed history", async () =>
        {
            foreach (var contradiction in new[] { Line(11, "event_msg", new { type = "turn_aborted", thread_id = Thread, turn_id = Turn }),
                Context("gpt-other", seconds: 5), Context(effort: "high", seconds: 5) })
            {
                using var home = new Home(); home.Write(Valid()); var reader = home.Reader(); await reader.PollAsync();
                home.Append(StartAt("next", 11));
                home.Archive(Header() + contradiction);
                var retained = (await reader.PollAsync()).Sample!;
                Check.Equal(151d, retained.TokensPerSecond!.Value);
                Check.Equal("gpt-6.1-sol", retained.Model); Check.Equal("medium", retained.ReasoningEffort);
            }
        });
        asyncTest("response retention: archive effort conflict clears only the disputed label permanently", async () =>
        {
            using var home = new Home(); home.Write(Valid());
            home.Archive(Valid().Replace("medium", "high", StringComparison.Ordinal));
            using var reader = home.Reader();
            var conflicted = (await reader.PollAsync()).Sample!;
            Check.Equal(151d, conflicted.TokensPerSecond!.Value); Check.Null(conflicted.ReasoningEffort);
            // A third matching copy cannot resurrect either disputed effort.
            home.ArchiveCopy(Valid());
            var replayed = (await reader.PollAsync()).Sample!;
            Check.Equal(151d, replayed.TokensPerSecond!.Value); Check.Null(replayed.ReasoningEffort);
        });
        asyncTest("turn retention: conflicting completion setting copies stay unknown in either order and on replay", async () =>
        {
            foreach (var first in new[] { "priority", "default" })
            {
                using var home = new Home();
                home.Write(Header() + Setting(first, 0) + Valid().Substring(Header().Length));
                home.Archive(Header() + Setting(first == "priority" ? "default" : "priority", 0) + Valid().Substring(Header().Length));
                var reader = home.Reader(); var result = await reader.PollAsync();
                Check.Equal(ThreadRequestedSetting.Unknown, result.Sample!.ThreadRequestedSetting);
                Check.Equal(151d, result.Sample.TokensPerSecond!.Value);
                home.ArchiveCopy(Header() + Setting("priority", 0) + Valid().Substring(Header().Length));
                var replayed = (await reader.PollAsync()).Sample!;
                Check.Equal(ThreadRequestedSetting.Unknown, replayed.ThreadRequestedSetting);
                Check.Equal("", OutputSpeedView.Create(replayed, Epoch.AddSeconds(12)).FastPrefix);
            }
        });
        asyncTest("response retention: same-thread next start at identical response completion is historical", async () =>
        {
            using var home = new Home(); home.Write(Valid()); var reader = home.Reader(); await reader.PollAsync();
            home.Append(StartAt("next", 2));
            var retained = (await reader.PollAsync()).Sample!;
            Check.True(retained.IsRetainedTurnAverage);
            Check.Equal(151d, retained.TokensPerSecond!.Value);
            Check.True(OutputSpeedView.Create(retained, Epoch.AddSeconds(11)).CompactLine.StartsWith("上次", StringComparison.Ordinal));
        });
        asyncTest("response retention: bounded churn updates completed responses without waiting for turns", async () =>
        {
            using var home = new Home(); home.Write(Valid());
            var reader = new RolloutSpeedReader(home.Path, new() { DiscoveryInterval = TimeSpan.Zero, MaxRememberedSamples = 4 });
            await reader.PollAsync();
            for (var index = 0; index < 20; index++)
            {
                home.Append(StartAt("unfinished-" + index, 20 + index * 2) + Lifecycle("unfinished-" + index, "response-" + index, 100, 21 + index * 2));
                var newest = (await reader.PollAsync()).Sample!;
                Check.Equal(100d, newest.TokensPerSecond!.Value);
                Check.Equal("response-" + index, newest.ResponseId);
                var cached = (System.Collections.IDictionary)typeof(RolloutSpeedReader).GetField("samples", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(reader)!;
                Check.True(cached.Count <= 4, "Retention must not expand the configured sample budget.");
            }
            var restarted = (await home.Reader().PollAsync()).Sample!;
            Check.Equal(100d, restarted.TokensPerSecond!.Value); Check.Equal("response-19", restarted.ResponseId);
        });
        asyncTest("response retention: a one-sample budget still follows completed responses across threads", async () =>
        {
            using var home = new Home(); home.Write(Header() + Setting("priority", 0) + Valid().Substring(Header().Length));
            var reader = new RolloutSpeedReader(home.Path, new() { DiscoveryInterval = TimeSpan.Zero, MaxRememberedSamples = 1 });
            await reader.PollAsync();
            const string other = "77777777-7777-4777-8777-777777777777";
            var records = Header() + Setting("default", 11);
            for (var index = 0; index < 12; index++)
            {
                var id = "unfinished-" + index;
                records += StartAt(id, 20 + index * 2, "gpt-6.2-sol", "high") + Lifecycle(id, "response-" + index, 100, 21 + index * 2);
                home.WriteOther(other, records.Replace(Thread, other, StringComparison.Ordinal));
                var result = await reader.PollAsync();
                Check.Equal(100d, result.Sample!.TokensPerSecond!.Value);
                Check.Equal("response-" + index, result.Sample.ResponseId);
                Check.Equal(ThreadRequestedSetting.Standard, result.Sample.ThreadRequestedSetting);
                Check.Equal("gpt-6.2-sol", result.Sample.Model);
                Check.Equal("high", result.Sample.ReasoningEffort);
                Check.Contains("sampleSelection=Latest", OutputMetadataDiagnostic.Format(result));
                var cache = (System.Collections.IDictionary)typeof(RolloutSpeedReader).GetField("samples", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(reader)!;
                Check.Equal(1, cache.Count);
            }
            records += Complete(10000, 50).Replace(Turn, "unfinished-11", StringComparison.Ordinal);
            home.WriteOther(other, records.Replace(Thread, other, StringComparison.Ordinal));
            var newer = (await reader.PollAsync()).Sample!;
            Check.Equal(other, newer.ThreadId);
            Check.Equal(100d, newer.TokensPerSecond!.Value);
            Check.Equal("gpt-6.2-sol", newer.Model);
            Check.Equal(ThreadRequestedSetting.Standard, newer.ThreadRequestedSetting);
        });
        asyncTest("turn retention: an explicit foreign identity section withdraws this file's completed evidence", async () =>
        {
            using var home = new Home(); home.Write(Valid()); var reader = home.Reader(); await reader.PollAsync();
            home.Append(Header().Replace(Thread, "foreign", StringComparison.Ordinal));
            Check.Null((await reader.PollAsync()).Sample?.TokensPerSecond);
        });
        test("turn average: scoped remote compaction usage needs no ordinary output item", () =>
        {
            var sample = Parse(Header(), Start(), Context(), Usage("compaction", 200, 2, 150),
                Output("a", 9), Usage("a", 102, 9, cumulative: 302), Complete(10000, 10));
            Check.True(sample is not null, "A complete scoped compaction ledger must remain measurable without ordinary output items.");
            Check.Equal(302L, sample!.OutputTokens);
            Check.Equal(30.2, sample.TokensPerSecond!.Value);
        });
        test("turn average: legacy context without turn id binds only to an explicit undamaged active turn", () =>
        {
            var context = Line(0, "turn_context", new { model = "gpt-6.1-sol", effort = "medium" });
            var sample = Parse(Header(), Start(), context, Output("a", 2), Usage("a", 302, 2), Complete(10000, 10));
            Check.True(sample is not null, "A legal unscoped context must bind to its observed pristine task start.");
            Check.Equal(30.2, sample!.TokensPerSecond!.Value);
            Check.Null(Parse(Header(), context, Output("a", 2), Usage("a", 302, 2), Complete(10000, 10)));
            Check.Null(Parse(Header(), Start(), "{broken}\n", context, Output("a", 2), Usage("a", 302, 2), Complete(10000, 10)));
        });
        test("metadata parser: legal absent or null context turn ID binds only to explicit pristine start", () =>
        {
            foreach (var context in new[] { Line(0, "turn_context", new { model = "gpt-6.1-sol", effort = "medium" }),
                Line(0, "turn_context", new { turn_id = (string?)null, model = "gpt-6.1-sol", effort = "medium" }) })
            {
                var timing = Line(2, "event_msg", new { type = "item_completed", thread_id = Thread, turn_id = Turn,
                    item = new { id = "a", type = "AgentMessage" }, started_at_ms = Epoch.AddSeconds(1).ToUnixTimeMilliseconds(),
                    completed_at_ms = Epoch.AddSeconds(2).ToUnixTimeMilliseconds() });
                var parser = new RolloutSpeedParser(Thread);
                foreach (var line in (Header() + Start() + context + timing + Output("a", 2) + Usage("a", 302, 2)).Split('\n').Where(value => value.Length > 0)) parser.Feed(line);
                Check.Equal("gpt-6.1-sol", parser.LatestSample!.Model);
                Check.Equal("medium", parser.LatestSample.ReasoningEffort);
                Check.Null(parser.LatestSample.TokensPerSecond);
                Check.Equal(30.2, Parse(Header(), Start(), context, Output("a", 2), Usage("a", 302, 2), Complete(10000, 10))!.TokensPerSecond!.Value);
            }
            foreach (var context in new[] { Line(0, "turn_context", new { turn_id = "foreign", model = "gpt-6.1-sol" }),
                Line(0, "turn_context", new { turn_id = 12, model = "gpt-6.1-sol" }),
                Context().Replace("\"turn_id\":", "\"turn_id\":null,\"turn_id\":", StringComparison.Ordinal) })
                Check.Null(Parse(Header(), Start(), context, Output("a", 2), Usage("a", 302, 2), Complete(10000, 10)));
        });
        asyncTest("Fast setting reader: lightning means thread preference and never changes response rate", async () =>
        {
            foreach (var tier in new[] { "priority", "fast" })
            {
                using var home = new Home();
                home.Write(Header() + Setting(tier, 0) + Valid().Substring(Header().Length));
                var sample = (await home.Reader().PollAsync()).Sample!;
                Check.Equal(ThreadRequestedSetting.Fast, sample.ThreadRequestedSetting);
                Check.Equal(151d, sample.TokensPerSecond!.Value);
                var view = OutputSpeedView.Create(sample, Epoch.AddSeconds(10));
                Check.Equal("⚡ GPT6.1 Sol 中  ≈ 151.0 tok/s", view.CompactLine);
                Check.Contains("turnTierUnknown", view.AccessibleText);
                Check.Contains("ThreadRequestedSetting", view.AccessibleText);
                Check.Contains("threadSetting=Fast", OutputMetadataDiagnostic.Format(new(sample, RolloutSpeedReadStatus.Ready)));
            }
        });
        asyncTest("Fast setting reader: completed snapshot is never relabelled by later settings or unrelated damage", async () =>
        {
            foreach (var suffix in new[] { Setting("default", 11), Setting("standard", 11), Setting("off", 11),
                Setting("unknown-new-tier", 11), Setting(null, 11), "{broken}\n", Setting("priority", 11).Replace(Thread, "foreign", StringComparison.Ordinal) })
            {
                using var home = new Home();
                home.Write(Header() + Setting("priority", 0) + Valid().Substring(Header().Length));
                var reader = home.Reader();
                Check.Equal(ThreadRequestedSetting.Fast, (await reader.PollAsync()).Sample!.ThreadRequestedSetting);
                home.Append(suffix);
                var sample = (await reader.PollAsync()).Sample!;
                Check.Equal(ThreadRequestedSetting.Fast, sample.ThreadRequestedSetting);
                Check.Equal("⚡ ", OutputSpeedView.Create(sample, Epoch.AddSeconds(12)).FastPrefix);
                Check.Contains("fastSettingScope=CompletionSnapshot", OutputMetadataDiagnostic.Format(new(sample, RolloutSpeedReadStatus.Ready)));
                Check.Equal(151d, sample.TokensPerSecond!.Value);
            }
        });
        asyncTest("Fast setting reader: unverified metadata still clears stale latest-thread lightning", async () =>
        {
            using var home = new Home();
            // A scoped model and lifecycle metadata do not reconstruct an unseen request start.
            home.Write(Header() + Setting("priority", 0) + Context() + Lifecycle(Turn, "a", 302, 2));
            var reader = home.Reader();
            Check.Equal(ThreadRequestedSetting.Fast, (await reader.PollAsync()).Sample!.ThreadRequestedSetting);
            home.Append(Setting(null, 11));
            var sample = (await reader.PollAsync()).Sample!;
            Check.Equal(ThreadRequestedSetting.Unknown, sample.ThreadRequestedSetting);
            Check.Null(sample.TokensPerSecond);
        });
        asyncTest("Fast setting reader: another thread and changed global-looking fields never supply lightning", async () =>
        {
            using var home = new Home();
            home.Write(Valid());
            const string other = "77777777-7777-4777-8777-777777777777";
            home.WriteOther(other, (Header() + Setting("priority", 0)).Replace(Thread, other, StringComparison.Ordinal));
            var sample = (await home.Reader().PollAsync()).Sample!;
            Check.Equal(ThreadRequestedSetting.Unknown, sample.ThreadRequestedSetting);
            Check.Equal("", OutputSpeedView.Create(sample, Epoch.AddSeconds(11)).FastPrefix);
        });
        asyncTest("response reader: exact usage takes priority over a later complete whole-turn average", async () =>
        {
            using var home = new Home();
            home.Write(Header() + Start() + Context() + Output("a", 2) + Usage("a", 302, 2) + Complete(10000, 10));
            var result = await home.Reader().PollAsync();
            Check.Equal(151d, result.Sample!.TokensPerSecond!.Value);
            Check.Equal(OutputSpeedTimingReliability.ResponseRateEstimate, result.Sample.TimingReliability);
            Check.Equal(TurnAverageReason.Ready, result.TurnAverageDiagnostic.Reason);
        });
        asyncTest("response reader: reliable whole-turn timing remains a fallback when response timing is unavailable", async () =>
        {
            using var home = new Home();
            // Equal rollout timestamps cannot establish a response denominator.
            // The separate official whole-turn duration still has complete usage evidence.
            home.Write(Header() + Start() + Context() + Usage("a", 302, 0) + Complete(10000, 10));
            var result = await home.Reader().PollAsync();
            Check.Equal(OutputSpeedTimingReliability.TurnAverage, result.Sample!.TimingReliability);
            Check.Equal(30.2, result.Sample.TokensPerSecond!.Value);
            Check.Equal(ResponseRateReason.InvalidDuration, result.ResponseRateDiagnostic.Reason);
        });

        test("turn average: official duration is separate from generation and wall-clock interval", () =>
        {
            var sample = Parse(Header(), Start(), Context(), Output("a", 2), Usage("a", 302, 2, 200), Complete(10000, 100));
            Check.Equal(OutputSpeedTimingReliability.TurnAverage, sample!.TimingReliability);
            Check.Equal(30.2, sample.TokensPerSecond!.Value);
            Check.Null(sample.GenerationDuration);
            Check.Equal(100d, sample.ObservedInterval.TotalSeconds);
            Check.Equal(200L, sample.TurnAverage!.Responses.Single().ReasoningOutputTokens!.Value);
            var view = OutputSpeedView.Create(sample, Epoch.AddSeconds(101));
            Check.Equal("GPT6.1 Sol 中  ≈ 30.2 tok/s", view.CompactLine);
            Check.False(view.CompactLine.Contains("·", StringComparison.Ordinal));
            Check.Contains("TurnAverage", view.AccessibleText);
            Check.Contains("工具执行", view.AccessibleText);
            Check.Contains("不是模型纯生成 TPS", view.AccessibleText);
            Check.Contains("WholeTurnIncludingWaitsAndTools", OutputMetadataDiagnostic.Format(new(sample, RolloutSpeedReadStatus.Ready)));
        });
        test("turn average: every response contributes once while reasoning is already included", () =>
        {
            var sample = Parse(Header(), Start(), Context(), Output("a", 2), Usage("a", 200, 2, 150),
                Usage("a", 200, 3, 150), Output("b", 9), Usage("b", 102, 9, 50, 302), Complete(10000, 10));
            Check.Equal(302L, sample!.OutputTokens);
            Check.Equal(2, sample.TurnAverage!.Responses.Count);
            Check.Equal(30.2, sample.TokensPerSecond!.Value);
        });
        test("turn average: timestamp rollback cannot hide usage after completion", () =>
        {
            Check.Null(Parse(Header(), Start(), Context(), Usage("a", 200, 20),
                Usage("b", 102, 9, cumulative: 302), Complete(10000, 10)));
        });
        test("turn average: missing start context usage checkpoint or completion never manufactures data", () =>
        {
            foreach (var text in new[]
            {
                Header() + Context() + Output("a", 2) + Usage("a", 302, 2) + Complete(10000, 10),
                Header() + Start() + Output("a", 2) + Usage("a", 302, 2) + Complete(10000, 10),
                Header() + Start() + Context() + Output("a", 2) + Complete(10000, 10),
                Header() + Start() + Context() + Output("a", 2) + Usage("a", 302, 2),
                Header() + Start() + Context() + Output("a", 2) + Line(2, "token_usage_record", new { thread_id = Thread, turn_id = Turn, response_id = "a", usage = new { output_tokens = 302 } }) + Complete(10000, 10),
                Header() + Start() + Context() + Output("a", 2) + Line(2, "event_msg", new { type = "token_count", info = new { last_token_usage = new { output_tokens = 302 } } }) + Complete(10000, 10)
            }) Check.Null(Parse(text));
        });
        test("turn average: missing invalid duplicate or failed official duration never falls back", () =>
        {
            foreach (var ending in new[]
            {
                Line(10, "event_msg", new { type = "task_complete", turn_id = Turn }),
                Complete(0, 10), Complete(-1, 10), Complete(long.MaxValue, 10),
                Complete(10000, 10).Replace("10000", "\"10000\"", StringComparison.Ordinal),
                Complete(10000, 10).Replace("\"duration_ms\":", "\"duration_ms\":2000,\"duration_ms\":", StringComparison.Ordinal),
                Line(10, "event_msg", new { type = "task_complete", turn_id = Turn, duration_ms = 10000, error = new { message = "synthetic failure" } }),
                Line(10, "event_msg", new { type = "task_complete", turn_id = Turn, duration_ms = 10000, status = "failed" }),
                Complete(10000, 10).Replace(Turn, "different-turn", StringComparison.Ordinal)
            }) Check.Null(Parse(Header(), Start(), Context(), Output("a", 2), Usage("a", 302, 2), ending));
        });
        test("turn average: cancellation stream errors gaps and unsealed output reject whole turn", () =>
        {
            foreach (var middle in new[]
            {
                Line(5, "event_msg", new { type = "turn_aborted", turn_id = Turn }),
                Line(5, "event_msg", new { type = "task_failed", turn_id = Turn }),
                Line(5, "event_msg", new { type = "stream_error" }), "{broken}\n",
                Output("unsealed", 9)
            }) Check.Null(Parse(Header(), Start(), Context(), Output("a", 2), Usage("a", 302, 2), middle, Complete(10000, 10)));
            var parser = new TurnAverageParser(Thread);
            Feed(parser, Header() + Start() + Context() + Output("a", 2) + Usage("a", 302, 2));
            parser.ResetPending();
            Check.Null(Feed(parser, Complete(10000, 10)));
        });
        test("turn average: model or effort changes never label a mixed turn with one model", () =>
        {
            foreach (var changed in new[] { Context("gpt-other", seconds: 4), Context(effort: "high", seconds: 4),
                Line(4, "event_msg", new { type = "thread_settings_applied", thread_id = Thread,
                    thread_settings = new { model = "gpt-other", reasoning_effort = "medium" } }) })
                Check.Null(Parse(Header(), Start(), Context(), Output("a", 2), Usage("a", 100, 2), changed,
                    Output("b", 9), Usage("b", 202, 9, cumulative: 302), Complete(10000, 10)));
        });
        test("turn average: usage gaps impossible reasoning counters and arithmetic overflow reject", () =>
        {
            foreach (var usage in new[] { Usage("a", 302, 2, 303), Usage("a", -1, 2), Usage("a", 302, 2, cumulative: 400),
                Usage("a", 302, 2).Replace(Thread, "foreign", StringComparison.Ordinal),
                Usage("a", 302, 2).Replace(Turn, "foreign", StringComparison.Ordinal),
                Usage("a", 302, 2).Replace("\"output_tokens\":", "\"output_tokens\":9,\"output_tokens\":", StringComparison.Ordinal) })
                Check.Null(Parse(Header(), Start(), Context(), Output("a", 2), usage, Complete(10000, 10)));
            Check.Null(Parse(Header(), Start(), Context(), Output("a", 2), Usage("a", long.MaxValue, 2),
                Output("b", 9), Usage("b", 1, 9, cumulative: long.MaxValue), Complete(10000, 10)));
        });
        test("turn average: late conflicting duplicate usage checkpoint or completion withdraws prior turn", () =>
        {
            foreach (var late in new[] { Usage("a", 303, 11), Usage("a", 302, 11, cumulative: 303),
                Context("gpt-other", seconds: 11), Complete(11000, 11) })
            {
                var parser = new TurnAverageParser(Thread);
                Check.True(Feed(parser, Valid())?.TokensPerSecond is not null);
                Check.Null(Feed(parser, late));
                Check.True(parser.DrainInvalidatedTurnIds().Contains(Turn));
            }
        });
        test("turn average: exact duplicate completion is idempotent and tiny valid official duration is not clamped", () =>
        {
            var parser = new TurnAverageParser(Thread);
            var first = Feed(parser, Valid());
            var second = Feed(parser, Complete(10000, 10));
            Check.True(first!.IsCompatibleWith(second!));
            Check.Equal(0, parser.DrainInvalidatedTurnIds().Count);
            var shortSample = Parse(Header(), Start(), Context(), Output("a", .001), Usage("a", 1, .001), Complete(1, .001));
            Check.Equal(1000d, shortSample!.TokensPerSecond!.Value);
        });
        test("turn average: inherited parent history and following turns never aggregate", () =>
        {
            Check.Null(Parse(Valid().Replace(Thread, "foreign", StringComparison.Ordinal)));
            var parser = new TurnAverageParser(Thread);
            Check.Equal(302L, Feed(parser, Valid())!.OutputTokens);
            var next = Valid().Replace(Turn, "second-turn", StringComparison.Ordinal).Replace("302", "100", StringComparison.Ordinal);
            Check.Equal(100L, Feed(parser, next)!.OutputTokens);
        });
        asyncTest("response reader: completion timestamp tie prefers exact response over turn and lifecycle", async () =>
        {
            using var home = new Home();
            var lifecycle = Line(10, "event_msg", new { type = "item_completed", thread_id = Thread, turn_id = Turn,
                item = new { id = "a", type = "AgentMessage" }, started_at_ms = Epoch.AddSeconds(9.75).ToUnixTimeMilliseconds(),
                completed_at_ms = Epoch.AddSeconds(10).ToUnixTimeMilliseconds() });
            home.Write(Header() + Start() + Context() + lifecycle + Output("a", 10) + Usage("a", 1000, 10) + Complete(10000, 10));
            var sample = (await home.Reader().PollAsync()).Sample;
            Check.Equal(OutputSpeedTimingReliability.ResponseRateEstimate, sample!.TimingReliability);
            Check.Equal(100d, sample.TokensPerSecond!.Value);
        });
        asyncTest("response reader: partial usage line completes only once and idle polls preserve it", async () =>
        {
            using var home = new Home();
            home.Write(Header() + Start() + Context() + Output("a", 2) + Usage("a", 302, 2).TrimEnd('\n'));
            var reader = home.Reader();
            Check.Null((await reader.PollAsync()).Sample?.TokensPerSecond);
            home.Append("\n");
            var completed = (await reader.PollAsync()).Sample;
            Check.Equal(151d, completed!.TokensPerSecond!.Value);
            Check.True(completed.IsCompatibleWith((await reader.PollAsync()).Sample!));
        });
        asyncTest("response reader: cold tail recovers its real start and truncate clears cached value", async () =>
        {
            using var home = new Home();
            home.Write(Valid());
            using var limited = new RolloutSpeedReader(home.Path, new() { InitialTailBytes = 128 });
            Check.Equal(151d, (await CatchUp(limited)).Sample!.TokensPerSecond!.Value);
            var reader = home.Reader();
            Check.Equal(151d, (await reader.PollAsync()).Sample!.TokensPerSecond!.Value);
            home.Write(Header());
            Check.Null((await reader.PollAsync()).Sample?.TokensPerSecond);
        });
        asyncTest("response reader: contradictory archive usage invalidates response and dependent turn average", async () =>
        {
            using var home = new Home();
            home.Write(Valid());
            var reader = home.Reader();
            Check.Equal(151d, (await reader.PollAsync()).Sample!.TokensPerSecond!.Value);
            home.Archive(Header() + Usage("a", 303, 11));
            var both = new RolloutSpeedReader(home.Path);
            Check.Null((await both.PollAsync()).Sample?.TokensPerSecond);
        });
        asyncTest("turn average reader: cumulative-only archive contradiction is not hidden by equal response counts", async () =>
        {
            using var home = new Home();
            home.Write(Valid());
            home.Archive(Header() + Usage("a", 302, 11, cumulative: 303));
            Check.Null((await home.Reader().PollAsync()).Sample?.TokensPerSecond);
        });
        asyncTest("response reader: archive copies dedupe and newer response threads are selected not summed", async () =>
        {
            using var home = new Home();
            home.Write(Valid()); home.Archive(Valid());
            Check.Equal(302L, (await home.Reader().PollAsync()).Sample!.OutputTokens);
            const string other = "77777777-7777-4777-8777-777777777777";
            home.WriteOther(other, (Header() + Start() + Context() + Output("a", 4) + Usage("a", 200, 4) + Complete(10000, 11))
                .Replace(Thread, other, StringComparison.Ordinal));
            var newest = (await home.Reader().PollAsync()).Sample;
            Check.Equal(other, newest!.ThreadId);
            Check.Equal(200L, newest.OutputTokens);
            Check.Equal(50d, newest.TokensPerSecond!.Value);
        });
    }

    private static string Valid() => Header() + Start() + Context() + Output("a", 2) + Usage("a", 302, 2) + Complete(10000, 10);
    private static string StartAt(string turn, double seconds, string model = "gpt-6.1-sol", string effort = "medium") =>
        Line(seconds, "event_msg", new { type = "task_started", thread_id = Thread, turn_id = turn }) +
        Line(seconds, "turn_context", new { thread_id = Thread, turn_id = turn, model, effort });
    private static string Lifecycle(string turn, string id, long count, double seconds) =>
        Line(seconds, "event_msg", new { type = "item_completed", thread_id = Thread, turn_id = turn,
            item = new { id, type = "AgentMessage" }, started_at_ms = Epoch.AddSeconds(seconds - 1).ToUnixTimeMilliseconds(),
            completed_at_ms = Epoch.AddSeconds(seconds).ToUnixTimeMilliseconds() }) + Output(id, seconds) +
        Usage(id, count, seconds).Replace(Turn, turn, StringComparison.Ordinal);
    private static OutputSpeedSample? Parse(params string[] records) => Feed(new TurnAverageParser(Thread), string.Concat(records));
    private static OutputSpeedSample? Feed(TurnAverageParser parser, string records)
    {
        OutputSpeedSample? result = null;
        foreach (var line in records.Split('\n').Where(value => value.Length > 0)) result = parser.Feed(line) ?? result;
        return result;
    }

    private static async Task<RolloutSpeedReadResult> CatchUp(RolloutSpeedReader reader)
    {
        var result = await reader.PollAsync();
        // This is a finite static synthetic file. A test limit detects a stuck
        // recovery loop; it does not turn an incomplete read into a success.
        for (var poll = 0; result.CanCatchUp && poll < 32; poll++) result = await reader.PollAsync();
        Check.False(result.CanCatchUp, "Synthetic history recovery must finish within its bounded fixture.");
        return result;
    }

    private static string Setting(string? tier, double seconds) => Line(seconds, "event_msg", new { type = "thread_settings_applied", thread_id = Thread,
        thread_settings = new { model = "gpt-6.1-sol", reasoning_effort = "medium", service_tier = tier } });
    private static string Header() => Line(0, "session_meta", new { id = Thread });
    private static string Start() => Line(0, "event_msg", new { type = "task_started", thread_id = Thread, turn_id = Turn });
    private static string Context(string model = "gpt-6.1-sol", string effort = "medium", double seconds = 0) =>
        Line(seconds, "turn_context", new { thread_id = Thread, turn_id = Turn, model, effort });
    private static string Output(string id, double seconds) =>
        Line(seconds, "response_item", new { type = "message", role = "assistant", id });
    private static string Usage(string id, long count, double seconds, long reasoning = 0, long? cumulative = null) =>
        Line(seconds, "token_usage_record", new { thread_id = Thread, turn_id = Turn, response_id = id,
            usage = new { output_tokens = count, reasoning_output_tokens = reasoning },
            turn_token_usage = new { output_tokens = cumulative ?? count } });
    private static string Complete(long duration, double seconds) =>
        Line(seconds, "event_msg", new { type = "task_complete", thread_id = Thread, turn_id = Turn, duration_ms = duration });
    private static string Line(double seconds, string type, object payload) =>
        JsonSerializer.Serialize(new { timestamp = Epoch.AddSeconds(seconds).ToString("O"), type, payload }) + "\n";

    private sealed class Home : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quota-average-synthetic-" + Guid.NewGuid().ToString("N"));
        public Home() => Directory.CreateDirectory(System.IO.Path.Combine(Path, "sessions"));
        public void Write(string content) => File.WriteAllText(System.IO.Path.Combine(Path, "sessions", "rollout-2026-10-12-" + Thread + ".jsonl"), content);
        public void Append(string content) => File.AppendAllText(System.IO.Path.Combine(Path, "sessions", "rollout-2026-10-12-" + Thread + ".jsonl"), content);
        public void Archive(string content)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "archived_sessions"));
            File.WriteAllText(System.IO.Path.Combine(Path, "archived_sessions", "rollout-2026-10-12-" + Thread + ".jsonl"), content);
        }
        public void ArchiveCopy(string content)
        {
            var directory = System.IO.Path.Combine(Path, "archived_sessions", "third-copy");
            Directory.CreateDirectory(directory);
            File.WriteAllText(System.IO.Path.Combine(directory, "rollout-2026-10-12-" + Thread + ".jsonl"), content);
        }
        public void WriteOther(string thread, string content) => File.WriteAllText(System.IO.Path.Combine(Path, "sessions", "rollout-2026-10-12-" + thread + ".jsonl"), content);
        public RolloutSpeedReader Reader() => new(Path, new() { DiscoveryInterval = TimeSpan.Zero });
        public void Dispose() => Directory.Delete(Path, true);
    }
}
