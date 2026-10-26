using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text.Json;

namespace CodexQuota.Core;

public sealed class CodexQuotaClient
{
    private readonly QuotaDataSource _source;
    private readonly Func<CancellationToken, SourceBackendDiscovery> _discover;
    private readonly Action<CliDiagnostic>? _diagnostic;
    private readonly Action<SourceBackendDiagnostic>? _trace;
    private readonly TimeSpan _sessionTimeout;
    private readonly Func<ProcessStartInfo, IOwnedQuotaProcess> _processFactory;
    private readonly Action<BackendCleanupProgress>? _cleanupProgress;
    private readonly CancellationToken _cleanupStop;
    private readonly BackendCleanupOptions _cleanupOptions;

    public CodexQuotaClient(string? cliPath = null, Action<CliDiagnostic>? diagnostic = null)
        : this(QuotaDataSource.Cli, cliPath, diagnostic) { }

    public CodexQuotaClient(QuotaDataSource source, string? cliPath = null,
        Action<CliDiagnostic>? diagnostic = null, Action<SourceBackendDiagnostic>? trace = null,
        Action<BackendCleanupProgress>? cleanupProgress = null, CancellationToken cleanupStop = default)
        : this(source, token => CliLocator.Discover(source, cliPath, token), diagnostic, trace,
            cleanupProgress: cleanupProgress, cleanupStop: cleanupStop) { }

    internal CodexQuotaClient(Func<IEnumerable<CliCandidate>> candidates, Action<CliDiagnostic>? diagnostic = null,
        TimeSpan? sessionTimeout = null)
        : this(QuotaDataSource.Cli, candidates, diagnostic, null, sessionTimeout) { }

    internal CodexQuotaClient(QuotaDataSource source, Func<IEnumerable<CliCandidate>> candidates,
        Action<CliDiagnostic>? diagnostic = null, Action<SourceBackendDiagnostic>? trace = null,
        TimeSpan? sessionTimeout = null)
        : this(source, () =>
        {
            // Never enumerate a second candidate, even if the first fails to start.
            var first = candidates().FirstOrDefault();
            return new SourceBackendDiscovery(first, first is null ? 0 : 1);
        }, diagnostic, trace, sessionTimeout) { }

    internal CodexQuotaClient(QuotaDataSource source, Func<SourceBackendDiscovery> discover,
        Action<CliDiagnostic>? diagnostic, Action<SourceBackendDiagnostic>? trace,
        TimeSpan? sessionTimeout = null)
        : this(source, _ => discover(), diagnostic, trace, sessionTimeout) { }

    internal CodexQuotaClient(QuotaDataSource source, Func<CancellationToken, SourceBackendDiscovery> discover,
        Action<CliDiagnostic>? diagnostic, Action<SourceBackendDiagnostic>? trace,
        TimeSpan? sessionTimeout = null, Func<ProcessStartInfo, IOwnedQuotaProcess>? processFactory = null,
        Action<BackendCleanupProgress>? cleanupProgress = null, CancellationToken cleanupStop = default,
        BackendCleanupOptions? cleanupOptions = null)
    {
        CliLocator.ValidateSource(source);
        _source = source;
        _discover = discover;
        _diagnostic = diagnostic;
        _trace = trace;
        _sessionTimeout = sessionTimeout ?? TimeSpan.FromSeconds(40);
        _processFactory = processFactory ?? (start => new OwnedQuotaProcess(start));
        _cleanupProgress = cleanupProgress;
        _cleanupStop = cleanupStop;
        _cleanupOptions = cleanupOptions ?? BackendCleanupOptions.Default;
    }

    // Discovery and process creation use synchronous OS APIs. Dispatch the entire lifetime
    // off the caller (including WinForms), but return the real lifetime task: cancellation
    // must not abandon a blocked discovery/start and let a new source overlap its cleanup.
    public Task<QuotaRead> ReadAsync(CancellationToken token) =>
        Task.Run(() => ReadCoreAsync(token), token);

    private async Task<QuotaRead> ReadCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_sessionTimeout);
        var watch = Stopwatch.StartNew();
        SourceBackendDiscovery discovery;
        try { discovery = _discover(deadline.Token); }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or SecurityException)
        { discovery = new(null, Excluded: 1, Exclusions: SourceBackendExclusion.Inaccessible, Code: error.HResult); }
        token.ThrowIfCancellationRequested();
        var candidate = discovery.Candidate;
        var mismatch = candidate is not null && !MatchesSource(candidate.Source);
        if (candidate is null || mismatch || deadline.IsCancellationRequested)
        {
            var ambiguous = (discovery.Exclusions & (SourceBackendExclusion.DesktopComponent |
                SourceBackendExclusion.VsCodeComponent | SourceBackendExclusion.WindowsAppsAlias)) != 0;
            var desktopUnverified = (discovery.Exclusions & (SourceBackendExclusion.DesktopPackageUnverified |
                SourceBackendExclusion.DesktopRuntimeUnverified | SourceBackendExclusion.DesktopSignatureUnverified |
                SourceBackendExclusion.DesktopVersionMismatch | SourceBackendExclusion.DesktopFileChanged |
                SourceBackendExclusion.DesktopUnsafePath | SourceBackendExclusion.DesktopEvidenceLimit |
                SourceBackendExclusion.DesktopPublisherUnverified)) != 0;
            var errorKind = (discovery.Exclusions & SourceBackendExclusion.DesktopEvidenceLimit) != 0 ? CliErrorKind.DesktopEvidenceLimit :
                desktopUnverified ? CliErrorKind.DesktopRuntimeUnverified : mismatch || (discovery.Exclusions & SourceBackendExclusion.SourceMismatch) != 0
                ? CliErrorKind.SourceMismatch : ambiguous ? CliErrorKind.SourceAmbiguous : CliErrorKind.None;
            var discoveryFailed = discovery.Code is not null || (discovery.Exclusions &
                (SourceBackendExclusion.Inaccessible | SourceBackendExclusion.ProviderUnavailable |
                 SourceBackendExclusion.InvalidPath | SourceBackendExclusion.UnsupportedPlatform)) != 0;
            var status = deadline.IsCancellationRequested ? ReadStatus.Timeout :
                errorKind != CliErrorKind.None || discoveryFailed ? ReadStatus.Unavailable : ReadStatus.CliMissing;
            Publish(new(CliSource.None, null, CliStage.Discovery, status, discovery.Code, errorKind)
            { Desktop = discovery.Desktop });
            PublishTrace(new(_source, SourceBackendStage.Discover, SourceBackendOutcome.Failed, status,
                watch.ElapsedMilliseconds, candidate?.Location ?? SourceBackendLocation.None,
                candidate?.Component ?? SourceBackendComponent.None,
                Examined: discovery.Examined, Candidates: candidate is null ? 0 : 1,
                Excluded: discovery.Excluded + (mismatch ? 1 : 0),
                Exclusions: discovery.Exclusions | (mismatch ? SourceBackendExclusion.SourceMismatch : SourceBackendExclusion.None),
                Code: discovery.Code, ErrorKind: errorKind,
                Timeout: status == ReadStatus.Timeout ? SourceBackendTimeout.Session : SourceBackendTimeout.None,
                IdentityEvidence: candidate?.IdentityEvidence ?? SourceBackendIdentityEvidence.Unknown)
            { Desktop = discovery.Desktop });
            return new(status);
        }
        PublishTrace(new(_source, SourceBackendStage.Discover, SourceBackendOutcome.Succeeded, ReadStatus.Connecting,
            watch.ElapsedMilliseconds, candidate.Location, candidate.Component,
            Examined: discovery.Examined, Candidates: 1, Excluded: discovery.Excluded, Exclusions: discovery.Exclusions,
            IdentityEvidence: candidate.IdentityEvidence));
        return await ReadOnceAsync(candidate, token, deadline.Token).ConfigureAwait(false);
    }

    private bool MatchesSource(CliSource source) => _source switch
    {
        QuotaDataSource.Cli => source is CliSource.Explicit or CliSource.Path or CliSource.Npm,
        QuotaDataSource.Desktop => source == CliSource.Desktop,
        QuotaDataSource.VsCode => source == CliSource.VsCode,
        _ => false
    };

    // Retained for source compatibility with older test callers. There is no fallback policy.
    internal static bool CanTryNext(ReadStatus status) => false;

    private async Task<QuotaRead> ReadOnceAsync(CliCandidate candidate, CancellationToken token, CancellationToken sessionToken)
    {
        var process = _processFactory(candidate.StartInfo);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        Task stderr = Task.CompletedTask;
        JsonRpcConnection? rpc = null;
        var stage = CliStage.Start;
        var stageWatch = Stopwatch.StartNew();
        var started = false;
        var validatingCandidate = false;
        var validationExclusions = SourceBackendExclusion.None;
        DesktopEvidenceDiagnostic? desktopFailure = null;
        IDisposable? launchLease = null;
        Version? version = null;
        ReadStatus finalStatus = ReadStatus.Connecting;
        SourceBackendExit exit = SourceBackendExit.NotStarted;
        int? exitCode = null;
        QuotaRead? completedRead = null;
        DateTimeOffset? readAt = null;
        BackendCleanupEvidence? cleanupEvidence = null;
        CliStartOperation StartOperation()
        {
            if (validatingCandidate) return CliStartOperation.ValidateDesktopRuntime;
            return process.StartOperation;
        }
        void ObserveExit()
        {
            started |= process.HasOwnedProcess;
            if (!started) return;
            try
            {
                var observed = process.ObserveExit();
                if (observed.RootExited) { exitCode = observed.ExitCode; exit = SourceBackendExit.Exited; }
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
            { exit = SourceBackendExit.Unknown; }
        }
        void Trace(SourceBackendStage current, SourceBackendOutcome outcome, ReadStatus status,
            int? code = null, CliErrorKind kind = CliErrorKind.None, SourceBackendTimeout timeout = SourceBackendTimeout.None)
        {
            PublishTrace(new(_source, current, outcome, status, stageWatch.ElapsedMilliseconds,
                candidate.Location, candidate.Component, version,
                version is null ? SourceBackendVersionEvidence.Unknown : SourceBackendVersionEvidence.InitializeResponse,
                Candidates: 1, Code: code, ErrorKind: kind,
                StartOperation: current == SourceBackendStage.Start ? StartOperation() : CliStartOperation.None,
                Exit: exit, Timeout: timeout, ExitCode: exitCode, Exclusions: validationExclusions,
                IdentityEvidence: candidate.IdentityEvidence)
            {
                Desktop = desktopFailure,
                RootExitConfirmed = current == SourceBackendStage.Cleanup ? cleanupEvidence?.RootExited : null,
                TreeExitConfirmed = current == SourceBackendStage.Cleanup ? cleanupEvidence?.TreeExited : null,
                PipesCompleted = current == SourceBackendStage.Cleanup ? cleanupEvidence?.PipesCompleted : null
            });
        }
        SourceBackendStage CurrentStage() => stage switch
        {
            CliStage.Start => SourceBackendStage.Start,
            CliStage.Initialize => SourceBackendStage.Initialize,
            _ => SourceBackendStage.QuotaRead
        };
        QuotaRead Complete(QuotaRead result, int? code = null, CliErrorKind errorKind = CliErrorKind.None,
            SourceBackendTimeout timeout = SourceBackendTimeout.None)
        {
            finalStatus = result.Status;
            if (result.Status == ReadStatus.Ready) { completedRead = result; readAt = DateTimeOffset.UtcNow; }
            ObserveExit();
            Publish(new(candidate.Source, version, stage, result.Status, code, errorKind,
                stage == CliStage.Start ? StartOperation() : CliStartOperation.None, 1, 1) { Desktop = desktopFailure });
            Trace(CurrentStage(), result.Status is ReadStatus.Ready or ReadStatus.NoQuotaWindow
                ? SourceBackendOutcome.Succeeded : SourceBackendOutcome.Failed, result.Status, code, errorKind, timeout);
            return result;
        }
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (candidate.AcquireLaunchLease is { } acquireLease)
            {
                validatingCandidate = true;
                launchLease = acquireLease();
                validatingCandidate = false;
                deadline.Token.ThrowIfCancellationRequested();
            }
            if (!process.Start()) return Complete(new(ReadStatus.Unavailable));
            started = true;
            exit = SourceBackendExit.Running;
            Trace(SourceBackendStage.Start, SourceBackendOutcome.Succeeded, ReadStatus.Connecting);
            // Keep the actual stderr read task, not a cancellation wrapper that can finish
            // before synchronous Framework pipe I/O. Terminating our Job supplies EOF.
            stderr = DrainAsync(process.StandardError, CancellationToken.None);
            rpc = new JsonRpcConnection(process.StandardOutput, process.StandardInput);
            var result = await CodexQuotaProtocol.ReadAsync(rpc, TimeSpan.FromSeconds(12), deadline.Token,
                (currentStage, currentVersion) =>
                {
                    version = currentVersion ?? version;
                    if (currentStage == stage) return;
                    if (stage == CliStage.Initialize)
                        Trace(SourceBackendStage.Initialize, SourceBackendOutcome.Succeeded, ReadStatus.Connecting);
                    stage = currentStage;
                    stageWatch.Restart();
                }).ConfigureAwait(false);
            return Complete(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            finalStatus = ReadStatus.Unavailable;
            ObserveExit();
            Trace(CurrentStage(), SourceBackendOutcome.Cancelled, finalStatus);
            throw;
        }
        catch (OperationCanceledException) { return Complete(new(ReadStatus.Timeout), timeout: SourceBackendTimeout.Session); }
        catch (TimeoutException) { return Complete(new(ReadStatus.Timeout), timeout: SourceBackendTimeout.Request); }
        catch (RpcException error)
        {
            if (stage == CliStage.RateLimitsRead && error.ErrorKind == CliErrorKind.QuotaAuthenticationRequired)
                return Complete(new(ReadStatus.NotSignedIn), error.Code, error.ErrorKind);
            if (stage == CliStage.RateLimitsRead && error.ErrorKind == CliErrorKind.QuotaChatGptRequired)
                return Complete(new(ReadStatus.UnsupportedAccount), error.Code, error.ErrorKind);
            return Complete(new(error.IsRateLimited ? ReadStatus.RateLimited :
                error.Code == -32601 ? ReadStatus.ProtocolError : ReadStatus.Unavailable), error.Code, error.ErrorKind);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        { return Complete(new(ReadStatus.ProtocolError)); }
        catch (DesktopRuntimeValidationException error)
        {
            validationExclusions = error.Exclusion;
            desktopFailure = error.Desktop;
            if (token.IsCancellationRequested)
            {
                finalStatus = ReadStatus.Unavailable;
                ObserveExit();
                Trace(CurrentStage(), SourceBackendOutcome.Cancelled, finalStatus);
                token.ThrowIfCancellationRequested();
            }
            var expired = deadline.IsCancellationRequested;
            return Complete(new(expired ? ReadStatus.Timeout : ReadStatus.Unavailable), error.Code,
                (error.Exclusion & SourceBackendExclusion.DesktopEvidenceLimit) != 0
                    ? CliErrorKind.DesktopEvidenceLimit : CliErrorKind.DesktopRuntimeUnverified,
                expired ? SourceBackendTimeout.Session : SourceBackendTimeout.None);
        }
        catch (Win32Exception error) { return Complete(new(ReadStatus.Unavailable), error.NativeErrorCode); }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or SecurityException)
        { return Complete(new(ReadStatus.Unavailable), error.HResult); }
        finally
        {
            stageWatch.Restart();
            deadline.Cancel();
            var pending = false;
            started |= process.HasOwnedProcess;
            if (started) exit = SourceBackendExit.OwnedTreeTerminationRequested;
            try
            {
                cleanupEvidence = await BackendCleanupCoordinator.WaitAsync(process, rpc, stderr,
                    _cleanupStop, _cleanupOptions, (evidence, elapsed) =>
                    {
                        pending = true;
                        cleanupEvidence = evidence;
                        Trace(SourceBackendStage.Cleanup, SourceBackendOutcome.Pending, finalStatus,
                            evidence.Code, timeout: SourceBackendTimeout.Cleanup);
                        PublishCleanup(new(_source, BackendCleanupState.Pending, completedRead, readAt, elapsed));
                    }).ConfigureAwait(false);
                exitCode = cleanupEvidence.ExitCode;
            }
            catch (Exception error)
            {
                Trace(SourceBackendStage.Cleanup, SourceBackendOutcome.Failed, finalStatus,
                    error is Win32Exception native ? native.NativeErrorCode : error.HResult,
                    timeout: SourceBackendTimeout.Cleanup);
                throw new BackendCleanupUnconfirmedException();
            }
            finally
            {
                // Retain the pinned executable until confirmed, or until the application exits.
                // A lease disposal error must not bypass the host's fail-closed signal.
                try
                {
                    try { process.Dispose(); }
                    catch (Exception error)
                    {
                        Trace(SourceBackendStage.Cleanup, SourceBackendOutcome.Failed, finalStatus, error.HResult);
                        throw new BackendCleanupUnconfirmedException();
                    }
                }
                finally
                {
                    try { launchLease?.Dispose(); }
                    catch (Exception error)
                    {
                        Trace(SourceBackendStage.Cleanup, SourceBackendOutcome.Failed, finalStatus, error.HResult);
                        throw new BackendCleanupUnconfirmedException();
                    }
                }
            }
            Trace(SourceBackendStage.Cleanup, SourceBackendOutcome.Succeeded, finalStatus);
            if (pending) PublishCleanup(new(_source, BackendCleanupState.Confirmed, completedRead, readAt,
                stageWatch.ElapsedMilliseconds));
        }
    }

    private void PublishCleanup(BackendCleanupProgress progress)
    {
        try { _cleanupProgress?.Invoke(progress); }
        catch (Exception) { /* Optional UI observers cannot release process ownership. */ }
    }

    private void Publish(CliDiagnostic diagnostic)
    {
        try { _diagnostic?.Invoke(diagnostic); }
        catch (Exception) { /* Optional observers must never break quota reads. */ }
    }
    private void PublishTrace(SourceBackendDiagnostic diagnostic)
    {
        try { _trace?.Invoke(diagnostic); }
        catch (Exception) { /* Optional observers must never break quota reads. */ }
    }
    private static async Task DrainAsync(TextReader reader, CancellationToken token)
    {
        var buffer = new char[2048];
        while (await AsyncCompatibility.ReadAsync(reader, buffer, token).ConfigureAwait(false) != 0) { }
    }
}
