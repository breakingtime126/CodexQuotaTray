namespace CodexQuota.Core;

public static class CodexQuotaProtocol
{
    public static async Task<QuotaRead> ReadAsync(JsonRpcConnection rpc, TimeSpan requestTimeout, CancellationToken token)
        => await ReadAsync(rpc, requestTimeout, token, null).ConfigureAwait(false);

    internal static async Task<QuotaRead> ReadAsync(JsonRpcConnection rpc, TimeSpan requestTimeout, CancellationToken token,
        Action<CliStage, Version?>? progress)
    {
        progress?.Invoke(CliStage.Initialize, null);
        var initialize = await rpc.RequestAsync("initialize", new
        {
            clientInfo = new { name = "codex_quota_tray", title = "Codex Quota Tray", version = typeof(CodexQuotaProtocol).Assembly.GetName().Version?.ToString() ?? "unknown" }
        }, requestTimeout, token).ConfigureAwait(false);
        var version = CliDiagnostic.ReadVersion(initialize);
        progress?.Invoke(CliStage.Initialize, version);
        await rpc.NotifyAsync("initialized", new { }, token).ConfigureAwait(false);
        // Quota-only RPC surface. The official backend owns its login and may refresh/persist
        // that managed login; this is not an authentication-storage read-only guarantee.
        // The quota snapshot itself owns the plan; an absent plan remains unknown.
        progress?.Invoke(CliStage.RateLimitsRead, version);
        var result = await rpc.RequestAsync("account/rateLimits/read", new { }, requestTimeout, token).ConfigureAwait(false);
        var selection = QuotaParser.ReadSelection(result);
        if (!selection.IsValid) return new(ReadStatus.ProtocolError, Selection: selection);
        // Detailed reset credits are included by default. Do not opt out of details and do
        // not call the separate consume method: this tool only reads quota and expiry dates.
        return new(selection.Active is null ? ReadStatus.NoQuotaWindow : ReadStatus.Ready, selection.Active, selection,
            ResetCards.Parse(result));
    }
}
