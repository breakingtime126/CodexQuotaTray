using System.Diagnostics;

namespace CodexQuota.Core;

internal interface IDesktopRuntimeEvidencePhase
{
    void BeginEvidencePhase();
}

/// <summary>Cooperative limits. A synchronous OS call is never abandoned or run in the background.</summary>
internal sealed class DesktopEvidenceBudget
{
    internal static readonly TimeSpan MaximumVerificationTime = TimeSpan.FromSeconds(40);
    private readonly Func<TimeSpan> _clock;
    private readonly CancellationToken _cancellationToken;
    private TimeSpan? _started;
    private TimeSpan _phaseStarted;
    private long? _imageBytes;

    internal DesktopEvidenceBudget(Func<TimeSpan>? clock = null, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        _clock = clock ?? (() => watch.Elapsed);
        _cancellationToken = cancellationToken;
    }

    internal void BeginPhase()
    {
        // Registration is also repeated after expensive launch validation. It must never
        // restart the overall deadline, or let an expired query begin another OS call.
        Check(DesktopEvidenceStep.PackageFamilyQuery, DesktopEvidenceLocation.PackageRegistration);
        _phaseStarted = _clock();
        _imageBytes = null;
    }

    internal void Check(DesktopEvidenceStep step, DesktopEvidenceLocation location)
    {
        var now = _clock();
        if (_started is null) { _started = now; _phaseStarted = now; }
        if (_cancellationToken.IsCancellationRequested || now - _started.Value >= MaximumVerificationTime)
            throw Limit(DesktopEvidenceLimit.VerificationTime, step, location);
    }

    internal void CheckImageSize(long length, DesktopEvidenceLocation location)
    {
        Check(DesktopEvidenceStep.ImageRead, location);
        _imageBytes = length;
        if (length > DesktopRuntimeDiscovery.MaximumImageBytes)
            throw Limit(DesktopEvidenceLimit.ImageBytes, DesktopEvidenceStep.ImageRead, location);
        if (length <= 0) throw DesktopRuntimeDiscovery.Reject(SourceBackendExclusion.DesktopRuntimeUnverified,
            DesktopEvidenceStep.ImageRead, location);
    }

    internal DesktopRuntimeValidationException Limit(DesktopEvidenceLimit limit, DesktopEvidenceStep step,
        DesktopEvidenceLocation location) => new(SourceBackendExclusion.DesktopEvidenceLimit,
            desktop: Snapshot(new(step, location) { Limit = limit }));

    internal DesktopRuntimeValidationException Decorate(DesktopRuntimeValidationException error) =>
        error.Desktop is null ? error : new(error.Exclusion, error.Code, Snapshot(error.Desktop), error.EvidenceReadFailure);

    private DesktopEvidenceDiagnostic Snapshot(DesktopEvidenceDiagnostic detail)
    {
        var now = _clock();
        return detail with
        {
            // Preserve the public diagnostic shape without claiming any explicit image hashing.
            ImageBytes = _imageBytes, HashBytes = 0, HashMilliseconds = 0,
            PhaseMilliseconds = (long)(now - _phaseStarted).TotalMilliseconds,
            VerificationMilliseconds = _started is { } started ? (long)(now - started).TotalMilliseconds : 0,
            HashProvider = DesktopHashProvider.None
        };
    }
}
