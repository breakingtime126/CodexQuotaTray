using System.Globalization;

namespace CodexQuota.Core;

// Closed labels only. These describe the actual failed operation, never a guessed cause.
public enum DesktopEvidenceStep
{
    None, PackageFamilyQuery, PackagePathQuery, PackageOriginQuery, PackageIdentityValidation,
    LocalPathValidation, VolumeTypeQuery, RootOpen, RootAttributes, RootCanonicalPath,
    TargetOpen, TargetAttributes, TargetCanonicalPath, OpenReadStream, ManifestRead,
    ImageRead, ImageHash, SignatureVerify, VersionRead, DirectoryEnumeration, LaunchRevalidation, HashProviderCreate
}
public enum DesktopEvidenceLocation
{
    None, PackageRegistration, PackageManifest, PackageImage, LocalVolumeRoot,
    RuntimeRoot, RuntimeDirectory, RuntimeImage
}

public enum DesktopEvidenceLimit { None, ImageBytes, TotalHashBytes, HashTime, VerificationTime, DirectoryCount }
public enum DesktopHashProvider { None, WindowsCng, PlatformDefault }

/// <summary>No paths, account data, exception messages, or other arbitrary strings.</summary>
public sealed record DesktopEvidenceDiagnostic(DesktopEvidenceStep Step, DesktopEvidenceLocation Location,
    int? NativeCode = null, int? NtStatus = null)
{
    public DesktopEvidenceLimit Limit { get; init; }
    public DesktopHashProvider HashProvider { get; init; }
    public long? ImageBytes { get; init; }
    public long? HashBytes { get; init; }
    public long? HashMilliseconds { get; init; }
    public long? PhaseMilliseconds { get; init; }
    public long? VerificationMilliseconds { get; init; }
    public string FormatSafe() => "desktopStep=" + Label(Step) + " desktopLocation=" + Label(Location) +
        " nativeCode=" + (NativeCode?.ToString(CultureInfo.InvariantCulture) ?? "none") +
        " ntStatus=" + (NtStatus is { } status ? "0x" + unchecked((uint)status).ToString("X8", CultureInfo.InvariantCulture) : "none") +
        " desktopLimit=" + Label(Limit) + " hashProvider=" + Label(HashProvider) +
        " imageBytes=" + Number(ImageBytes) + " hashBytes=" + Number(HashBytes) +
        " hashMs=" + Number(HashMilliseconds) + " phaseMs=" + Number(PhaseMilliseconds) +
        " verificationMs=" + Number(VerificationMilliseconds);
    // Clamp all optional numeric diagnostics, including values supplied by a fixture.
    private static string Number(long? value) => value is { } number
        ? Math.Max(0, Math.Min(number, 999999999999L)).ToString(CultureInfo.InvariantCulture) : "none";
    private static string Label<T>(T value) where T : struct => Enum.GetName(typeof(T), value) ?? "Unknown";
}

internal static class DesktopEvidenceOperation
{
    internal static T Run<T>(DesktopEvidenceStep step, DesktopEvidenceLocation location, Func<T> action,
        DesktopEvidenceBudget? budget = null)
    {
        try
        {
            budget?.Check(step, location);
            var result = action();
            try { budget?.Check(step, location); }
            catch
            {
                // A completed native open can return after the cooperative deadline.
                // Its resource has not reached the caller's ownership scope yet.
                if (result is IDisposable disposable) disposable.Dispose();
                throw;
            }
            return result;
        }
        catch (Exception error) when (DesktopRuntimeDiscovery.IsExpectedFailure(error))
        {
            // Preserve a more specific nested API failure. In particular, a failed manifest
            // handle open must never be relabelled ManifestRead or PackageIdentityValidation.
            if (error is DesktopRuntimeValidationException { Desktop: not null } validationError)
                throw budget?.Decorate(validationError) ?? validationError;
            DesktopRuntimeValidationException failure;
            if (error is DesktopReadOnlyFileLease.NativeOpenException nativeOpen)
                failure = NativeFailure(step, location, nativeOpen.NativeErrorCode, nativeOpen.NtStatus);
            else if (error is System.ComponentModel.Win32Exception native)
                failure = NativeFailure(step, location, native.NativeErrorCode);
            else failure = new DesktopRuntimeValidationException(DesktopRuntimeDiscovery.FailureReason(error),
                DesktopRuntimeDiscovery.FailureCode(error), new(step, location, NativeCode(error)),
                error is not DesktopRuntimeValidationException validation || validation.EvidenceReadFailure);
            throw budget?.Decorate(failure) ?? failure;
        }
    }
    internal static void Run(DesktopEvidenceStep step, DesktopEvidenceLocation location, Action action,
        DesktopEvidenceBudget? budget = null) => Run(step, location, () => { action(); return true; }, budget);

    private static int? NativeCode(Exception error) => error is System.ComponentModel.Win32Exception native
        ? native.NativeErrorCode : error is DesktopRuntimeValidationException validation ? validation.Code : null;

    internal static DesktopRuntimeValidationException NativeFailure(DesktopEvidenceStep step,
        DesktopEvidenceLocation location, int code, int? ntStatus = null)
    {
        var reason = code is 2 or 3 ? SourceBackendExclusion.Missing :
            code is 4390 or 4393 or 4395 or 1920 or 681 || ntStatus == unchecked((int)0xC000050B)
                ? SourceBackendExclusion.DesktopUnsafePath : SourceBackendExclusion.Inaccessible;
        return new(reason, code, new(step, location, code, ntStatus), evidenceReadFailure: true);
    }
}
