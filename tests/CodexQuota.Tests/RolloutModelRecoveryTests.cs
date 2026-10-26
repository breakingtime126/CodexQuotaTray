using System.Reflection;
using System.Text;
using System.Text.Json;
using CodexQuota.Core;

static class RolloutModelRecoveryTests
{
    private const string Thread = "11111111-1111-4111-8111-111111111111";
    private const string OtherThread = "22222222-2222-4222-8222-222222222222";
    private const string Turn = "turn-a";
    private static readonly DateTimeOffset Epoch = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SampleStart = Epoch.AddSeconds(10);

    public static void Register(Action<string, Action> test)
    {
        test("model recovery: exact historical turn context is recovered through a FileStream", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "quota-model-recovery-" + Guid.NewGuid().ToString("N") + ".jsonl");
            try
            {
                File.WriteAllText(path, Header(Thread) + Context() + Filler(64) + Seal(), new UTF8Encoding(false));
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var recovery = new RolloutModelRecovery(Thread, Turn, SampleStart, stream.Length);
                Check.True(recovery.Scan(stream, 256 * 1024) <= 256 * 1024);
                Check.True(recovery.IsComplete);
                Check.Equal("gpt-exact", recovery.ResultModel);
                Check.Equal(Epoch, recovery.ContextAt);
            }
            finally { File.Delete(path); }
        });

        test("model recovery: optional thread identity can be absent but an explicit identity must match", () =>
        {
            Check.Equal("gpt-exact", Recover(Context(thread: null) + Seal()).ResultModel);
            Check.Equal("gpt-exact", Recover(Context(thread: Thread) + Seal()).ResultModel);
            Check.Null(Recover(Context(thread: OtherThread) + Seal()).ResultModel);
        });

        test("model recovery: nearest exact context wins without crossing an older turn", () =>
        {
            var result = Recover(Context(turn: "older-turn", model: "older-model") + Context() + Seal());
            Check.Equal("gpt-exact", result.ResultModel);
        });

        test("model recovery: different or unscoped turn contexts are terminal misses", () =>
        {
            foreach (var boundary in new[]
            {
                Context(turn: "other-turn"),
                Line(0, "turn_context", new { model = "not-evidence" }),
                Context(thread: OtherThread)
            }) Check.Null(Recover(Context() + boundary + Seal()).ResultModel);
        });

        test("model recovery: all session and settings ownership boundaries stop the backward scan", () =>
        {
            foreach (var boundary in new[]
            {
                Header(Thread), Header(OtherThread),
                Line(0, "event_msg", new { type = "thread_settings_applied", thread_id = Thread, model = "current-setting" }),
                Line(0, "event_msg", new { type = "thread_settings_applied", thread_id = OtherThread, model = "foreign-setting" }),
                Line(0, "event_msg", new { type = "thread_settings_applied", model = "unscoped-setting" })
            })
            {
                var result = Recover(Context() + boundary + Seal());
                Check.True(result.IsComplete);
                Check.Null(result.ResultModel);
            }
        });

        test("model recovery: same-turn task start is traversable but different or foreign starts stop", () =>
        {
            Check.Equal("gpt-exact", Recover(Context() +
                Line(1, "event_msg", new { type = "task_started", turn_id = Turn, thread_id = Thread }) + Seal()).ResultModel);
            foreach (var boundary in new[]
            {
                Line(1, "event_msg", new { type = "task_started", turn_id = "other-turn", thread_id = Thread }),
                Line(1, "event_msg", new { type = "task_started", turn_id = Turn, thread_id = OtherThread }),
                Line(1, "event_msg", new { type = "task_started", thread_id = Thread }),
                Line(11, "event_msg", new { type = "task_started", turn_id = Turn, thread_id = Thread })
            }) Check.Null(Recover(Context() + boundary + Seal()).ResultModel);
        });

        test("model recovery: missing or malformed nearest model never falls back to an older context", () =>
        {
            foreach (var payload in new object[]
            {
                new { turn_id = Turn }, new { turn_id = Turn, model = "" },
                new { turn_id = Turn, model = " " }, new { turn_id = Turn, model = 42 },
                new { turn_id = Turn, model = "bad\nmodel" }, new { turn_id = Turn, model = new string('m', 129) }
            })
            {
                var result = Recover(Context() + Line(0, "turn_context", payload) + Seal());
                Check.Null(result.ResultModel);
                Check.Null(result.ContextAt);
            }
        });

        test("model recovery: a repeated frozen context cannot conceal an earlier same-turn settings change", () =>
        {
            var changed = Line(2, "event_msg", new { type = "thread_settings_applied", thread_id = Thread, model = "changed-model" });
            var result = Recover(Context(model: "frozen-model") + changed + Context(model: "frozen-model", seconds: 3) + Seal());
            Check.Null(result.ResultModel);
            Check.Null(result.ContextAt);
        });

        test("model recovery: repeated consistent contexts are verified through the own-session boundary", () =>
        {
            var result = Recover(Context() + Filler(100) + Context(seconds: 3) + Seal(), 512);
            Check.Equal("gpt-exact", result.ResultModel);
            Check.Equal(Epoch.AddSeconds(3), result.ContextAt);
            Check.Null(Recover(Context(model: "original-model") + Context(model: "conflicting-model", seconds: 3) + Seal()).ResultModel);
        });

        test("model recovery: a candidate needs a proven earlier boundary and cannot cross foreign ownership", () =>
        {
            foreach (var text in new[] { Context() + Seal(), Header(OtherThread) + Context() + Seal() })
            {
                using var stream = new CountingStream(Encoding.UTF8.GetBytes(text));
                var result = new RolloutModelRecovery(Thread, Turn, SampleStart, stream.Length);
                ScanAll(result, stream, 512);
                Check.Null(result.ResultModel);
            }
            var taskStart = Line(0, "event_msg", new { type = "task_started", turn_id = Turn, thread_id = Thread });
            Check.Equal("gpt-exact", Recover(taskStart + Context() + Seal()).ResultModel);
            Check.Equal("gpt-exact", Recover(Context() + taskStart + Seal()).ResultModel);
        });

        test("model recovery: duplicate context identity model and envelope fields are rejected", () =>
        {
            foreach (var malformed in new[]
            {
                RawContext("\"turn_id\":\"turn-a\",\"model\":\"other\",\"model\":\"gpt-exact\""),
                RawContext("\"turn_id\":\"other-turn\",\"turn_id\":\"turn-a\",\"model\":\"gpt-exact\""),
                RawContext("\"turn_id\":\"turn-a\",\"thread_id\":\"foreign\",\"thread_id\":\"" + Thread + "\",\"model\":\"gpt-exact\""),
                Context().TrimEnd('\n').Replace("\"type\":\"turn_context\"", "\"type\":\"session_meta\",\"type\":\"turn_context\"") + "\n",
                Context().TrimEnd('\n').Replace("\"payload\":", "\"payload\":{},\"payload\":") + "\n",
                Context().TrimEnd('\n').Replace("\"timestamp\":", "\"timestamp\":\"2026-10-02T10:00:11Z\",\"timestamp\":") + "\n"
            }) Check.Null(Recover(Context() + malformed + Seal()).ResultModel);
        });

        test("model recovery: context must predate sample start and have an explicit valid timestamp", () =>
        {
            Check.Equal("gpt-exact", Recover(Context(seconds: 10) + Seal()).ResultModel);
            Check.Null(Recover(Context() + Context(seconds: 11) + Seal()).ResultModel);
            foreach (var timestamp in new[] { "2026-10-02T10:00:00", "invalid", "" })
            {
                var line = JsonSerializer.Serialize(new { timestamp, type = "turn_context", payload = new { turn_id = Turn, model = "gpt-exact" } }) + "\n";
                Check.Null(Recover(Context() + line + Seal()).ResultModel);
            }
        });

        test("model recovery: records appended after the original seal cannot supply or replace its model", () =>
        {
            var historical = Header(Thread) + Context() + Seal();
            using var stream = new CountingStream(Encoding.UTF8.GetBytes(historical + Context(model: "future-model", seconds: 30)));
            var result = new RolloutModelRecovery(Thread, Turn, SampleStart, Encoding.UTF8.GetByteCount(historical));
            ScanAll(result, stream, 512);
            Check.Equal("gpt-exact", result.ResultModel);
            Check.True(stream.HighestReadEnd <= result.EndOffset);
        });

        test("model recovery: complete UTF8 context split across scan windows remains exact", () =>
        {
            var text = Header(Thread) + Context(model: "gpt-模型-é") + string.Concat(Enumerable.Repeat(Filler(97, "秘密é"), 30)) + Seal();
            using var stream = new CountingStream(Encoding.UTF8.GetBytes(text), maxRead: 7);
            var result = new RolloutModelRecovery(Thread, Turn, SampleStart, stream.Length);
            var calls = ScanAll(result, stream, 509);
            Check.True(calls > 5);
            Check.Equal("gpt-模型-é", result.ResultModel);
            Check.True(stream.ReadCalls > calls);
        });

        test("model recovery: split-line metadata waits for sufficient budget without retaining content", () =>
        {
            var text = Header(Thread) + Context() + Seal();
            using var stream = new CountingStream(Encoding.UTF8.GetBytes(text));
            var result = new RolloutModelRecovery(Thread, Turn, SampleStart, stream.Length);
            var firstOffset = result.Offset;
            Check.Equal(17, result.Scan(stream, 17));
            Check.True(result.Offset < firstOffset);
            Check.False(result.IsComplete);
            for (var index = 0; index < 100 && result.MinimumBytesForProgress == 1; index++) result.Scan(stream, 17);
            Check.True(result.MinimumBytesForProgress > 17);
            var pendingOffset = result.Offset;
            var readBefore = stream.BytesRead;
            Check.Equal(0, result.Scan(stream, 17));
            Check.Equal(readBefore, stream.BytesRead);
            Check.Equal(pendingOffset, result.Offset);
            ScanAll(result, stream, 512);
            Check.Equal("gpt-exact", result.ResultModel);
        });

        test("model recovery: no raw body buffers or JSON objects survive a scan", () =>
        {
            var forbidden = new[] { typeof(byte[]), typeof(char[]), typeof(JsonDocument), typeof(JsonElement), typeof(MemoryStream) };
            Check.False(typeof(RolloutModelRecovery).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Any(field => forbidden.Contains(field.FieldType)), "Recovery state must contain offsets and bounded metadata only.");
        });

        test("model recovery: multimegabyte filler is scanned incrementally once with bounded reads", () =>
        {
            var text = Header(Thread) + Context() + string.Concat(Enumerable.Repeat(Filler(1024), 3000)) + Seal();
            using var stream = new CountingStream(Encoding.UTF8.GetBytes(text));
            var result = new RolloutModelRecovery(Thread, Turn, SampleStart, stream.Length);
            var calls = ScanAll(result, stream, int.MaxValue);
            Check.True(calls > 10);
            Check.Equal("gpt-exact", result.ResultModel);
            Check.True(stream.BytesRead < stream.Length + calls * 10000L, "Only split-line framing may be reread, not whole history windows.");
            var before = stream.BytesRead;
            Check.Equal(0, result.Scan(stream, int.MaxValue));
            Check.Equal(before, stream.BytesRead);
        });

        test("model recovery: bounded history cutoff never parses a partial prefix or rescans after a miss", () =>
        {
            var text = Context() + string.Concat(Enumerable.Repeat(Filler(256), 100)) + Seal();
            using var stream = new CountingStream(Encoding.UTF8.GetBytes(text));
            var minimum = stream.Length - 2048;
            var result = new RolloutModelRecovery(Thread, Turn, SampleStart, stream.Length, minimumOffset: minimum);
            ScanAll(result, stream, 512);
            Check.Null(result.ResultModel);
            Check.True(result.Offset >= minimum);
            Check.True(stream.LowestReadStart >= minimum);
            Check.Equal(0, result.Scan(stream, 512));
        });

        test("model recovery: oversize records are unknown rather than a skipped ownership boundary", () =>
        {
            var text = Context() + Filler(RolloutModelRecovery.MaximumLineBytes + 1) + Seal();
            var result = Recover(text, RolloutModelRecovery.MaximumWindowBytes);
            Check.True(result.IsComplete);
            Check.Null(result.ResultModel);
        });

        test("model recovery: maximum-sized complete context is permitted", () =>
        {
            var plain = Line(0, "turn_context", new { turn_id = Turn, model = "gpt-exact", padding = "" });
            var padding = new string('x', RolloutModelRecovery.MaximumLineBytes - Encoding.UTF8.GetByteCount(plain) + 1);
            var context = Line(0, "turn_context", new { turn_id = Turn, model = "gpt-exact", padding });
            Check.Equal(RolloutModelRecovery.MaximumLineBytes + 1, Encoding.UTF8.GetByteCount(context));
            Check.Equal("gpt-exact", Recover(context + Seal(), RolloutModelRecovery.MaximumWindowBytes).ResultModel);
        });

        test("model recovery: malformed JSON UTF8 and incomplete terminal lines fail closed", () =>
        {
            Check.Null(Recover(Context() + "{broken json}\n" + Seal()).ResultModel);
            Check.Null(Recover(Context() + Seal().TrimEnd('\n')).ResultModel);
            var bytes = Encoding.UTF8.GetBytes(Context()).Concat(new byte[] { 0xc3, 0x28, (byte)'\n' })
                .Concat(Encoding.UTF8.GetBytes(Seal())).ToArray();
            using var stream = new CountingStream(bytes);
            var result = new RolloutModelRecovery(Thread, Turn, SampleStart, stream.Length);
            ScanAll(result, stream, 512);
            Check.Null(result.ResultModel);
        });

        test("model recovery: CRLF and UTF8 BOM framing are supported", () =>
        {
            var text = "\uFEFF" + Context().Replace("\n", "\r\n") + Seal().Replace("\n", "\r\n");
            Check.Equal("gpt-exact", Recover(text).ResultModel);
        });

        test("model recovery: empty history and absent context complete without guessed labels", () =>
        {
            Check.Null(Recover("").ResultModel);
            Check.Null(Recover(Header(Thread) + Filler(32) + Seal()).ResultModel);
            Check.Null(Recover(Filler(32) + Seal()).ResultModel);
        });

        test("model recovery: cancellation and zero budget leave the cursor unchanged", () =>
        {
            using var stream = new CountingStream(Encoding.UTF8.GetBytes(Header(Thread) + Context() + Seal()));
            var result = new RolloutModelRecovery(Thread, Turn, SampleStart, stream.Length);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Check.Throws<OperationCanceledException>(() => result.Scan(stream, 512, cancelled.Token));
            Check.Equal(0, result.Scan(stream, 0));
            Check.Equal(stream.Length, result.Offset);
            Check.Equal(0L, stream.BytesRead);
            Check.False(result.IsComplete);
            ScanAll(result, stream, 512);
            Check.Equal("gpt-exact", result.ResultModel);
        });

        test("model recovery: truncation and invalid arguments cannot read outside the sealed range", () =>
        {
            using var stream = new CountingStream(Encoding.UTF8.GetBytes(Context()));
            var result = new RolloutModelRecovery(Thread, Turn, SampleStart, stream.Length + 10);
            Check.Equal(0, result.Scan(stream, 512));
            Check.True(result.IsComplete);
            Check.Null(result.ResultModel);
            Check.Throws<ArgumentOutOfRangeException>(() => result.Scan(stream, -1));
            Check.Throws<ArgumentOutOfRangeException>(() => new RolloutModelRecovery(Thread, Turn, SampleStart, -1));
            Check.Throws<ArgumentOutOfRangeException>(() => new RolloutModelRecovery(Thread, Turn, SampleStart, 10, minimumOffset: 11));
            Check.Throws<ArgumentOutOfRangeException>(() => new RolloutModelRecovery(Thread, Turn, SampleStart, 10, maxLineBytes: 129 * 1024));
            Check.Throws<ArgumentException>(() => new RolloutModelRecovery(Thread, "", SampleStart, 10));
        });
    }

    private static RolloutModelRecovery Recover(string text, int budget = 4096)
    {
        using var stream = new CountingStream(Encoding.UTF8.GetBytes(Header(Thread) + text));
        var result = new RolloutModelRecovery(Thread, Turn, SampleStart, stream.Length);
        ScanAll(result, stream, budget);
        return result;
    }

    private static int ScanAll(RolloutModelRecovery result, CountingStream stream, int budget)
    {
        var calls = 0;
        while (!result.IsComplete)
        {
            Check.True(++calls < 10000, "Recovery must eventually reach a context, boundary, or history cutoff.");
            var before = stream.BytesRead;
            var offset = result.Offset;
            var used = result.Scan(stream, budget);
            Check.Equal(stream.BytesRead - before, (long)used);
            Check.True(used <= Math.Min(budget, RolloutModelRecovery.MaximumWindowBytes));
            Check.True(result.Offset <= offset, "A recovery cursor must not restart its history scan.");
            Check.True(used > 0 || result.IsComplete, "The test budget must fit each complete bounded line.");
        }
        return calls;
    }

    private static string Header(string thread) => Line(0, "session_meta", new { id = thread });
    private static string Context(string turn = Turn, string model = "gpt-exact", string? thread = Thread, int seconds = 0) =>
        thread is null ? Line(seconds, "turn_context", new { turn_id = turn, model }) :
            Line(seconds, "turn_context", new { turn_id = turn, thread_id = thread, model });
    private static string RawContext(string payload) =>
        "{\"timestamp\":\"2026-10-02T10:00:00Z\",\"type\":\"turn_context\",\"payload\":{" + payload + "}}\n";
    private static string Seal() => Line(12, "token_usage_record", new { thread_id = Thread, turn_id = Turn,
        response_id = "response-a", usage = new { output_tokens = 100 } });
    private static string Filler(int characters, string text = "x") => Line(1, "response_item", new { type = "message", role = "user",
        content = new[] { new { type = "input_text", text = new string('x', characters) + text } } });
    private static string Line(int seconds, string type, object payload) => JsonSerializer.Serialize(new
    {
        timestamp = Epoch.AddSeconds(seconds).ToString("O"), type, payload
    }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";

    private sealed class CountingStream : MemoryStream
    {
        private readonly int maxRead;
        public CountingStream(byte[] bytes, int maxRead = int.MaxValue) : base(bytes, false) => this.maxRead = maxRead;
        public long BytesRead { get; private set; }
        public long HighestReadEnd { get; private set; }
        public long LowestReadStart { get; private set; } = long.MaxValue;
        public int ReadCalls { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            LowestReadStart = Math.Min(LowestReadStart, Position);
            var read = base.Read(buffer, offset, Math.Min(count, maxRead));
            HighestReadEnd = Math.Max(HighestReadEnd, Position);
            BytesRead += read;
            ReadCalls++;
            return read;
        }
    }
}
