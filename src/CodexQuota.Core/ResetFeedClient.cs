using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;

namespace CodexQuota.Core;

/// <summary>Reads the optional PowerShell JSON result. No account or quota inputs, and no direct HTTP production path.</summary>
public sealed class ResetFeedClient : IDisposable
{
    public static readonly Uri Endpoint = new("https://raw.githubusercontent.com/breakingtime126/CodexQuotaTray/reset-data/status.json");
    public static readonly Uri UpstreamEndpoint = new("https://codex-resets.com/api/v1/status");
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(12);
    private readonly Func<string?, CancellationToken, Task<HttpResponseMessage>> _read;
    private readonly IDisposable _owner;
    private readonly TimeSpan _timeout;
    private readonly ResetFeedTransport _transport;
    private readonly bool _requireMirror;
    private readonly bool _requireUpstreamFreshness, _disableCache;
    private readonly ResetFeedClient? _upstream, _fallback;
    private readonly Func<DateTimeOffset> _utcNow = () => DateTimeOffset.UtcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ResetFeedSnapshot? _cache;
    private EntityTagHeaderValue? _etag;
    private int _disposed;

    public ResetFeedClient() : this(CreateSource(ResetFeedSource.Upstream), CreateSource(ResetFeedSource.Mirror)) { }

    // Each source gets its own bounded attempt. Only a fully parsed, fresh upstream result wins.
    internal ResetFeedClient(ResetFeedClient upstream, ResetFeedClient fallback)
    {
        _upstream = upstream;
        _fallback = fallback;
        _read = null!;
        _owner = upstream;
        _timeout = DefaultTimeout;
    }

    private static ResetFeedClient CreateSource(ResetFeedSource source)
    {
        var runner = new PowerShellResetFeedRunner(AppDomain.CurrentDomain.BaseDirectory);
        return new ResetFeedClient(new PowerShellResetFeedHandler(runner.FetchAsync, owner: runner, source: source),
            transport: ResetFeedTransport.PowerShell7Json, requireMirror: source == ResetFeedSource.Mirror,
            requireUpstreamFreshness: source == ResetFeedSource.Upstream, disableCache: true,
            endpoint: source == ResetFeedSource.Upstream ? UpstreamEndpoint : Endpoint);
    }

    /// <summary>Internal synthetic-response seam only. No built-in direct network handler exists.</summary>
    internal ResetFeedClient(HttpMessageHandler handler, TimeSpan? timeout = null,
        ResetFeedTransport transport = ResetFeedTransport.Injected, bool requireMirror = false, Func<DateTimeOffset>? utcNow = null,
        bool requireUpstreamFreshness = false, bool disableCache = false, Uri? endpoint = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        _transport = transport;
        _requireMirror = requireMirror;
        _requireUpstreamFreshness = requireUpstreamFreshness;
        _disableCache = disableCache;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var invoker = new HttpMessageInvoker(handler, disposeHandler: true);
        _owner = invoker;
        _read = async (etag, token) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint ?? Endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, MaxAge = TimeSpan.Zero };
            request.Headers.Pragma.ParseAdd("no-cache");
            if (etag is not null) request.Headers.IfNoneMatch.ParseAdd(etag);
            return await invoker.SendAsync(request, token).ConfigureAwait(false);
        };
    }

    public async Task<ResetFeedReadResult> FetchAsync(CancellationToken cancellationToken = default)
    {
        if (_upstream is not null)
        {
            using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            total.CancelAfter(TimeSpan.FromSeconds(24));
            var enteredChain = false;
            ResetFeedReadResult Stopped() => new(cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0
                ? ResetFeedReadStatus.Cancelled : ResetFeedReadStatus.Timeout, Diagnostic:
                new(ResetFeedStage.Waiting, cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0
                    ? ResetFeedFailure.Cancelled : ResetFeedFailure.Timeout, null, 0, Transport: ResetFeedTransport.PowerShell7Json));
            try
            {
                if (Volatile.Read(ref _disposed) != 0) return Stopped();
                await _gate.WaitAsync(total.Token).ConfigureAwait(false);
                enteredChain = true;
                if (Volatile.Read(ref _disposed) != 0) return Stopped();
                var primary = await _upstream.FetchAsync(total.Token).ConfigureAwait(false);
                if (total.IsCancellationRequested) return Stopped();
                if (primary.IsSuccess || primary.Status == ResetFeedReadStatus.Cancelled) return primary;
                var fallback = await _fallback!.FetchAsync(total.Token).ConfigureAwait(false);
                return total.IsCancellationRequested ? Stopped() : fallback;
            }
            catch (OperationCanceledException) { return Stopped(); }
            finally { if (enteredChain) _gate.Release(); }
        }
        var clock = Stopwatch.StartNew();
        var stage = ResetFeedStage.Waiting;
        int? httpStatus = null;
        ResetFeedReadResult Finish(ResetFeedReadStatus status, ResetFeedSnapshot? snapshot = null,
            TimeSpan? retry = null, ResetFeedFailure failure = ResetFeedFailure.None, int? code = null,
            WebExceptionStatus? webStatus = null, ResetFeedFileFailure local = ResetFeedFileFailure.None,
            int? childExit = null, ResetFeedExceptionType exceptionType = ResetFeedExceptionType.None) =>
            new(status, snapshot, retry, new(stage, failure, httpStatus, clock.ElapsedMilliseconds, code, webStatus,
                _transport, local, childExit, exceptionType));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        var entered = false;
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return Finish(ResetFeedReadStatus.Cancelled, failure: ResetFeedFailure.Cancelled);
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            stage = ResetFeedStage.Request;
            var pendingResponse = _read(_disableCache ? null : _etag?.ToString(), deadline.Token);
            try { await AsyncCompatibility.WaitAsync(pendingResponse, deadline.Token).ConfigureAwait(false); }
            catch
            {
                // A test/custom handler can ignore cancellation. Dispose any eventual response.
                _ = pendingResponse.ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
                    else _ = task.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            using var response = await pendingResponse.ConfigureAwait(false);
            stage = ResetFeedStage.Headers;
            httpStatus = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.NotModified)
                return !_disableCache && _cache is not null && _etag is not null &&
                    (!_requireMirror || _cache.UpstreamSuccessAtUtc is { } success && ResetFeedMirror.IsFresh(success, _utcNow())) &&
                    (response.Headers.ETag is not { } responseTag || responseTag.Tag == _etag.Tag)
                    ? Finish(ResetFeedReadStatus.NotModified, _cache)
                    : Finish(ResetFeedReadStatus.InvalidResponse, failure: ResetFeedFailure.InvalidCache);
            if ((int)response.StatusCode == 429)
                return Finish(ResetFeedReadStatus.RateLimited, retry: RetryDelay(response.Headers.RetryAfter),
                    failure: ResetFeedFailure.HttpStatus);
            if (response.StatusCode != HttpStatusCode.OK)
                return Finish(ResetFeedReadStatus.HttpError, failure: ResetFeedFailure.HttpStatus);
            if (response.Content is null)
                return Finish(ResetFeedReadStatus.InvalidResponse, failure: ResetFeedFailure.MissingBody);
            if (response.Content.Headers.ContentLength > ResetFeedParser.MaximumBodyBytes)
                return Finish(ResetFeedReadStatus.InvalidResponse, failure: ResetFeedFailure.BodyTooLarge);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
                return Finish(ResetFeedReadStatus.InvalidResponse, failure: ResetFeedFailure.ContentType);
            stage = ResetFeedStage.Body;
            var pendingStream = response.Content.ReadAsStreamAsync();
            try { await AsyncCompatibility.WaitAsync(pendingStream, deadline.Token).ConfigureAwait(false); }
            catch
            {
                _ = pendingStream.ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
                    else _ = task.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            using var stream = await pendingStream.ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                // Bound even a stream implementation that ignores its cancellation argument.
                var pending = stream.ReadAsync(buffer, 0, buffer.Length, deadline.Token);
                await AsyncCompatibility.WaitAsync(pending, deadline.Token).ConfigureAwait(false);
                var count = await pending.ConfigureAwait(false);
                if (count == 0) break;
                if (body.Length + count > ResetFeedParser.MaximumBodyBytes)
                    return Finish(ResetFeedReadStatus.InvalidResponse, failure: ResetFeedFailure.BodyTooLarge);
                body.Write(buffer, 0, count);
            }
            stage = ResetFeedStage.Parse;
            ResetFeedSnapshot? snapshot;
            if (!(_requireMirror ? ResetFeedMirror.TryParse(body.ToArray(), _utcNow(), out snapshot) :
                ResetFeedParser.TryParse(body.ToArray(), out snapshot)))
                return Finish(ResetFeedReadStatus.InvalidResponse, failure: ResetFeedFailure.InvalidSchema);
            if (_requireUpstreamFreshness)
            {
                if (!ResetFeedMirror.IsFresh(snapshot!.GeneratedAt, _utcNow()))
                    return Finish(ResetFeedReadStatus.InvalidResponse, failure: ResetFeedFailure.InvalidSchema);
                // Direct API age is its own generated_at, not the mirror's upstream success timestamp.
                snapshot = snapshot with { DirectGeneratedAtUtc = snapshot.GeneratedAt };
            }
            _cache = snapshot;
            var etag = response.Headers.ETag;
            _etag = !_disableCache && etag is not null && etag.ToString().Length <= 256 ? etag : null;
            stage = ResetFeedStage.Complete;
            return Finish(ResetFeedReadStatus.Success, snapshot);
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? Finish(ResetFeedReadStatus.Cancelled, failure: ResetFeedFailure.Cancelled)
                : Finish(ResetFeedReadStatus.Timeout, failure: ResetFeedFailure.Timeout);
        }
        catch (ResetFeedFileException error)
        {
            httpStatus = error.HttpStatus;
            var invalid = error.Failure is ResetFeedFileFailure.OutputInvalid or ResetFeedFileFailure.OutputStale or
                ResetFeedFileFailure.OutputTooLarge or ResetFeedFileFailure.ParseFailed or ResetFeedFileFailure.ContentType;
            return Finish(error.Failure == ResetFeedFileFailure.RequestTimeout ? ResetFeedReadStatus.Timeout :
                invalid ? ResetFeedReadStatus.InvalidResponse : ResetFeedReadStatus.NetworkError,
                failure: error.Failure == ResetFeedFileFailure.RequestTimeout ? ResetFeedFailure.Timeout :
                    error.Failure == ResetFeedFileFailure.OutputTooLarge ? ResetFeedFailure.BodyTooLarge :
                    error.Failure == ResetFeedFileFailure.ContentType ? ResetFeedFailure.ContentType :
                    invalid ? ResetFeedFailure.InvalidSchema : ResetFeedFailure.Transport,
                code: error.NativeCode, local: error.Failure, childExit: error.ChildExitCode, exceptionType: error.ExceptionType);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException)
        {
            return Finish(ResetFeedReadStatus.NetworkError, failure: ResetFeedDiagnostic.Classify(error),
                code: ResetFeedDiagnostic.NumericCode(error), webStatus: ResetFeedDiagnostic.WebStatusOf(error));
        }
        finally
        {
            if (entered) _gate.Release();
        }
    }

    private static TimeSpan? RetryDelay(RetryConditionHeaderValue? retry)
    {
        var delay = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
        return delay is null ? null : delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) { _owner.Dispose(); _fallback?.Dispose(); }
        // Do not dispose the semaphore while a cancelled request may still unwind.
    }
}
