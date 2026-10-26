using System.ComponentModel;
using System.Globalization;

namespace CodexQuota.Core;

public enum SourceReadOutcome { InProgress, Succeeded, Failed, Cancelled, UnexpectedFailure, CleanupUnconfirmed }
public enum SourceReadExceptionKind
{
    AccessDenied, Io, InvalidOperation, InvalidArgument, PlatformNotSupported, NotSupported,
    Timeout, Cancelled, Win32, Other
}

/// <summary>Exception classification never retains the exception, its text, stack, data or inner exception.</summary>
public sealed record SourceReadFailureDiagnostic(SourceReadExceptionKind Kind, int Code)
{
    public static SourceReadFailureDiagnostic FromException(Exception error) => new(error switch
    {
        UnauthorizedAccessException => SourceReadExceptionKind.AccessDenied,
        IOException => SourceReadExceptionKind.Io,
        InvalidOperationException => SourceReadExceptionKind.InvalidOperation,
        ArgumentException => SourceReadExceptionKind.InvalidArgument,
        PlatformNotSupportedException => SourceReadExceptionKind.PlatformNotSupported,
        NotSupportedException => SourceReadExceptionKind.NotSupported,
        TimeoutException => SourceReadExceptionKind.Timeout,
        OperationCanceledException => SourceReadExceptionKind.Cancelled,
        Win32Exception => SourceReadExceptionKind.Win32,
        _ => SourceReadExceptionKind.Other
    }, error is Win32Exception windows ? windows.NativeErrorCode : error.HResult);

    public string FormatSafe() => "exceptionKind=" + SourceReadDiagnosticText.Label(Kind) +
        " exceptionCode=" + Code.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One read's immutable evidence. Collection instances cannot be mutated by consumers.</summary>
public sealed record SourceReadRoundDiagnostic
{
    public long Generation { get; internal init; }
    public long Round { get; internal init; }
    public QuotaDataSource SelectedSource { get; internal init; }
    public QuotaDataSource? LastAttemptedSource { get; internal init; }
    public QuotaDataSource? SuccessfulSource { get; internal init; }
    public DateTimeOffset StartedAt { get; internal init; }
    public DateTimeOffset? FinishedAt { get; internal init; }
    public ReadStatus Status { get; internal init; } = ReadStatus.Connecting;
    public SourceReadOutcome Outcome { get; internal init; } = SourceReadOutcome.InProgress;
    public SourceReadFailureDiagnostic? Failure { get; internal init; }
    public IReadOnlyList<AutoSourceDiagnostic> Policy { get; internal init; } = Array.AsReadOnly(Array.Empty<AutoSourceDiagnostic>());
    public IReadOnlyList<SourceBackendDiagnostic> Backend { get; internal init; } = Array.AsReadOnly(Array.Empty<SourceBackendDiagnostic>());
    public IReadOnlyList<CliDiagnostic> Protocol { get; internal init; } = Array.AsReadOnly(Array.Empty<CliDiagnostic>());

    public string ToSafeLogCode() => "readRound generation=" + Generation.ToString(CultureInfo.InvariantCulture) +
        " round=" + Round.ToString(CultureInfo.InvariantCulture) + " selectedSource=" + SourceReadDiagnosticText.Label(SelectedSource) +
        " lastAttemptedSource=" + (LastAttemptedSource is { } attempted ? SourceReadDiagnosticText.Label(attempted) : "None") +
        " successfulSource=" + (SuccessfulSource is { } successful ? SourceReadDiagnosticText.Label(successful) : "None") +
        " outcome=" + SourceReadDiagnosticText.Label(Outcome) + " status=" + SourceReadDiagnosticText.Label(Status) +
        (Failure is null ? "" : " " + Failure.FormatSafe());
}

/// <summary>Cleanup safety can originate in a superseded selection, without becoming its successor's result.</summary>
public sealed record SourceReadGateBlockDiagnostic(long Generation, QuotaDataSource SelectedSource);

public sealed record SourceReadDiagnosticSnapshot
{
    public long Generation { get; internal init; }
    public QuotaDataSource SelectedSource { get; internal init; }
    public bool SwitchPending { get; internal init; }
    public SourceReadGateBlockDiagnostic? GateBlock { get; internal init; }
    public bool ReadsBlocked => GateBlock is not null;
    public SourceReadRoundDiagnostic? Current { get; internal init; }
    public SourceReadRoundDiagnostic? LastCompleted { get; internal init; }
}

/// <summary>
/// Publishes all connection evidence with one reference replacement. Gate validation always precedes
/// the snapshot lock. A completed/superseded lease cannot write another round or selection's evidence.
/// Read this once when opening a diagnostics window, rather than reading independent mutable fields.
/// </summary>
public sealed class SourceReadDiagnostics
{
    public const int MaximumBackendEvents = 32;
    public const int MaximumProtocolEvents = 8;
    private readonly SourceRequestGate _gate;
    private readonly object _sync = new();
    private SourceReadLease? _active, _latestRead;
    private long _nextRound;
    private volatile SourceReadDiagnosticSnapshot _snapshot;

    public SourceReadDiagnostics(SourceRequestGate gate)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _snapshot = new() { SelectedSource = gate.Source };
    }

    public SourceReadDiagnosticSnapshot Capture() => _snapshot;

    public void BeginSelection(SourceSwitch change)
    {
        if (!ReferenceEquals(change.Owner, _gate)) return;
        lock (_sync)
        {
            if (change.Generation <= _snapshot.Generation) return;
            _active = null;
            // Cancellation has been requested, not completed. Never invent a completed old round.
            _snapshot = _snapshot with
            {
                Generation = change.Generation, SelectedSource = change.Source,
                SwitchPending = true, Current = null
            };
        }
    }

    public void CompleteSelection(SourceSwitch change)
    {
        if (!ReferenceEquals(change.Owner, _gate) || _gate.SwitchPending) return;
        lock (_sync)
        {
            if (change.Generation == _snapshot.Generation)
                _snapshot = _snapshot with { SwitchPending = false };
        }
    }

    public bool Begin(SourceReadLease request, DateTimeOffset now) => Apply(request, () =>
    {
        if (_active is not null || request.Generation != _snapshot.Generation || _snapshot.ReadsBlocked) return false;
        _active = _latestRead = request;
        _snapshot = _snapshot with
        {
            SwitchPending = false,
            Current = new()
            {
                Generation = request.Generation, Round = ++_nextRound,
                SelectedSource = request.Source, StartedAt = now
            }
        };
        return true;
    });

    public bool Record(SourceReadLease request, AutoSourceDiagnostic diagnostic) => Update(request, current => current with
    {
        LastAttemptedSource = diagnostic.Stage == AutoSourceStage.AttemptStarted ? diagnostic.Source : current.LastAttemptedSource,
        Policy = Append(current.Policy, diagnostic, AutoQuotaSourceClient.MaximumPolicyEvents)
    });

    public bool Record(SourceReadLease request, SourceBackendDiagnostic diagnostic) => Update(request, current => current with
    { Backend = Append(current.Backend, diagnostic, MaximumBackendEvents) });

    public bool Record(SourceReadLease request, CliDiagnostic diagnostic) => Update(request, current => current with
    { Protocol = Append(current.Protocol, diagnostic, MaximumProtocolEvents) });

    public bool Complete(SourceReadLease request, ReadStatus status, SourceReadOutcome outcome,
        DateTimeOffset now, QuotaDataSource? successfulSource = null, SourceReadFailureDiagnostic? failure = null)
    {
        if (outcome == SourceReadOutcome.InProgress) throw new ArgumentOutOfRangeException(nameof(outcome));
        return Apply(request, () =>
        {
            if (!IsCurrent(request)) return false;
            var completed = _snapshot.Current! with
            {
                Status = status, Outcome = outcome, FinishedAt = now, Failure = failure,
                SuccessfulSource = outcome == SourceReadOutcome.Succeeded && status == ReadStatus.Ready ? successfulSource : null
            };
            _active = null;
            _snapshot = _snapshot with { Current = null, LastCompleted = completed };
            return true;
        });
    }

    public bool BlockFurtherReads(SourceReadLease failedRequest, DateTimeOffset now)
    {
        // This global safety latch is intentionally not generation-gated. No old trace or result is
        // copied into the new generation. Only this recorder's latest actual read can trip the latch.
        lock (_sync) { if (!ReferenceEquals(_latestRead, failedRequest)) return false; }
        _gate.BlockFurtherReads();
        var completed = false;
        var currentGeneration = _gate.TryApply(failedRequest, () =>
        {
            lock (_sync)
            {
                var snapshot = _snapshot with { GateBlock = new(failedRequest.Generation, failedRequest.Source) };
                if (IsCurrent(failedRequest))
                {
                    snapshot = snapshot with
                    {
                        Current = null,
                        LastCompleted = snapshot.Current! with
                        {
                            Status = ReadStatus.Unavailable, Outcome = SourceReadOutcome.CleanupUnconfirmed,
                            FinishedAt = now, SuccessfulSource = null
                        }
                    };
                    _active = null;
                    completed = true;
                }
                _snapshot = snapshot; // Gate failure and this round's terminal are one publication.
            }
        });
        if (!currentGeneration)
        {
            lock (_sync)
                _snapshot = _snapshot with { GateBlock = new(failedRequest.Generation, failedRequest.Source) };
        }
        return completed;
    }

    private bool Update(SourceReadLease request, Func<SourceReadRoundDiagnostic, SourceReadRoundDiagnostic> update) => Apply(request, () =>
    {
        if (!IsCurrent(request)) return false;
        _snapshot = _snapshot with { Current = update(_snapshot.Current!) };
        return true;
    });

    private bool IsCurrent(SourceReadLease request) => ReferenceEquals(_active, request) &&
        _snapshot.Current is { } current && current.Generation == request.Generation && current.SelectedSource == request.Source;

    private bool Apply(SourceReadLease request, Func<bool> update)
    {
        var applied = false;
        _gate.TryApply(request, () => { lock (_sync) applied = update(); });
        return applied;
    }

    private static IReadOnlyList<T> Append<T>(IReadOnlyList<T> previous, T value, int limit) =>
        Array.AsReadOnly(previous.Skip(Math.Max(0, previous.Count - limit + 1)).Concat(new[] { value }).ToArray());
}

/// <summary>Formats only a captured immutable snapshot; does not query a router, gate or UI status.</summary>
public static class SourceReadDiagnosticText
{
    public static string Format(SourceReadDiagnosticSnapshot snapshot)
    {
        var text = "所选数据来源：" + snapshot.SelectedSource.DisplayName() +
            "\r\nselectionGeneration=" + snapshot.Generation.ToString(CultureInfo.InvariantCulture) +
            "\r\nreadGateBlocked=" + (snapshot.ReadsBlocked ? "True" : "False") + "\r\n";
        if (snapshot.GateBlock is { } blocked)
            text += "查询清理未确认，已停止后续查询，请退出后重开。\r\n" +
                "gateBlockOriginGeneration=" + blocked.Generation.ToString(CultureInfo.InvariantCulture) +
                " gateBlockOriginSource=" + Label(blocked.SelectedSource) + "\r\n";
        if (snapshot.SwitchPending) text += "正在等待上一来源的查询清理结束。\r\n";
        text += "\r\n当前轮：\r\n" + (snapshot.Current is { } current ? FormatRound(current) :
            "当前没有正在执行的查询。\r\n") + "\r\n最近完成轮：\r\n" +
            (snapshot.LastCompleted is { } completed ? FormatRound(completed) : "尚无已完成的查询。\r\n");
        return text;
    }

    private static string FormatRound(SourceReadRoundDiagnostic round) =>
        round.ToSafeLogCode() + "\r\n" +
        "本轮所选来源：" + round.SelectedSource.DisplayName() + "\r\n" +
        "startedAtUtc=" + round.StartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) +
        " finishedAtUtc=" + (round.FinishedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "None") + "\r\n" +
        "本轮最近尝试来源：" + (round.LastAttemptedSource?.DisplayName() ?? "尚未开始来源尝试") + "\r\n" +
        "本轮实际成功来源：" + (round.SuccessfulSource?.DisplayName() ?? "本轮尚无成功来源") + "\r\n" +
        "本轮状态：" + (round.Outcome == SourceReadOutcome.InProgress ? "正在读取…" :
            round.Outcome == SourceReadOutcome.Cancelled ? "本轮已取消。" :
            round.Outcome == SourceReadOutcome.CleanupUnconfirmed ? "本轮查询清理未确认。" :
            round.Outcome == SourceReadOutcome.UnexpectedFailure ? "查询处理异常，本轮已结束；具体阶段见已记录证据。" :
            round.Status == ReadStatus.Ready ? "读取成功。" : QuotaState.StatusText(round.Status)) + "\r\n" +
        "本轮来源策略与尝试顺序：\r\n" + (round.Policy.Count == 0 ? "本轮未记录策略事件。" :
            string.Join("\r\n", round.Policy.Select(item => item.FormatSafe()))) + "\r\n" +
        "本轮分阶段诊断：\r\n" + (round.Backend.Count == 0 ? "本轮未记录后端阶段；不能据此认定具体失败阶段。" :
            string.Join("\r\n", round.Backend.Select(item => item.FormatSafe()))) + "\r\n" +
        "本轮协议状态：\r\n" + (round.Protocol.Count == 0 ? "本轮未记录协议事件；不能据此认定已完成握手。" :
            string.Join("\r\n", round.Protocol.Select(item =>
                (item.Explanation is { } explanation ? explanation + "\r\n" : "") + item.ToSafeLogCode()))) + "\r\n";

    internal static string Label<T>(T value) where T : struct => Enum.GetName(typeof(T), value) ?? "Unknown";
}
