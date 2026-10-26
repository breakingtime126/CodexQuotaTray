using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Runtime.InteropServices;
using CodexQuota.Core;

if (args.Length == 1 && args[0] == "app-server") return await FakeAppServer.RunAsync();
if (args.Length == 1 && args[0] == "--read") return await FakeReadOnlyQuotaReader.RunAsync();

// Dependency-free integration tests. No network, real CLI, login, or account is used.
// Each JSON-RPC peer communicates over a pair of asynchronous in-memory streams.
var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action body) => tests.Add((name, () => { body(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> body) => tests.Add((name, body));
UpdateManifestTests.Register(Test);
UpdateDownloadTests.Register(Test, AsyncTest);

Test("parser: Codex map wins over legacy and unrelated buckets", () =>
{
    var quota = Parse("""
        {"rateLimitsByLimitId":{"other":{"primary":{"usedPercent":99,"windowDurationMins":10080}},
         "codex":{"primary":{"usedPercent":23.4,"windowDurationMins":10080,"resetsAt":1800000000}}},
         "rateLimits":{"limitId":"codex","primary":{"usedPercent":90,"windowDurationMins":10080}}}
        """);
    Check.Quota(quota, 76.6, 76);
    Check.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000000), quota!.ResetsAt);
});

Test("parser: real CLI 0.159.0 primary-only weekly shape", () =>
{
    var quota = Parse("""
        {"rateLimits":{"limitId":"codex","limitName":"Codex","primary":{"usedPercent":15,
         "windowDurationMins":10080,"resetsAt":1800000000},"secondary":null,"credits":null,"planType":"plus"},
         "rateLimitsByLimitId":{"codex":{"limitId":"codex","limitName":"Codex", "primary":{
         "usedPercent":15,"windowDurationMins":10080,"resetsAt":1800000000},"secondary":null,
         "credits":null,"planType":"plus"}}}
        """);
    Check.Quota(quota, 85, 85);
});

Test("parser: secondary weekly is found by duration, never slot", () =>
{
    var quota = Parse("""
        {"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":80,"windowDurationMins":300},
        "secondary":{"usedPercent":12.75,"windowDurationMins":10080}}}}
        """);
    Check.Quota(quota, 87.25, 87);
    Check.Null(quota!.ResetsAt);
});

Test("parser: legacy rateLimits with omitted or Codex limitId", () =>
{
    foreach (var id in new[] { "", "\"limitId\":\"codex\"," })
        Check.Quota(Parse("{\"rateLimits\":{" + id + "\"secondary\":{\"usedPercent\":41,\"windowDurationMins\":10080}}}"), 59, 59);
});

Test("parser: unrelated bucket is never selected", () =>
{
    foreach (var json in new[]
    {
        """{"rateLimitsByLimitId":{"other":{"primary":{"usedPercent":11,"windowDurationMins":10080}}}}""",
        """{"rateLimits":{"limitId":"other","secondary":{"usedPercent":11,"windowDurationMins":10080}}}""",
        """{"rateLimitsByLimitId":{"other":{"primary":{"usedPercent":11,"windowDurationMins":10080}}},"rateLimits":{"limitId":"other","primary":{"usedPercent":12,"windowDurationMins":10080}}}"""
    }) Check.Null(Parse(json));
});

Test("parser: explicit Codex bucket without weekly does not fall back", () =>
    Check.Null(Parse("""
        {"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":5,"windowDurationMins":300},"secondary":null}},
        "rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":10080}}}
        """)));

foreach (var (label, json) in new (string, string)[]
{
    ("empty", "{}"), ("null", "null"), ("array", "[]"), ("number", "17"), ("string", "\"not an object\""),
    ("null map", "{\"rateLimitsByLimitId\":null}"), ("null legacy", "{\"rateLimits\":null}"),
    ("null window", "{\"rateLimits\":{\"primary\":null,\"secondary\":null}}"),
    ("missing duration", "{\"rateLimits\":{\"primary\":{\"usedPercent\":20}}}"),
    ("string duration", "{\"rateLimits\":{\"primary\":{\"usedPercent\":20,\"windowDurationMins\":\"10080\"}}}"),
    ("fractional duration", "{\"rateLimits\":{\"primary\":{\"usedPercent\":20,\"windowDurationMins\":10080.5}}}"),
    ("huge duration", "{\"rateLimits\":{\"primary\":{\"usedPercent\":20,\"windowDurationMins\":9223372036854775807}}}"),
    ("other duration", "{\"rateLimits\":{\"primary\":{\"usedPercent\":20,\"windowDurationMins\":10079}}}"),
    ("missing percent", "{\"rateLimits\":{\"primary\":{\"windowDurationMins\":10080}}}"),
    ("string percent", "{\"rateLimits\":{\"primary\":{\"usedPercent\":\"20\",\"windowDurationMins\":10080}}}"),
    ("null percent", "{\"rateLimits\":{\"primary\":{\"usedPercent\":null,\"windowDurationMins\":10080}}}"),
    ("boolean percent", "{\"rateLimits\":{\"primary\":{\"usedPercent\":true,\"windowDurationMins\":10080}}}"),
    ("overflow percent", "{\"rateLimits\":{\"primary\":{\"usedPercent\":1e400,\"windowDurationMins\":10080}}}")
})
{
    Test($"parser: unknown rather than zero for {label}", () => Check.Null(Parse(json)));
}

Test("parser: malformed primary still permits valid weekly secondary", () =>
    Check.Quota(Parse("""{"rateLimits":{"primary":{"usedPercent":"bad","windowDurationMins":10080},"secondary":{"usedPercent":7,"windowDurationMins":10080}}}"""), 93, 93));

foreach (var (used, remaining, display) in new[] { ("-10", 100d, 100), ("0", 100d, 100), ("100", 0d, 0), ("125", 0d, 0), ("33.1", 66.9, 66), ("99.999", 0.001, 0) })
    Test($"parser: percent {used} is clamped and conservatively rounded", () =>
        Check.Quota(Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":" + used + ",\"windowDurationMins\":10080}}}"), remaining, display));

foreach (var reset in new[] { "null", "\"1800000000\"", "true", "1.5", "9223372036854775807", "-9223372036854775808", "253402300800", "-62135596801", "1e400", "[]", "{}" })
    Test($"parser: unavailable reset {reset} does not hide quota", () =>
    {
        var quota = Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":40,\"windowDurationMins\":10080,\"resetsAt\":" + reset + "}}}");
        Check.Quota(quota, 60, 60);
        Check.Null(quota!.ResetsAt);
    });

foreach (var seconds in new[] { 0L, -1L, -62135596800L, 253402300799L })
    Test($"parser: valid Unix seconds {seconds}", () =>
        Check.Equal(DateTimeOffset.FromUnixTimeSeconds(seconds), Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":40,\"windowDurationMins\":10080,\"resetsAt\":" + seconds + "}}}")!.ResetsAt));

foreach (var (json, kind) in new (string, AccountKind)[]
{
    ("{\"account\":{\"type\":\"chatgpt\"}}", AccountKind.ChatGpt),
    ("{\"account\":{\"type\":\"apiKey\"}}", AccountKind.ApiKey),
    ("{\"account\":null}", AccountKind.NotSignedIn), ("{}", AccountKind.NotSignedIn),
    ("{\"account\":{\"type\":\"futureAccount\"}}", AccountKind.Unsupported),
    ("{\"account\":{\"type\":17}}", AccountKind.Unsupported), ("{\"account\":{}}", AccountKind.Unsupported)
}) Test($"account parser: {json}", () => Check.Equal(kind, QuotaParser.ReadAccountKind(Json(json))));

Test("state: unknown is not zero or stale", () =>
{
    var state = QuotaState.Initial;
    Check.Null(state.Quota);
    Check.Null(state.LastSuccess);
    Check.False(state.IsStale(FixedNow()));
    Check.Contains("未知", state.Tooltip(FixedNow()));
    Check.False(state.Tooltip(FixedNow()).Contains("0%", StringComparison.Ordinal));
});

Test("state: successful zero remains known zero", () =>
{
    var state = QuotaState.Initial.Apply(new(ReadStatus.Ready, new(0, null)), FixedNow());
    Check.Equal(0, state.Quota!.DisplayPercent);
    Check.Equal(FixedNow(), state.LastSuccess);
    Check.False(state.IsStale(FixedNow()));
    Check.Contains("0%", state.Tooltip(FixedNow()));
});

Test("state: transient errors retain last value and timestamp as stale", () =>
{
    foreach (var status in new[] { ReadStatus.Timeout, ReadStatus.Unavailable, ReadStatus.ProtocolError, ReadStatus.CliMissing, ReadStatus.Connecting })
    {
        var state = ReadyState().Apply(new(status), FixedNow().AddMinutes(1));
        Check.Quota(state.Quota, 42, 42);
        Check.Equal(FixedNow(), state.LastSuccess);
        Check.Equal(status, state.Status);
        Check.True(state.IsStale(FixedNow().AddMinutes(1)));
        Check.Contains("过期", state.Tooltip(FixedNow().AddMinutes(1)));
    }
});

Test("state: account or window changes clear previous account quota", () =>
{
    foreach (var status in new[] { ReadStatus.NotSignedIn, ReadStatus.ApiKey, ReadStatus.UnsupportedAccount, ReadStatus.NoWeeklyWindow, ReadStatus.NoQuotaWindow })
    {
        var state = ReadyState().Apply(new(status), FixedNow().AddMinutes(1));
        Check.Null(state.Quota);
        Check.Null(state.LastSuccess);
        Check.False(state.IsStale(FixedNow()));
        Check.Contains("未知", state.Tooltip(FixedNow()));
    }
});

Test("state: stale threshold and reset instant are inclusive", () =>
{
    var state = ReadyState();
    Check.False(state.IsStale(FixedNow().AddMinutes(10).AddTicks(-1)));
    Check.True(state.IsStale(FixedNow().AddMinutes(10)));
    var resetState = QuotaState.Initial.Apply(new(ReadStatus.Ready, new(10, FixedNow().AddMinutes(2))), FixedNow());
    Check.False(resetState.IsStale(FixedNow().AddMinutes(2).AddTicks(-1)));
    Check.True(resetState.IsStale(FixedNow().AddMinutes(2)));
    Check.True(new QuotaState(new(42, null), null, ReadStatus.Ready).IsStale(FixedNow()));
});

Test("state: fresh success replaces stale data", () =>
{
    var refreshed = ReadyState().Apply(new(ReadStatus.Timeout), FixedNow().AddMinutes(1))
        .Apply(new(ReadStatus.Ready, new(85, null)), FixedNow().AddMinutes(2));
    Check.Quota(refreshed.Quota, 85, 85);
    Check.Equal(FixedNow().AddMinutes(2), refreshed.LastSuccess);
    Check.False(refreshed.IsStale(FixedNow().AddMinutes(2)));
});

Test("state: every tooltip obeys NotifyIcon limit", () =>
{
    foreach (var status in Enum.GetValues<ReadStatus>())
    foreach (var known in new[] { false, true })
    {
        var state = new QuotaState(known ? new(100, FixedNow().AddDays(7)) : null, known ? FixedNow() : null, status);
        var text = state.Tooltip(FixedNow());
        Check.True(text.Length <= 127, $"Tooltip has {text.Length} UTF-16 code units.");
        Check.False(string.IsNullOrWhiteSpace(text));
        Check.True(!string.IsNullOrWhiteSpace(QuotaState.StatusText(status)));
    }
});

AsyncTest("protocol: waits for initialize and requests quota directly without account information", async () =>
{
    await using var peer = new FakePeer();
    var resultTask = CodexQuotaProtocol.ReadAsync(peer.Rpc, TimeSpan.FromSeconds(2), CancellationToken.None);
    var initialize = await peer.ReadAsync();
    Check.Method(initialize, "initialize");
    Check.Contains("codex_quota", initialize.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString()!);
    var next = peer.ReadAsync();
    await Check.StillPendingAsync(next);
    await peer.SendAsync(new { method = "arbitrary/notification", @params = new { harmless = true } });
    await Check.StillPendingAsync(next);
    await peer.RespondAsync(initialize, new { userAgent = "fake" });
    var initialized = await next;
    Check.Method(initialized, "initialized");
    Check.False(initialized.TryGetProperty("id", out _), "initialized must be a notification.");
    var limits = await peer.ReadAsync();
    Check.Method(limits, "account/rateLimits/read");
    await peer.RespondAsync(limits, Json("""{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":9,"windowDurationMins":10080},"secondary":null}}}"""));
    var result = await resultTask;
    Check.Equal(ReadStatus.Ready, result.Status);
    Check.Quota(result.Quota, 91, 91);
});

foreach (var code in new[] { -32603, -32601, -32000 })
    AsyncTest($"protocol: quota RPC error {code} propagates without account information reads", async () =>
{
    await using var peer = new FakePeer();
    var resultTask = CodexQuotaProtocol.ReadAsync(peer.Rpc, TimeSpan.FromSeconds(2), CancellationToken.None);
    await peer.RespondAsync(await peer.ReadAsync(), new { });
    Check.Method(await peer.ReadAsync(), "initialized");
    var limits = await peer.ReadAsync();
    Check.Method(limits, "account/rateLimits/read");
    await peer.SendAsync(new { id = limits.GetProperty("id").GetInt64(), error = new { code, message = "synthetic failure" } });
    var error = await Check.ThrowsAsync<RpcException>(resultTask);
    Check.Equal(code, error.Code);
    await Check.StillPendingAsync(peer.ReadAsync());
});

AsyncTest("protocol: absent windows are unknown, not zero", async () =>
{
    await using var peer = new FakePeer();
    var resultTask = CodexQuotaProtocol.ReadAsync(peer.Rpc, TimeSpan.FromSeconds(2), CancellationToken.None);
    await peer.RespondAsync(await peer.ReadAsync(), new { });
    Check.Method(await peer.ReadAsync(), "initialized");
    await peer.RespondAsync(await peer.ReadAsync(), Json("""{"rateLimits":{"primary":null,"secondary":null}}"""));
    var result = await resultTask;
    Check.Equal(ReadStatus.NoQuotaWindow, result.Status);
    Check.Null(result.Quota);
});

AsyncTest("RPC: concurrent requests route out of order with unrelated input", async () =>
{
    await using var peer = new FakePeer();
    var first = peer.RequestAsync("first");
    var second = peer.RequestAsync("second");
    var firstRequest = await peer.ReadAsync();
    var secondRequest = await peer.ReadAsync();
    Check.Method(firstRequest, "first");
    Check.Method(secondRequest, "second");
    Check.True(firstRequest.GetProperty("id").GetInt64() != secondRequest.GetProperty("id").GetInt64());
    await peer.SendAsync(new { id = 999999, result = new { value = "unknown" } });
    await peer.SendAsync(new { id = firstRequest.GetProperty("id").GetInt64().ToString(), result = new { value = "wrong id type" } });
    await peer.SendAsync(new { method = "account/rateLimits/updated", @params = new { value = "notification" } });
    await peer.SendRawAsync("{}\n");
    await peer.RespondAsync(secondRequest, new { value = 2 });
    Check.Equal(2, (await second).GetProperty("value").GetInt32());
    Check.False(first.IsCompleted);
    await peer.RespondAsync(firstRequest, new { value = 1 });
    Check.Equal(1, (await first).GetProperty("value").GetInt32());
    await peer.RespondAsync(firstRequest, new { value = "duplicate" });
    var third = peer.RequestAsync("third");
    await peer.RespondAsync(await peer.ReadAsync(), new { value = 3 });
    Check.Equal(3, (await third).GetProperty("value").GetInt32());
});

AsyncTest("RPC: results remain valid after their JSON document is disposed", async () =>
{
    await using var peer = new FakePeer();
    var pending = peer.RequestAsync("value");
    await peer.RespondAsync(await peer.ReadAsync(), new { nested = new[] { "retained" } });
    var result = await pending;
    await peer.SendAsync(new { method = "tick", @params = new { } });
    Check.Equal("retained", result.GetProperty("nested")[0].GetString());
});

AsyncTest("RPC: error code preserved but server text never propagated", async () =>
{
    await using var peer = new FakePeer();
    var pending = peer.RequestAsync("unsupported");
    var request = await peer.ReadAsync();
    await peer.SendAsync(new { id = request.GetProperty("id").GetInt64(), error = new { code = -32601, message = "SECRET: account data", data = "SECRET: token" } });
    var error = await Check.ThrowsAsync<RpcException>(pending);
    Check.Equal(-32601, error.Code);
    Check.False(error.ToString().Contains("SECRET", StringComparison.Ordinal));
});

foreach (var (message, expected) in new[]
{
    ("failed to fetch codex rate limits: network timeout", false),
    ("failed to fetch codex rate limits: HTTP 401 Unauthorized", false),
    ("failed to fetch codex rate limits: HTTP 429 Too Many Requests", true),
    ("Too Many Requests", true),
    ("account abc429def unavailable", false)
})
{
    AsyncTest($"RPC: throttling classification for {message}", async () =>
    {
        await using var peer = new FakePeer();
        var pending = peer.RequestAsync("account/rateLimits/read");
        var request = await peer.ReadAsync();
        await peer.SendAsync(new { id = request.GetProperty("id").GetInt64(), error = new { code = -32000, message } });
        var error = await Check.ThrowsAsync<RpcException>(pending);
        Check.Equal(expected, error.IsRateLimited);
        Check.False(error.ToString().Contains(message, StringComparison.Ordinal));
    });
}

AsyncTest("RPC: malformed error body still safely faults request", async () =>
{
    await using var peer = new FakePeer();
    var pending = peer.RequestAsync("error");
    var request = await peer.ReadAsync();
    await peer.SendAsync(new { id = request.GetProperty("id").GetInt64(), error = "SECRET: malformed server error" });
    var error = await Check.ThrowsAsync<RpcException>(pending);
    Check.Null(error.Code);
    Check.False(error.ToString().Contains("SECRET", StringComparison.Ordinal));
});

AsyncTest("RPC: timeout does not poison connection or accept late response", async () =>
{
    await using var peer = new FakePeer();
    var pending = peer.RequestAsync("slow", TimeSpan.FromMilliseconds(80));
    var expired = await peer.ReadAsync();
    await Check.ThrowsAsync<TimeoutException>(pending);
    await peer.RespondAsync(expired, new { ignored = true });
    var next = peer.RequestAsync("next");
    await peer.RespondAsync(await peer.ReadAsync(), new { ok = true });
    Check.True((await next).GetProperty("ok").GetBoolean());
});

AsyncTest("RPC: caller cancellation is not reported as timeout", async () =>
{
    await using var peer = new FakePeer();
    using var cancellation = new CancellationTokenSource();
    var pending = peer.RequestAsync("cancel", cancellationToken: cancellation.Token);
    await peer.ReadAsync();
    cancellation.Cancel();
    await Check.ThrowsAsync<OperationCanceledException>(pending);
    var next = peer.RequestAsync("still alive");
    await peer.RespondAsync(await peer.ReadAsync(), new { ok = true });
    Check.True((await next).GetProperty("ok").GetBoolean());
});

AsyncTest("RPC: pre-canceled request emits no message", async () =>
{
    await using var peer = new FakePeer();
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await Check.ThrowsAsync<OperationCanceledException>(peer.RequestAsync("never sent", cancellationToken: cancellation.Token));
    await Check.StillPendingAsync(peer.ReadAsync());
});

foreach (var (name, text, exceptionType) in new (string, string, Type)[]
{
    ("malformed JSON", "{broken}\n", typeof(JsonException)),
    ("non-object JSON", "[]\n", typeof(InvalidDataException)),
    ("oversized line", new string('x', JsonRpcConnection.MaximumLineLength + 1), typeof(InvalidDataException))
}) AsyncTest($"RPC: {name} fails every pending request", async () =>
{
    await using var peer = new FakePeer();
    var first = peer.RequestAsync("first");
    var second = peer.RequestAsync("second");
    await peer.ReadAsync();
    await peer.ReadAsync();
    await peer.SendRawAsync(text);
    await Check.ThrowsAsync(first, exceptionType);
    await Check.ThrowsAsync(second, exceptionType);
    await Check.ThrowsAsync<IOException>(peer.RequestAsync("after terminal failure"));
});

AsyncTest("RPC: response without result or error faults only its request", async () =>
{
    await using var peer = new FakePeer();
    var pending = peer.RequestAsync("bad response");
    var request = await peer.ReadAsync();
    await peer.SendAsync(new { id = request.GetProperty("id").GetInt64() });
    await Check.ThrowsAsync<InvalidDataException>(pending);
    var next = peer.RequestAsync("healthy");
    await peer.RespondAsync(await peer.ReadAsync(), (object?)null);
    Check.Equal(JsonValueKind.Null, (await next).ValueKind);
});

AsyncTest("RPC: EOF promptly fails pending and subsequent requests", async () =>
{
    await using var peer = new FakePeer();
    var first = peer.RequestAsync("first");
    var second = peer.RequestAsync("second");
    await peer.ReadAsync();
    await peer.ReadAsync();
    peer.CloseServerOutput();
    await Check.ThrowsAsync<EndOfStreamException>(first);
    await Check.ThrowsAsync<EndOfStreamException>(second);
    await Check.ThrowsAsync<IOException>(peer.RequestAsync("after EOF"));
});

AsyncTest("RPC: blank lines, CRLF and fragmented UTF-8 are accepted", async () =>
{
    await using var peer = new FakePeer();
    var pending = peer.RequestAsync("utf8");
    var request = await peer.ReadAsync();
    var bytes = Encoding.UTF8.GetBytes("\r\n\n{\"id\":" + request.GetProperty("id").GetInt64() + ",\"result\":{\"value\":\"额度😀\"}}\r\n");
    foreach (var value in bytes) await peer.SendBytesAsync(new[] { value });
    Check.Equal("额度😀", (await pending).GetProperty("value").GetString());
});

AsyncTest("RPC: unsolicited server request is refused with matching id", async () =>
{
    await using var peer = new FakePeer();
    var pending = peer.RequestAsync("quota");
    var request = await peer.ReadAsync();
    // Deliberately reuse the outstanding numeric id. A request is never a response.
    await peer.SendAsync(new { id = request.GetProperty("id").GetInt64(), method = "item/commandExecution/requestApproval", @params = new { command = "must never run" } });
    var refused = await peer.ReadAsync();
    Check.Equal(request.GetProperty("id").GetInt64(), refused.GetProperty("id").GetInt64());
    Check.Equal(-32601, refused.GetProperty("error").GetProperty("code").GetInt32());
    Check.False(pending.IsCompleted);
    await peer.SendAsync(new { id = "server-request", method = "account/login/start", @params = new { } });
    var stringRefused = await peer.ReadAsync();
    Check.Equal("server-request", stringRefused.GetProperty("id").GetString());
    Check.Equal(-32601, stringRefused.GetProperty("error").GetProperty("code").GetInt32());
    await peer.RespondAsync(request, new { ok = true });
    Check.True((await pending).GetProperty("ok").GetBoolean());
});

AsyncTest("RPC: concurrent writes remain one complete JSON message per line", async () =>
{
    await using var peer = new FakePeer();
    const int count = 40;
    var pending = Enumerable.Range(0, count).Select(i => peer.RequestAsync($"request/{i}")).ToArray();
    var requests = new List<JsonElement>();
    for (var i = 0; i < count; i++) requests.Add(await peer.ReadAsync());
    Check.Equal(count, requests.Select(x => x.GetProperty("id").GetInt64()).Distinct().Count());
    foreach (var request in requests.AsEnumerable().Reverse())
        await peer.RespondAsync(request, new { method = request.GetProperty("method").GetString() });
    for (var i = 0; i < count; i++) Check.Equal($"request/{i}", (await pending[i]).GetProperty("method").GetString());
});

AsyncTest("RPC: disposal releases pending reads and is idempotent", async () =>
{
    await using var peer = new FakePeer();
    var pending = peer.RequestAsync("pending");
    await peer.ReadAsync();
    await peer.Rpc.DisposeAsync();
    await Check.ThrowsAsync<OperationCanceledException>(pending);
    await peer.Rpc.DisposeAsync();
    await Check.ThrowsAsync<ObjectDisposedException>(peer.RequestAsync("after dispose"));
});

AsyncTest("RPC: disposal joins canceled server-request writer before disposing it", async () =>
{
    DelayedWriter? delayed = null;
    await using var peer = new FakePeer(writer => delayed = new DelayedWriter(writer));
    await peer.SendAsync(new { id = "delayed", method = "approval/never", @params = new { } });
    await delayed!.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var disposal = peer.Rpc.DisposeAsync().AsTask();
    await delayed.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Check.False(disposal.IsCompleted, "Dispose must wait for the canceled write to unwind.");
    Check.False(delayed.DisposedDuringWrite);
    delayed.Release.TrySetResult();
    await disposal.WaitAsync(TimeSpan.FromSeconds(2));
    Check.True(delayed.WasDisposed);
    Check.False(delayed.DisposedDuringWrite);
});

AsyncTest("RPC: throwing stream disposal cannot interrupt cleanup", async () =>
{
    var reader = new ThrowingDisposeReader();
    var writer = new ThrowingDisposeWriter();
    var rpc = new JsonRpcConnection(reader, writer);
    await rpc.DisposeAsync();
    await rpc.DisposeAsync();
    Check.Equal(1, reader.DisposeCount);
    Check.Equal(1, writer.DisposeCount);
});

AsyncTest("client lifecycle: fake app-server is killed after successful read", async () =>
{
    using var environment = new FakeChildEnvironment("success");
    var result = await new CodexQuotaClient(environment.Executable).ReadAsync(CancellationToken.None);
    Check.Equal(ReadStatus.Ready, result.Status);
    Check.Quota(result.Quota, 73, 73);
    await environment.AssertChildrenExitedAsync(expectedCount: 1);
});

AsyncTest("client lifecycle: caller cancellation kills fake app-server", async () =>
{
    using var environment = new FakeChildEnvironment("hang");
    using var cancellation = new CancellationTokenSource();
    var result = new CodexQuotaClient(environment.Executable).ReadAsync(cancellation.Token);
    await environment.WaitForChildAsync();
    cancellation.Cancel();
    await Check.ThrowsAsync<OperationCanceledException>(result);
    await environment.AssertChildrenExitedAsync(expectedCount: 1);
});

AsyncTest("client lifecycle: request timeout does not retry and kills the fake child", async () =>
{
    using var environment = new FakeChildEnvironment("hang");
    var result = await new CodexQuotaClient(environment.Executable).ReadAsync(CancellationToken.None);
    Check.Equal(ReadStatus.Timeout, result.Status);
    Check.Null(result.Quota);
    await environment.AssertChildrenExitedAsync(expectedCount: 1);
});

Test("CLI: explicit executable path with shell characters stays one filename", () =>
{
    using var files = new TempDirectory();
    var path = files.Touch(OperatingSystem.IsWindows() ? "codex & echo ignored.exe" : "codex ; echo ignored");
    var start = CliLocator.Find(path) ?? throw new TestFailure("Explicit CLI was not found.");
    Check.Equal(path, start.FileName);
    Check.False(start.UseShellExecute);
    Check.True(start.CreateNoWindow);
    Check.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
    Check.Equal(1, start.ArgumentList.Count);
    Check.Equal("app-server", start.ArgumentList[0]);
    Check.Equal("", start.Arguments);
    Check.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), start.WorkingDirectory);
});

Test("CLI: npm cmd shim uses sibling Node and official JS, never cmd.exe", () =>
{
    using var files = new TempDirectory();
    var shim = files.Touch("codex.cmd");
    var node = files.Touch(OperatingSystem.IsWindows() ? "node.exe" : "node");
    var script = files.Touch(Path.Combine("node_modules", "@openai", "codex", "bin", "codex.js"));
    var start = CliLocator.Find(shim) ?? throw new TestFailure("Official npm shim was not resolved.");
    Check.Equal(node, start.FileName);
    Check.False(start.UseShellExecute);
    Check.Equal(2, start.ArgumentList.Count);
    Check.Equal(script, start.ArgumentList[0]);
    Check.Equal("app-server", start.ArgumentList[1]);
});

Test("CLI: arbitrary cmd shim without official JS is rejected", () =>
{
    using var files = new TempDirectory();
    Check.Null(CliLocator.Find(files.Touch("codex.cmd")));
    Check.Null(CliLocator.Find(Path.Combine(files.Path, "missing.exe")));
});

Test("CLI: direct JS entry uses Node without shell expansion", () =>
{
    using var files = new TempDirectory();
    var script = files.Touch("codex & ignored.js");
    var node = files.Touch(OperatingSystem.IsWindows() ? "node.exe" : "node");
    var start = CliLocator.Find(script) ?? throw new TestFailure("JS entry was not resolved.");
    Check.Equal(node, start.FileName);
    Check.Equal(script, start.ArgumentList[0]);
    Check.False(start.UseShellExecute);
});

Test("CLI: PATH lookup skips relative entries and selects absolute entry", () =>
{
    using var files = new TempDirectory();
    var filename = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
    var absolute = files.Touch(filename);
    var saved = Environment.GetEnvironmentVariable("PATH");
    try
    {
        Environment.SetEnvironmentVariable("PATH", "." + Path.PathSeparator + "relative" + Path.PathSeparator + files.Path);
        Check.Equal(absolute, CliLocator.Find()!.FileName);
    }
    finally { Environment.SetEnvironmentVariable("PATH", saved); }
});

foreach (var size in new[] { 16, 20, 24, 32, 64 })
{
    Test($"icons: {size}px RGBA geometry and deterministic pixels", () =>
    {
        foreach (var percent in new int?[] { null, 0, 1, 37, 100 })
        {
            var image = TrayIconRasterizer.Render(size, percent);
            Check.Equal(size, image.Width);
            Check.Equal(size, image.Height);
            Check.Equal(size * size * 4, image.Pixels.Length);
            Check.Equal((byte)0, image.Pixels[3]);
            Check.Equal((byte)0, image.Pixels[(size - 1) * 4 + 3]);
            Check.Equal((byte)0, image.Pixels[(size * (size - 1)) * 4 + 3]);
            Check.Equal((byte)0, image.Pixels[^1]);
            var alphas = image.Pixels.Where((_, index) => index % 4 == 3).ToArray();
            Check.True(alphas.Contains((byte)255), "Icon must contain opaque artwork.");
            Check.True(alphas.Any(alpha => alpha is > 0 and < 255), "Antialiased edges must retain fractional alpha.");
            Check.True(image.Pixels.SequenceEqual(TrayIconRasterizer.Render(size, percent).Pixels));
        }
    });
    Test($"icons: {size}px PNG losslessly preserves RGBA and valid chunk CRCs", () =>
    {
        var image = TrayIconRasterizer.Render(size, 37);
        var decoded = PngFixture.Decode(TrayIconRasterizer.EncodePng(image));
        Check.Equal(image.Width, decoded.Width);
        Check.Equal(image.Height, decoded.Height);
        Check.True(image.Pixels.SequenceEqual(decoded.Pixels));
    });
}

Test("icons: known zero, unknown and stale remain visually distinct", () =>
{
    var zero = TrayIconRasterizer.Render(64, 0);
    var unknown = TrayIconRasterizer.Render(64, null);
    var fresh = TrayIconRasterizer.Render(64, 37);
    var stale = TrayIconRasterizer.Render(64, 37, stale: true);
    Check.False(zero.Pixels.SequenceEqual(unknown.Pixels));
    Check.False(fresh.Pixels.SequenceEqual(stale.Pixels));
    Check.Equal(0, CountColor(zero, 101, 236, 183));
    Check.Equal(0, CountColor(unknown, 101, 236, 183));
    Check.Equal(0, CountColor(stale, 101, 236, 183));
    Check.True(CountColor(stale, 158, 174, 186) > 0, "Stale ring must use its neutral color.");
    Check.True(CountColor(unknown, 158, 174, 186) > 0, "Unknown ring must use its neutral color.");
    Check.True(CountColor(TrayIconRasterizer.Render(64, 100), 101, 236, 183) > CountColor(fresh, 101, 236, 183));
    Check.True(CountColor(fresh, 101, 236, 183) > CountColor(TrayIconRasterizer.Render(64, 31), 101, 236, 183));
});

Test("icons: removing percent glyph keeps percentage in the tooltip", () =>
{
    var state = QuotaState.Initial.Apply(new(ReadStatus.Ready, new QuotaWindow(68, null)), DateTimeOffset.UtcNow);
    Check.True(state.Tooltip(DateTimeOffset.UtcNow).Contains("68%", StringComparison.Ordinal));
});

Test("icons: invalid image dimensions and quota inputs are rejected", () =>
{
    Check.Throws<ArgumentException>(() => new RgbaImage(2, 2, new byte[4]));
    Check.Throws<ArgumentException>(() => new RgbaImage(0, 2, []));
    Check.Throws<ArgumentNullException>(() => new RgbaImage(2, 2, null!));
    foreach (var size in new[] { 0, 15, 257 }) Check.Throws<ArgumentOutOfRangeException>(() => TrayIconRasterizer.Render(size, 20));
    foreach (var percent in new[] { -1, 101 }) Check.Throws<ArgumentOutOfRangeException>(() => TrayIconRasterizer.Render(16, percent));
    Check.Throws<ArgumentNullException>(() => TrayIconRasterizer.EncodePng(null!));
});

Test("icons: ICO has five complete native-sized lossless PNG frames", () =>
{
    var bytes = TrayIconRasterizer.EncodeIco(37, stale: true);
    using var stream = new MemoryStream(bytes);
    using var reader = new BinaryReader(stream);
    Check.Equal((ushort)0, reader.ReadUInt16());
    Check.Equal((ushort)1, reader.ReadUInt16());
    Check.Equal((ushort)5, reader.ReadUInt16());
    var expectedOffset = 6 + 5 * 16;
    foreach (var size in new[] { 16, 20, 24, 32, 64 })
    {
        Check.Equal((byte)size, reader.ReadByte());
        Check.Equal((byte)size, reader.ReadByte());
        Check.Equal((byte)0, reader.ReadByte());
        Check.Equal((byte)0, reader.ReadByte());
        Check.Equal((ushort)1, reader.ReadUInt16());
        Check.Equal((ushort)32, reader.ReadUInt16());
        var length = reader.ReadInt32();
        var offset = reader.ReadInt32();
        Check.Equal(expectedOffset, offset);
        Check.True(length > 0 && offset + length <= bytes.Length);
        var decoded = PngFixture.Decode(bytes.AsSpan(offset, length).ToArray());
        Check.Equal(size, decoded.Width);
        Check.Equal(size, decoded.Height);
        Check.True(decoded.Pixels.SequenceEqual(TrayIconRasterizer.Render(size, 37, stale: true).Pixels));
        expectedOffset += length;
    }
    Check.Equal(bytes.Length, expectedOffset);
});

RefreshTests.Register(Test);
StartupTests.Register(Test);
InstanceHandoffTests.Register(Test, AsyncTest);
CliDiscoveryTests.Register(Test);
AnimationTests.Register(Test);
PopupTests.Register(Test);
MenuPlacementTests.Register(Test);
SubmenuPlacementTests.Register(Test);
TrayRegistrationTests.Register(Test);
VectorIconTests.Register(Test);
QuotaRingColorTests.Register(Test);

PlanQuotaTests.Register(Test, AsyncTest);
MinimalTooltipTests.Register(Test);
ResetCardTests.Register(Test, AsyncTest);
TrayHoverTests.Register(Test);
HoverCardTests.Register(Test);
CompactHoverPresentationTests.Register(Test);
SingleLineHoverTests.Register(Test);
MetadataLabelTests.Register(Test);
LongTurnMetadataTests.Register(Test, AsyncTest);
HistoricalEffortTests.Register(Test);
OutputSpeedViewTests.Register(Test);
OutputMetadataDiagnosticTests.Register(Test);
OutputSpeedReaderTests.Register(Test, AsyncTest);
CliCompatibilityTests.Register(Test, AsyncTest);
SelectedBackendTests.Register(Test, AsyncTest);
SourceSelectionTests.Register(Test, AsyncTest);
SourceMenuPresentationTests.Register(Test);
ThemePreferencesTests.Register(Test);
TrayVisibilityHelperTests.Register(Test, AsyncTest);
DesktopRuntimeDiscoveryTests.Register(Test, AsyncTest);
SourceReadDiagnosticsTests.Register(Test, AsyncTest);
DesktopNativeReadTests.Register(Test, AsyncTest);
DesktopBudgetProtocolTests.Register(Test, AsyncTest);
DesktopEvidenceBudgetTests.Register(Test);
AutoSourcePolicyTests.Register(Test, AsyncTest);
TurnAverageTests.Register(Test, AsyncTest);
RolloutPollScheduleTests.Register(Test);
ResponseRateTests.Register(Test);
ResponseRateViewTests.Register(Test);
ReaderCatchupTests.Register(Test, AsyncTest);
BoundaryReasonTests.Register(Test);
ReadOnlyQuotaReaderTests.Register(Test, AsyncTest);
PlusProtocolCompatibilityTests.Register(Test, AsyncTest);
ColdStartMetadataTests.Register(Test, AsyncTest);
OutputSpeedParserTests.Register(Test);
RolloutModelRecoveryTests.Register(Test);
BackendCleanupTests.Register(Test, AsyncTest);
ResetFeedTests.Register(Test, AsyncTest);
ResetFeedFileTests.Register(Test, AsyncTest);
PowerShellResetFeedRunnerTests.Register(Test, AsyncTest);
ResetFeedDiagnosticTests.Register(Test, AsyncTest);
ResetNotificationUiTests.Register(Test);
ReaderPerformanceTests.Register(Test, AsyncTest);

var failures = new List<string>();
if (args.Length == 2 && args[0] == "--filter")
    tests.RemoveAll(test => !test.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase));
var suite = Stopwatch.StartNew();
foreach (var (name, run) in tests)
{
    try
    {
        await run().WaitAsync(TimeSpan.FromSeconds(name.Contains("request timeout", StringComparison.Ordinal) ? 20 : 8));
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add(name);
        Console.Error.WriteLine($"FAIL {name}\n{exception}");
    }
}
Console.WriteLine($"\n{tests.Count - failures.Count}/{tests.Count} tests passed in {suite.Elapsed.TotalSeconds:0.00}s.");
if (failures.Count > 0) Console.Error.WriteLine("Failed tests:\n- " + string.Join("\n- ", failures));
return failures.Count == 0 ? 0 : 1;

static int CountColor(RgbaImage image, byte red, byte green, byte blue)
{
    var count = 0;
    for (var i = 0; i < image.Pixels.Length; i += 4)
        if (image.Pixels[i] == red && image.Pixels[i + 1] == green && image.Pixels[i + 2] == blue && image.Pixels[i + 3] == 255) count++;
    return count;
}
static DateTimeOffset FixedNow() => new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
static QuotaWindow? Parse(string value) => QuotaParser.ReadWeekly(Json(value));
static QuotaState ReadyState() => QuotaState.Initial.Apply(new(ReadStatus.Ready, new(42, null)), FixedNow());

sealed class TestFailure(string message) : Exception(message);

static class Check
{
    public static void True(bool actual, string message = "Expected true.") { if (!actual) throw new TestFailure(message); }
    public static void False(bool actual, string message = "Expected false.") => True(!actual, message);
    public static void Null(object? value) { if (value is not null) throw new TestFailure($"Expected null, got {value}."); }
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new TestFailure($"Expected {expected}, got {actual}.");
    }
    public static void Contains(string expected, string actual) => True(actual.Contains(expected, StringComparison.Ordinal), $"Expected '{actual}' to contain '{expected}'.");
    public static void Method(JsonElement request, string expected) => Equal(expected, request.GetProperty("method").GetString());
    public static void Quota(QuotaWindow? actual, double remaining, int display)
    {
        True(actual is not null, "Expected a known quota.");
        True(Math.Abs(actual!.RemainingPercent - remaining) < 0.0000001, $"Expected remaining {remaining}, got {actual.RemainingPercent}.");
        Equal(display, actual.DisplayPercent);
    }
    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new TestFailure($"Expected {typeof(T).Name}, but operation succeeded.");
    }
    public static async Task StillPendingAsync(Task task)
    {
        await Task.Delay(40);
        False(task.IsCompleted, "Unexpected message/completion before the prerequisite response.");
    }
    public static async Task<T> ThrowsAsync<T>(Task task) where T : Exception
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (T exception) { return exception; }
        catch (Exception exception) { throw new TestFailure($"Expected {typeof(T).Name}, got {exception.GetType().Name}: {exception.Message}"); }
        throw new TestFailure($"Expected {typeof(T).Name}, but operation succeeded.");
    }
    public static async Task ThrowsAsync(Task task, Type expected)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception exception) when (expected.IsInstanceOfType(exception)) { return; }
        catch (Exception exception) { throw new TestFailure($"Expected {expected.Name}, got {exception.GetType().Name}: {exception.Message}"); }
        throw new TestFailure($"Expected {expected.Name}, but operation succeeded.");
    }
}

sealed class FakePeer : IAsyncDisposable
{
    private readonly MemoryDuplexStream _client;
    private readonly MemoryDuplexStream _server;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    public JsonRpcConnection Rpc { get; }
    public FakePeer(Func<TextWriter, TextWriter>? wrapWriter = null)
    {
        (_client, _server) = MemoryDuplexStream.CreatePair();
        var clientWriter = new StreamWriter(_client, new UTF8Encoding(false), 1024, true);
        Rpc = new JsonRpcConnection(new StreamReader(_client, Encoding.UTF8, false, 1024, true), wrapWriter?.Invoke(clientWriter) ?? clientWriter);
        _reader = new StreamReader(_server, Encoding.UTF8, false, 1024, true);
        _writer = new StreamWriter(_server, new UTF8Encoding(false), 1024, true);
    }
    public Task<JsonElement> RequestAsync(string method, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        Rpc.RequestAsync(method, new { }, timeout ?? TimeSpan.FromSeconds(2), cancellationToken);
    public async Task<JsonElement> ReadAsync()
    {
        var line = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
        if (line is null) throw new TestFailure("Client closed its stream before a message arrived.");
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }
    public Task RespondAsync(JsonElement request, object? result) => SendAsync(new { id = request.GetProperty("id").GetInt64(), result });
    public async Task SendAsync(object message) => await SendRawAsync(JsonSerializer.Serialize(message) + "\n");
    public async Task SendRawAsync(string message)
    {
        await _writer.WriteAsync(message);
        await _writer.FlushAsync();
    }
    public async Task SendBytesAsync(byte[] bytes) => await _server.WriteAsync(bytes);
    public void CloseServerOutput() => _server.CompleteWrites();
    public async ValueTask DisposeAsync()
    {
        // Cancel the server's deliberately pending absence probes before disposing its reader.
        _server.Dispose();
        await Rpc.DisposeAsync();
        _client.Dispose();
        _reader.Dispose();
        _writer.Dispose();
    }
}

// Stream-backed fake transport: channels deliver bytes, not pre-parsed RPC messages.
// It exercises StreamReader framing, UTF-8 decoding, fragmentation, EOF and cancellation.
sealed class MemoryDuplexStream(Channel<byte[]> incoming, Channel<byte[]> outgoing) : Stream
{
    private readonly CancellationTokenSource _closed = new();
    private byte[]? _buffer;
    private int _offset;
    private int _disposed;
    public static (MemoryDuplexStream Client, MemoryDuplexStream Server) CreatePair()
    {
        var toClient = Channel.CreateUnbounded<byte[]>();
        var toServer = Channel.CreateUnbounded<byte[]>();
        return (new(toClient, toServer), new(toServer, toClient));
    }
    public void CompleteWrites() => outgoing.Writer.TryComplete();
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.IsEmpty) return 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        while (_buffer is null || _offset == _buffer.Length)
        {
            if (!await incoming.Reader.WaitToReadAsync(linked.Token)) return 0;
            if (!incoming.Reader.TryRead(out _buffer)) continue;
            _offset = 0;
        }
        var length = Math.Min(destination.Length, _buffer.Length - _offset);
        _buffer.AsMemory(_offset, length).CopyTo(destination);
        _offset += length;
        return length;
    }
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return outgoing.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _closed.Cancel();
            outgoing.Writer.TryComplete();
            // Keep CTS alive while canceled asynchronous reads unwind.
        }
        base.Dispose(disposing);
    }
}

sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codex-quota-tests-" + Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Path);
    public string Touch(string relative)
    {
        var path = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not executable; test fixture only");
        return path;
    }
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

sealed class DelayedWriter(TextWriter inner) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _writing;
    public bool WasDisposed { get; private set; }
    public bool DisposedDuringWrite { get; private set; }
    public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        _writing = true;
        Started.TrySetResult();
        try
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException)
            {
                Canceled.TrySetResult();
                await Release.Task;
                throw;
            }
        }
        finally { _writing = false; }
    }
    protected override void Dispose(bool disposing)
    {
        DisposedDuringWrite |= _writing;
        WasDisposed = true;
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

sealed class ThrowingDisposeReader() : StringReader("")
{
    public int DisposeCount { get; private set; }
    protected override void Dispose(bool disposing)
    {
        DisposeCount++;
        base.Dispose(disposing);
        throw new IOException("Synthetic reader disposal failure.");
    }
}

sealed class ThrowingDisposeWriter : StringWriter
{
    public int DisposeCount { get; private set; }
    protected override void Dispose(bool disposing)
    {
        DisposeCount++;
        base.Dispose(disposing);
        throw new InvalidOperationException("Synthetic active writer disposal failure.");
    }
}

static class FakeAppServer
{
    public static async Task<int> RunAsync()
    {
        var scenario = Environment.GetEnvironmentVariable("CODEX_QUOTA_TEST_SCENARIO");
        var marker = Environment.GetEnvironmentVariable("CODEX_QUOTA_TEST_PID_FILE");
        if (scenario is not ("success" or "hang") || string.IsNullOrEmpty(marker)) return 64;
        File.AppendAllText(marker, Environment.ProcessId + Environment.NewLine);
        if (scenario == "hang") { await Task.Delay(Timeout.Infinite); return 0; }
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            var method = message.GetProperty("method").GetString();
            if (!message.TryGetProperty("id", out var id)) continue;
            object result = method switch
            {
                "initialize" => new { userAgent = "isolated test fixture" },
                "account/rateLimits/read" => new { rateLimits = new { primary = new { usedPercent = 27, windowDurationMins = 10080 } } },
                _ => new { }
            };
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id = id.GetInt64(), result }));
            await Console.Out.FlushAsync();
            if (method == "account/rateLimits/read") await Task.Delay(Timeout.Infinite);
        }
        return 0;
    }
}

sealed class FakeChildEnvironment : IDisposable
{
    private readonly TempDirectory _files = new();
    private readonly Dictionary<string, string?> _saved = new();
    private readonly string _marker;
    public string Executable { get; }
    public FakeChildEnvironment(string scenario)
    {
        Executable = System.IO.Path.Combine(AppContext.BaseDirectory, "CodexQuota.Tests" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Check.True(File.Exists(Executable), "The test apphost must exist to run isolated child-process tests.");
        _marker = System.IO.Path.Combine(_files.Path, "children.txt");
        Set("CODEX_QUOTA_TEST_SCENARIO", scenario);
        Set("CODEX_QUOTA_TEST_PID_FILE", _marker);
        // The test apphost locates the same runtime even when the SDK was unpacked into a temporary directory.
        var runtimeRoot = Directory.GetParent(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(System.IO.Path.DirectorySeparatorChar))?.Parent?.Parent?.FullName;
        Set("DOTNET_ROOT", runtimeRoot);
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64) Set("DOTNET_ROOT_X64", runtimeRoot);
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) Set("DOTNET_ROOT_ARM64", runtimeRoot);
    }
    private void Set(string name, string? value)
    {
        _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }
    public async Task WaitForChildAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!File.Exists(_marker) || new FileInfo(_marker).Length == 0) await Task.Delay(10, deadline.Token);
    }
    public async Task AssertChildrenExitedAsync(int expectedCount)
    {
        await WaitForChildAsync();
        var pids = File.ReadAllLines(_marker).Where(line => !string.IsNullOrWhiteSpace(line)).Select(int.Parse).ToArray();
        Check.Equal(expectedCount, pids.Length);
        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                Check.True(process.HasExited, $"Fake app-server {pid} survived cleanup.");
            }
            catch (ArgumentException) { /* Already reaped by the process owner. */ }
        }
    }
    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        _files.Dispose();
    }
}

static class PngFixture
{
    public static RgbaImage Decode(byte[] bytes)
    {
        Check.True(bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        var offset = 8;
        var width = 0;
        var height = 0;
        var headerSeen = false;
        var endSeen = false;
        using var compressed = new MemoryStream();
        while (offset < bytes.Length)
        {
            Check.True(offset + 12 <= bytes.Length, "Truncated PNG chunk.");
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            Check.True(length >= 0 && offset + 12L + length <= bytes.Length);
            var type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
            var payload = bytes.AsSpan(offset + 8, length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + length, 4));
            Check.Equal(crc, Crc(bytes.AsSpan(offset + 4, length + 4)));
            if (!headerSeen) Check.Equal("IHDR", type);
            switch (type)
            {
                case "IHDR":
                    Check.False(headerSeen);
                    Check.Equal(13, length);
                    width = BinaryPrimitives.ReadInt32BigEndian(payload);
                    height = BinaryPrimitives.ReadInt32BigEndian(payload[4..]);
                    Check.Equal((byte)8, payload[8]);
                    Check.Equal((byte)6, payload[9]);
                    Check.True(payload[10..].ToArray().All(value => value == 0));
                    headerSeen = true;
                    break;
                case "IDAT": compressed.Write(payload); break;
                case "IEND": Check.Equal(0, length); endSeen = true; break;
                default: throw new TestFailure($"Unexpected PNG chunk: {type}.");
            }
            offset += length + 12;
            if (endSeen) break;
        }
        Check.True(headerSeen && endSeen);
        Check.Equal(bytes.Length, offset);
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        zlib.CopyTo(decoded);
        var raw = decoded.ToArray();
        var stride = checked(width * 4);
        Check.Equal(height * (stride + 1), raw.Length);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            Check.Equal((byte)0, raw[y * (stride + 1)]);
            raw.AsSpan(y * (stride + 1) + 1, stride).CopyTo(pixels.AsSpan(y * stride, stride));
        }
        return new(width, height, pixels);
    }
    private static uint Crc(ReadOnlySpan<byte> input)
    {
        uint crc = uint.MaxValue;
        foreach (var value in input)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }
}
