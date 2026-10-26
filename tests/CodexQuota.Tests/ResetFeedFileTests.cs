using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexQuota.Core;

static class ResetFeedFileTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 6, 0, 0, TimeSpan.Zero);
    private static readonly ResetFeedFileRequest Request = new("01234567-89ab-cdef-0123-456789abcdef", Now, null);
    private const string Latest = """{"id":"2112432076767289678","reset_type":"regular","announced_at":"2026-10-20T06:33:48.000Z","text":"Historical reset announcement.","source":{"type":"x_post","author":"thsottiaux","url":"https://x.com/thsottiaux/status/2112432076767289678"}}""";
    private const string Scheduled = """{"id":"2112432076767289679","status":"scheduled","reset_type":"regular","announced_at":"2026-10-22T05:00:00Z","scheduled_for":"2026-10-22T07:00:00Z","text":"Public forecast.","source":{"type":"observed"}}""";
    private const string Stats = """{"total":64,"last_reset_at":"2026-10-20T06:33:48.000Z","days_since_last":1.98,"avg_interval_days":6.3}""";
    private const string Meta = """{"api_version":"v1","generated_at":"2026-10-22T06:00:00Z"}""";
    private static string Data => "{\"latest_reset\":" + Latest + ",\"scheduled_reset\":null,\"active_watch\":null,\"stats\":" + Stats + "}";
    private static string ForecastData => Data.Replace("\"scheduled_reset\":null", "\"scheduled_reset\":" + Scheduled);

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        asyncTest("reset file: success preserves full public data stats and meta", async () =>
        {
            using var response = ResetFeedFileProtocol.Parse(Envelope(Request, etag: "W/\"fixture\""), Request, Now);
            Check.Equal(HttpStatusCode.OK, response.StatusCode);
            Check.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
            Check.Equal("W/\"fixture\"", response.Headers.ETag!.ToString());
            var body = await response.Content.ReadAsByteArrayAsync();
            using var document = JsonDocument.Parse(body);
            Check.Equal(2, document.RootElement.EnumerateObject().Count());
            Check.Equal(Data, document.RootElement.GetProperty("data").GetRawText());
            Check.Equal(Stats, document.RootElement.GetProperty("data").GetProperty("stats").GetRawText());
            Check.Equal(Meta, document.RootElement.GetProperty("meta").GetRawText());
            Check.True(ResetFeedParser.TryParse(body, out _));
        });

        asyncTest("reset file: only fixed anonymous GET invokes one fetch", async () =>
        {
            var calls = 0;
            using var http = new HttpClient(new PowerShellResetFeedHandler((request, token) =>
            {
                calls++;
                Check.True(token.CanBeCanceled);
                Check.Equal(Now, request.StartedAtUtc);
                Check.True(Guid.TryParseExact(request.RequestId, "D", out _));
                Check.Null(request.ETag);
                return Task.FromResult(Envelope(request));
            }, () => Now));
            using var success = await http.GetAsync(ResetFeedClient.Endpoint);
            Check.Equal(HttpStatusCode.OK, success.StatusCode);
            Check.Equal(1, calls);
            using var otherEndpoint = new HttpRequestMessage(HttpMethod.Get, "https://invalid.example/status");
            using var post = new HttpRequestMessage(HttpMethod.Post, ResetFeedClient.Endpoint);
            using var body = new HttpRequestMessage(HttpMethod.Get, ResetFeedClient.Endpoint) { Content = new StringContent("private") };
            using var authenticated = new HttpRequestMessage(HttpMethod.Get, ResetFeedClient.Endpoint);
            authenticated.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "private");
            using var proxy = new HttpRequestMessage(HttpMethod.Get, ResetFeedClient.Endpoint);
            proxy.Headers.TryAddWithoutValidation("Proxy-Authorization", "private");
            foreach (var invalid in new[] { otherEndpoint, post, body, authenticated, proxy })
            {
                var error = await Check.ThrowsAsync<ResetFeedFileException>(http.SendAsync(invalid));
                Check.Equal(ResetFeedFileFailure.OutputInvalid, error.Failure);
            }
            Check.Equal(1, calls);
        });

        asyncTest("reset file: fresh transport allows historical latest without forecast dot", async () =>
        {
            var calls = 0;
            using var client = Client((request, _) => { calls++; return Task.FromResult(Envelope(request)); });
            var result = await client.FetchAsync();
            Check.Equal(ResetFeedReadStatus.Success, result.Status);
            Check.Equal(new DateTimeOffset(2026, 10, 20, 6, 33, 48, TimeSpan.Zero), result.Snapshot!.LatestReset!.AnnouncedAt);
            var view = ResetFeedPresentation.Create(result, Now);
            Check.True(view.IsVisible);
            Check.Equal("上次重置", view.Latest!.Label);
            Check.Null(view.Forecast);
            Check.Null(view.EventIdentity);
            Check.Equal(1, calls);
        });

        asyncTest("reset file: all-null feed is a valid hidden success", async () =>
        {
            using var client = Client((request, _) => Task.FromResult(Envelope(request, data: Data.Replace(Latest, "null"))));
            var result = await client.FetchAsync();
            Check.True(result.IsSuccess);
            Check.Null(result.Snapshot!.LatestReset);
            Check.Null(result.Snapshot.ScheduledReset);
            Check.Null(result.Snapshot.ActiveWatch);
            Hidden(result);
        });

        test("reset file: malformed JSON never becomes a response", () =>
        {
            foreach (var input in new[] { "", "{", "null", "[]", "{\"schemaVersion\":1,}" })
                Reject(Encoding.UTF8.GetBytes(input), ResetFeedFileFailure.OutputInvalid);
            Reject(new byte[] { 0xff, 0xfe, 0xfd }, ResetFeedFileFailure.OutputInvalid);
        });

        test("reset file: required unique exact fields and schema version are enforced", () =>
        {
            var valid = Encoding.UTF8.GetString(Envelope(Request));
            Reject(Encoding.UTF8.GetBytes(valid.Replace("\"schemaVersion\":1,", "")), ResetFeedFileFailure.OutputInvalid);
            Reject(Encoding.UTF8.GetBytes(valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")), ResetFeedFileFailure.OutputInvalid);
            Reject(Encoding.UTF8.GetBytes(valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":2")), ResetFeedFileFailure.OutputInvalid);
            Reject(Encoding.UTF8.GetBytes(valid.Replace("\"schemaVersion\":1", "\"SchemaVersion\":1")), ResetFeedFileFailure.OutputInvalid);
            Reject(Encoding.UTF8.GetBytes(valid.Insert(1, "\"privateExtra\":\"do not accept\",")), ResetFeedFileFailure.OutputInvalid);
            var missingErrorCode = Fields(Request);
            missingErrorCode.Remove("errorCode");
            Reject(JsonSerializer.SerializeToUtf8Bytes(missingErrorCode), ResetFeedFileFailure.OutputInvalid);
        });

        test("reset file: another invocation cannot supply this result", () =>
        {
            var other = Request with { RequestId = "fedcba98-7654-3210-fedc-ba9876543210" };
            Reject(Envelope(other), ResetFeedFileFailure.OutputStale);
            Reject(Envelope(Request with { RequestId = Request.RequestId.ToUpperInvariant() }), ResetFeedFileFailure.OutputStale);
        });

        test("reset file: transport freshness rejects old future and expired attempts", () =>
        {
            Reject(Envelope(Request, fetched: Now.AddSeconds(-3)), ResetFeedFileFailure.OutputStale);
            Reject(Envelope(Request, fetched: Now.AddSeconds(3)), ResetFeedFileFailure.OutputStale);
            var old = Request with { StartedAtUtc = Now.AddMinutes(-2).AddTicks(-1) };
            Reject(Envelope(old), ResetFeedFileFailure.OutputStale, old);
            var future = Request with { StartedAtUtc = Now.AddSeconds(3) };
            Reject(Envelope(future), ResetFeedFileFailure.OutputStale, future);
            using var oldestAllowed = ResetFeedFileProtocol.Parse(Envelope(Request, fetched: Now.AddSeconds(-2)), Request, Now);
            using var newestAllowed = ResetFeedFileProtocol.Parse(Envelope(Request, fetched: Now.AddSeconds(2)), Request, Now);
            Check.Equal(HttpStatusCode.OK, oldestAllowed.StatusCode);
            Check.Equal(HttpStatusCode.OK, newestAllowed.StatusCode);
        });

        test("reset file: fetched timestamp must explicitly use UTC", () =>
        {
            foreach (var timestamp in new[] { "2026-10-22T06:00:00", "2026-10-22T07:00:00+01:00", "not-a-time" })
            {
                var fields = Fields(Request);
                fields["fetchedAtUtc"] = timestamp;
                Reject(JsonSerializer.SerializeToUtf8Bytes(fields), ResetFeedFileFailure.OutputInvalid);
            }
        });

        test("reset file: local envelope byte limit is independent of body limit", () =>
        {
            Check.Equal(96 * 1024, ResetFeedFileProtocol.MaximumEnvelopeBytes);
            Reject(new byte[ResetFeedFileProtocol.MaximumEnvelopeBytes + 1], ResetFeedFileFailure.OutputTooLarge);
        });

        test("reset file: reconstructed body is bounded by UTF-8 bytes", () =>
        {
            var data = "{\"padding\":\"" + new string('界', ResetFeedParser.MaximumBodyBytes / 3) + "\"}";
            // Raw UTF-8 avoids the JSON serializer's optional ASCII escaping obscuring the byte boundary.
            var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Envelope(Request)).Replace(Data, data));
            Check.True(bytes.Length < ResetFeedFileProtocol.MaximumEnvelopeBytes);
            Reject(bytes, ResetFeedFileFailure.OutputTooLarge);
        });

        test("reset file: exact status and HTTP combinations cannot smuggle bodies", () =>
        {
            foreach (var status in new[] { "success", "SUCCESS", "Unknown", "Failed", "Timeout", "HttpError", "NotModified", "Error" })
                Reject(Envelope(Request, status: status), ResetFeedFileFailure.OutputInvalid);
            Reject(Envelope(Request, httpStatus: 304), ResetFeedFileFailure.OutputInvalid);
            Reject(Envelope(Request, status: "NotModified", httpStatus: 304), ResetFeedFileFailure.OutputInvalid);
            Reject(Envelope(Request, status: "Error", httpStatus: 503), ResetFeedFileFailure.OutputInvalid);
            Reject(Envelope(Request, status: "Success", httpStatus: 200, data: "null"), ResetFeedFileFailure.OutputInvalid);
            Reject(Envelope(Request, status: "Success", httpStatus: 200, meta: "null"), ResetFeedFileFailure.OutputInvalid);
            foreach (var code in new[] { 99, 600 })
                Reject(Envelope(Request, status: "Error", httpStatus: code, data: "null", meta: "null"), ResetFeedFileFailure.OutputInvalid);
        });

        test("reset file: ETag and Retry-After values are bounded typed metadata", () =>
        {
            foreach (var etag in new[] { "unquoted", "\"bad\r\ntag\"", "\"" + new string('a', 255) + "\"" })
                Reject(Envelope(Request, etag: etag), ResetFeedFileFailure.OutputInvalid);
            foreach (var seconds in new[] { -1, 604801 })
                Reject(Envelope(Request, retry: seconds), ResetFeedFileFailure.OutputInvalid);
            foreach (var name in new[] { "httpStatus", "retryAfterSeconds" })
            {
                var fields = Fields(Request);
                fields[name] = "200";
                Reject(JsonSerializer.SerializeToUtf8Bytes(fields), ResetFeedFileFailure.OutputInvalid);
            }
            using var bounded = ResetFeedFileProtocol.Parse(Envelope(Request, retry: 604800), Request, Now);
            Check.Equal(TimeSpan.FromDays(7), bounded.Headers.RetryAfter!.Delta);
        });

        test("reset file: child failure and timeout expose only closed exceptions", () =>
        {
            Reject(Envelope(Request, status: "Error", httpStatus: null, data: "null", meta: "null", errorCode: "FetchOrValidationFailed"), ResetFeedFileFailure.RequestFailed);
            Reject(Envelope(Request, status: "Error", httpStatus: null, data: "null", meta: "null", errorCode: "DeadlineExceeded"), ResetFeedFileFailure.RequestTimeout);
            Reject(Envelope(Request, status: "Error", httpStatus: null, data: "null", meta: "null", errorCode: "EnvelopeTooLarge"), ResetFeedFileFailure.OutputTooLarge);
            Reject(Envelope(Request, status: "Error", httpStatus: null, data: "null", meta: "null", errorCode: "private child error"), ResetFeedFileFailure.OutputInvalid);
            Reject(Envelope(Request, errorCode: "FetchOrValidationFailed"), ResetFeedFileFailure.OutputInvalid);
            Reject(Envelope(Request, status: "Error", httpStatus: 200, data: "null", meta: "null"), ResetFeedFileFailure.OutputInvalid);
        });

        asyncTest("reset file: 200 then 304 round-trips validator and revalidates cache", async () =>
        {
            var calls = 0;
            string? firstId = null;
            using var client = Client((request, _) =>
            {
                if (++calls == 1)
                {
                    firstId = request.RequestId;
                    Check.Null(request.ETag);
                    return Task.FromResult(Envelope(request, etag: "\"fixture\""));
                }
                Check.False(request.RequestId == firstId);
                Check.Equal("\"fixture\"", request.ETag);
                return Task.FromResult(Envelope(request, status: "NotModified", httpStatus: 304,
                    data: "null", meta: "null", etag: "\"fixture\""));
            });
            var first = await client.FetchAsync();
            var second = await client.FetchAsync();
            Check.Equal(ResetFeedReadStatus.Success, first.Status);
            Check.Equal(ResetFeedReadStatus.NotModified, second.Status);
            Check.Equal(first.Snapshot, second.Snapshot);
            Check.Equal(2, calls);
        });

        asyncTest("reset file: 304 without prior success or stored ETag is rejected", async () =>
        {
            foreach (var seedWithoutTag in new[] { false, true })
            {
                var calls = 0;
                using var client = Client((request, _) =>
                {
                    Check.Null(request.ETag);
                    return Task.FromResult(++calls == 1 && seedWithoutTag ? Envelope(request) :
                        Envelope(request, status: "NotModified", httpStatus: 304, data: "null", meta: "null"));
                });
                if (seedWithoutTag) Check.True((await client.FetchAsync()).IsSuccess);
                var result = await client.FetchAsync();
                Check.Equal(ResetFeedReadStatus.InvalidResponse, result.Status);
                Check.Equal(ResetFeedFailure.InvalidCache, result.Diagnostic!.Failure);
                Check.Null(result.Snapshot);
                Check.Equal(seedWithoutTag ? 2 : 1, calls);
            }
        });

        asyncTest("reset file: mismatched 304 validator never exposes cached data", async () =>
        {
            var calls = 0;
            using var client = Client((request, _) => Task.FromResult(++calls == 1 ? Envelope(request, etag: "\"first\"") :
                Envelope(request, status: "NotModified", httpStatus: 304, data: "null", meta: "null", etag: "\"other\"")));
            Check.True((await client.FetchAsync()).IsSuccess);
            var result = await client.FetchAsync();
            Check.Equal(ResetFeedReadStatus.InvalidResponse, result.Status);
            Check.Null(result.Snapshot);
            Hidden(result);
            Check.Equal(2, calls);
        });

        asyncTest("reset file: HTTP 429 preserves Retry-After without retrying", async () =>
        {
            var calls = 0;
            using var client = Client((request, _) =>
            {
                calls++;
                return Task.FromResult(Envelope(request, status: "Error", httpStatus: 429, data: "null", meta: "null", retry: 1200));
            });
            var result = await client.FetchAsync();
            Check.Equal(ResetFeedReadStatus.RateLimited, result.Status);
            Check.Equal(TimeSpan.FromMinutes(20), result.RetryAfter);
            Check.Equal(429, result.Diagnostic!.HttpStatus);
            Check.Null(result.Snapshot);
            Check.Equal(1, calls);
        });

        asyncTest("reset file: child timeout retains timeout classification without retry", async () =>
        {
            var calls = 0;
            using var client = Client((request, _) =>
            {
                calls++;
                return Task.FromResult(Envelope(request, status: "Error", httpStatus: null, data: "null", meta: "null", errorCode: "DeadlineExceeded"));
            });
            var result = await client.FetchAsync();
            Check.Equal(ResetFeedReadStatus.Timeout, result.Status);
            Check.Equal(ResetFeedFailure.Timeout, result.Diagnostic!.Failure);
            Check.Null(result.Snapshot);
            Check.Equal(1, calls);
        });

        asyncTest("reset file: parent deadline bounds an uncooperative fetch without retry", async () =>
        {
            var calls = 0;
            var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = Client((_, _) => { calls++; return completion.Task; }, TimeSpan.FromMilliseconds(40));
            try
            {
                var result = await client.FetchAsync().WaitAsync(TimeSpan.FromSeconds(3));
                Check.Equal(ResetFeedReadStatus.Timeout, result.Status);
                Check.Null(result.Snapshot);
                Check.Equal(1, calls);
            }
            finally { completion.TrySetResult(Array.Empty<byte>()); }
        });

        asyncTest("reset file: caller cancellation stays distinct and never retries", async () =>
        {
            var calls = 0;
            using var cancellation = new CancellationTokenSource();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = Client(async (_, token) =>
            {
                calls++;
                started.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Array.Empty<byte>();
            });
            var pending = client.FetchAsync(cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Check.Equal(ResetFeedReadStatus.Cancelled, result.Status);
            Check.Equal(ResetFeedFailure.Cancelled, result.Diagnostic!.Failure);
            Check.Null(result.Snapshot);
            Check.Equal(1, calls);
            Check.Equal(ResetFeedReadStatus.Cancelled, (await client.FetchAsync(cancellation.Token)).Status);
            Check.Equal(1, calls);
        });

        asyncTest("reset file: active invocation blocks overlap until its fetch unwinds", async () =>
        {
            var calls = 0;
            ResetFeedFileRequest? firstRequest = null;
            var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var http = new HttpClient(new PowerShellResetFeedHandler((request, _) =>
            {
                if (++calls != 1) return Task.FromResult(Envelope(request));
                firstRequest = request;
                return completion.Task;
            }, () => Now));
            var first = http.GetAsync(ResetFeedClient.Endpoint);
            try
            {
                var error = await Check.ThrowsAsync<ResetFeedFileException>(http.GetAsync(ResetFeedClient.Endpoint));
                Check.Equal(ResetFeedFileFailure.CleanupPending, error.Failure);
                Check.Equal(1, calls);
                completion.SetResult(Envelope(firstRequest!));
                using var firstResponse = await first.WaitAsync(TimeSpan.FromSeconds(3));
                using var nextResponse = await http.GetAsync(ResetFeedClient.Endpoint);
                Check.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
                Check.Equal(HttpStatusCode.OK, nextResponse.StatusCode);
                Check.Equal(2, calls);
            }
            finally { completion.TrySetResult(Array.Empty<byte>()); }
        });

        asyncTest("reset file: invalid output is a safe diagnostic not a transport retry", async () =>
        {
            var calls = 0;
            using var client = Client((_, _) =>
            {
                calls++;
                return Task.FromResult(Encoding.UTF8.GetBytes("{private child output C:\\Users\\Private https://private.invalid}"));
            });
            var result = await client.FetchAsync();
            Check.Equal(ResetFeedReadStatus.InvalidResponse, result.Status);
            Check.Null(result.Snapshot);
            var diagnostics = new ResetFeedDiagnosticSnapshot(null, new(1, Now, Now, result), null);
            var text = ResetFeedDiagnosticText.Format(diagnostics, ResetFeedPresentation.Hidden, Now);
            Check.Contains("PowerShell7Json", text);
            Check.Contains("OutputInvalid", text);
            Check.False(text.Contains("Private", StringComparison.Ordinal));
            Check.False(text.Contains("https://", StringComparison.Ordinal));
            Check.Equal(1, calls);
        });

        asyncTest("reset file: original public parser still rejects invalid stats and duplicate nested fields", async () =>
        {
            foreach (var data in new[]
            {
                Data.Replace("\"total\":64", "\"total\":-1"),
                Data.Replace("\"total\":64", "\"total\":64,\"total\":64"),
                Data.Replace("\"stats\":" + Stats, "\"stats\":null")
            })
            {
                using var client = Client((request, _) => Task.FromResult(Envelope(request, data: data)));
                var result = await client.FetchAsync();
                Check.Equal(ResetFeedReadStatus.InvalidResponse, result.Status);
                Check.Equal(ResetFeedFailure.InvalidSchema, result.Diagnostic!.Failure);
                Check.Null(result.Snapshot);
            }
        });

        asyncTest("reset file: every failure hides cached rows and forecast identity", async () =>
        {
            foreach (var failure in new[] { "Failed", "HttpError", "Malformed" })
            {
                var calls = 0;
                using var client = Client((request, _) =>
                {
                    if (++calls == 1) return Task.FromResult(Envelope(request, data: ForecastData, etag: "\"fixture\""));
                    return Task.FromResult(failure switch
                    {
                        "Failed" => Envelope(request, status: "Error", httpStatus: null, data: "null", meta: "null", errorCode: "FetchOrValidationFailed"),
                        "HttpError" => Envelope(request, status: "Error", httpStatus: 503, data: "null", meta: "null"),
                        _ => Encoding.UTF8.GetBytes("{")
                    });
                });
                var first = await client.FetchAsync();
                var visible = ResetFeedPresentation.Create(first, Now);
                Check.True(visible.Latest is not null && visible.Forecast is not null && visible.EventIdentity is not null);
                var failed = await client.FetchAsync();
                Check.Equal(failure == "Failed" ? ResetFeedReadStatus.NetworkError : failure == "HttpError" ?
                    ResetFeedReadStatus.HttpError : ResetFeedReadStatus.InvalidResponse, failed.Status);
                Check.Null(failed.Snapshot);
                Hidden(failed);
                Check.Equal(2, calls);
            }
        });
    }

    private static ResetFeedClient Client(Func<ResetFeedFileRequest, CancellationToken, Task<byte[]>> fetch,
        TimeSpan? timeout = null) => new(new PowerShellResetFeedHandler(fetch, () => Now), timeout,
            transport: ResetFeedTransport.PowerShell7Json);

    private static Dictionary<string, object?> Fields(ResetFeedFileRequest request, string status = "Success",
        int? httpStatus = 200, string? data = null, string? meta = null, string? etag = null,
        int? retry = null, DateTimeOffset? fetched = null, string? errorCode = null) => new()
    {
        ["schemaVersion"] = 1,
        ["requestId"] = request.RequestId,
        ["status"] = status,
        ["fetchedAtUtc"] = (fetched ?? Now).ToString("O"),
        ["httpStatus"] = httpStatus,
        ["etag"] = etag,
        ["retryAfterSeconds"] = retry,
        ["data"] = JsonSerializer.Deserialize<JsonElement>(data ?? Data),
        ["meta"] = JsonSerializer.Deserialize<JsonElement>(meta ?? Meta),
        ["errorCode"] = errorCode ?? (status == "Error" ? "FetchOrValidationFailed" : null)
    };

    private static byte[] Envelope(ResetFeedFileRequest request, string status = "Success", int? httpStatus = 200,
        string? data = null, string? meta = null, string? etag = null, int? retry = null, DateTimeOffset? fetched = null,
        string? errorCode = null) =>
        JsonSerializer.SerializeToUtf8Bytes(Fields(request, status, httpStatus, data, meta, etag, retry, fetched, errorCode));

    private static void Reject(byte[] bytes, ResetFeedFileFailure expected, ResetFeedFileRequest? request = null)
    {
        try
        {
            using var response = ResetFeedFileProtocol.Parse(bytes, request ?? Request, Now);
        }
        catch (ResetFeedFileException error)
        {
            Check.Equal(expected, error.Failure);
            Check.False(error.Message.Contains("http", StringComparison.OrdinalIgnoreCase));
            return;
        }
        throw new TestFailure("Expected closed reset-file failure " + expected + ".");
    }

    private static void Hidden(ResetFeedReadResult result)
    {
        var view = ResetFeedPresentation.Create(result, Now);
        Check.False(view.IsVisible);
        Check.Null(view.Latest);
        Check.Null(view.Forecast);
        Check.Null(view.EventIdentity);
    }
}
