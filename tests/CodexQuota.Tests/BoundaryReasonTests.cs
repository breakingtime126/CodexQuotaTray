using System.Text;
using System.Text.Json;
using CodexQuota.Core;

internal static class BoundaryReasonTests
{
    private const string Thread = "11111111-1111-4111-8111-111111111111";
    private const string Turn = "synthetic-turn";
    private static readonly DateTimeOffset Epoch = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SampleStart = Epoch.AddSeconds(10);

    public static void Register(Action<string, Action> test)
    {
        test("model boundary reason: malformed JSON and UTF8 have distinct fixed reasons", () =>
        {
            AssertBoundary("{broken}\n", RolloutModelBoundaryReason.InvalidJson);
            var bytes = Encoding.UTF8.GetBytes(Header() + Context()).Concat(new byte[] { 0xc3, 0x28, (byte)'\n' })
                .Concat(Encoding.UTF8.GetBytes(Seal())).ToArray();
            using var stream = new MemoryStream(bytes, false);
            var recovery = Create(stream);
            ScanAll(recovery, stream);
            AssertStopped(recovery, RolloutModelBoundaryReason.InvalidUtf8);
        });

        test("model boundary reason: malformed envelopes and timestamps never fall back to older context", () =>
        {
            foreach (var record in new[]
            {
                "[]\n", "{}\n",
                "{\"timestamp\":\"2026-10-02T10:00:00Z\",\"type\":\"event_msg\",\"payload\":[]}\n",
                Line("event_msg", new { type = "agent_reasoning" }).Replace("\"type\":\"event_msg\"",
                    "\"type\":\"session_meta\",\"type\":\"event_msg\"", StringComparison.Ordinal)
            }) AssertBoundary(record, RolloutModelBoundaryReason.InvalidEnvelope);
            foreach (var timestamp in new[] { "invalid", "2026-10-02T10:00:00", "" })
                AssertBoundary(JsonSerializer.Serialize(new { timestamp, type = "turn_context",
                    payload = new { turn_id = Turn, model = "synthetic-model" } }) + "\n",
                    RolloutModelBoundaryReason.InvalidTimestamp);
            AssertBoundary(Line("turn_context", new { turn_id = Turn, model = "synthetic-model" })
                .Replace("\"timestamp\":", "\"timestamp\":\"invalid\",\"timestamp\":", StringComparison.Ordinal),
                RolloutModelBoundaryReason.InvalidTimestamp);
        });

        test("model boundary reason: session turn thread and model rejection remain distinguishable", () =>
        {
            foreach (var record in new[] { Line("session_meta", new { id = "foreign-thread" }), Line("session_meta", new { }) })
                AssertBoundary(record, RolloutModelBoundaryReason.SessionMismatch);
            foreach (var kind in new[] { "turn_context", "task_started" })
            {
                var envelope = kind == "turn_context" ? "turn_context" : "event_msg";
                AssertBoundary(Line(envelope, new { type = kind, model = "synthetic-model" }),
                    RolloutModelBoundaryReason.InvalidTurnIdentity);
                AssertBoundary(Line(envelope, new { type = kind, turn_id = Turn, thread_id = "foreign-thread", model = "synthetic-model" }),
                    RolloutModelBoundaryReason.ThreadMismatch);
                AssertBoundary(Line(envelope, new { type = kind, turn_id = Turn, thread_id = Thread, model = "synthetic-model" }, 11),
                    RolloutModelBoundaryReason.MetadataAfterSample);
            }
            foreach (var payload in new object[]
            {
                new { turn_id = Turn }, new { turn_id = Turn, model = "" }, new { turn_id = Turn, model = 42 },
                new { turn_id = Turn, model = "private\nmodel" }, new { turn_id = Turn, model = new string('m', 129) }
            }) AssertBoundary(Line("turn_context", payload), RolloutModelBoundaryReason.InvalidModel);
            AssertBoundary(Context().Replace("\"turn_id\":", "\"turn_id\":\"foreign-turn\",\"turn_id\":", StringComparison.Ordinal),
                RolloutModelBoundaryReason.InvalidTurnIdentity);
            AssertBoundary(Context().Replace("\"thread_id\":", "\"thread_id\":\"foreign-thread\",\"thread_id\":", StringComparison.Ordinal),
                RolloutModelBoundaryReason.ThreadMismatch);
            AssertBoundary(Context().Replace("\"model\":", "\"model\":\"foreign-model\",\"model\":", StringComparison.Ordinal),
                RolloutModelBoundaryReason.InvalidModel);
        });

        test("model boundary reason: settings distinguish owner time shape and model without guessing", () =>
        {
            AssertBoundary(Line("event_msg", new { }), RolloutModelBoundaryReason.InvalidEventKind);
            AssertBoundary(Line("event_msg", new { type = "thread_settings_applied", thread_id = "foreign-thread" }),
                RolloutModelBoundaryReason.ThreadMismatch);
            AssertBoundary(Line("event_msg", new { type = "thread_settings_applied", thread_id = Thread }, 11),
                RolloutModelBoundaryReason.MetadataAfterSample);
            foreach (var payload in new object[]
            {
                new { type = "thread_settings_applied", thread_id = Thread, model = "synthetic-model" },
                new { type = "thread_settings_applied", thread_id = Thread, thread_settings = "not-an-object" }
            }) AssertBoundary(Line("event_msg", payload), RolloutModelBoundaryReason.InvalidSettings);
            AssertBoundary(Line("event_msg", new { type = "thread_settings_applied", thread_id = Thread, thread_settings = new { } }),
                RolloutModelBoundaryReason.InvalidModel);
        });

        test("model boundary reason: bounded large records still require exact validated tool shape", () =>
        {
            var largeBody = new string('x', 800);
            foreach (var record in new[]
            {
                Line("response_item", new { type = "message", text = largeBody }),
                Tool(largeBody, thread: "foreign-thread"), Tool(largeBody, turn: "foreign-turn"),
                Tool(largeBody, type: "AgentMessage"),
                Tool(largeBody).Replace("\"thread_id\":", "\"thread_id\":\"foreign-thread\",\"thread_id\":", StringComparison.Ordinal),
                Tool(largeBody).Replace("\"output\":", "\"output\":\"hidden\",\"output\":", StringComparison.Ordinal)
            }) AssertBoundary(record, RolloutModelBoundaryReason.UnrecognizedLargeRecord, maxLineBytes: 512, maxToolBytes: 4096);
            foreach (var type in new[] { "CommandExecution", "FileChange" })
            {
                using var stream = StreamFor(Header() + Context() + Tool(largeBody, type: type) + Seal());
                var recovery = Create(stream, maxLineBytes: 512, maxToolBytes: 4096);
                ScanAll(recovery, stream, 257);
                Check.Equal(RolloutModelMetadataStatus.Known, recovery.Status);
                Check.Equal("synthetic-model", recovery.ResultModel);
                Check.Equal(RolloutModelBoundaryReason.None, recovery.BoundaryReason);
            }
        });

        test("model boundary reason: absolute line cap applies to complete and split tool records", () =>
        {
            var record = Tool(new string('x', 5000));
            foreach (var budget in new[] { 257, RolloutModelRecovery.MaximumWindowBytes })
                AssertBoundary(record, RolloutModelBoundaryReason.LineTooLong, budget, 512, 4096);
        });

        test("model boundary reason: broken seal truncation and short read stop with separate reasons", () =>
        {
            using var stream = StreamFor(Header() + Context() + Seal().TrimEnd('\n'));
            var incomplete = Create(stream);
            ScanAll(incomplete, stream);
            AssertStopped(incomplete, RolloutModelBoundaryReason.IncompleteLine);
            var truncated = new RolloutModelRecovery(Thread, Turn, SampleStart, stream.Length + 1);
            Check.Equal(0, truncated.Scan(stream, 4096));
            AssertStopped(truncated, RolloutModelBoundaryReason.TruncatedStream);
            using var shortStream = new ShortReadStream(Encoding.UTF8.GetBytes(Header() + Context() + Seal()));
            var shortRead = Create(shortStream);
            Check.True(shortRead.Scan(shortStream, 4096) > 0);
            AssertStopped(shortRead, RolloutModelBoundaryReason.IncompleteRead);
        });

        test("model boundary reason: pending split reread detects a newly incomplete read", () =>
        {
            using var stream = new ShortReadStream(Encoding.UTF8.GetBytes(Header() + Context() + Seal()), failReads: false);
            var recovery = Create(stream);
            for (var index = 0; index < 100 && recovery.MinimumBytesForProgress == 1; index++) recovery.Scan(stream, 17);
            Check.True(recovery.MinimumBytesForProgress > 17);
            Check.Equal(RolloutModelBoundaryReason.None, recovery.BoundaryReason);
            stream.FailReads = true;
            recovery.Scan(stream, 4096);
            AssertStopped(recovery, RolloutModelBoundaryReason.IncompleteRead);
        });

        test("model boundary reason: successful pending missing limited and conflicting results have no boundary reason", () =>
        {
            using var stream = StreamFor(Header() + Context() + Seal());
            var pending = Create(stream);
            Check.Equal(RolloutModelMetadataStatus.PendingHistory, pending.Status);
            Check.Equal(RolloutModelBoundaryReason.None, pending.BoundaryReason);
            Check.Equal(0, pending.Scan(stream, 0));
            Check.Equal(RolloutModelBoundaryReason.None, pending.BoundaryReason);
            ScanAll(pending, stream);
            Check.Equal(RolloutModelMetadataStatus.Known, pending.Status);
            Check.Equal(RolloutModelBoundaryReason.None, pending.BoundaryReason);
            var limited = new RolloutModelRecovery(Thread, Turn, SampleStart, 1, minimumOffset: 1);
            Check.Equal(RolloutModelMetadataStatus.HistoryLimit, limited.Status);
            Check.Equal(RolloutModelBoundaryReason.None, limited.BoundaryReason);
            var missing = new RolloutModelRecovery(Thread, Turn, SampleStart, 0);
            Check.Equal(RolloutModelMetadataStatus.NotFound, missing.Status);
            Check.Equal(RolloutModelBoundaryReason.None, missing.BoundaryReason);
            using var conflictingStream = StreamFor(Header() + Context() + Context("different-model") + Seal());
            var conflicting = Create(conflictingStream);
            ScanAll(conflicting, conflictingStream);
            Check.Equal(RolloutModelMetadataStatus.Conflict, conflicting.Status);
            Check.Equal(RolloutModelBoundaryReason.None, conflicting.BoundaryReason);
            Check.Null(conflicting.ResultModel);
        });

        test("model boundary reason: fixed enum state remains terminal and contains no input labels", () =>
        {
            foreach (var label in Enum.GetNames<RolloutModelBoundaryReason>())
                Check.True(label.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'));
            foreach (var privateValue in new[] { "private-thread", "C:\\private\\rollout.jsonl", "secret-body\nforged=true" })
            {
                using var stream = StreamFor(Header() + Context() + Line("session_meta", new { id = privateValue }) + Seal());
                var recovery = Create(stream);
                ScanAll(recovery, stream);
                AssertStopped(recovery, RolloutModelBoundaryReason.SessionMismatch);
                Check.Equal(0, recovery.Scan(stream, 4096));
                AssertStopped(recovery, RolloutModelBoundaryReason.SessionMismatch);
            }
        });
    }

    private static void AssertBoundary(string record, RolloutModelBoundaryReason reason,
        int budget = RolloutModelRecovery.MaximumWindowBytes, int maxLineBytes = RolloutModelRecovery.MaximumLineBytes,
        int maxToolBytes = RolloutModelRecovery.MaximumWindowBytes)
    {
        using var stream = StreamFor(Header() + Context() + record + Seal());
        var recovery = Create(stream, maxLineBytes, maxToolBytes);
        ScanAll(recovery, stream, budget);
        AssertStopped(recovery, reason);
    }

    private static void AssertStopped(RolloutModelRecovery recovery, RolloutModelBoundaryReason reason)
    {
        Check.True(recovery.IsComplete);
        Check.Equal(RolloutModelMetadataStatus.Boundary, recovery.Status);
        Check.Equal(reason, recovery.BoundaryReason);
        Check.Null(recovery.ResultModel);
        Check.Null(recovery.ResultReasoningEffort);
        Check.Null(recovery.ContextAt);
    }

    private static RolloutModelRecovery Create(Stream stream, int maxLineBytes = RolloutModelRecovery.MaximumLineBytes,
        int maxToolBytes = RolloutModelRecovery.MaximumWindowBytes) =>
        new(Thread, Turn, SampleStart, stream.Length, maxLineBytes, maxToolCompletionBytes: maxToolBytes);

    private static void ScanAll(RolloutModelRecovery recovery, Stream stream, int budget = RolloutModelRecovery.MaximumWindowBytes)
    {
        for (var index = 0; !recovery.IsComplete; index++)
        {
            Check.True(index < 1000, "Synthetic bounded history must terminate.");
            var actualBudget = Math.Max(budget, recovery.MinimumBytesForProgress);
            var before = recovery.Offset;
            var used = recovery.Scan(stream, actualBudget);
            Check.True(used <= Math.Min(actualBudget, RolloutModelRecovery.MaximumWindowBytes));
            Check.True(recovery.Offset <= before);
            Check.True(used > 0 || recovery.IsComplete);
        }
    }

    private static MemoryStream StreamFor(string text) => new(Encoding.UTF8.GetBytes(text), false);
    private static string Header() => Line("session_meta", new { id = Thread });
    private static string Context(string model = "synthetic-model") => Line("turn_context", new { turn_id = Turn, thread_id = Thread, model });
    private static string Seal() => Line("token_usage_record", new { thread_id = Thread, turn_id = Turn, response_id = "synthetic-response" }, 12);
    private static string Tool(string output, string thread = Thread, string turn = Turn, string type = "CommandExecution") =>
        Line("event_msg", new { type = "item_completed", thread_id = thread, turn_id = turn, item = new { type, output } });
    private static string Line(string type, object payload, int seconds = 0) =>
        JsonSerializer.Serialize(new { timestamp = Epoch.AddSeconds(seconds).ToString("O"), type, payload }) + "\n";

    private sealed class ShortReadStream : MemoryStream
    {
        public ShortReadStream(byte[] bytes, bool failReads = true) : base(bytes, false) => FailReads = failReads;
        public bool FailReads { get; set; }
        private bool failedOnce;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!FailReads) return base.Read(buffer, offset, count);
            if (failedOnce) return 0;
            failedOnce = true;
            return base.Read(buffer, offset, Math.Min(1, count));
        }
    }
}
