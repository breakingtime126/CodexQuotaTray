using System.Net.Http;

namespace CodexQuota.Core;

/// <summary>
/// Explicit file transport adapter, not an HTTP fallback. The existing client retains its deadline,
/// cache validation and API parser. The injected runner performs exactly one owned local invocation.
/// </summary>
public sealed class PowerShellResetFeedHandler : HttpMessageHandler
{
    private readonly Func<ResetFeedFileRequest, CancellationToken, Task<byte[]>> _fetch;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly IDisposable? _owner;
    private int _inFlight;
    private readonly ResetFeedSource _source;

    public PowerShellResetFeedHandler(Func<ResetFeedFileRequest, CancellationToken, Task<byte[]>> fetch,
        Func<DateTimeOffset>? utcNow = null, IDisposable? owner = null, ResetFeedSource source = ResetFeedSource.Mirror)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _owner = owner;
        if (source is not (ResetFeedSource.Mirror or ResetFeedSource.Upstream)) throw new ArgumentOutOfRangeException(nameof(source));
        _source = source;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method != HttpMethod.Get || request.RequestUri != (_source == ResetFeedSource.Upstream ? ResetFeedClient.UpstreamEndpoint : ResetFeedClient.Endpoint) || request.Content is not null ||
            request.Headers.Authorization is not null || request.Headers.Contains("Proxy-Authorization") ||
            request.Headers.IfNoneMatch.Count > 1)
            throw new ResetFeedFileException(ResetFeedFileFailure.OutputInvalid);
        return await ReadAsync(request.Headers.IfNoneMatch.SingleOrDefault()?.ToString(), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<HttpResponseMessage> ReadAsync(string? etag, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            throw new ResetFeedFileException(ResetFeedFileFailure.CleanupPending);
        try
        {
            if (etag?.Length > 256) throw new ResetFeedFileException(ResetFeedFileFailure.OutputInvalid);
            var invocation = new ResetFeedFileRequest(Guid.NewGuid().ToString("D"), _utcNow(), etag) { Source = _source };
            var result = await _fetch(invocation, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return ResetFeedFileProtocol.Parse(result, invocation, _utcNow());
        }
        finally { Interlocked.Exchange(ref _inFlight, 0); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _owner?.Dispose();
        base.Dispose(disposing);
    }
}
