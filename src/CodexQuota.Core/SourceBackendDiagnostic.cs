using System.Globalization;

namespace CodexQuota.Core;

public enum SourceBackendStage { Discover, Start, Initialize, QuotaRead, Cleanup }
public enum SourceBackendOutcome { Succeeded, Failed, Cancelled, Pending }
public enum SourceBackendLocation { None, CliExplicit, CliPath, CliNpm, DesktopPackageResource, VsCodeExtensionBinary, DesktopExistingRuntime }
public enum SourceBackendComponent { None, CodexExecutable, CodexNpmEntry, DesktopBundledCodex, VsCodeBundledCodex }
public enum SourceBackendIdentityEvidence { Unknown, RegisteredDesktopPackage, RegisteredPackageBytesAndSignature }
public enum SourceBackendProcessEvidence { NotChecked, ObservedDesktopChild }
public enum SourceBackendVersionEvidence { Unknown, InitializeResponse }
public enum SourceBackendExit { NotStarted, Running, Exited, OwnedTreeTerminationRequested, Unknown }
public enum SourceBackendTimeout { None, Session, Request, Cleanup }
[Flags]
public enum SourceBackendExclusion
{
    None = 0, Missing = 1, InvalidPath = 2, UnsupportedLayout = 4, Inaccessible = 8,
    DesktopComponent = 16, VsCodeComponent = 32, WindowsAppsAlias = 64, SourceMismatch = 128,
    UnsupportedPlatform = 256, InvalidSource = 512, ProviderUnavailable = 1024,
    DesktopPackageUnverified = 8192, DesktopRuntimeUnverified = 16384, DesktopSignatureUnverified = 32768,
    DesktopVersionMismatch = 65536, DesktopFileChanged = 131072, DesktopUnsafePath = 262144, DesktopEvidenceLimit = 524288, DesktopPublisherUnverified = 1048576
}

/// <summary>
/// Closed, bounded support trace. There are deliberately no arbitrary string fields:
/// no path, user/account identifiers, environment, server output, or credentials can be logged.
/// Version is only the numeric version reported by initialize, never an assumed binary version.
/// </summary>
public sealed record SourceBackendDiagnostic(
    QuotaDataSource Source,
    SourceBackendStage Stage,
    SourceBackendOutcome Outcome,
    ReadStatus Status,
    long ElapsedMilliseconds = 0,
    SourceBackendLocation Location = SourceBackendLocation.None,
    SourceBackendComponent Component = SourceBackendComponent.None,
    Version? Version = null,
    SourceBackendVersionEvidence VersionEvidence = SourceBackendVersionEvidence.Unknown,
    int Examined = 0,
    int Candidates = 0,
    int Excluded = 0,
    SourceBackendExclusion Exclusions = SourceBackendExclusion.None,
    int? Code = null,
    CliErrorKind ErrorKind = CliErrorKind.None,
    CliStartOperation StartOperation = CliStartOperation.None,
    SourceBackendExit Exit = SourceBackendExit.NotStarted,
    SourceBackendTimeout Timeout = SourceBackendTimeout.None,
    int? ExitCode = null,
    SourceBackendIdentityEvidence IdentityEvidence = SourceBackendIdentityEvidence.Unknown,
    SourceBackendProcessEvidence ProcessEvidence = SourceBackendProcessEvidence.NotChecked)
{
    public DesktopEvidenceDiagnostic? Desktop { get; init; }
    public bool? RootExitConfirmed { get; init; }
    public bool? TreeExitConfirmed { get; init; }
    public bool? PipesCompleted { get; init; }

    public string FormatSafe() => "backend source=" + Label(Source) + " stage=" + Label(Stage) +
        " outcome=" + Label(Outcome) + " status=" + Label(Status) +
        " elapsedMs=" + Number(Math.Min(86_400_000, Math.Max(0, ElapsedMilliseconds))) +
        " location=" + Label(Location) + " component=" + Label(Component) +
        " identityEvidence=" + Label(IdentityEvidence) + " processEvidence=" + Label(ProcessEvidence) +
        " version=" + (Version?.ToString() ?? "unknown") + " versionEvidence=" + Label(VersionEvidence) +
        " examined=" + Count(Examined) + " candidates=" + Count(Candidates) + " excluded=" + Count(Excluded) +
        " exclusions=" + ExclusionLabels(Exclusions) +
        " code=" + (Code is { } code ? Number(code) : "none") + " errorKind=" + Label(ErrorKind) +
        " startOperation=" + Label(StartOperation) + " exit=" + Label(Exit) +
        " exitCode=" + (ExitCode is { } exitCode ? Number(exitCode) : "none") + " timeout=" + Label(Timeout) +
        (Stage == SourceBackendStage.Cleanup ? " rootExitConfirmed=" + Flag(RootExitConfirmed) +
            " treeExitConfirmed=" + Flag(TreeExitConfirmed) + " pipesCompleted=" + Flag(PipesCompleted) : "") +
        (Desktop is null ? "" : " " + Desktop.FormatSafe());

    public string ToSafeLogCode() => FormatSafe();
    private static string Label<T>(T value) where T : struct => Enum.GetName(typeof(T), value) ?? "Unknown";
    private static string Number(long number) => number.ToString(CultureInfo.InvariantCulture);
    private static string Flag(bool? value) => value is null ? "Unknown" : value.Value ? "True" : "False";
    private static string Count(int count) => Number(Math.Min(4096, Math.Max(0, count)));
    private static string ExclusionLabels(SourceBackendExclusion reasons)
    {
        if (reasons == SourceBackendExclusion.None) return "None";
        var labels = new List<string>();
        var known = SourceBackendExclusion.None;
        foreach (SourceBackendExclusion value in Enum.GetValues(typeof(SourceBackendExclusion)))
        {
            known |= value;
            if (value != SourceBackendExclusion.None && (reasons & value) != 0) labels.Add(Label(value));
        }
        if ((reasons & ~known) != 0) labels.Add("Unknown");
        return string.Join(",", labels);
    }
}

internal sealed record SourceBackendDiscovery(CliCandidate? Candidate, int Examined = 0, int Excluded = 0,
    SourceBackendExclusion Exclusions = SourceBackendExclusion.None, int? Code = null)
{
    public DesktopEvidenceDiagnostic? Desktop { get; init; }
}
