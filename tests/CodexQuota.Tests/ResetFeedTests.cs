using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexQuota.Core;

static class ResetFeedTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 7, 0, 0, TimeSpan.Zero);
    private const string SourceUrl = "https://x.com/thsottiaux/status/2112432076767289678";
    private const string Source = """{"type":"x_post","author":"thsottiaux","url":"https://x.com/thsottiaux/status/2112432076767289678"}""";
    private static string Latest => """{"id":"2112432076767289678","reset_type":"regular","announced_at":"2026-10-20T06:00:00Z","text":"Reset observed.","source":""" + Source + "}";
    private static string Scheduled => """{"id":"2112432076767289678","status":"scheduled","reset_type":"regular","announced_at":"2026-10-20T06:15:00Z","scheduled_for":"2026-10-20T08:00:00Z","text":"Expected later.","source":""" + Source + "}";
    private static string Watch => """{"level":"strong","reset_chance_percent":75,"forecast_window":"in the coming days","observed_at":"2026-10-20T06:15:00Z","expires_at":"2026-10-21T06:15:00Z","text":"Forecast, not official.","source":""" + Source + "}";

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        asyncTest("reset fallback: complete chains serialize and queued cancellation does not query", async () =>
        {
            var primaryCalls = 0;
            var mirrorEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = new ResetFeedClient(
                new ResetFeedClient(new Handler((_, _) => { primaryCalls++; return Task.FromResult(Response("{}")); }), requireUpstreamFreshness: true, disableCache: true, utcNow: () => Now),
                new ResetFeedClient(new Handler(async (_, _) => { mirrorEntered.TrySetResult(true); await release.Task; return Response(Mirror(Now)); }), requireMirror: true, disableCache: true, utcNow: () => Now));
            var first = client.FetchAsync();
            await mirrorEntered.Task;
            using var cancel = new CancellationTokenSource();
            var second = client.FetchAsync(cancel.Token);
            cancel.Cancel();
            Check.Equal(ResetFeedReadStatus.Cancelled, (await second).Status);
            Check.Equal(1, primaryCalls);
            release.TrySetResult(true);
            Check.True((await first).IsSuccess);
        });
        asyncTest("reset fallback: valid direct data wins without querying mirror and expires independently", async () =>
        {
            var fallbackCalls = 0;
            using var client = new ResetFeedClient(
                new ResetFeedClient(new Handler((request, _) => {
                    Check.Equal(ResetFeedClient.UpstreamEndpoint, request.RequestUri!);
                    Check.Equal(0, request.Headers.IfNoneMatch.Count);
                    return Task.FromResult(Response(Envelope(Latest)));
                }), requireUpstreamFreshness: true, disableCache: true, endpoint: ResetFeedClient.UpstreamEndpoint, utcNow: () => Now),
                new ResetFeedClient(new Handler((_, _) => { fallbackCalls++; return Task.FromResult(Response(Mirror(Now))); }), requireMirror: true, disableCache: true, utcNow: () => Now));
            var result = await client.FetchAsync();
            Check.True(result.IsSuccess);
            Check.Equal(0, fallbackCalls);
            Check.Null(result.Snapshot!.UpstreamSuccessAtUtc);
            Check.Equal(Now, result.Snapshot.DirectGeneratedAtUtc!.Value);
            Check.False(ResetFeedPresentation.Create(result, Now.AddHours(1)).IsVisible);
        });
        foreach (var mode in new[] { "network", "timeout", "schema", "stale", "rate", "304" })
            asyncTest("reset fallback: upstream " + mode + " uses only fresh mirror", async () =>
            {
                var upstreamCalls = 0; var fallbackCalls = 0;
                using var client = new ResetFeedClient(
                    new ResetFeedClient(new Handler(async (request, token) => {
                        upstreamCalls++; Check.Equal(0, request.Headers.IfNoneMatch.Count);
                        if (mode == "network") throw new HttpRequestException();
                        if (mode == "timeout") await Task.Delay(2000, token);
                        if (mode == "schema") return Response("{}");
                        if (mode == "rate") return new HttpResponseMessage((HttpStatusCode)429);
                        if (mode == "304") return new HttpResponseMessage(HttpStatusCode.NotModified);
                        return Response(Envelope(Latest).Replace("2026-10-20T07:00:00Z", "2026-10-20T05:00:00Z"));
                    }), timeout: TimeSpan.FromMilliseconds(80), requireUpstreamFreshness: true, disableCache: true, utcNow: () => Now),
                    new ResetFeedClient(new Handler((request, _) => {
                        fallbackCalls++; Check.Equal(0, request.Headers.IfNoneMatch.Count);
                        return Task.FromResult(Response(Mirror(Now)));
                    }), requireMirror: true, disableCache: true, utcNow: () => Now));
                var result = await client.FetchAsync();
                Check.True(result.IsSuccess); Check.Equal(1, upstreamCalls); Check.Equal(1, fallbackCalls);
                Check.Equal(Now, result.Snapshot!.UpstreamSuccessAtUtc!.Value);
                Check.Null(result.Snapshot.DirectGeneratedAtUtc);
            });
        foreach (var mirrorBody in new[] { "{}", Mirror(Now.AddHours(-1)) })
            asyncTest("reset fallback: both invalid or stale sources stay hidden " + mirrorBody.Length, async () =>
            {
                using var client = new ResetFeedClient(
                    new ResetFeedClient(new Handler((_, _) => Task.FromResult(Response("{}"))), requireUpstreamFreshness: true, disableCache: true, utcNow: () => Now),
                    new ResetFeedClient(new Handler((_, _) => Task.FromResult(Response(mirrorBody))), requireMirror: true, disableCache: true, utcNow: () => Now));
                var result = await client.FetchAsync(); Check.False(result.IsSuccess); Check.Null(result.Snapshot);
                Check.False(ResetFeedPresentation.Create(result, Now).IsVisible);
            });
        asyncTest("reset fallback: caller cancellation never starts mirror", async () =>
        {
            var calls = 0;
            using var stop = new CancellationTokenSource(); stop.Cancel();
            using var client = new ResetFeedClient(
                new ResetFeedClient(new Handler((_, _) => Task.FromResult(Response("{}")))),
                new ResetFeedClient(new Handler((_, _) => { calls++; return Task.FromResult(Response(Mirror(Now))); })));
            Check.Equal(ResetFeedReadStatus.Cancelled, (await client.FetchAsync(stop.Token)).Status);
            Check.Equal(0, calls);
        });
        test("reset mirror: independent freshness bounds retain historical latest events", () =>
        {
            foreach (var age in new[] { TimeSpan.Zero, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(-5) })
            {
                Check.True(ResetFeedMirror.TryParse(Encoding.UTF8.GetBytes(Mirror(Now - age)), Now, out var snapshot));
                Check.Equal(Now - age, snapshot!.UpstreamSuccessAtUtc!.Value);
            }
            foreach (var age in new[] { TimeSpan.FromMinutes(60), TimeSpan.FromDays(1), TimeSpan.FromMinutes(-5) - TimeSpan.FromSeconds(1) })
                Check.False(ResetFeedMirror.TryParse(Encoding.UTF8.GetBytes(Mirror(Now - age)), Now, out _));
            Check.False(ResetFeedMirror.TryParse(Encoding.UTF8.GetBytes(Envelope(Latest)), Now, out _));
            var valid = Mirror(Now);
            foreach (var invalid in new[] {
                valid.Replace("\"mirrorSchemaVersion\":1", "\"mirrorSchemaVersion\":2"),
                valid.Replace("\"mirrorSchemaVersion\":1", "\"mirrorSchemaVersion\":1,\"mirrorSchemaVersion\":1"),
                valid.Replace(ResetFeedMirror.Source, "https://invalid.example/status"),
                valid.Replace(JsonSerializer.Serialize(Now.ToString("o")), JsonSerializer.Serialize("2026-10-20T07:00:00")),
                valid.Replace("\"total\":64", "\"total\":-1") })
                Check.False(ResetFeedMirror.TryParse(Encoding.UTF8.GetBytes(invalid), Now, out _));
            Check.False(ResetFeedMirror.TryParse(new byte[65537], Now, out _));
        });
        test("reset mirror: presentation expires without another network fetch", () =>
        {
            Check.True(ResetFeedMirror.TryParse(Encoding.UTF8.GetBytes(Mirror(Now)), Now, out var snapshot));
            var result = new ResetFeedReadResult(ResetFeedReadStatus.Success, snapshot);
            Check.True(ResetFeedPresentation.Create(result, Now).IsVisible);
            Check.True(ResetFeedPresentation.Create(result, Now).NextChange <= Now.AddHours(1));
            Check.False(ResetFeedPresentation.Create(result, Now.AddHours(1)).IsVisible);
        });
        asyncTest("reset mirror: 304 never refreshes upstream timestamp and stale cache is hidden", async () =>
        {
            var now = Now;
            var calls = 0;
            using var client = new ResetFeedClient(new Handler((request, _) =>
            {
                Check.Equal("raw.githubusercontent.com", request.RequestUri!.Host);
                var response = ++calls == 1 ? Response(Mirror(Now)) : new HttpResponseMessage(HttpStatusCode.NotModified);
                response.Headers.ETag = new EntityTagHeaderValue("\"mirror\"");
                response.Headers.Date = now; // A fresh HTTP Date cannot freshen an old upstream success.
                return Task.FromResult(response);
            }), requireMirror: true, utcNow: () => now);
            Check.True((await client.FetchAsync()).IsSuccess);
            now = Now.AddMinutes(59);
            var cached = await client.FetchAsync();
            Check.Equal(ResetFeedReadStatus.NotModified, cached.Status);
            Check.Equal(Now, cached.Snapshot!.UpstreamSuccessAtUtc!.Value);
            now = Now.AddHours(1);
            var stale = await client.FetchAsync();
            Check.False(stale.IsSuccess);
            Check.Null(stale.Snapshot);
        });
        asyncTest("reset mirror: production policy rejects original unmirrored schema", async () =>
        {
            using var client = new ResetFeedClient(new Handler((_, _) => Task.FromResult(Response(Envelope(Latest)))), requireMirror: true, utcNow: () => Now);
            Check.Equal(ResetFeedReadStatus.InvalidResponse, (await client.FetchAsync()).Status);
        });
        test("reset feed: published v1 envelope supports current latest sample", () =>
        {
            var parsed = Parse(Envelope(Latest));
            Check.Equal("2112432076767289678", parsed.LatestReset!.Id);
            Check.Equal(SourceUrl, parsed.LatestReset.SourceUrl!.AbsoluteUri);
            Check.Equal(Now.AddHours(-1), parsed.LatestReset.AnnouncedAt);
            Check.Null(parsed.ScheduledReset);
            Check.Null(parsed.ActiveWatch);
        });
        test("reset feed: valid all-null success is not a failure or fabricated reset", () =>
        {
            var result = new ResetFeedReadResult(ResetFeedReadStatus.Success, Parse(Envelope()));
            Check.True(result.IsSuccess);
            var view = ResetFeedPresentation.Create(result, Now);
            Check.False(view.IsVisible);
            Check.Null(view.Latest);
            Check.Null(view.Forecast);
            Check.Null(view.EventIdentity);
        });
        test("reset feed: scheduled announcement takes priority over prediction", () =>
        {
            var view = Present(Envelope(Latest, Scheduled, Watch));
            Check.Equal("上次重置", view.Latest!.Label);
            Check.Equal("1 小时前", view.Latest.Detail);
            Check.Equal("预计重置", view.Forecast!.Label);
            Check.Contains("约1小时内", view.Forecast.Detail);
            Check.False(view.Forecast.Detail.Contains("待确认"));
            Check.Contains("公告或首次观测时间", view.Latest.Description);
            Check.Equal(view.Forecast.EventIdentity, view.EventIdentity);
            Check.True(view.NextChange > Now);
        });
        test("reset feed: readable countdown rounds up and never reports a negative duration", () =>
        {
            foreach (var sample in new (double Seconds, string Text)[] {
                (-1d, "已到预定时间"), (0d, "已到预定时间"),
                (1d, "约1分钟内"), (60d, "约1分钟内"), (61d, "约2分钟内"),
                (3600d, "约1小时内"), (3601d, "约1小时1分内"),
                (4500d, "约1小时15分内"), (86400d, "约1天内"),
                (90060d, "约1天1小时1分内") })
                Check.Equal(sample.Text, ResetFeedPresentation.RelativeUntil(Now.AddSeconds(sample.Seconds), Now));
        });
        test("reset feed: countdown refreshes at the next rounded-minute boundary", () =>
        {
            var view = Present(Envelope(scheduled: Scheduled), Now.AddSeconds(15));
            Check.Equal(Now.AddMinutes(1), view.NextChange!.Value);
            Check.Equal(Now.AddMinutes(1), Present(Envelope(scheduled: Scheduled)).NextChange!.Value);
            Check.Equal(Now.AddHours(1), Present(Envelope(scheduled: Scheduled), Now.AddHours(1).AddSeconds(-1)).NextChange!.Value);
        });
        test("reset feed: exact local time is compact today and dated across local midnight", () =>
        {
            var localNoon = new DateTimeOffset(DateTime.Now.Date.AddHours(12));
            foreach (var target in new[] { localNoon.AddHours(1), localNoon.AddDays(1).AddHours(1) })
            {
                var scheduled = new ResetFeedScheduledReset("qa", "regular", localNoon.AddMinutes(-1), target, "qa", null);
                var snapshot = new ResetFeedSnapshot(null, scheduled, null, localNoon);
                var row = ResetFeedPresentation.Create(new(ResetFeedReadStatus.Success, snapshot), localNoon).Forecast!;
                var format = target.ToLocalTime().Date == localNoon.ToLocalTime().Date ? "HH:mm" : "MM-dd HH:mm";
                Check.Equal(row.CompactDetail + " · " + target.ToLocalTime().ToString(format), row.Detail);
                Check.Contains(target.ToLocalTime().ToString("MM-dd HH:mm"), row.Description);
                Check.False(row.Detail.Contains("待确认"));
            }
        });
        test("reset feed: single-line selection drops exact time before relative estimate", () =>
        {
            var row = Present(Envelope(scheduled: Scheduled)).Forecast!;
            Check.Equal(row.Detail, row.DetailForWidth(row.Detail.Length, text => text.Length));
            Check.Equal(row.CompactDetail, row.DetailForWidth(row.Detail.Length - 1, text => text.Length));
            Check.Equal(row.CompactDetail, row.DetailForWidth(0, text => text.Length));
            var plain = row with { CompactDetail = null };
            Check.Equal(plain.Detail, plain.DetailForWidth(0, text => text.Length));
            Check.False(row.Detail.Contains('\n'));
        });
        test("reset feed: scheduled time absent stays unknown rather than invented", () =>
        {
            var view = Present(Envelope(scheduled: Scheduled.Replace("\"2026-10-20T08:00:00Z\"", "null")));
            Check.Equal("已预告，时间未定", view.Forecast!.Detail);
        });
        test("reset feed: elapsed scheduled time remains pending not completed", () =>
        {
            var view = Present(Envelope(scheduled: Scheduled), Now.AddHours(2));
            Check.Equal("已到预定时间", view.Forecast!.Detail);
            Check.Null(view.Latest);
        });
        test("reset feed: scheduled forecasts have finite confirmation lifetimes", () =>
        {
            var view = Present(Envelope(scheduled: Scheduled), Now.AddHours(26));
            Check.False(view.IsVisible);
            var undated = Scheduled.Replace("\"2026-10-20T08:00:00Z\"", "null");
            Check.False(Present(Envelope(scheduled: undated), Now.AddDays(8)).IsVisible);
        });
        test("reset feed: expired watch omitted while latest remains", () =>
        {
            var view = Present(Envelope(Latest, watch: Watch), Now.AddDays(2));
            Check.True(view.IsVisible);
            Check.Null(view.Forecast);
            Check.Null(view.EventIdentity);
        });
        test("reset feed: watch expiry precise and visible valid lifetime", () =>
        {
            var snapshot = Parse(Envelope(watch: Watch));
            var before = ResetFeedPresentation.Create(new(ResetFeedReadStatus.Success, snapshot),
                snapshot.ActiveWatch!.ExpiresAt.AddTicks(-1));
            Check.True(before.IsVisible);
            Check.Equal(snapshot.ActiveWatch.ExpiresAt, before.NextChange);
            Check.False(ResetFeedPresentation.Create(new(ResetFeedReadStatus.Success, snapshot),
                snapshot.ActiveWatch.ExpiresAt).IsVisible);
        });
        test("reset feed: probability nullable and prediction time remains original text", () =>
        {
            var view = Present(Envelope(watch: Watch.Replace("\"reset_chance_percent\":75", "\"reset_chance_percent\":null")));
            Check.Contains("in the coming days", view.Forecast!.Detail);
            Check.False(view.Forecast.Detail.Contains('%'));
            Check.Contains("非 OpenAI 承诺", view.Forecast.Description);
            Check.Contains("in the coming days", view.Forecast.Description);
        });
        test("reset feed: observed sources without URL produce non-clickable factual row", () =>
        {
            var view = Present(Envelope(Latest.Replace(Source, """{"type":"observed"}""")));
            Check.True(view.IsVisible);
            Check.Null(view.Latest!.SourceUrl);
        });
        test("reset feed: missing and null X source URL disable link without hiding data", () =>
        {
            foreach (var source in new[]
            {
                """{"type":"x_post","author":"thsottiaux"}""",
                """{"type":"x_post","author":"thsottiaux","url":null}"""
            })
            {
                var view = Present(Envelope(Latest.Replace(Source, source), Scheduled.Replace(Source, source)));
                Check.True(view.IsVisible);
                Check.Null(view.Latest!.SourceUrl);
                Check.Null(view.Forecast!.SourceUrl);
            }
        });
        test("reset feed: observation website links never become arbitrary external links", () =>
        {
            var view = Present(Envelope(Latest.Replace(Source, """{"type":"observed","url":"https://other.example/"}""")));
            Check.True(view.IsVisible);
            Check.Null(view.Latest!.SourceUrl);
        });
        test("reset feed: same source identity survives polls probability edits and watch upgrades", () =>
        {
            var first = Present(Envelope(watch: Watch));
            var next = Present(Envelope(watch: Watch.Replace(":75", ":80"))
                .Replace("\"generated_at\":\"2026-10-20T07:00:00Z\"", "\"generated_at\":\"2026-10-20T07:01:00Z\""),
                Now.AddMinutes(1));
            var scheduled = Present(Envelope(scheduled: Scheduled));
            Check.Equal(first.EventIdentity, next.EventIdentity);
            Check.Equal(first.EventIdentity, scheduled.EventIdentity);
            Check.Equal(64, first.EventIdentity!.Length);
        });
        test("reset feed: different source posts generate different unread event identity", () =>
        {
            var first = Present(Envelope(watch: Watch));
            var next = Present(Envelope(watch: Watch.Replace("2112432076767289678", "2112432076767289679")));
            Check.False(first.EventIdentity == next.EventIdentity);
        });
        test("reset feed: no-source watch identity ignores summary and probability edits", () =>
        {
            var withoutSource = Watch.Replace(Source, """{"type":"observed"}""");
            var first = Present(Envelope(watch: withoutSource));
            var next = Present(Envelope(watch: withoutSource.Replace("Forecast, not official.", "Updated summary.").Replace(":75", ":80")));
            Check.Equal(first.EventIdentity, next.EventIdentity);
        });
        test("reset feed: every failure immediately hides both rows and event dots", () =>
        {
            foreach (var status in Enum.GetValues<ResetFeedReadStatus>().Where(s =>
                s is not (ResetFeedReadStatus.Success or ResetFeedReadStatus.NotModified)))
            {
                var view = ResetFeedPresentation.Create(new(status, Parse(Envelope(Latest, Scheduled, Watch))), Now);
                Check.False(view.IsVisible);
                Check.Null(view.Latest);
                Check.Null(view.Forecast);
                Check.Null(view.EventIdentity);
            }
        });

        foreach (var (name, input) in InvalidFixtures())
            test("reset feed: rejects malformed " + name, () =>
            {
                Check.False(ResetFeedParser.TryParse(input, out var snapshot));
                Check.Null(snapshot);
            });

        foreach (var value in new[]
        {
            "http://x.com/thsottiaux/status/1", "https://evil.example/thsottiaux/status/1",
            "https://x.com.evil.example/thsottiaux/status/1", "https://x.com@evil.example/thsottiaux/status/1",
            "https://evil@x.com/thsottiaux/status/1", "https://x.com:443/thsottiaux/status/1",
            "https://x.com:8443/thsottiaux/status/1", "https://x.com/other/status/1",
            "https://x.com/thsottiaux/status/1?redirect=https://evil.example", "https://x.com/thsottiaux/status/1#x",
            "https://x.com/thsottiaux/status/1/", "https://x.com/thsottiaux/status/abc",
            "https://x.com/thsottiaux/status/%31", "https://x.com/thsottiaux/status/1/../2",
            "https://x.com/thsottiaux", "https://t.co/example", "javascript:alert(1)", "file:///tmp/data",
            "https://x.com\\@evil.example/thsottiaux/status/1", " https://x.com/thsottiaux/status/1"
        })
        {
            var unsafeUrl = value;
            test("reset feed: refuses unsafe source " + unsafeUrl, () =>
            {
                Check.False(ResetFeedUrlPolicy.IsAllowedSource(unsafeUrl));
                Check.False(ResetFeedParser.TryParse(Envelope(Latest.Replace(SourceUrl, unsafeUrl)), out _));
            });
        }
        test("reset feed: safe twitter routes normalized to canonical X post", () =>
        {
            Check.True(ResetFeedUrlPolicy.TryNormalizeSource("https://twitter.com/thsottiaux/status/123", out var url));
            Check.Equal("https://x.com/thsottiaux/status/123", url);
        });
        test("reset feed: future latest is never displayed as a past completed reset", () =>
            Check.Null(Present(Envelope(Latest.Replace("06:00:00Z", "08:00:00Z"))).Latest));
        test("reset feed: presentation safely bounds maximum date arithmetic", () =>
        {
            var max = DateTimeOffset.MaxValue;
            var latest = new ResetFeedReset("max", "regular", max.AddHours(-1), "", null);
            var scheduled = new ResetFeedScheduledReset("max", "regular", max, max, "", null);
            var snapshot = new ResetFeedSnapshot(latest, scheduled, null, max);
            var view = ResetFeedPresentation.Create(new(ResetFeedReadStatus.Success, snapshot), max);
            Check.True(view.IsVisible);
            Check.Null(view.NextChange);
            Check.Equal("已到预定时间", view.Forecast!.Detail);
            snapshot = snapshot with { ScheduledReset = scheduled with { ScheduledFor = null } };
            Check.True(ResetFeedPresentation.Create(new(ResetFeedReadStatus.Success, snapshot), max).IsVisible);
        });
        test("reset feed: body size and text bounds enforced", () =>
        {
            Check.False(ResetFeedParser.TryParse(new byte[ResetFeedParser.MaximumBodyBytes + 1], out _));
            var tooLong = Latest.Replace("Reset observed.", new string('a', ResetFeedParser.MaximumTextLength + 1));
            Check.False(ResetFeedParser.TryParse(Envelope(tooLong), out _));
        });

        asyncTest("reset feed: anonymous fixed endpoint no-cache ETag and successful 304", async () =>
        {
            var calls = 0;
            using var client = new ResetFeedClient(new Handler((request, _) =>
            {
                calls++;
                Check.Equal(ResetFeedClient.Endpoint, request.RequestUri);
                Check.Equal(HttpMethod.Get, request.Method);
                Check.True(request.Headers.CacheControl!.NoCache);
                Check.Equal(TimeSpan.Zero, request.Headers.CacheControl.MaxAge);
                Check.Null(request.Headers.Authorization);
                Check.Null(request.Content);
                Check.False(request.Headers.Contains("Cookie"));
                if (calls == 1)
                {
                    Check.Equal(0, request.Headers.IfNoneMatch.Count);
                    var response = Response(Envelope(Latest));
                    response.Headers.ETag = new EntityTagHeaderValue("\"fixture\"");
                    return Task.FromResult(response);
                }
                Check.Equal("\"fixture\"", request.Headers.IfNoneMatch.Single().ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }));
            var first = await client.FetchAsync();
            var second = await client.FetchAsync();
            Check.True(first.IsSuccess);
            Check.True(second.IsSuccess);
            Check.Equal(ResetFeedReadStatus.NotModified, second.Status);
            Check.Equal(first.Snapshot, second.Snapshot);
        });
        asyncTest("reset feed: failure never exposes cache but later 304 revalidates it", async () =>
        {
            var calls = 0;
            using var client = new ResetFeedClient(new Handler((_, _) =>
            {
                calls++;
                if (calls == 2) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                if (calls == 3) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
                var response = Response(Envelope(Latest, Scheduled));
                response.Headers.ETag = new EntityTagHeaderValue("\"fixture\"");
                return Task.FromResult(response);
            }));
            Check.True((await client.FetchAsync()).IsSuccess);
            var failed = await client.FetchAsync();
            Check.False(failed.IsSuccess);
            Check.Null(failed.Snapshot);
            Check.False(ResetFeedPresentation.Create(failed, Now).IsVisible);
            Check.True((await client.FetchAsync()).IsSuccess);
        });
        asyncTest("reset feed: 304 without prior success rejected", async () =>
        {
            using var client = new ResetFeedClient(new Handler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified))));
            Check.Equal(ResetFeedReadStatus.InvalidResponse, (await client.FetchAsync()).Status);
        });
        asyncTest("reset feed: cached 200 without ETag cannot authorize 304 reuse", async () =>
        {
            var calls = 0;
            using var client = new ResetFeedClient(new Handler((request, _) =>
            {
                Check.Equal(0, request.Headers.IfNoneMatch.Count);
                return Task.FromResult(++calls == 1 ? Response(Envelope(Latest)) :
                    new HttpResponseMessage(HttpStatusCode.NotModified));
            }));
            Check.True((await client.FetchAsync()).IsSuccess);
            var result = await client.FetchAsync();
            Check.Equal(ResetFeedReadStatus.InvalidResponse, result.Status);
            Check.Null(result.Snapshot);
        });
        asyncTest("reset feed: mismatching 304 validator never reuses body", async () =>
        {
            var calls = 0;
            using var client = new ResetFeedClient(new Handler((_, _) =>
            {
                var response = ++calls == 1 ? Response(Envelope(Latest)) : new HttpResponseMessage(HttpStatusCode.NotModified);
                response.Headers.ETag = new EntityTagHeaderValue(calls == 1 ? "\"first\"" : "\"other\"");
                return Task.FromResult(response);
            }));
            Check.True((await client.FetchAsync()).IsSuccess);
            Check.Equal(ResetFeedReadStatus.InvalidResponse, (await client.FetchAsync()).Status);
        });
        asyncTest("reset feed: timeout bounded even handler ignores cancellation", async () =>
        {
            var completion = new TaskCompletionSource<HttpResponseMessage>();
            using var client = new ResetFeedClient(new Handler((_, _) => completion.Task), TimeSpan.FromMilliseconds(40));
            var result = await client.FetchAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Check.Equal(ResetFeedReadStatus.Timeout, result.Status);
            Check.Null(result.Snapshot);
            completion.SetResult(Response(Envelope()));
        });
        asyncTest("reset feed: caller cancellation distinguished from timeout", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            using var client = new ResetFeedClient(new Handler((_, _) => throw new TestFailure("Unexpected HTTP request.")));
            var result = await client.FetchAsync(cancellation.Token);
            Check.Equal(ResetFeedReadStatus.Cancelled, result.Status);
        });
        asyncTest("reset feed: streamed body timeout bounded when stream ignores cancellation", async () =>
        {
            var stream = new HangingStream();
            using var client = new ResetFeedClient(new Handler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })),
                TimeSpan.FromMilliseconds(40));
            var result = await client.FetchAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Check.Equal(ResetFeedReadStatus.Timeout, result.Status);
            stream.Release();
        });
        asyncTest("reset feed: oversized streamed response rejected without content length", async () =>
        {
            using var client = new ResetFeedClient(new Handler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new UnknownLengthStream(new byte[ResetFeedParser.MaximumBodyBytes + 1])) })));
            Check.Equal(ResetFeedReadStatus.InvalidResponse, (await client.FetchAsync()).Status);
        });
        asyncTest("reset feed: oversized declared content length rejected", async () =>
        {
            using var client = new ResetFeedClient(new Handler((_, _) =>
            {
                var response = Response(Envelope());
                response.Content.Headers.ContentLength = ResetFeedParser.MaximumBodyBytes + 1;
                return Task.FromResult(response);
            }));
            Check.Equal(ResetFeedReadStatus.InvalidResponse, (await client.FetchAsync()).Status);
        });
        asyncTest("reset feed: rate limit Retry-After returned without payload leakage", async () =>
        {
            using var client = new ResetFeedClient(new Handler((_, _) =>
            {
                var response = new HttpResponseMessage((HttpStatusCode)429);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(20));
                return Task.FromResult(response);
            }));
            var result = await client.FetchAsync();
            Check.Equal(ResetFeedReadStatus.RateLimited, result.Status);
            Check.Equal(TimeSpan.FromMinutes(20), result.RetryAfter);
            Check.Null(result.Snapshot);
        });
        asyncTest("reset feed: redirects and HTML treated as silent feature failure", async () =>
        {
            foreach (var code in new[] { HttpStatusCode.Redirect, HttpStatusCode.OK })
            {
                using var client = new ResetFeedClient(new Handler((_, _) =>
                    Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("<html>Login</html>", Encoding.UTF8, "text/html") })));
                var result = await client.FetchAsync();
                Check.False(result.IsSuccess);
                Check.Null(result.Snapshot);
            }
        });
        asyncTest("reset feed: malformed new response does not display cached success", async () =>
        {
            var calls = 0;
            using var client = new ResetFeedClient(new Handler((_, _) =>
                Task.FromResult(Response(++calls == 1 ? Envelope(Latest, Scheduled) : """{"data":null}"""))));
            Check.True((await client.FetchAsync()).IsSuccess);
            var failed = await client.FetchAsync();
            Check.Equal(ResetFeedReadStatus.InvalidResponse, failed.Status);
            Check.Null(failed.Snapshot);
        });
        test("reset feed: poll policy independent five-minute success interval and one in-flight", () =>
        {
            var policy = new ResetFeedPollPolicy(() => .5);
            Check.True(policy.TryBegin(Now));
            Check.False(policy.TryBegin(Now));
            policy.Complete(new(ResetFeedReadStatus.Success, Parse(Envelope())), Now);
            Check.Equal(Now.AddMinutes(5), policy.NextPollAt);
            Check.False(policy.TryBegin(Now.AddMinutes(4)));
            Check.True(policy.TryBegin(Now.AddMinutes(5)));
        });
        test("reset feed: polling jitters and bounded failure backoff resets after success", () =>
        {
            var early = new ResetFeedPollPolicy(() => 0);
            var late = new ResetFeedPollPolicy(() => 1);
            var success = new ResetFeedReadResult(ResetFeedReadStatus.Success, Parse(Envelope()));
            early.Complete(success, Now);
            late.Complete(success, Now);
            Check.Equal(Now.AddMinutes(4.5), early.NextPollAt);
            Check.Equal(Now.AddMinutes(5.5), late.NextPollAt);
            for (var i = 0; i < 10; i++) late.Complete(new(ResetFeedReadStatus.NetworkError), Now);
            Check.Equal(Now.AddHours(1), late.NextPollAt);
            late.Complete(success, Now);
            Check.Equal(Now.AddMinutes(5.5), late.NextPollAt);
        });
        test("reset feed: polling honors Retry-After as minimum including more than backoff", () =>
        {
            var policy = new ResetFeedPollPolicy(() => .5);
            policy.Complete(new(ResetFeedReadStatus.RateLimited, RetryAfter: TimeSpan.FromHours(2)), Now);
            Check.Equal(Now.AddHours(2), policy.NextPollAt);
            Check.False(policy.TryBegin(Now.AddHours(1)));
        });
    }

    private static string Mirror(DateTimeOffset success) =>
        "{\"mirrorSchemaVersion\":1,\"upstreamSuccessAtUtc\":" + JsonSerializer.Serialize(success.ToString("o")) +
        ",\"source\":" + JsonSerializer.Serialize(ResetFeedMirror.Source) + "," + Envelope(Latest).Substring(1);

    private static string Envelope(string latest = "null", string scheduled = "null", string watch = "null") =>
        """{"data":{"latest_reset":""" + latest + ""","scheduled_reset":""" + scheduled + ""","active_watch":""" +
        watch + ""","stats":{"total":64,"last_reset_at":"2026-10-20T06:00:00Z","days_since_last":0,"avg_interval_days":6.3}},"meta":{"api_version":"v1","generated_at":"2026-10-20T07:00:00Z"}}""";

    private static ResetFeedSnapshot Parse(string json)
    {
        Check.True(ResetFeedParser.TryParse(json, out var parsed), "Fixture should parse: " + json);
        return parsed!;
    }

    private static ResetFeedPresentation Present(string json, DateTimeOffset? now = null) =>
        ResetFeedPresentation.Create(new(ResetFeedReadStatus.Success, Parse(json)), now ?? Now);

    private static IEnumerable<(string Name, string Json)> InvalidFixtures()
    {
        yield return ("syntax", "{");
        yield return ("empty object", "{}");
        yield return ("null root", "null");
        yield return ("array root", "[]");
        yield return ("missing meta", """{"data":{"latest_reset":null,"scheduled_reset":null,"active_watch":null,"stats":{}}}""");
        yield return ("partial data", """{"data":{"latest_reset":null},"meta":{"api_version":"v1","generated_at":"2026-10-20T07:00:00Z"}}""");
        yield return ("unknown version", Envelope().Replace("\"v1\"", "\"v2\""));
        yield return ("null stats", Envelope().Replace("""{"total":64,"last_reset_at":"2026-10-20T06:00:00Z","days_since_last":0,"avg_interval_days":6.3}""", "null"));
        yield return ("negative stats", Envelope().Replace("\"total\":64", "\"total\":-1"));
        yield return ("duplicate data property", Envelope().Replace("\"latest_reset\":null", "\"latest_reset\":null,\"latest_reset\":null"));
        yield return ("partial latest", Envelope("""{"id":"1"}"""));
        yield return ("null latest timestamp", Envelope(Latest.Replace("\"2026-10-20T06:00:00Z\"", "null")));
        yield return ("local timestamp lacks offset", Envelope(Latest.Replace("2026-10-20T06:00:00Z", "2026-10-20T06:00:00")));
        yield return ("absurd timestamp", Envelope(Latest.Replace("2026-10-20", "9999-12-31")));
        yield return ("wrong scheduled status", Envelope(scheduled: Scheduled.Replace("\"scheduled\"", "\"completed\"")));
        yield return ("missing nullable scheduled time", Envelope(scheduled: Scheduled.Replace("\"scheduled_for\":\"2026-10-20T08:00:00Z\",", "")));
        yield return ("non-numeric chance", Envelope(watch: Watch.Replace(":75", ":\"75\"")));
        yield return ("chance too high", Envelope(watch: Watch.Replace(":75", ":101")));
        yield return ("unknown watch level", Envelope(watch: Watch.Replace("\"strong\"", "\"certain\"")));
        yield return ("expiry before observation", Envelope(watch: Watch.Replace("2026-10-21T06:15:00Z", "2026-10-19T06:15:00Z")));
        yield return ("untrusted author", Envelope(Latest.Replace("\"author\":\"thsottiaux\"", "\"author\":\"other\"")));
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class HangingStream : Stream
    {
        private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _completion.TrySetResult(0);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _completion.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
