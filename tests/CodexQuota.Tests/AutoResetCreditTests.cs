using System.Text.Json;
using CodexQuota.Core;

// Every operation is an in-memory fake RPC. Only durable records use isolated temp files.
// No app-server, credentials, browser, desktop, or live account is touched by these tests.
static class AutoResetCreditTests
{
    private static readonly DateTimeOffset Expiry = new(2026, 11, 3, 12, 0, 37, TimeSpan.Zero);
    private const string Read = "account/rateLimits/read";
    private const string Consume = "account/rateLimitResetCredit/consume";

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        asyncTest("auto reset: disabled by default performs no requests or disk writes", async () =>
        {
            using var h = new Harness(enabled: false);
            Check.False(h.Controller.Enabled);
            await h.Run();
            Check.Equal(0, h.Calls.Count);
            Check.False(Directory.Exists(h.Path));
            Check.Null(h.Controller.NextDue(ResetCards.Parse(h.Snapshot), h.Now));
        });

        foreach (var seconds in new[] { -61.0, -60.001, -60, -59.999, -1, -0.001, 0, 0.001, 1, 600 })
            asyncTest($"auto reset: precise expiry window at {seconds} seconds", async () =>
            {
                using var h = new Harness { Now = Expiry.AddSeconds(seconds) };
                await h.Run();
                var expected = seconds >= -60 && seconds < 0 ? 1 : 0;
                Check.Equal(expected, h.Mutations.Count);
                Check.Equal(expected * 3, h.Calls.Count);
            });

        test("auto reset: scheduling preserves seconds and absolute timezone", () =>
        {
            using var h = new Harness();
            var cards = ResetCards.Parse(h.Snapshot);
            var now = Expiry.AddMinutes(-2).ToOffset(TimeSpan.FromHours(9));
            Check.Equal(Expiry.AddSeconds(-60), h.Controller.NextDue(cards, now));
            Check.Null(h.Controller.NextDue(cards, Expiry.AddSeconds(-60)));
            Check.Null(h.Controller.NextDue(cards, Expiry.AddSeconds(-1)));
            Check.Null(h.Controller.NextDue(cards, Expiry.AddSeconds(1)));
            h.Controller.Enabled = false;
            Check.Null(h.Controller.NextDue(cards, now));
        });

        foreach (var resumeSeconds in new[] { -59, -10, 0, 120 })
            asyncTest($"auto reset: resume at {resumeSeconds} seconds uses current wall clock", async () =>
            {
                using var h = new Harness { Now = Expiry.AddMinutes(-30) };
                await h.Run();
                Check.Equal(0, h.Mutations.Count);
                h.Now = Expiry.AddSeconds(resumeSeconds);
                await h.Run();
                Check.Equal(resumeSeconds < 0 ? 1 : 0, h.Mutations.Count);
            });

        foreach (var jumpSeconds in new[] { -120, 0, 1 })
            asyncTest($"auto reset: revalidation clock jump to {jumpSeconds} seconds prevents stale send", async () =>
            {
                using var h = new Harness();
                h.OnRead = _ => { h.Now = Expiry.AddSeconds(jumpSeconds); return Task.FromResult(h.Snapshot); };
                await h.Run();
                Check.Equal(0, h.Mutations.Count);
            });

        var invalidRows = new Dictionary<string, string>
        {
            ["never expires"] = Card("credit", "null"),
            ["missing expiry"] = "{\"id\":\"credit\",\"status\":\"available\",\"resetType\":\"codexRateLimits\"}",
            ["string expiry"] = Card("credit", "\"2026-11-03T12:00:37Z\""),
            ["fraction expiry"] = Card("credit", "1.5"),
            ["invalid expiry"] = Card("credit", "9223372036854775807"),
            ["old card"] = Card("credit", Expiry.AddDays(-1).ToUnixTimeSeconds().ToString()),
            ["redeemed"] = Card("credit", status: "redeemed"),
            ["redeeming"] = Card("credit", status: "redeeming"),
            ["expired status"] = Card("credit", status: "expired"),
            ["unknown status"] = Card("credit", status: "futureStatus"),
            ["case status"] = Card("credit", status: "Available"),
            ["unknown type"] = Card("credit", type: "futureResetType"),
            ["empty ID"] = Card(""),
            ["oversized ID"] = Card(new string('x', 1025)),
            ["missing ID"] = "{\"status\":\"available\",\"resetType\":\"codexRateLimits\",\"expiresAt\":" + Expiry.ToUnixTimeSeconds() + "}",
            ["null row"] = "null"
        };
        foreach (var pair in invalidRows)
            asyncTest("auto reset: rejects " + pair.Key, async () =>
            {
                using var h = new Harness { Snapshot = Snapshot(rows: pair.Value) };
                await h.Run();
                Check.Equal(0, h.Calls.Count);
            });

        foreach (var summary in new[] { "null", "{}", "{\"availableCount\":1}", "{\"availableCount\":1,\"credits\":null}", "{\"availableCount\":1,\"credits\":[]}", "{\"availableCount\":-1}", "{\"availableCount\":\"1\"}" })
            asyncTest("auto reset: absent unknown or count-only summary fails closed: " + summary, async () =>
            {
                using var h = new Harness { Snapshot = Json("{\"accountId\":\"account\",\"rateLimits\":{},\"rateLimitResetCredits\":" + summary + "}") };
                await h.Run();
                Check.Equal(0, h.Calls.Count);
            });

        foreach (var account in new[] { "null", "\"\"", "1", JsonSerializer.Serialize(new string('a', 1025)) })
            asyncTest("auto reset: invalid account identifier fails closed: " + account[..Math.Min(account.Length, 30)], async () =>
            {
                using var h = new Harness { Snapshot = Snapshot(accountJson: account) };
                await h.Run();
                Check.Equal(0, h.Calls.Count);
            });

        foreach (var malformed in new[] { "{}", "null", "[]", "{\"accountId\":\"account\"}", "{\"rateLimits\":null}" })
            asyncTest("auto reset: unavailable quota snapshot fails closed: " + malformed, async () =>
            {
                using var h = new Harness { Snapshot = Json(malformed) };
                await h.Run();
                Check.Equal(0, h.Calls.Count);
            });

        asyncTest("auto reset: earliest expiry then ordinal ID determines exact targeted credit", async () =>
        {
            using var h = new Harness { Snapshot = Snapshot(rows: Card("later", Expiry.AddSeconds(10).ToUnixTimeSeconds().ToString()) + "," + Card("b") + "," + Card("a")) };
            await h.Run();
            Check.Equal("a", h.Mutations.Single().GetProperty("creditId").GetString());
            Check.Equal(2, h.Mutations.Single().EnumerateObject().Count());
            Check.True(Guid.TryParseExact(h.Mutations.Single().GetProperty("idempotencyKey").GetString(), "D", out _));
            Check.Equal(Read + "," + Consume + "," + Read, string.Join(",", h.Calls));
        });

        asyncTest("auto reset: repeated snapshots advance past terminal cards with equal expiry", async () =>
        {
            using var h = new Harness { Snapshot = Snapshot(rows: Card("b") + "," + Card("a")) };
            await h.Run(); await h.Run(); await h.Run();
            Check.Equal(2, h.Mutations.Count);
            Check.Equal("a", h.Mutations[0].GetProperty("creditId").GetString());
            Check.Equal("b", h.Mutations[1].GetProperty("creditId").GetString());
        });

        foreach (var duplicate in new[] { Card("credit"), Card("credit", status: "redeemed"), Card("credit", Expiry.AddSeconds(1).ToUnixTimeSeconds().ToString()), Card("credit", type: "futureType") })
            asyncTest("auto reset: duplicate identity is ambiguous and cannot be consumed: " + duplicate, async () =>
            {
                using var h = new Harness { Snapshot = Snapshot(rows: Card("credit") + "," + duplicate) };
                await h.Run(); Check.Equal(0, h.Calls.Count);
            });

        asyncTest("auto reset: duplicate identity appearing during revalidation prevents consume", async () =>
        {
            using var h = new Harness();
            h.OnRead = _ => Task.FromResult(Snapshot(rows: Card("credit") + "," + Card("credit", status: "redeemed")));
            await h.Run(); Check.Equal(0, h.Mutations.Count);
        });

        var changedSnapshots = new Dictionary<string, JsonElement>
        {
            ["account"] = Snapshot(accountJson: "\"other-account\""),
            ["card ID"] = Snapshot(rows: Card("other-credit")),
            ["card expiry"] = Snapshot(rows: Card("credit", Expiry.AddSeconds(1).ToUnixTimeSeconds().ToString())),
            ["card status"] = Snapshot(rows: Card("credit", status: "redeemed")),
            ["card type"] = Snapshot(rows: Card("credit", type: "unknown")),
            ["missing account"] = Snapshot(accountJson: "null"),
            ["zero count"] = Snapshot(rows: Card("credit"), count: 0),
            ["quota unavailable"] = Json("{}")
        };
        foreach (var pair in changedSnapshots)
            asyncTest("auto reset: authoritative revalidation changes " + pair.Key + " and prevents consume", async () =>
            {
                using var h = new Harness();
                h.OnRead = _ => Task.FromResult(pair.Value);
                var actual = await h.Run();
                Check.Equal(0, h.Mutations.Count);
                Check.Equal(pair.Value.GetRawText(), actual.GetRawText());
            });

        foreach (var outcome in new[] { "reset", "alreadyRedeemed", "nothingToReset", "noCredit" })
            asyncTest("auto reset: " + outcome + " is terminal across repeated calls and restart", async () =>
            {
                using var h = new Harness();
                h.OnConsume = (_, _) => Task.FromResult(Json("{\"outcome\":\"" + outcome + "\"}"));
                await h.Run();
                h.Now = h.Now.AddSeconds(10);
                await h.Run(); h.Restart(); await h.Run();
                Check.Equal(1, h.Mutations.Count);
                Check.Equal(outcome, h.Store.Read(AutoResetCreditStore.Identity("account", "credit"))!.State);
            });

        asyncTest("auto reset: timeout retries same durable key after 10 seconds, at most twice across restarts", async () =>
        {
            using var h = new Harness();
            h.OnConsume = (_, _) => throw new TimeoutException();
            await h.Run();
            var saved = h.Store.Read(AutoResetCreditStore.Identity("account", "credit"))!;
            Check.Equal("pending", saved.State); Check.Equal(1, saved.Sends);
            h.Restart(); h.Now = h.Now.AddSeconds(9); await h.Run();
            Check.Equal(1, h.Mutations.Count);
            h.Now = h.Now.AddSeconds(1); await h.Run();
            Check.Equal(2, h.Mutations.Count);
            Check.Equal(saved.Key, h.Mutations[1].GetProperty("idempotencyKey").GetString());
            h.Restart(); h.Now = h.Now.AddSeconds(10); await h.Run();
            Check.Equal(2, h.Mutations.Count);
            Check.Equal(2, h.Store.Read(AutoResetCreditStore.Identity("account", "credit"))!.Sends);
            Check.Equal(4, h.Calls.Count(method => method == Read));
        });

        asyncTest("auto reset: clock moving backward cannot accelerate pending retry", async () =>
        {
            using var h = new Harness();
            h.OnConsume = (_, _) => throw new TimeoutException();
            await h.Run(); h.Now = h.Now.AddSeconds(-1); await h.Run();
            Check.Equal(1, h.Mutations.Count);
        });

        asyncTest("auto reset: unknown outcome still rereads and preserves original retry key", async () =>
        {
            using var h = new Harness();
            h.OnConsume = (_, _) => Task.FromResult(Json("{\"outcome\":\"newUnknownOutcome\"}"));
            await h.Run(); h.Now = h.Now.AddSeconds(10); await h.Run();
            Check.Equal(2, h.Mutations.Count);
            Check.Equal(h.Mutations[0].GetProperty("idempotencyKey").GetString(), h.Mutations[1].GetProperty("idempotencyKey").GetString());
            Check.Equal(4, h.Calls.Count(method => method == Read));
        });

        asyncTest("auto reset: pending restart reconciles card removal without consuming again", async () =>
        {
            using var h = new Harness();
            h.OnConsume = (_, _) => throw new TimeoutException();
            await h.Run(); h.Restart(); h.Now = h.Now.AddSeconds(10);
            h.OnRead = _ => Task.FromResult(Snapshot(count: 0));
            await h.Run();
            Check.Equal(1, h.Mutations.Count);
        });

        foreach (var nextRows in new[] { Card("b"), Card("a", status: "redeeming") + "," + Card("b"), Card("a", status: "redeemed") + "," + Card("b") })
            asyncTest("auto reset: unresolved account attempt blocks a different card across restart: " + nextRows, async () =>
            {
                using var h = new Harness { Snapshot = Snapshot(rows: Card("a") + "," + Card("b")) };
                h.OnConsume = (_, _) => throw new TimeoutException();
                await h.Run(); Check.Equal(1, h.Mutations.Count);
                h.Now = h.Now.AddSeconds(10); h.Snapshot = Snapshot(rows: nextRows);
                await h.Run(); Check.Equal(1, h.Mutations.Count);
                h.Restart(); await h.Run(); Check.Equal(1, h.Mutations.Count);
            });

        asyncTest("auto reset: unresolved expired card still blocks a later credit", async () =>
        {
            using var h = new Harness { Snapshot = Snapshot(rows: Card("a") + "," + Card("b", Expiry.AddSeconds(30).ToUnixTimeSeconds().ToString())) };
            h.OnConsume = (_, _) => throw new TimeoutException();
            await h.Run(); h.Now = Expiry.AddSeconds(1); h.Restart(); await h.Run();
            Check.Equal(1, h.Mutations.Count);
        });

        asyncTest("auto reset: retry delay is ten real seconds even at fractional initial timestamp", async () =>
        {
            using var h = new Harness { Now = Expiry.AddSeconds(-59.001) };
            h.OnConsume = (_, _) => throw new TimeoutException();
            await h.Run(); h.Now = Expiry.AddSeconds(-50); await h.Run();
            Check.Equal(1, h.Mutations.Count);
            h.Now = Expiry.AddSeconds(-49.001); await h.Run();
            Check.Equal(2, h.Mutations.Count);
        });

        asyncTest("auto reset: durable intent exists before mutation and contains no account or card plaintext", async () =>
        {
            using var h = new Harness();
            h.OnConsume = (parameters, _) =>
            {
                var files = Directory.GetFiles(h.Path, "*.json"); Check.Equal(2, files.Length);
                var text = string.Join("", files.Select(File.ReadAllText));
                Check.False(text.Contains("account", StringComparison.Ordinal));
                Check.False(text.Contains("credit", StringComparison.Ordinal));
                var pending = h.Store.Read(AutoResetCreditStore.Identity("account", "credit"))!;
                Check.Equal("pending", pending.State); Check.Equal(1, pending.Sends);
                Check.Equal(AutoResetCreditStore.Identity("account", "credit"), h.Store.ReadPending("account"));
                Check.Equal(parameters.GetProperty("idempotencyKey").GetString(), pending.Key);
                Check.Equal(Expiry.ToUnixTimeSeconds(), pending.ExpiresAt);
                return Task.FromResult(Json("{\"outcome\":\"reset\"}"));
            };
            await h.Run();
        });

        asyncTest("auto reset: authoritative post-consume snapshot is always returned", async () =>
        {
            using var h = new Harness();
            var after = Snapshot(count: 0, usedPercent: 0);
            h.OnRead = count => Task.FromResult(count == 1 ? h.Snapshot : after);
            Check.Equal(after.GetRawText(), (await h.Run()).GetRawText());
            Check.Equal(1, h.Mutations.Count);
        });

        foreach (var failure in new Exception[] { new TimeoutException(), new IOException(), new RpcException(-32000), new OperationCanceledException() })
            asyncTest("auto reset: consume " + failure.GetType().Name + " still attempts authoritative reread", async () =>
            {
                using var h = new Harness();
                h.OnConsume = (_, _) => throw failure;
                await h.Run();
                Check.Equal(Read + "," + Consume + "," + Read, string.Join(",", h.Calls));
                Check.Equal("pending", h.Store.Read(AutoResetCreditStore.Identity("account", "credit"))!.State);
            });

        asyncTest("auto reset: failed post-consume reread propagates rather than returning stale quota", async () =>
        {
            using var h = new Harness();
            h.OnRead = count => count == 1 ? Task.FromResult(h.Snapshot) : throw new IOException("fake reread transport failure");
            await Check.ThrowsAsync<IOException>(h.Run());
            Check.Equal(1, h.Mutations.Count);
        });

        foreach (var code in new[] { -32601, -32600 })
            asyncTest("auto reset: unsupported method " + code + " stops retries and scheduling", async () =>
            {
                using var h = new Harness();
                h.OnConsume = (_, _) => throw new RpcException(code);
                await h.Run(); h.Now = h.Now.AddSeconds(10); await h.Run();
                Check.Equal(1, h.Mutations.Count);
                Check.Null(h.Controller.NextDue(ResetCards.Parse(h.Snapshot), Expiry.AddMinutes(-2)));
                h.Restart(); await h.Run();
                Check.Equal(1, h.Mutations.Count);
                Check.Equal(2, h.Calls.Count(method => method == Read));
            });

        foreach (var rereadFails in new[] { false, true })
            asyncTest("auto reset: rate-limited consume preserves 429 and persisted cooldown, reread fails=" + rereadFails, async () =>
            {
                using var h = new Harness();
                h.OnConsume = (_, _) => throw new RpcException(429, isRateLimited: true);
                h.OnRead = count => count == 2 && rereadFails ? throw new IOException("fake transport") : Task.FromResult(h.Snapshot);
                var failure = await Check.ThrowsAsync<RpcException>(h.Run());
                Check.True(failure.IsRateLimited);
                Check.Equal(Read + "," + Consume + "," + Read, string.Join(",", h.Calls));
                Check.Equal(1L, h.Controller.MutationSequence);
                var saved = h.Store.Read(AutoResetCreditStore.Identity("account", "credit"))!;
                Check.Equal("pending", saved.State);
                Check.Equal(h.Now.AddMinutes(5).ToUnixTimeMilliseconds(), saved.RetryNotBeforeMilliseconds);
                h.Now = h.Now.AddSeconds(10); await h.Run();
                h.Restart(); await h.Run();
                Check.Equal(1, h.Mutations.Count); Check.Equal(3, h.Calls.Count);
                h.Now = Expiry; await h.Run();
                h.Now = Expiry.AddMinutes(5); h.Restart(); await h.Run();
                Check.Equal(1, h.Mutations.Count); Check.Equal(3, h.Calls.Count);
            });

        asyncTest("auto reset: user cancellation during 429 reread is not replaced with rate-limit error", async () =>
        {
            using var h = new Harness(); using var cts = new CancellationTokenSource();
            h.OnConsume = (_, _) => { cts.Cancel(); throw new RpcException(429, isRateLimited: true); };
            h.OnRead = count => { if (count == 2) cts.Token.ThrowIfCancellationRequested(); return Task.FromResult(h.Snapshot); };
            await Check.ThrowsAsync<OperationCanceledException>(h.Run(cts.Token));
            Check.Equal(2, h.Calls.Count(method => method == Read));
            Check.Equal(1, h.Mutations.Count);
        });

        asyncTest("auto reset: cancellation before send never consumes", async () =>
        {
            using var h = new Harness(); using var cts = new CancellationTokenSource();
            h.OnRead = _ => { cts.Cancel(); return Task.FromResult(h.Snapshot); };
            await Check.ThrowsAsync<OperationCanceledException>(h.Run(cts.Token));
            Check.Equal(0, h.Mutations.Count);
        });

        asyncTest("auto reset: disabling during authoritative read never consumes", async () =>
        {
            using var h = new Harness();
            h.OnRead = _ => { h.Controller.Enabled = false; return Task.FromResult(h.Snapshot); };
            await h.Run();
            Check.Equal(0, h.Mutations.Count);
        });

        asyncTest("auto reset: overlapping calls on one controller perform a single consume", async () =>
        {
            using var h = new Harness();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.OnRead = async count => { if (count == 1) { started.SetResult(); await release.Task; } return h.Snapshot; };
            var first = h.Run(); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await h.Run(); Check.Equal(1, h.Calls.Count);
            release.SetResult(); await first;
            Check.Equal(1, h.Mutations.Count);
        });

        asyncTest("auto reset: independent controller cannot consume while disk lease is held", async () =>
        {
            using var h = new Harness();
            using (h.Store.Acquire()) { await h.Run(); Check.Equal(0, h.Calls.Count); }
            await h.Run(); Check.Equal(1, h.Mutations.Count);
        });

        asyncTest("auto reset: concurrent controllers share exclusive disk lock across revalidation", async () =>
        {
            using var h = new Harness();
            var other = new AutoResetCreditController(new AutoResetCreditStore(h.Path), () => h.Now) { Enabled = true };
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.OnRead = async count => { if (count == 1) { started.SetResult(); await release.Task; } return h.Snapshot; };
            var first = h.Run(); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await other.ProcessAsync(h.Snapshot, h.Request, CancellationToken.None);
            Check.Equal(1, h.Calls.Count);
            release.SetResult(); await first;
            Check.Equal(1, h.Mutations.Count);
        });

        foreach (var contents in new[] { "{", "null", "{}", new string('x', 4097), "{\"Version\":1,\"Key\":\"not-a-guid\",\"ExpiresAt\":9999999999,\"LastAttempt\":0,\"Sends\":1,\"State\":\"pending\"}" })
            asyncTest("auto reset: corrupt record prevents consume: " + contents[..Math.Min(contents.Length, 20)], async () =>
            {
                using var h = new Harness(); Directory.CreateDirectory(h.Path);
                File.WriteAllText(System.IO.Path.Combine(h.Path, AutoResetCreditStore.Identity("account", "credit") + ".json"), contents);
                await h.Run(); Check.Equal(0, h.Calls.Count);
            });

        asyncTest("auto reset: unavailable record directory prevents consume", async () =>
        {
            using var h = new Harness(); File.WriteAllText(h.Path, "not a directory");
            await h.Run(); Check.Equal(0, h.Calls.Count);
        });

        asyncTest("auto reset: unreadable record path prevents consume", async () =>
        {
            using var h = new Harness(); Directory.CreateDirectory(h.Path);
            Directory.CreateDirectory(System.IO.Path.Combine(h.Path, AutoResetCreditStore.Identity("account", "credit") + ".json"));
            await h.Run(); Check.Equal(0, h.Calls.Count);
        });

        asyncTest("auto reset: failed durable intent write prevents consume", async () =>
        {
            using var h = new Harness();
            h.OnRead = _ =>
            {
                Directory.CreateDirectory(System.IO.Path.Combine(h.Path, AutoResetCreditStore.Identity("account", "credit") + ".json"));
                return Task.FromResult(h.Snapshot);
            };
            await h.Run(); Check.Equal(0, h.Mutations.Count);
            Check.Equal(0, Directory.GetFiles(h.Path, ".pending-*.tmp").Length);
        });

        asyncTest("auto reset: terminal persistence failure still rereads and pauses further requests", async () =>
        {
            using var h = new Harness();
            h.OnConsume = (_, _) =>
            {
                var target = System.IO.Path.Combine(h.Path, AutoResetCreditStore.Identity("account", "credit") + ".json");
                File.Delete(target); Directory.CreateDirectory(target);
                return Task.FromResult(Json("{\"outcome\":\"reset\"}"));
            };
            await h.Run(); Check.Equal(2, h.Calls.Count(method => method == Read));
            h.Now = h.Now.AddSeconds(10); await h.Run();
            Check.Equal(1, h.Mutations.Count);
        });

        asyncTest("auto reset: previous pending expiry cannot be silently replaced on restart", async () =>
        {
            using var h = new Harness();
            using (h.Store.Acquire())
                h.Store.Save(AutoResetCreditStore.Identity("account", "credit"), new ResetCreditAttempt(1, Guid.NewGuid().ToString("D"), Expiry.AddSeconds(1).ToUnixTimeSeconds(), h.Now.AddSeconds(-11).ToUnixTimeSeconds(), 1, "pending"));
            h.Restart(); await h.Run(); Check.Equal(0, h.Mutations.Count);
        });

        foreach (var record in new[]
        {
            new ResetCreditAttempt(2, Guid.NewGuid().ToString("D"), Expiry.ToUnixTimeSeconds(), Expiry.AddMinutes(-1).ToUnixTimeSeconds(), 1, "pending"),
            new ResetCreditAttempt(1, Guid.NewGuid().ToString("D"), Expiry.ToUnixTimeSeconds(), Expiry.ToUnixTimeSeconds(), 1, "pending"),
            new ResetCreditAttempt(1, Guid.NewGuid().ToString("D"), Expiry.ToUnixTimeSeconds(), Expiry.AddMinutes(-1).ToUnixTimeSeconds(), 0, "pending"),
            new ResetCreditAttempt(1, Guid.NewGuid().ToString("D"), Expiry.ToUnixTimeSeconds(), Expiry.AddMinutes(-1).ToUnixTimeSeconds(), 3, "pending"),
            new ResetCreditAttempt(1, Guid.NewGuid().ToString("D"), Expiry.ToUnixTimeSeconds(), Expiry.AddMinutes(-1).ToUnixTimeSeconds(), 1, "unknown")
        })
            asyncTest("auto reset: invalid durable record fails closed: " + JsonSerializer.Serialize(record), async () =>
            {
                using var h = new Harness();
                using (h.Store.Acquire()) h.Store.Save(AutoResetCreditStore.Identity("account", "credit"), record);
                await h.Run(); Check.Equal(0, h.Calls.Count);
            });

        foreach (var marker in new[] { "null", "{}", "\"not-an-identity\"", new string('x', 1025), "\"" + new string('a', 64) + "\"" })
            asyncTest("auto reset: corrupt or orphaned account pending marker prevents consume: " + marker[..Math.Min(marker.Length, 20)], async () =>
            {
                using var h = new Harness(); Directory.CreateDirectory(h.Path);
                File.WriteAllText(System.IO.Path.Combine(h.Path, "account-" + AutoResetCreditStore.Identity("account", "") + ".json"), marker);
                await h.Run(); Check.Equal(0, h.Calls.Count);
            });

        asyncTest("auto reset: valid quota-less snapshot cannot trigger mutation", async () =>
        {
            using var h = new Harness { Snapshot = Json("{\"accountId\":\"account\",\"rateLimits\":{},\"rateLimitResetCredits\":{\"availableCount\":1,\"credits\":[" + Card("credit") + "]}}") };
            await h.Run(); Check.Equal(0, h.Calls.Count);
        });

        foreach (var milliseconds in new[] { -1L, long.MaxValue, Expiry.AddDays(-1).ToUnixTimeMilliseconds(), Expiry.ToUnixTimeMilliseconds() })
            asyncTest("auto reset: inconsistent persisted millisecond timestamp fails closed: " + milliseconds, async () =>
            {
                using var h = new Harness();
                using (h.Store.Acquire()) h.Store.Save(AutoResetCreditStore.Identity("account", "credit"),
                    new ResetCreditAttempt(1, Guid.NewGuid().ToString("D"), Expiry.ToUnixTimeSeconds(), h.Now.ToUnixTimeSeconds(), 1, "pending") { LastAttemptMilliseconds = milliseconds });
                await h.Run(); Check.Equal(0, h.Calls.Count);
            });

        asyncTest("auto reset: legacy second-only pending record waits a conservative full ten seconds", async () =>
        {
            using var h = new Harness();
            var key = Guid.NewGuid().ToString("D"); var initial = h.Now;
            using (h.Store.Acquire()) h.Store.Save(AutoResetCreditStore.Identity("account", "credit"),
                new ResetCreditAttempt(1, key, Expiry.ToUnixTimeSeconds(), initial.ToUnixTimeSeconds(), 1, "pending"));
            h.Now = initial.AddSeconds(10); await h.Run(); Check.Equal(0, h.Mutations.Count);
            h.Now = initial.AddSeconds(11); await h.Run(); Check.Equal(1, h.Mutations.Count);
            Check.Equal(key, h.Mutations.Single().GetProperty("idempotencyKey").GetString());
        });

        test("auto reset: account and credit identity hashing is unambiguous", () =>
            Check.False(AutoResetCreditStore.Identity("a", "bc") == AutoResetCreditStore.Identity("ab", "c")));
    }

    private static JsonElement Json(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    private static string Card(string id, string? expiry = null, string status = "available", string type = "codexRateLimits") =>
        "{\"id\":" + JsonSerializer.Serialize(id) + ",\"status\":" + JsonSerializer.Serialize(status) + ",\"resetType\":" + JsonSerializer.Serialize(type) + ",\"expiresAt\":" + (expiry ?? Expiry.ToUnixTimeSeconds().ToString()) + "}";
    private static JsonElement Snapshot(string? rows = null, string accountJson = "\"account\"", int? count = null, int usedPercent = 95)
    {
        rows ??= Card("credit");
        var actualCount = count ?? Json("[" + rows + "]").GetArrayLength();
        return Json("{\"accountId\":" + accountJson + ",\"rateLimits\":{\"primary\":{\"usedPercent\":" + usedPercent + ",\"windowDurationMins\":300}},\"rateLimitResetCredits\":{\"availableCount\":" + actualCount + ",\"credits\":[" + rows + "]}}");
    }

    private sealed class Harness : IDisposable
    {
        internal readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codex-quota-auto-reset-tests-" + Guid.NewGuid().ToString("N"));
        internal AutoResetCreditStore Store;
        internal AutoResetCreditController Controller;
        internal DateTimeOffset Now = Expiry.AddSeconds(-60);
        internal JsonElement Snapshot = AutoResetCreditTests.Snapshot();
        internal readonly List<string> Calls = new();
        internal readonly List<JsonElement> Mutations = new();
        internal Func<int, Task<JsonElement>>? OnRead;
        internal Func<JsonElement, CancellationToken, Task<JsonElement>>? OnConsume;
        private int _reads;
        internal Harness(bool enabled = true) { Store = new(Path); Controller = new(Store, () => Now) { Enabled = enabled }; }
        internal void Restart() { Store = new(Path); Controller = new(Store, () => Now) { Enabled = true }; }
        internal Task<JsonElement> Run(CancellationToken token = default) => Controller.ProcessAsync(Snapshot, Request, token);
        internal Task<JsonElement> Request(string method, object parameters, CancellationToken token)
        {
            Calls.Add(method);
            if (method == Read) { _reads++; return OnRead?.Invoke(_reads) ?? Task.FromResult(Snapshot); }
            Check.Equal(Consume, method);
            var data = JsonSerializer.SerializeToElement(parameters); Mutations.Add(data);
            return OnConsume?.Invoke(data, token) ?? Task.FromResult(Json("{\"outcome\":\"reset\"}"));
        }
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            else if (File.Exists(Path)) File.Delete(Path);
        }
    }
}
