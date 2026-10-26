using System.Diagnostics;
using System.Security.Cryptography;

namespace CodexQuota.Core;

internal interface IDesktopRuntimeEvidencePhase
{
    void BeginEvidencePhase();
}

/// <summary>Cooperative limits. A synchronous OS call is never abandoned or run in the background.</summary>
internal sealed class DesktopEvidenceBudget
{
    internal const long MaximumHashBytes = 2L * 1024 * 1024 * 1024;
    internal static readonly TimeSpan MaximumHashTime = TimeSpan.FromSeconds(12);
    internal static readonly TimeSpan MaximumVerificationTime = TimeSpan.FromSeconds(40);
    private readonly Func<TimeSpan> _clock;
    private readonly CancellationToken _cancellationToken;
    private TimeSpan? _started;
    private TimeSpan _phaseStarted;
    private TimeSpan _hashTime;
    private long _reservedBytes;
    private long _hashBytes;
    private long? _imageBytes;
    internal DesktopHashProvider HashProvider { get; set; }

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
        _hashTime = TimeSpan.Zero;
        _reservedBytes = _hashBytes = 0;
        _imageBytes = null;
    }

    internal void Check(DesktopEvidenceStep step, DesktopEvidenceLocation location)
    {
        var now = _clock();
        if (_started is null) { _started = now; _phaseStarted = now; }
        if (_cancellationToken.IsCancellationRequested || now - _started.Value >= MaximumVerificationTime)
            throw Limit(DesktopEvidenceLimit.VerificationTime, step, location);
    }

    internal void ReserveImage(long length, DesktopEvidenceLocation location)
    {
        Check(DesktopEvidenceStep.ImageHash, location);
        _imageBytes = length;
        if (length > DesktopRuntimeDiscovery.MaximumImageBytes)
            throw Limit(DesktopEvidenceLimit.ImageBytes, DesktopEvidenceStep.ImageHash, location);
        if (length <= 0) throw DesktopRuntimeDiscovery.Reject(SourceBackendExclusion.DesktopRuntimeUnverified,
            DesktopEvidenceStep.ImageRead, location);
        if (length > MaximumHashBytes - _reservedBytes)
            throw Limit(DesktopEvidenceLimit.TotalHashBytes, DesktopEvidenceStep.ImageHash, location);
        _reservedBytes += length;
        CheckHash(location);
    }

    internal T HashOperation<T>(DesktopEvidenceLocation location, Func<T> operation)
    {
        CheckHash(location);
        var started = _clock();
        try
        {
            T result;
            try { result = DesktopEvidenceOperation.Run(DesktopEvidenceStep.ImageHash, location, operation); }
            finally { _hashTime += _clock() - started; }
            CheckHash(location);
            return result;
        }
        catch (DesktopRuntimeValidationException error) { throw Decorate(error); }
    }

    internal void AddHashBytes(int count) => _hashBytes += count;

    private void CheckHash(DesktopEvidenceLocation location)
    {
        Check(DesktopEvidenceStep.ImageHash, location);
        if (_hashTime >= MaximumHashTime)
            throw Limit(DesktopEvidenceLimit.HashTime, DesktopEvidenceStep.ImageHash, location);
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
            ImageBytes = _imageBytes, HashBytes = _hashBytes, HashMilliseconds = (long)_hashTime.TotalMilliseconds,
            PhaseMilliseconds = (long)(now - _phaseStarted).TotalMilliseconds,
            VerificationMilliseconds = _started is { } started ? (long)(now - started).TotalMilliseconds : 0,
            HashProvider = HashProvider
        };
    }
}

internal static class DesktopImageHasher
{
    internal const int BufferBytes = 1024 * 1024;
    internal static DesktopHashProvider Provider =>
#if NETFRAMEWORK
        DesktopHashProvider.WindowsCng;
#else
        DesktopHashProvider.PlatformDefault;
#endif

    internal static HashAlgorithm CreateHash() =>
#if NETFRAMEWORK
        // SHA256.Create defaults to SHA256Managed on .NET Framework. Select Windows CNG
        // explicitly for the Desktop evidence path; trust verification remains unchanged.
        new SHA256Cng();
#else
        SHA256.Create();
#endif

    // The injected factory and stream exercise the same read/transform/finalize loop used
    // for pinned production images. No content, trust result, or identity is cached.
    internal static string Compute(Stream stream, DesktopEvidenceBudget budget, DesktopEvidenceLocation location,
        Func<HashAlgorithm>? hashFactory = null)
    {
        budget.HashProvider = hashFactory is null ? Provider : DesktopHashProvider.None;
        budget.ReserveImage(stream.Length, location);
        using var hash = DesktopEvidenceOperation.Run(DesktopEvidenceStep.HashProviderCreate, location,
            hashFactory ?? CreateHash, budget);
        var buffer = new byte[BufferBytes];
        while (true)
        {
            var count = budget.HashOperation(location, () =>
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                budget.AddHashBytes(read);
                return read;
            });
            if (count == 0) break;
            budget.HashOperation(location, () => hash.TransformBlock(buffer, 0, count, buffer, 0));
        }
        budget.HashOperation(location, () => hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0));
        return BitConverter.ToString(hash.Hash!).Replace("-", "").ToLowerInvariant();
    }
}
