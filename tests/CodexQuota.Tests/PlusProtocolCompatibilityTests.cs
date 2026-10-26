using System.Text.Json;
using CodexQuota.Core;

internal static class PlusProtocolCompatibilityTests
{
    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        var creditCases = new (string Name, string Json, ResetCardDataStatus Status)[]
        {
            ("missing", "", ResetCardDataStatus.NotReturned),
            ("null", ",\"rateLimitResetCredits\":null", ResetCardDataStatus.NotReturned),
            ("zero with null details", ",\"rateLimitResetCredits\":{\"availableCount\":0,\"credits\":null}", ResetCardDataStatus.CountOnly),
            ("zero with empty details", ",\"rateLimitResetCredits\":{\"availableCount\":0,\"credits\":[]}", ResetCardDataStatus.Details)
        };
        const string fiveHour = "{\"usedPercent\":20,\"windowDurationMins\":300,\"resetsAt\":1800000000}";
        const string weekly = "{\"usedPercent\":35,\"windowDurationMins\":10080,\"resetsAt\":1800500000}";
        var windowCases = new (string Name, string Primary, string Secondary, bool Both)[]
        {
            ("5h then weekly", fiveHour, weekly, true),
            ("weekly then 5h", weekly, fiveHour, true),
            ("weekly primary only", weekly, "null", false),
            ("weekly secondary only", "null", weekly, false)
        };
        foreach (var credit in creditCases)
        foreach (var windows in windowCases)
        {
            asyncTest($"Plus protocol: {windows.Name}; reset credits {credit.Name} remains Ready", async () =>
            {
                await using var peer = new FakePeer();
                var read = CodexQuotaProtocol.ReadAsync(peer.Rpc, TimeSpan.FromSeconds(2), CancellationToken.None);
                var initialize = await peer.ReadAsync();
                Check.Method(initialize, "initialize");
                await peer.RespondAsync(initialize, new { userAgent = "codex_quota_tray/0.159.2" });
                Check.Method(await peer.ReadAsync(), "initialized");
                var quota = await peer.ReadAsync();
                Check.Method(quota, "account/rateLimits/read");
                Check.False(quota.GetProperty("params").TryGetProperty("excludeResetCreditDetails", out var excluded) && excluded.GetBoolean());
                // Only the quota snapshot supplies the plan; account/read is never sent.
                using var result = JsonDocument.Parse("{\"rateLimits\":{\"limitId\":\"codex\",\"planType\":\"plus\",\"primary\":" +
                    windows.Primary + ",\"secondary\":" + windows.Secondary + "}" + credit.Json + "}");
                await peer.RespondAsync(quota, result.RootElement);
                var actual = await read;
                Check.Equal(ReadStatus.Ready, actual.Status);
                Check.Equal("plus", actual.Selection!.PlanType);
                Check.Equal(PlanSource.RateLimits, actual.Selection.PlanSource);
                Check.Quota(actual.Quota, windows.Both ? 80 : 65, windows.Both ? 80 : 65);
                Check.Equal(windows.Both ? 300 : 10080, actual.Quota!.WindowMinutes);
                Check.Quota(actual.Selection.AvailableWindows.Single(window => window.WindowMinutes == 10080), 65, 65);
                Check.Equal(DateTimeOffset.FromUnixTimeSeconds(1800500000),
                    actual.Selection.AvailableWindows.Single(window => window.WindowMinutes == 10080).ResetsAt);
                Check.Equal(windows.Both ? 2 : 1, actual.Selection.AvailableWindows.Count);
                Check.Equal(windows.Both ? QuotaSelectionReason.Preferred : QuotaSelectionReason.PreferredWindowUnavailable,
                    actual.Selection.Reason);
                Check.Equal(credit.Status, actual.ResetCards!.Status);
                Check.Equal(0L, actual.ResetCards.AvailableCount);
                Check.Equal(0, actual.ResetCards.Cards.Count);
            });
        }

        asyncTest("Plus protocol: null reset dates and zero cards preserve both quota values", async () =>
        {
            await using var peer = new FakePeer();
            var read = CodexQuotaProtocol.ReadAsync(peer.Rpc, TimeSpan.FromSeconds(2), CancellationToken.None);
            var quota = await InitializeToQuota(peer);
            using var response = JsonDocument.Parse("""{"rateLimits":{"planType":"plus","primary":{"usedPercent":20,"windowDurationMins":300,"resetsAt":null},"secondary":{"usedPercent":35,"windowDurationMins":10080,"resetsAt":null}},"rateLimitResetCredits":{"availableCount":0,"credits":[]}}""");
            await peer.RespondAsync(quota, response.RootElement);
            var result = await read;
            Check.Equal(ReadStatus.Ready, result.Status);
            Check.Quota(result.Quota, 80, 80);
            Check.Quota(result.Selection!.AvailableWindows.Single(window => window.WindowMinutes == 10080), 65, 65);
            Check.True(result.Selection.AvailableWindows.All(window => window.ResetsAt is null));
            Check.Equal(0L, result.ResetCards!.AvailableCount);
        });

        asyncTest("quota-only protocol: missing snapshot plan remains unknown", async () =>
        {
            await using var peer = new FakePeer();
            var read = CodexQuotaProtocol.ReadAsync(peer.Rpc, TimeSpan.FromSeconds(2), CancellationToken.None);
            var quota = await InitializeToQuota(peer);
            using var response = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":35,"windowDurationMins":10080}}}""");
            await peer.RespondAsync(quota, response.RootElement);
            var result = await read;
            Check.Equal(ReadStatus.Ready, result.Status);
            Check.Null(result.Selection!.PlanType);
            Check.Equal(PlanSource.None, result.Selection.PlanSource);
            Check.Equal(QuotaSelectionReason.UnknownPlan, result.Selection.Reason);
            Check.Quota(result.Quota, 65, 65);
        });

        asyncTest("quota-only protocol: caller cancellation interrupts quota without other requests", async () =>
        {
            await using var peer = new FakePeer();
            using var cancellation = new CancellationTokenSource();
            var read = CodexQuotaProtocol.ReadAsync(peer.Rpc, TimeSpan.FromSeconds(2), cancellation.Token);
            await InitializeToQuota(peer);
            cancellation.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(read);
            await Check.StillPendingAsync(peer.ReadAsync());
        });

        foreach (var auth in new (string Message, CliErrorKind Kind)[]
        {
            ("codex account authentication required to read rate limits", CliErrorKind.QuotaAuthenticationRequired),
            ("chatgpt authentication required to read rate limits", CliErrorKind.QuotaChatGptRequired)
        })
        {
            asyncTest("quota-only protocol: exact authentication error " + auth.Kind, async () =>
            {
                await using var peer = new FakePeer();
                var read = CodexQuotaProtocol.ReadAsync(peer.Rpc, TimeSpan.FromSeconds(2), CancellationToken.None);
                var quota = await InitializeToQuota(peer);
                await peer.SendAsync(new { id = quota.GetProperty("id").GetInt64(), error = new { code = -32600, message = auth.Message } });
                var error = await Check.ThrowsAsync<RpcException>(read);
                Check.Equal(auth.Kind, error.ErrorKind);
                Check.Equal(-32600, error.Code);
                Check.False(error.Message.Contains(auth.Message, StringComparison.Ordinal));
                await Check.StillPendingAsync(peer.ReadAsync());
            });
            test("quota authentication diagnostics: strict code and complete message for " + auth.Kind, () =>
            {
                Check.Equal(CliErrorKind.None, RpcException.ClassifyError(-32603, auth.Message));
                Check.Equal(CliErrorKind.None, RpcException.ClassifyError(-32600, auth.Message + " private-account"));
                Check.Equal(CliErrorKind.None, RpcException.ClassifyError(-32600, "prefix " + auth.Message));
            });
        }

        var routingCases = new (string Message, CliErrorKind Kind)[]
        {
            ("workspace routing discovery unauthorized (401)", CliErrorKind.RoutingUnauthorized401),
            ("workspace routing discovery failed", CliErrorKind.RoutingDiscoveryFailed),
            ("workspace routing discovery timed out", CliErrorKind.RoutingDiscoveryTimeout)
        };
        foreach (var routing in routingCases)
        {
            asyncTest($"RPC diagnostics: {routing.Kind} survives RPC without raw error text", async () =>
            {
                await using var peer = new FakePeer();
                var read = peer.RequestAsync("account/rateLimits/read");
                var account = await peer.ReadAsync();
                Check.Method(account, "account/rateLimits/read");
                await peer.SendAsync(new { id = account.GetProperty("id").GetInt64(),
                    error = new { code = -32603, message = routing.Message, data = new { secret = "private-account-token" } } });
                var error = await Check.ThrowsAsync<RpcException>(read);
                Check.Equal(-32603, error.Code);
                Check.Equal(routing.Kind, error.ErrorKind);
                Check.False(error.IsRateLimited);
                Check.Equal(0, error.Data.Count);
                Check.Null(error.InnerException);
                Check.False(error.ToString().Contains(routing.Message, StringComparison.Ordinal));
                Check.False(error.ToString().Contains("private", StringComparison.Ordinal));
                var safe = new CliDiagnostic(CliSource.Desktop, new Version(0, 159, 2), CliStage.RateLimitsRead, ReadStatus.Unavailable, error.Code, error.ErrorKind).ToSafeLogCode();
                Check.Contains("errorKind=" + routing.Kind, safe);
                Check.False(safe.Contains("private", StringComparison.Ordinal));
                Check.False(safe.Contains(routing.Message, StringComparison.Ordinal));
            });
        }

        foreach (var message in new[]
        {
            "workspace routing discovery unauthorized (401): private-account",
            "workspace routing discovery failed: HTTP 403 private-account",
            "workspace routing discovery timed out\nprivate-token",
            "prefix workspace routing discovery failed",
            "workspace routing discovery failed ",
            "WORKSPACE ROUTING DISCOVERY FAILED",
            "403 Forbidden",
            "account private-401-403 could not be read"
        })
        {
            var input = message;
            test("RPC diagnostics: unrecognized routing text is never classified: " + input.Replace('\n', ' '), () =>
                Check.Equal(CliErrorKind.None, RpcException.ClassifyError(-32603, input)));
        }
        foreach (var code in new int?[] { null, -32601, 401, 403, 429 })
        {
            var input = code;
            test("RPC diagnostics: routing category requires internal-error wrapper " + (input?.ToString() ?? "null"), () =>
                Check.Equal(CliErrorKind.None, RpcException.ClassifyError(input, "workspace routing discovery unauthorized (401)")));
        }
        test("RPC diagnostics: old constructors remain unclassified and formatter rejects unknown enum values", () =>
        {
            Check.Equal(CliErrorKind.None, new RpcException(-32603).ErrorKind);
            Check.Contains("errorKind=None", new CliDiagnostic(CliSource.Path, null, CliStage.Start, ReadStatus.Ready).ToSafeLogCode());
            Check.Contains("errorKind=Unknown", new CliDiagnostic(CliSource.Path, null, CliStage.Start, ReadStatus.Unavailable,
                ErrorKind: (CliErrorKind)999).ToSafeLogCode());
        });
    }

    private static async Task<JsonElement> InitializeToQuota(FakePeer peer)
    {
        var initialize = await peer.ReadAsync();
        Check.Method(initialize, "initialize");
        await peer.RespondAsync(initialize, new { userAgent = "codex_quota_tray/0.159.2" });
        Check.Method(await peer.ReadAsync(), "initialized");
        var quota = await peer.ReadAsync();
        Check.Method(quota, "account/rateLimits/read");
        return quota;
    }
}
