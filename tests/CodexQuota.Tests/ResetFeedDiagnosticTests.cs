using System.Net;
using System.Net.Http;
using System.Text;
using CodexQuota.Core;

static class ResetFeedDiagnosticTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 8, 18, 0, TimeSpan.Zero);
    // Exact public response pasted by the user after the missing-row report.
    private const string LiveUserResponse = """{"data":{"latest_reset":{"id":"2112432076767289678","reset_type":"regular","announced_at":"2026-10-20T06:33:48.000Z","text":"Day 15/ Nevermind, we're still cooking... and reset. Enjoy. https://t.co/RZ9WhtI802","source":{"type":"x_post","author":"thsottiaux","url":"https://x.com/thsottiaux/status/2112432076767289678"}},"scheduled_reset":null,"active_watch":null,"stats":{"total":64,"last_reset_at":"2026-10-20T06:33:48.000Z","days_since_last":0.1,"avg_interval_days":6.3}},"meta":{"api_version":"v1","generated_at":"2026-10-20T08:17:51.443Z"}}""";

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        asyncTest("reset diagnosis: exact user live response reaches visible latest via production client", async () =>
        {
            var calls = 0;
            using var client = new ResetFeedClient(new Handler(() =>
            {
                calls++;
                return new(HttpStatusCode.OK) { Content = new StringContent(LiveUserResponse, Encoding.UTF8, "application/json") };
            }));
            var result = await client.FetchAsync();
            Check.Equal(ResetFeedReadStatus.Success, result.Status);
            Check.Equal(ResetFeedStage.Complete, result.Diagnostic!.Stage);
            Check.Equal(200, result.Diagnostic.HttpStatus!.Value);
            var presentation = ResetFeedPresentation.Create(result, Now);
            Check.Equal("1 小时前", presentation.Latest!.Detail);
            Check.Null(presentation.Forecast);
            var text = Format(result, presentation);
            Check.Contains("parse=Passed latestPresent=true", text);
            Check.Contains("latestVisible=true forecastVisible=false hiddenReason=None", text);
            Check.Equal(1, calls); // Formatting is read-only and does not poll.
            Check.False(text.Contains("2112432076767289678"));
            Check.False(text.Contains("https://"));
            Check.False(text.Contains("Nevermind"));
        });
        asyncTest("reset diagnosis: HTTP failures retain only numeric response status", async () =>
        {
            foreach (var code in new[] { 302, 407, 429, 503 })
            {
                using var client = new ResetFeedClient(new Handler(() => new((HttpStatusCode)code)
                { Content = new StringContent("private payload https://proxy.example/secret") }));
                var result = await client.FetchAsync();
                Check.Equal(code, result.Diagnostic!.HttpStatus!.Value);
                Check.Equal(ResetFeedStage.Headers, result.Diagnostic.Stage);
                Check.Equal(ResetFeedFailure.HttpStatus, result.Diagnostic.Failure);
                var text = Format(result, ResetFeedPresentation.Hidden);
                Check.Contains("http=" + code, text);
                Check.Contains("parse=NotReached", text);
                Check.Contains("hiddenReason=ReadFailed", text);
                Check.False(text.Contains("private"));
                Check.False(text.Contains("proxy.example"));
            }
        });
        asyncTest("reset diagnosis: malformed JSON is parsing failure, not network failure", async () =>
        {
            using var client = new ResetFeedClient(new Handler(() => new(HttpStatusCode.OK)
            { Content = new StringContent("{\"secret\":\"private\"}", Encoding.UTF8, "application/json") }));
            var result = await client.FetchAsync();
            Check.Equal(ResetFeedFailure.InvalidSchema, result.Diagnostic!.Failure);
            Check.Equal(ResetFeedStage.Parse, result.Diagnostic.Stage);
            Check.Contains("parse=Failed", Format(result, ResetFeedPresentation.Hidden));
        });
        asyncTest("reset diagnosis: content type is a closed failure label, not a raw header", async () =>
        {
            using var client = new ResetFeedClient(new Handler(() => new(HttpStatusCode.OK)
            { Content = new StringContent("private", Encoding.UTF8, "text/private-sensitive-header") }));
            var result = await client.FetchAsync();
            Check.Equal(ResetFeedFailure.ContentType, result.Diagnostic!.Failure);
            Check.False(Format(result, ResetFeedPresentation.Hidden).Contains("private"));
        });
        asyncTest("reset diagnosis: request timeout retains stage without HTTP response", async () =>
        {
            using var client = new ResetFeedClient(new DelayHandler(), TimeSpan.FromMilliseconds(30));
            var result = await client.FetchAsync();
            Check.Equal(ResetFeedReadStatus.Timeout, result.Status);
            Check.Equal(ResetFeedStage.Request, result.Diagnostic!.Stage);
            Check.Equal(ResetFeedFailure.Timeout, result.Diagnostic.Failure);
            Check.Null(result.Diagnostic.HttpStatus);
        });
        foreach (var entry in new[]
        {
            (WebExceptionStatus.NameResolutionFailure, ResetFeedFailure.Dns),
            (WebExceptionStatus.ProxyNameResolutionFailure, ResetFeedFailure.ProxyDns),
            (WebExceptionStatus.ConnectFailure, ResetFeedFailure.Connect),
            (WebExceptionStatus.TrustFailure, ResetFeedFailure.Tls),
            (WebExceptionStatus.SecureChannelFailure, ResetFeedFailure.Tls),
            (WebExceptionStatus.RequestProhibitedByProxy, ResetFeedFailure.Proxy)
        })
        {
            asyncTest("reset diagnosis: typed inner web failure " + entry.Item1, async () =>
            {
                using var client = new ResetFeedClient(new Handler(() => throw new HttpRequestException(
                    "private outer message", new WebException("private host and proxy", entry.Item1))));
                var result = await client.FetchAsync();
                Check.Equal(entry.Item2, result.Diagnostic!.Failure);
                Check.Equal(entry.Item1, result.Diagnostic.WebStatus!.Value);
                Check.Equal(ResetFeedStage.Request, result.Diagnostic.Stage);
                Check.False(Format(result, ResetFeedPresentation.Hidden).Contains("private"));
            });
        }
        test("reset diagnosis: unexpected dependency failures contain no file path or message", () =>
        {
            var diagnostic = ResetFeedDiagnostic.Unexpected(new FileLoadException("private message", "C:\\private\\secret.dll"), 12);
            Check.Equal(ResetFeedFailure.Dependency, diagnostic.Failure);
            var text = Format(new(ResetFeedReadStatus.NetworkError, Diagnostic: diagnostic), ResetFeedPresentation.Hidden);
            Check.Contains("failure=Dependency", text);
            Check.False(text.Contains("private"));
            Check.False(text.Contains("secret"));
        });
        test("reset diagnosis: unknown enum values and invalid status codes never become raw labels", () =>
        {
            var diagnostic = new ResetFeedDiagnostic((ResetFeedStage)8675309, (ResetFeedFailure)8675309,
                8675309, long.MaxValue, null, (WebExceptionStatus)8675309);
            var text = Format(new((ResetFeedReadStatus)8675309, Diagnostic: diagnostic), ResetFeedPresentation.Hidden);
            Check.Contains("status=Unknown stage=Unknown failure=Unknown http=Unknown elapsedMs=120000", text);
            Check.Contains("webStatus=Unknown", text);
            Check.False(text.Contains("8675309"));
        });
        test("reset diagnosis: in-flight and last completed attempts remain distinct without stale retry", () =>
        {
            var last = new ResetFeedAttemptDiagnostic(1, Now.AddMinutes(-6), Now.AddMinutes(-5),
                new(ResetFeedReadStatus.HttpError, Diagnostic: new(ResetFeedStage.Headers, ResetFeedFailure.HttpStatus, 503, 120)));
            var current = new ResetFeedAttemptDiagnostic(2, Now);
            var text = ResetFeedDiagnosticText.Format(new(current, last, Now.AddSeconds(-1)), ResetFeedPresentation.Hidden, Now);
            Check.Contains("current attempt=2 status=InFlight", text);
            Check.Contains("completed attempt=1", text);
            Check.Contains("http=503", text);
            Check.Contains("nextPoll=AfterCurrent", text);
            var initial = ResetFeedDiagnosticText.Format(ResetFeedDiagnosticSnapshot.Initial, ResetFeedPresentation.Hidden, Now);
            Check.Contains("current status=NotStarted", initial);
            Check.Contains("completed status=None", initial);
        });
        test("reset diagnosis: empty, future timestamps and expired forecasts have distinct hidden reasons", () =>
        {
            var empty = new ResetFeedSnapshot(null, null, null, Now);
            Check.Contains("hiddenReason=EmptyFeed", FromSnapshot(empty));
            Check.Contains("hiddenReason=GeneratedTimeAhead", FromSnapshot(empty with { GeneratedAt = Now.AddMinutes(6) }));
            Check.Contains("hiddenReason=LatestTimeAhead", FromSnapshot(empty with
            { LatestReset = new("id", "regular", Now.AddMinutes(1), "private", null) }));
            Check.Contains("hiddenReason=ForecastExpired", FromSnapshot(empty with
            { ActiveWatch = new("strong", 75, "window", Now.AddDays(-2), Now.AddDays(-1), "private", null) }));
            Check.Contains("hiddenReason=ForecastTimeAhead", FromSnapshot(empty with
            { ActiveWatch = new("strong", 75, "window", Now.AddHours(1), Now.AddHours(2), "private", null) }));
            var mixed = FromSnapshot(empty with
            {
                LatestReset = new("id", "regular", Now.AddMinutes(1), "private", null),
                ActiveWatch = new("strong", 75, "window", Now.AddMinutes(-1), Now.AddHours(1), "private", null)
            });
            Check.Contains("latestVisible=false forecastVisible=true hiddenReason=None latestReason=LatestTimeAhead", mixed);
        });
    }

    private static string FromSnapshot(ResetFeedSnapshot snapshot)
    {
        var result = new ResetFeedReadResult(ResetFeedReadStatus.Success, snapshot);
        return Format(result, ResetFeedPresentation.Create(result, Now));
    }
    private static string Format(ResetFeedReadResult result, ResetFeedPresentation presentation) =>
        ResetFeedDiagnosticText.Format(new(null, new(1, Now.AddSeconds(-1), Now, result), Now.AddMinutes(5)), presentation, Now);

    private sealed class Handler(Func<HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send());
    }
    private sealed class DelayHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
