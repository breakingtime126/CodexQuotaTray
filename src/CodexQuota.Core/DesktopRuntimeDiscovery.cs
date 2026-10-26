using System.ComponentModel;
using System.Security;
using System.Security.Cryptography;

namespace CodexQuota.Core;

internal sealed record DesktopPackageRegistration(string FullName, string Root);
internal sealed record DesktopImageIdentity(long Length, string Sha256, Version? FileVersion, bool TrustedOpenAiSignature);
internal interface IDesktopImageLease : IDisposable
{
    string Path { get; }
    DesktopImageIdentity Identity { get; }
}
internal interface IDesktopRuntimeEvidence
{
    IEnumerable<DesktopPackageRegistration> RegisteredPackages();
    string LocalApplicationData { get; }
    bool VerifyPackageIdentity(DesktopPackageRegistration package);
    IEnumerable<string> ExistingRuntimePaths(string localApplicationData);
    IDesktopImageLease OpenImage(string path, DesktopImageIdentity? expected = null);
}

/// <summary>
/// Finds only existing, bounded Desktop runtime layouts. Package matching establishes official
/// component identity, not which application created the file or a stable external Desktop API.
/// Never copies a package image, searches arbitrary caches, or attempts to repair permissions.
/// </summary>
internal static class DesktopRuntimeDiscovery
{
    internal const long MaximumImageBytes = 512L * 1024 * 1024;
    internal const int MaximumRuntimeDirectories = 64;
    internal const string StorePublisher = "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B";

    internal static SourceBackendDiscovery Discover(IDesktopRuntimeEvidence evidence)
    {
        var examined = 0;
        var excluded = 0;
        var exclusions = SourceBackendExclusion.None;
        int? exclusionCode = null;
        DesktopEvidenceDiagnostic? exclusionDesktop = null;
        try
        {
            (evidence as IDesktopRuntimeEvidencePhase)?.BeginEvidencePhase();
            // The registration provider is current-user scoped and version ordered. Do not
            // fall back to an older installed package if the current registration is invalid.
            var package = evidence.RegisteredPackages().Take(64).FirstOrDefault();
            if (package is null) return new(null);
            if (!ValidRegistration(package) || !evidence.VerifyPackageIdentity(package))
                throw Reject(SourceBackendExclusion.DesktopPackageUnverified, DesktopEvidenceStep.PackageIdentityValidation,
                    DesktopEvidenceLocation.PackageRegistration);
            var resourcePath = ResourcePath(package);
            examined++;
            using var anchor = evidence.OpenImage(resourcePath);
            ValidateImage(anchor.Identity);
            var anchorIdentity = anchor.Identity;
            var localApplicationData = evidence.LocalApplicationData;
            var runtimePaths = evidence.ExistingRuntimePaths(localApplicationData)
                .Take(MaximumRuntimeDirectories + 1).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var path in runtimePaths)
            {
                examined++;
                if (!IsRuntimePath(localApplicationData, path))
                    throw Reject(SourceBackendExclusion.DesktopUnsafePath);
                try
                {
                    using var runtime = evidence.OpenImage(path, anchorIdentity);
                    ValidateMatch(anchorIdentity, runtime.Identity);
                    return Candidate(path, SourceBackendLocation.DesktopExistingRuntime,
                        SourceBackendIdentityEvidence.RegisteredPackageBytesAndSignature);
                }
                catch (DesktopRuntimeValidationException error)
                {
                    const SourceBackendExclusion staleIdentityReasons = SourceBackendExclusion.DesktopRuntimeUnverified |
                        SourceBackendExclusion.DesktopSignatureUnverified | SourceBackendExclusion.DesktopVersionMismatch |
                        SourceBackendExclusion.DesktopPublisherUnverified;
                    // Only an established identity mismatch can describe a stale sibling.
                    // Native/read failures now carry safe operation labels in this exception
                    // type too; they must still stop immediately, as they did before wrapping.
                    if (error.EvidenceReadFailure || error.Exclusion == SourceBackendExclusion.None ||
                        (error.Exclusion & ~staleIdentityReasons) != 0 ||
                        error.Code == 5 || error.Code == unchecked((int)0x80070005)) throw;
                    // A stale sibling can coexist after an update. It is never launched.
                    excluded++;
                    exclusions |= error.Exclusion;
                    // Keep one failure's code and operation together. A later validation
                    // without a native code must not inherit an earlier candidate's error.
                    exclusionCode = error.Code;
                    exclusionDesktop = error.Desktop;
                }
            }
            // A present but unverifiable runtime must not cause a launch of the inaccessible
            // package image. No candidates are retried after a launch failure either.
            if (runtimePaths.Length != 0)
                return new(null, examined, excluded, exclusions, exclusionCode) { Desktop = exclusionDesktop };
            return Candidate(resourcePath, SourceBackendLocation.DesktopPackageResource,
                SourceBackendIdentityEvidence.RegisteredDesktopPackage);

            SourceBackendDiscovery Candidate(string path, SourceBackendLocation location,
                SourceBackendIdentityEvidence identityEvidence)
            {
                var start = CliLocator.ResolveDesktopExecutable(path);
                var candidate = new CliCandidate(CliSource.Desktop, start)
                {
                    Location = location,
                    IdentityEvidence = identityEvidence,
                    AcquireLaunchLease = () => Revalidate(evidence, package, path, anchorIdentity,
                        location == SourceBackendLocation.DesktopExistingRuntime, localApplicationData)
                };
                return new(candidate, examined, excluded, exclusions);
            }
        }
        catch (Exception error) when (IsExpectedFailure(error))
        {
            return new(null, examined, excluded + 1, exclusions | FailureReason(error), FailureCode(error))
            { Desktop = (error as DesktopRuntimeValidationException)?.Desktop };
        }
    }

    private static IDisposable Revalidate(IDesktopRuntimeEvidence evidence, DesktopPackageRegistration package,
        string path, DesktopImageIdentity expected, bool runtime, string localApplicationData)
    {
        IDesktopImageLease? anchor = null;
        IDesktopImageLease? image = null;
        try
        {
            (evidence as IDesktopRuntimeEvidencePhase)?.BeginEvidencePhase();
            // Recheck the *current* registration, not merely whether the old version remains
            // installed. A concurrent Desktop update requires a new discovery next time.
            var current = evidence.RegisteredPackages().Take(64).FirstOrDefault();
            if (current is null || current != package || !ValidRegistration(current) ||
                !evidence.VerifyPackageIdentity(current))
                throw Reject(SourceBackendExclusion.DesktopFileChanged, DesktopEvidenceStep.LaunchRevalidation,
                    DesktopEvidenceLocation.PackageRegistration);
            anchor = evidence.OpenImage(ResourcePath(package), expected);
            ValidateMatch(expected, anchor.Identity);
            if (!runtime)
            {
                ConfirmCurrentRegistration();
                return anchor;
            }
            if (!string.Equals(evidence.LocalApplicationData, localApplicationData, StringComparison.OrdinalIgnoreCase) ||
                !IsRuntimePath(localApplicationData, path))
                throw Reject(SourceBackendExclusion.DesktopUnsafePath);
            image = evidence.OpenImage(path, anchor.Identity);
            ValidateMatch(anchor.Identity, image.Identity);
            ConfirmCurrentRegistration();
            return new PairLease(anchor, image);

            void ConfirmCurrentRegistration()
            {
                // Hashing and local trust checks can overlap an AppX update. Recheck after
                // expensive validation while both images remain pinned. Registration and
                // CreateProcess are separate OS operations, so this is not an atomic lock.
                var latest = evidence.RegisteredPackages().Take(64).FirstOrDefault();
                if (latest != package || !evidence.VerifyPackageIdentity(package))
                    throw Reject(SourceBackendExclusion.DesktopFileChanged, DesktopEvidenceStep.LaunchRevalidation,
                    DesktopEvidenceLocation.PackageRegistration);
            }
        }
        catch (Exception error) when (IsExpectedFailure(error))
        {
            image?.Dispose();
            anchor?.Dispose();
            throw new DesktopRuntimeValidationException(FailureReason(error) | SourceBackendExclusion.DesktopFileChanged,
                FailureCode(error), (error as DesktopRuntimeValidationException)?.Desktop,
                (error as DesktopRuntimeValidationException)?.EvidenceReadFailure ?? true);
        }
    }

    internal static bool ValidRegistration(DesktopPackageRegistration package)
    {
        var fields = package.FullName.Split('_');
        return fields.Length == 5 && fields[0] == "OpenAI.Codex" && fields[2] == "x64" && fields[3] == "" &&
            fields[4] == "2p2nqsd0c76g0" && Version.TryParse(fields[1], out var version) &&
            version.Revision >= 0 && IsCanonicalLocalPath(package.Root);
    }

    internal static bool ManifestMatches(DesktopPackageRegistration package, string? name, string? publisher,
        string? version, string? architecture)
    {
        if (!ValidRegistration(package)) return false;
        var fields = package.FullName.Split('_');
        return name == fields[0] && publisher == StorePublisher && version == fields[1] && architecture == fields[2];
    }

    internal static string ResourcePath(DesktopPackageRegistration package) =>
        Path.Combine(package.Root, "app", "resources", "codex.exe");

    internal static bool IsRuntimePath(string localApplicationData, string path)
    {
        if (!IsCanonicalLocalPath(localApplicationData) || !IsCanonicalLocalPath(path)) return false;
        var root = Path.Combine(localApplicationData, "OpenAI", "Codex", "bin");
        if (string.Equals(path, Path.Combine(root, "codex.exe"), StringComparison.OrdinalIgnoreCase)) return true;
        var parent = Path.GetDirectoryName(path);
        if (parent is null || !string.Equals(Path.GetDirectoryName(parent), root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(path), "codex.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var directory = Path.GetFileName(parent);
        return directory.Length == 16 && directory.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    internal static bool IsCanonicalLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || path.Length > 32760) return false;
        // Reject remote/device paths before any file probe, including remote authentication.
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return false;
        if (Path.DirectorySeparatorChar == '\\' &&
            (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
             path.IndexOf(':', 2) >= 0 || path.IndexOf('/') >= 0)) return false;
        try
        {
            if (!Path.IsPathRooted(path)) return false;
            if (path.Split(new[] { '\\', '/' }).Any(part => part is "." or ".." || part.EndsWith(" ", StringComparison.Ordinal) ||
                    part.EndsWith(".", StringComparison.Ordinal))) return false;
            return string.Equals(path.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        { return false; }
    }

    private static void ValidateImage(DesktopImageIdentity identity)
    {
        if (identity.Length <= 0 || identity.Length > MaximumImageBytes || identity.Sha256.Length != 64 ||
            identity.Sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw Reject(SourceBackendExclusion.DesktopRuntimeUnverified);
        if (!identity.TrustedOpenAiSignature) throw Reject(SourceBackendExclusion.DesktopSignatureUnverified);
    }

    private static void ValidateMatch(DesktopImageIdentity anchor, DesktopImageIdentity runtime)
    {
        ValidateImage(runtime);
        if (anchor.Length != runtime.Length || !string.Equals(anchor.Sha256, runtime.Sha256, StringComparison.Ordinal))
            throw Reject(SourceBackendExclusion.DesktopRuntimeUnverified);
        // The Desktop MSIX version and bundled CLI version are different version domains.
        // Compare PE metadata only with the package resource, never the MSIX version.
        if (anchor.FileVersion != runtime.FileVersion)
            throw Reject(SourceBackendExclusion.DesktopVersionMismatch);
    }

    internal static bool IsExpectedFailure(Exception error) => error is IOException or UnauthorizedAccessException or
        SecurityException or ArgumentException or NotSupportedException or Win32Exception or DllNotFoundException or
        EntryPointNotFoundException or CryptographicException or System.Xml.XmlException;
    internal static SourceBackendExclusion FailureReason(Exception error) => error is DesktopRuntimeValidationException validation
        ? validation.Exclusion : error is FileNotFoundException or DirectoryNotFoundException ? SourceBackendExclusion.Missing :
        error is DllNotFoundException or EntryPointNotFoundException ? SourceBackendExclusion.ProviderUnavailable :
        error is ArgumentException or NotSupportedException ? SourceBackendExclusion.InvalidPath :
        error is CryptographicException ? SourceBackendExclusion.DesktopSignatureUnverified : SourceBackendExclusion.Inaccessible;
    internal static int? FailureCode(Exception error) => error is DesktopRuntimeValidationException validation ? validation.Code :
        error is Win32Exception native ? native.NativeErrorCode : error.HResult;
    internal static DesktopRuntimeValidationException Reject(SourceBackendExclusion reason,
        DesktopEvidenceStep step = DesktopEvidenceStep.None, DesktopEvidenceLocation location = DesktopEvidenceLocation.None) =>
        new(reason, desktop: step == DesktopEvidenceStep.None ? null : new(step, location));

    private sealed class PairLease(IDisposable anchor, IDisposable image) : IDisposable
    {
        public void Dispose() { try { image.Dispose(); } finally { anchor.Dispose(); } }
    }
}

internal sealed class DesktopRuntimeValidationException(SourceBackendExclusion exclusion, int? code = null,
    DesktopEvidenceDiagnostic? desktop = null, bool evidenceReadFailure = false)
    : IOException("Desktop runtime verification did not establish a safe current component.")
{
    public SourceBackendExclusion Exclusion { get; } = exclusion;
    public int? Code { get; } = code;
    public DesktopEvidenceDiagnostic? Desktop { get; } = desktop;
    internal bool EvidenceReadFailure { get; } = evidenceReadFailure;
}
