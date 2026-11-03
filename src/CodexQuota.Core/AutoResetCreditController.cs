using System.Security;
using System.Text.Json;

namespace CodexQuota.Core;

/// <summary>Opt-in expiry policy. Runs inside the existing short-lived official app-server
/// session, never reads credentials, starts a turn, buys credits, or selects an arbitrary card.</summary>
public sealed class AutoResetCreditController
{
    private readonly AutoResetCreditStore _store;
    private readonly Func<DateTimeOffset> _now;
    private int _enabled, _running, _unsupported;
    private long _mutationSequence, _cooldownUntilUtcTicks;
    public long MutationSequence => Interlocked.Read(ref _mutationSequence);
    private string _status = "已关闭";
    public AutoResetCreditController() : this(new AutoResetCreditStore(), () => DateTimeOffset.UtcNow) { }
    internal AutoResetCreditController(AutoResetCreditStore store, Func<DateTimeOffset> now)
    { _store = store; _now = now; }
    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set
        {
            Interlocked.Exchange(ref _enabled, value ? 1 : 0);
            if (!value) Interlocked.Exchange(ref _unsupported, 0);
            SetStatus(value ? "等待到期前 1 分钟；仅使用服务允许重置的卡" : "已关闭");
        }
    }
    public string StatusText => Volatile.Read(ref _status);
    private void SetStatus(string text) => Volatile.Write(ref _status, text);

    // Past due cards are handled by the current/next normal refresh, never a 100ms wake loop.
    public DateTimeOffset? NextDue(ResetCards? cards, DateTimeOffset now) => !Enabled ||
        Volatile.Read(ref _unsupported) != 0 || cards?.Status != ResetCardDataStatus.Details ? null :
        cards.Cards.Where(card => card.Id is { Length: > 0 } && card.ExpiryKind == ResetCardExpiryKind.Date &&
                card.ExpiresAt is { } at && at > now.AddSeconds(60))
            .Select(card => (DateTimeOffset?)card.ExpiresAt!.Value.AddSeconds(-60)).OrderBy(at => at).FirstOrDefault();

    public Task<JsonElement> ProcessAsync(JsonRpcConnection rpc, JsonElement initialSnapshot,
        TimeSpan requestTimeout, CancellationToken token) => ProcessAsync(initialSnapshot,
            (method, parameters, cancellation) => rpc.RequestAsync(method, parameters, requestTimeout, cancellation), token);

    internal async Task<JsonElement> ProcessAsync(JsonElement initialSnapshot,
        Func<string, object, CancellationToken, Task<JsonElement>> request, CancellationToken token)
    {
        if (!Enabled || Volatile.Read(ref _unsupported) != 0 || _now().UtcDateTime.Ticks < Interlocked.Read(ref _cooldownUntilUtcTicks) || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return initialSnapshot;
        var mutationSent = false;
        try
        {
            var selected = Select(initialSnapshot, _now());
            var account = Account(initialSnapshot);
            if (selected is null)
            {
                // Restore the unresolved-operation warning after restart even if no due card
                // remains. This is a bounded read, never a mutation or a directory scan.
                if (account is not null && _store.ReadPending(account) is { } pendingId &&
                    _store.Read(pendingId) is { State: "pending" })
                    SetStatus("上次使用结果仍未确认，已暂停此账号自动使用；请在 Codex 中核对");
                return initialSnapshot;
            }
            if (account is null)
            { SetStatus("未返回账号标识，无法安全自动使用；请更新 Codex 组件"); return initialSnapshot; }
            using var lease = _store.Acquire();
            var candidates = Candidates(initialSnapshot, _now()).ToArray();
            var identity = AutoResetCreditStore.Identity(account, selected.Id!);
            ResetCreditAttempt? prior = null;
            var pendingIdentity = _store.ReadPending(account);
            if (pendingIdentity is not null)
            {
                var pending = _store.Read(pendingIdentity) ?? throw new InvalidDataException("Missing pending operation.");
                if (pending.State == "pending")
                {
                    selected = candidates.FirstOrDefault(card => AutoResetCreditStore.Identity(account, card.Id!) == pendingIdentity);
                    if (selected is null)
                    { SetStatus("上次使用结果仍未确认，已暂停此账号自动使用；请在 Codex 中核对"); return initialSnapshot; }
                    identity = pendingIdentity;
                    prior = pending;
                }
                else _store.ClearPending(account);
            }
            if (prior is null)
            {
                // Known terminal cards cannot monopolize another card with the same expiry.
                foreach (var candidate in candidates)
                {
                    selected = candidate;
                    identity = AutoResetCreditStore.Identity(account, selected.Id!);
                    prior = _store.Read(identity);
                    if (prior is null || prior.State == "pending") break;
                }
            }
            if (prior is { State: not "pending" }) { SetStatus(OutcomeText(prior.State)); return initialSnapshot; }
            if (prior is not null && prior.RetryNotBeforeMilliseconds > _now().ToUnixTimeMilliseconds())
            { SetStatus("服务限制使用请求，等待冷却；结果待核对"); return initialSnapshot; }
            if (prior is { Sends: >= 2 })
            { SetStatus("使用结果尚未确认，已停止重试；请在 Codex 中查看"); return initialSnapshot; }
            if (prior is not null)
            {
                // Legacy second-only records use a conservative upper bound for the start instant.
                var previousAt = prior.LastAttemptMilliseconds != 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(prior.LastAttemptMilliseconds)
                    : DateTimeOffset.FromUnixTimeSeconds(prior.LastAttempt).AddSeconds(1);
                if (_now() - previousAt < TimeSpan.FromSeconds(10)) return initialSnapshot;
            }

            // Reconcile a pending operation before retry; never reuse a stale account/card snapshot.
            var fresh = await request("account/rateLimits/read", new { }, token).ConfigureAwait(false);
            var current = Candidates(fresh, _now()).FirstOrDefault(card => card.Id == selected.Id);
            if (!Enabled || Account(fresh) != account || current is null || current.Id != selected.Id ||
                current.ExpiresAt != selected.ExpiresAt)
            { SetStatus("账号或重置卡已变化，本次未使用"); return fresh; }
            var expires = current.ExpiresAt!.Value.ToUnixTimeSeconds();
            if (prior is not null && prior.ExpiresAt != expires)
            { SetStatus("重置卡期限已变化，已停止自动使用"); return fresh; }
            token.ThrowIfCancellationRequested();
            var now = _now();
            if (!InWindow(current, now) || !Enabled) return fresh;
            var attempt = new ResetCreditAttempt(1, prior?.Key ?? Guid.NewGuid().ToString("D"),
                expires, now.ToUnixTimeSeconds(), (prior?.Sends ?? 0) + 1, "pending")
            { LastAttemptMilliseconds = now.ToUnixTimeMilliseconds() };
            _store.Save(identity, attempt); // Durable before the first byte of a mutation request.
            _store.SavePending(account, identity);
            token.ThrowIfCancellationRequested();
            if (!Enabled || !InWindow(current, _now())) return fresh;
            var outcome = "pending";
            RpcException? rateLimitedFailure = null;
            try
            {
                mutationSent = true;
                Interlocked.Increment(ref _mutationSequence);
                var response = await request("account/rateLimitResetCredit/consume",
                    new { idempotencyKey = attempt.Key, creditId = current.Id }, token).ConfigureAwait(false);
                outcome = ReadOutcome(response);
            }
            catch (RpcException error) when (error.IsRateLimited)
            {
                rateLimitedFailure = error;
                var until = _now().AddMinutes(5);
                Interlocked.Exchange(ref _cooldownUntilUtcTicks, until.UtcDateTime.Ticks);
                attempt = attempt with { RetryNotBeforeMilliseconds = until.ToUnixTimeMilliseconds() };
            }
            catch (RpcException error) when (error.Code is -32601 or -32600)
            {
                outcome = "unsupported";
                Interlocked.Exchange(ref _unsupported, 1);
            }
            catch (Exception error) when (error is RpcException or TimeoutException or IOException or OperationCanceledException)
            { /* Unknown outcome: retain this same durable key, never manufacture a new attempt. */ }
            // Persist a known terminal response. Even a failed write must not skip the reread.
            var saved = true;
            try
            {
                if (rateLimitedFailure is not null) _store.Save(identity, attempt);
                if (outcome != "pending")
                {
                    _store.Save(identity, attempt with { State = outcome });
                    _store.ClearPending(account);
                }
            }
            catch (Exception error) when (StoreError(error)) { saved = false; }
            SetStatus(saved ? "正在核对使用结果与最新额度" : "使用结果记录失败，已暂停自动使用；请检查本地设置权限");
            if (!saved) Interlocked.Exchange(ref _unsupported, 1);
            // A cancellation/transport failure can make this read fail; the host then marks quota
            // unavailable, and the next session reconciles using the persisted operation key.
            JsonElement reconciled;
            try { reconciled = await request("account/rateLimits/read", new { }, token).ConfigureAwait(false); }
            catch (Exception) when (rateLimitedFailure is not null && !token.IsCancellationRequested)
            {
                SetStatus("服务限制使用请求，暂停 5 分钟；最新额度未确认");
                throw rateLimitedFailure;
            }
            if (rateLimitedFailure is not null)
            {
                SetStatus("服务限制使用请求，暂停 5 分钟；结果待核对");
                throw rateLimitedFailure; // Preserve the existing shared-provider cooldown policy.
            }
            if (saved) SetStatus(OutcomeText(outcome));
            return reconciled;
        }
        catch (Exception error) when (!mutationSent && StoreError(error))
        { SetStatus("无法锁定或保存使用记录，本次未再使用；请检查本地设置权限"); return initialSnapshot; }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private static bool StoreError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        SecurityException or JsonException or ArgumentException or NotSupportedException;
    private static string? Account(JsonElement snapshot) => snapshot.ValueKind == JsonValueKind.Object &&
        snapshot.TryGetProperty("accountId", out var id) && id.ValueKind == JsonValueKind.String &&
        id.GetString() is { Length: > 0 and <= 1024 } value ? value : null;
    private static bool InWindow(ResetCard card, DateTimeOffset now) => card.ExpiresAt is { } at &&
        now >= at.AddSeconds(-60) && now < at;
    private static ResetCard? Select(JsonElement snapshot, DateTimeOffset now) => Candidates(snapshot, now).FirstOrDefault();
    private static IEnumerable<ResetCard> Candidates(JsonElement snapshot, DateTimeOffset now)
    {
        var selection = QuotaParser.ReadSelection(snapshot);
        if (!selection.IsValid || selection.Active is null) return Array.Empty<ResetCard>();
        var cards = ResetCards.Parse(snapshot);
        // Conflicting duplicate rows are not an authoritative identity/expiry pair.
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        if (snapshot.TryGetProperty("rateLimitResetCredits", out var summary) && summary.ValueKind == JsonValueKind.Object &&
            summary.TryGetProperty("credits", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows.EnumerateArray())
                if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.String && id.GetString() is { } value && !seen.Add(value)) duplicates.Add(value);
        }
        return cards.Status != ResetCardDataStatus.Details ? Array.Empty<ResetCard>() : cards.Cards
            .Where(card => card.Id is { Length: > 0 and <= 1024 } && !duplicates.Contains(card.Id) && card.ExpiryKind == ResetCardExpiryKind.Date && InWindow(card, now))
            .OrderBy(card => card.ExpiresAt).ThenBy(card => card.Id, StringComparer.Ordinal);
    }
    private static string ReadOutcome(JsonElement response) => response.ValueKind == JsonValueKind.Object &&
        response.TryGetProperty("outcome", out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is "reset" or "alreadyRedeemed" or "nothingToReset" or "noCredit" ? value.GetString()! : "pending";
    private static string OutcomeText(string state) => state switch
    {
        "reset" => "已自动使用到期重置卡，并重新读取额度",
        "alreadyRedeemed" => "这次操作已使用重置卡，未重复扣卡",
        "nothingToReset" => "服务判断当前无需重置，本卡不再自动重试",
        "noCredit" => "服务未找到可用卡，本卡不再自动重试",
        "unsupported" => "当前 Codex 组件不支持自动使用，已暂停；请更新组件",
        _ => "使用结果尚未确认；将先核对状态，最多使用原请求重试一次"
    };
}
