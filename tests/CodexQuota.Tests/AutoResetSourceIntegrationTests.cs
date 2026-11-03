using System.Text.Json;
using CodexQuota.Core;

// Synthetic protocol peers and a temp operation ledger only. No real source discovery,
// installed component, account, Windows process, credential, or remote endpoint is used.
internal static class AutoResetSourceIntegrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    public static void Register(Action<string, Func<Task>> asyncTest)
    {
        asyncTest("auto reset source integration: post-consume reread timeout keeps retry and fallback read-only", async () =>
        {
            using var temporary = new TempDirectory();
            var controller = new AutoResetCreditController(new AutoResetCreditStore(temporary.Path), () => Now) { Enabled = true };
            var router = new AutoQuotaSourceClient(QuotaDataSource.Auto, () => Now, (_, _) => Task.CompletedTask);
            // Establish CLI as the current winner so the real router exercises its retry path.
            await router.ReadAsync((_, _) => Task.FromResult(new QuotaRead(ReadStatus.Ready, new QuotaWindow(5, null, 300))), CancellationToken.None);
            var roundSequence = controller.MutationSequence;
            var sources = new List<QuotaDataSource>();
            var allowed = new List<bool>();
            var mutations = 0;
            var result = await router.ReadAsync(async (source, token) =>
            {
                sources.Add(source);
                var attempt = sources.Count;
                // Same fence as TrayContext.RefreshAsync. Sequence is captured ONCE per round.
                var autoReset = controller.MutationSequence == roundSequence ? controller : null;
                allowed.Add(autoReset is not null);
                await using var peer = new FakePeer();
                var read = CodexQuotaProtocol.ReadAsync(peer.Rpc, TimeSpan.FromSeconds(1), token, null, autoReset);
                await Initialize(peer);
                var initial = await peer.ReadAsync();
                Check.Method(initial, "account/rateLimits/read");
                if (attempt == 2)
                {
                    // Current source retry also times out, requiring the next source.
                    await Check.ThrowsAsync<TimeoutException>(read);
                    return new QuotaRead(ReadStatus.Timeout);
                }
                var snapshot = Snapshot("synthetic-account-" + attempt, "synthetic-credit-" + attempt);
                await peer.RespondAsync(initial, snapshot);
                if (attempt == 1)
                {
                    var validate = await peer.ReadAsync();
                    Check.Method(validate, "account/rateLimits/read");
                    await peer.RespondAsync(validate, snapshot);
                    var consume = await peer.ReadAsync();
                    Check.Method(consume, "account/rateLimitResetCredit/consume");
                    mutations++;
                    await peer.RespondAsync(consume, new { outcome = "reset" });
                    var reread = await peer.ReadAsync();
                    Check.Method(reread, "account/rateLimits/read");
                    // The mutation has completed, but its authoritative quota reread does not.
                    await Check.ThrowsAsync<TimeoutException>(read);
                    return new QuotaRead(ReadStatus.Timeout);
                }
                var successful = await read;
                // This source has a different account/card that would otherwise be eligible.
                // No validation or consume RPC is permitted in this same fallback round.
                await Check.StillPendingAsync(peer.ReadAsync());
                return successful;
            }, CancellationToken.None);
            Check.Equal("Cli,Cli,VsCode", string.Join(",", sources));
            Check.Equal("True,False,False", string.Join(",", allowed));
            Check.Equal(1, mutations);
            Check.Equal(roundSequence + 1, controller.MutationSequence);
            Check.Equal(ReadStatus.Ready, result.Status);
            Check.Equal<QuotaDataSource?>(QuotaDataSource.VsCode, router.ActiveSource);
        });

        asyncTest("auto reset source integration: no-window source with expiring cards never consumes before fallback", async () =>
        {
            using var temporary = new TempDirectory();
            var controller = new AutoResetCreditController(new AutoResetCreditStore(temporary.Path), () => Now) { Enabled = true };
            var router = new AutoQuotaSourceClient(QuotaDataSource.Auto, () => Now, (_, _) => Task.CompletedTask);
            var roundSequence = controller.MutationSequence;
            var statuses = new List<ReadStatus>();
            var sources = new List<QuotaDataSource>();
            var result = await router.ReadAsync(async (source, token) =>
            {
                sources.Add(source);
                var noWindow = source == QuotaDataSource.Cli;
                await using var peer = new FakePeer();
                var read = CodexQuotaProtocol.ReadAsync(peer.Rpc, TimeSpan.FromSeconds(1), token, null,
                    controller.MutationSequence == roundSequence ? controller : null);
                await Initialize(peer);
                var initial = await peer.ReadAsync();
                Check.Method(initial, "account/rateLimits/read");
                await peer.RespondAsync(initial, Snapshot("synthetic-account", "synthetic-credit", noWindow,
                    expiresAt: noWindow ? Now.AddSeconds(45) : Now.AddHours(1)));
                var completed = await read;
                statuses.Add(completed.Status);
                await Check.StillPendingAsync(peer.ReadAsync());
                return completed;
            }, CancellationToken.None);
            Check.Equal("Cli,VsCode", string.Join(",", sources));
            Check.Equal("NoQuotaWindow,Ready", string.Join(",", statuses));
            Check.Equal(0L, controller.MutationSequence);
            Check.Equal(ReadStatus.Ready, result.Status);
            Check.Equal<QuotaDataSource?>(QuotaDataSource.VsCode, router.ActiveSource);
            Check.Equal(0, Directory.GetFiles(temporary.Path).Length);
        });
    }

    private static async Task Initialize(FakePeer peer)
    {
        var initialize = await peer.ReadAsync();
        Check.Method(initialize, "initialize");
        await peer.RespondAsync(initialize, new { });
        Check.Method(await peer.ReadAsync(), "initialized");
    }

    private static JsonElement Snapshot(string account, string credit, bool noWindow = false, DateTimeOffset? expiresAt = null)
    {
        var window = noWindow ? "{}" : "{\"primary\":{\"usedPercent\":95,\"windowDurationMins\":300}}";
        using var document = JsonDocument.Parse("{\"accountId\":" + JsonSerializer.Serialize(account) +
            ",\"rateLimits\":" + window + ",\"rateLimitResetCredits\":{\"availableCount\":1,\"credits\":[{\"id\":" +
            JsonSerializer.Serialize(credit) + ",\"status\":\"available\",\"resetType\":\"codexRateLimits\",\"expiresAt\":" +
            (expiresAt ?? Now.AddSeconds(45)).ToUnixTimeSeconds() + "}]}}");
        return document.RootElement.Clone();
    }
}
