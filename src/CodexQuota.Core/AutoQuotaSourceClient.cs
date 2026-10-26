using System.Globalization;

namespace CodexQuota.Core;

public enum AutoSourceStage
{
    AttemptStarted, AttemptFailed, RetryScheduled, Selected, Exhausted, RateLimitCooldown, Cancelled,
    CleanupUnconfirmed
}

/// <summary>Allowlisted policy evidence only, with no paths, server text or account information.</summary>
public sealed record AutoSourceDiagnostic(QuotaDataSource SelectedSource, QuotaDataSource? Source,
    AutoSourceStage Stage, ReadStatus Status, int Order = 0, int Attempt = 0)
{
    public string FormatSafe() => "sourcePolicy selectedSource=" + Label(SelectedSource) +
        " source=" + (Source is { } source ? Label(source) : "None") +
        " stage=" + Label(Stage) + " status=" + Label(Status) +
        " order=" + Math.Min(3, Math.Max(0, Order)).ToString(CultureInfo.InvariantCulture) +
        " attempt=" + Math.Min(2, Math.Max(0, Attempt)).ToString(CultureInfo.InvariantCulture);

    public string ToSafeLogCode() => FormatSafe();
    private static string Label<T>(T value) where T : struct => Enum.GetName(typeof(T), value) ?? "Unknown";
}

/// <summary>
/// One selection generation's bounded, sequential policy. The backend delegate must not complete
/// until its owned process has finished cleanup. Instantiate a new router on every user selection;
/// never transfer an old router's winner or cooldown into a new source generation.
/// </summary>
public sealed class AutoQuotaSourceClient
{
    public const int MaximumPolicyEvents = 16;
    public static readonly TimeSpan ActiveSourceRetryDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(5);
    private static readonly QuotaDataSource[] DiscoveryOrder =
        { QuotaDataSource.Cli, QuotaDataSource.VsCode, QuotaDataSource.Desktop };
    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private QuotaDataSource? _activeSource;
    private QuotaDataSource? _lastAttemptedSource;
    private DateTimeOffset _rateLimitCooldownUntil = DateTimeOffset.MinValue;
    private bool _cleanupBlocked;
    private int _reading;

    public AutoQuotaSourceClient(QuotaDataSource selectedSource = QuotaDataSource.Auto)
        : this(selectedSource, () => DateTimeOffset.UtcNow, (delay, token) => Task.Delay(delay, token)) { }

    internal AutoQuotaSourceClient(QuotaDataSource selectedSource, Func<DateTimeOffset> now,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        if (!QuotaDataSourceNames.IsValid(selectedSource)) throw new ArgumentOutOfRangeException(nameof(selectedSource));
        SelectedSource = selectedSource;
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    /// <summary>Persist this user-selected policy, never ActiveSource.</summary>
    public QuotaDataSource SelectedSource { get; }

    /// <summary>The last source that actually returned usable quota in this generation, if any.</summary>
    public QuotaDataSource? ActiveSource { get { lock (_sync) return _activeSource; } }
    public QuotaDataSource? LastAttemptedSource { get { lock (_sync) return _lastAttemptedSource; } }
    public DateTimeOffset RateLimitCooldownUntil { get { lock (_sync) return _rateLimitCooldownUntil; } }
    /// <summary>Prior process/pipe cleanup was not confirmed; this router can never start another read.</summary>
    public bool CleanupBlocked { get { lock (_sync) return _cleanupBlocked; } }

    /// <summary>
    /// Initial Auto search is CLI, VSCode, Desktop. Later reads start at the last winner, retry it
    /// at most once for a transient failure, then inspect remaining sources once in initial order.
    /// Concrete selections always make one attempt. Rate limits stop the entire shared-provider
    /// search, including manual/hover refreshes during its cooldown. Ordinary repeated-cycle
    /// backoff remains the caller's RefreshPolicy responsibility.
    /// </summary>
    public async Task<QuotaRead> ReadAsync(
        Func<QuotaDataSource, CancellationToken, Task<QuotaRead>> readSource,
        CancellationToken token, Action<AutoSourceDiagnostic>? trace = null)
    {
        if (readSource is null) throw new ArgumentNullException(nameof(readSource));
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
            throw new InvalidOperationException("A source policy read is already running.");
        var events = 0;
        var order = 0;
        var attempt = 0;
        QuotaDataSource? current = null;
        void Publish(AutoSourceStage stage, ReadStatus status)
        {
            if (++events > MaximumPolicyEvents) return;
            try { trace?.Invoke(new(SelectedSource, current, stage, status, order, attempt)); }
            catch (Exception) { /* Optional observers must never change the read policy. */ }
        }
        try
        {
            if (CleanupBlocked)
            {
                current = LastAttemptedSource;
                throw new BackendCleanupUnconfirmedException();
            }
            token.ThrowIfCancellationRequested();
            if (_now() < RateLimitCooldownUntil)
            {
                Publish(AutoSourceStage.RateLimitCooldown, ReadStatus.RateLimited);
                token.ThrowIfCancellationRequested();
                return new(ReadStatus.RateLimited);
            }
            var winner = ActiveSource;
            var sources = SelectedSource != QuotaDataSource.Auto ? new[] { SelectedSource } :
                winner is { } active ? new[] { active }.Concat(DiscoveryOrder.Where(source => source != active)).ToArray() :
                DiscoveryOrder;
            var last = new QuotaRead(ReadStatus.Unavailable);
            foreach (var source in sources)
            {
                current = source;
                ++order;
                var maximumAttempts = SelectedSource == QuotaDataSource.Auto && source == winner ? 2 : 1;
                for (attempt = 1; attempt <= maximumAttempts; ++attempt)
                {
                    token.ThrowIfCancellationRequested();
                    lock (_sync) _lastAttemptedSource = source;
                    Publish(AutoSourceStage.AttemptStarted, ReadStatus.Connecting);
                    token.ThrowIfCancellationRequested();
                    var result = await readSource(source, token).ConfigureAwait(false);
                    // A backend that ignores cancellation must never promote a late result or
                    // continue the search after its selection was superseded.
                    token.ThrowIfCancellationRequested();
                    if (IsQuotaSuccess(result))
                    {
                        lock (_sync)
                        {
                            token.ThrowIfCancellationRequested();
                            _activeSource = source;
                            _rateLimitCooldownUntil = DateTimeOffset.MinValue;
                        }
                        Publish(AutoSourceStage.Selected, ReadStatus.Ready);
                        return result;
                    }
                    // A success-shaped response without usable quota is not an Auto winner.
                    last = result.Status is ReadStatus.Ready or ReadStatus.Connecting
                        ? result with { Status = ReadStatus.ProtocolError } : result;
                    Publish(AutoSourceStage.AttemptFailed, last.Status);
                    token.ThrowIfCancellationRequested();
                    if (last.Status == ReadStatus.RateLimited)
                    {
                        lock (_sync) _rateLimitCooldownUntil = _now() + RateLimitCooldown;
                        Publish(AutoSourceStage.RateLimitCooldown, last.Status);
                        return last;
                    }
                    if (attempt >= maximumAttempts || !IsTransient(last.Status)) break;
                    Publish(AutoSourceStage.RetryScheduled, last.Status);
                    token.ThrowIfCancellationRequested();
                    await _delay(ActiveSourceRetryDelay, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
            }
            Publish(AutoSourceStage.Exhausted, last.Status);
            token.ThrowIfCancellationRequested();
            return last;
        }
        catch (BackendCleanupUnconfirmedException)
        {
            // Cancellation does not establish process termination. This failure takes priority
            // over cancellation and must also latch in the host across source generations.
            lock (_sync) _cleanupBlocked = true;
            Publish(AutoSourceStage.CleanupUnconfirmed, ReadStatus.Unavailable);
            throw;
        }
        catch (OperationCanceledException)
        {
            Publish(AutoSourceStage.Cancelled, ReadStatus.Unavailable);
            throw;
        }
        finally { Interlocked.Exchange(ref _reading, 0); }
    }

    private static bool IsTransient(ReadStatus status) => status is ReadStatus.Timeout or ReadStatus.Unavailable;
    private static bool IsQuotaSuccess(QuotaRead result) => result.Status == ReadStatus.Ready &&
        result.Quota is { } quota && !double.IsNaN(quota.RemainingPercent) && !double.IsInfinity(quota.RemainingPercent) &&
        quota.RemainingPercent >= 0 && quota.RemainingPercent <= 100;
}
