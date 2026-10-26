using System.Security;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexQuota.Core;

// Ready means response metadata is available, not that generation timing is verified.
public enum RolloutSpeedReadStatus { Ready, NoFiles, NoValidSample, Unavailable }

public sealed record RolloutSpeedReadResult(OutputSpeedSample? Sample, RolloutSpeedReadStatus Status,
    bool IsHistoryLimited = false, bool PendingWork = false, bool HasReadErrors = false,
    int TrackedFileCount = 0, int BytesRead = 0)
{
    public RolloutModelMetadataStatus ModelMetadataStatus { get; init; } = RolloutModelMetadataStatus.NotFound;
    public RolloutModelBoundaryReason BoundaryReason { get; init; } = RolloutModelBoundaryReason.None;
    public bool ActivityAmbiguous { get; init; }
    public ResponseRateDiagnostic ResponseRateDiagnostic { get; init; } = new(ResponseRateReason.NotObserved,
        ResponseRateStartBoundary.NotObserved, ResponseRateEndBoundary.NotObserved, ResponseRateUsageKind.NotObserved, false, false, false, 0);
    public bool CanCatchUp { get; init; }
    public bool DiscoveryPending { get; init; }
    public bool ForwardBacklogPending { get; init; }
    public bool HistoryRecoveryPending { get; init; }
    public int DiscoveryEntriesVisited { get; init; }
    public int DiscoveryDirectoriesVisited { get; init; }
    public TurnAverageDiagnostic TurnAverageDiagnostic { get; init; } = new();
}

/// <summary>Limits cover metadata discovery as well as content reads. No history is persisted.</summary>
public sealed class RolloutSpeedReaderOptions
{
    public TimeSpan DiscoveryInterval { get; init; } = TimeSpan.FromSeconds(3);
    public int MaxTrackedFiles { get; init; } = 24;
    public int MaxDiscoveryEntries { get; init; } = 4096;
    public int MaxDiscoveryDirectories { get; init; } = 128;
    public int InitialTailBytes { get; init; } = 256 * 1024;
    public int MaxLineBytes { get; init; } = 128 * 1024;
    public int MaxBytesPerFilePerPoll { get; init; } = 512 * 1024;
    public int MaxBytesPerPoll { get; init; } = 2 * 1024 * 1024;
    public int MaxRememberedSamples { get; init; } = 128;
    // Safety ceiling, not a startup read size. A cached cursor progresses within poll budgets.
    public long MaxModelHistoryBytes { get; init; } = 256L * 1024 * 1024;

    internal void Validate()
    {
        if (DiscoveryInterval < TimeSpan.Zero || MaxTrackedFiles < 1 || MaxTrackedFiles > 256 ||
            MaxDiscoveryEntries < 2 || MaxDiscoveryEntries > 100000 ||
            MaxDiscoveryDirectories < 2 || MaxDiscoveryDirectories > 4096 ||
            InitialTailBytes < 1 || InitialTailBytes > 16 * 1024 * 1024 ||
            MaxLineBytes < 128 || MaxLineBytes > 1024 * 1024 ||
            MaxBytesPerFilePerPoll < 2L * MaxLineBytes + 2048 ||
            MaxBytesPerPoll < MaxBytesPerFilePerPoll || MaxBytesPerPoll > 64 * 1024 * 1024 ||
            MaxRememberedSamples < 1 || MaxRememberedSamples > 4096 || MaxModelHistoryBytes < 1 || MaxModelHistoryBytes > 256L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(RolloutSpeedReaderOptions));
    }
}

/// <summary>
/// Read-only, bounded sampling of CODEX_HOME/sessions and archived_sessions. Call on a background
/// timer, never to open a hover card. Discovery is infrequent; each selected file keeps a byte
/// offset. Startup reads its first metadata line and a bounded recent tail, not its full history.
/// Incomplete UTF-8/JSON lines are reread on the next poll, never cached. Only identifiers,
/// numeric usage/timing metadata, offsets and small fingerprints survive a poll.
/// </summary>
public sealed class RolloutSpeedReader : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly StringComparer PathComparer = Path.DirectorySeparatorChar == '\\'
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly string? home;
    private readonly RolloutSpeedReaderOptions options;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, FileCursor> files = new(PathComparer);
    private readonly Dictionary<string, OutputSpeedSample> samples = new(StringComparer.Ordinal);
    // Bounded metadata and terminal misses are cached separately from numeric measurements.
    // A null model is a conflict tombstone and can never be revived by a later copy.
    private readonly Dictionary<string, RecoveredMetadata> recoveredMetadata = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RolloutModelMetadataStatus> metadataStatuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RolloutModelBoundaryReason> boundaryReasons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RememberedUsage> usages = new(StringComparer.Ordinal);
    private readonly HashSet<string> invalidSamples = new(StringComparer.Ordinal);
    private readonly Queue<string> invalidOrder = new();
    private readonly Dictionary<string, RecoveredMetadata> turnContexts = new(StringComparer.Ordinal);
    private readonly Queue<string> turnContextOrder = new();
    // One bounded ownership anchor survives candidate eviction/revocation. It
    // describes trusted log activity, never the user's focused app/window.
    private LatestActivity? latestActivity;
    private DateTimeOffset nextDiscovery = DateTimeOffset.MinValue;
    private bool historyLimited;
    private bool discoveryErrors;
    private bool sawRolloutFiles;
    private int nextFileIndex;
    private long observationOrder;
    private readonly Dictionary<string, SampleSource> sampleSources = new(StringComparer.Ordinal);
    private readonly List<DiscoveryCursor> discovery = new();
    private bool discoveryPending;
    private int discoveryEntriesVisited, discoveryDirectoriesVisited;
    private bool disposed;

    public RolloutSpeedReader(string? codexHome = null, RolloutSpeedReaderOptions? options = null)
    {
        this.options = options ?? new RolloutSpeedReaderOptions();
        this.options.Validate();
        try
        {
            var configured = codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME");
            home = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                : configured!);
        }
        catch (Exception exception) when (IsFileError(exception)) { home = null; }
    }

    public async Task<RolloutSpeedReadResult> PollAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Poll(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private RolloutSpeedReadResult Poll(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (home is null) return Result(RolloutSpeedReadStatus.Unavailable, true, false, 0);
        if (disposed) throw new ObjectDisposedException(nameof(RolloutSpeedReader));
        discoveryEntriesVisited = discoveryDirectoriesVisited = 0;
        if (discoveryPending || DateTimeOffset.UtcNow >= nextDiscovery)
        {
            Discover(token);
            if (!discoveryPending) nextDiscovery = DateTimeOffset.UtcNow + options.DiscoveryInterval;
        }

        var remaining = options.MaxBytesPerPoll;
        var errors = discoveryErrors;
        var readable = 0;
        var pending = discoveryPending;
        // Round-robin prevents a very active large file from starving other sessions.
        var selected = files.Values.OrderByDescending(file => file.DiscoveredWriteUtc).ToArray();
        var start = selected.Length == 0 ? 0 : nextFileIndex % selected.Length;
        // Give the displayed response one first slot while its exact model is pending.
        // The rest stays round-robin; skip this extra slot when the configured poll
        // budget cannot also afford another file, so small custom budgets stay fair.
        var latest = samples.Values.OrderByDescending(sample => sample.CompletedAt)
            .ThenBy(sample => sample.ThreadId, StringComparer.Ordinal)
            .ThenByDescending(sample => sample.TimingReliability == OutputSpeedTimingReliability.TurnAverage).FirstOrDefault();
        var priority = options.MaxBytesPerPoll >= 2L * options.MaxBytesPerFilePerPoll && latest is not null &&
            latest.Model is null && !recoveredMetadata.ContainsKey(Key(latest))
            ? selected.FirstOrDefault(file => file.ModelRecovery is { IsComplete: false } &&
                file.RecoveryPeers.TryGetValue(Key(latest), out var peer) && peer.IsCompatibleWith(latest)) : null;
        if (priority is not null)
        {
            try { ReadFile(priority, ref remaining, token, ref pending); readable++; }
            catch (FileNotFoundException) { files.Remove(priority.Path); nextDiscovery = DateTimeOffset.MinValue; }
            catch (DirectoryNotFoundException) { files.Remove(priority.Path); nextDiscovery = DateTimeOffset.MinValue; }
            catch (Exception exception) when (IsFileError(exception)) { priority.ReadFailed = true; errors = true; }
        }
        var visited = 0;
        for (; visited < selected.Length; visited++)
        {
            token.ThrowIfCancellationRequested();
            var file = selected[(start + visited) % selected.Length];
            if (ReferenceEquals(file, priority)) continue;
            if (remaining < 2 * options.MaxLineBytes + 2048) { pending = true; break; }
            try
            {
                ReadFile(file, ref remaining, token, ref pending);
                readable++;
            }
            catch (FileNotFoundException) { files.Remove(file.Path); nextDiscovery = DateTimeOffset.MinValue; }
            catch (DirectoryNotFoundException) { files.Remove(file.Path); nextDiscovery = DateTimeOffset.MinValue; }
            catch (Exception exception) when (IsFileError(exception)) { file.ReadFailed = true; errors = true; }
        }
        if (selected.Length != 0) nextFileIndex = (start + Math.Max(1, visited)) % selected.Length;
        var status = errors && readable == 0 ? RolloutSpeedReadStatus.Unavailable
            : !sawRolloutFiles ? RolloutSpeedReadStatus.NoFiles
            : samples.Count == 0 ? RolloutSpeedReadStatus.NoValidSample : RolloutSpeedReadStatus.Ready;
        return Result(status, errors, pending, options.MaxBytesPerPoll - remaining);
    }

    private RolloutSpeedReadResult Result(RolloutSpeedReadStatus status, bool errors, bool pending, int bytesRead)
    {
        var sample = samples.Values.Where(value => latestActivity is null || value.ThreadId == latestActivity.ThreadId)
            .OrderByDescending(value => value.CompletedAt).ThenBy(value => value.ThreadId,
            StringComparer.Ordinal).ThenByDescending(value => value.TimingReliability == OutputSpeedTimingReliability.TurnAverage).FirstOrDefault();
        var response = OrderedSamples(samples.Values.Where(IsCompleteResponse)).FirstOrDefault();
        var averages = samples.Values.Where(IsCompleteAverage).OrderByDescending(value => value.CompletedAt)
            .ThenBy(value => value.ThreadId, StringComparer.Ordinal).ThenBy(value => value.TurnId, StringComparer.Ordinal).ToArray();
        var average = averages.FirstOrDefault();
        if (response is not null)
        {
            var otherThread = response.ThreadId != latestActivity?.ThreadId;
            var retained = otherThread || files.Values.Any(file =>
                file.ThreadId == response.ThreadId && (file.ResponseParser.CurrentTurnId is { } turn && turn != response.TurnId ||
                file.ResponseParser.ThreadSetting.RecordedAt > response.CompletedAt ||
                file.ResponseParser.Diagnostic.Reason != ResponseRateReason.Ready && file.ResponseParser.LastObservedAt >= response.CompletedAt));
            sample = response with { IsHistoricalOtherThread = otherThread, IsRetainedTurnAverage = retained };
        }
        else if (average is not null)
        {
            // Independent historical group: no claim that this rate belongs to the
            // current activity (which may itself have ambiguous ownership).
            var otherThread = average.ThreadId != latestActivity?.ThreadId;
            var retained = otherThread || sample is null || Key(sample) != Key(average) || latestActivity?.At > average.CompletedAt ||
                files.Values.Any(file => file.ThreadId == average.ThreadId && file.AverageParser.LatestStartedTurn is { } started &&
                    started.StartedAt >= average.CompletedAt && started.TurnId != average.TurnId) ||
                files.Values.Any(file => file.ThreadId == average.ThreadId &&
                    file.AverageParser.ThreadSetting.RecordedAt > average.CompletedAt);
            sample = average with { IsRetainedTurnAverage = retained, IsHistoricalOtherThread = otherThread };
        }
        var metadataStatus = sample?.Model is null ? RolloutModelMetadataStatus.NotFound : RolloutModelMetadataStatus.Known;
        if (sample is not null)
        {
            if (sample.Model is null && metadataStatuses.TryGetValue(Key(sample), out var knownStatus)) metadataStatus = knownStatus;
            if (recoveredMetadata.TryGetValue(Key(sample), out var metadata))
            {
                var modelConflict = metadata.Model is null || sample.Model is not null && sample.Model != metadata.Model;
                var effortConflict = metadata.EffortConflict || sample.ReasoningEffort is not null &&
                    metadata.ReasoningEffort is not null && sample.ReasoningEffort != metadata.ReasoningEffort;
                sample = sample with
                {
                    Model = modelConflict ? null : metadata.Model,
                    ReasoningEffort = modelConflict || effortConflict ? null : metadata.ReasoningEffort ?? sample.ReasoningEffort
                };
                metadataStatus = modelConflict || effortConflict ? RolloutModelMetadataStatus.Conflict : RolloutModelMetadataStatus.Known;
            }
        }
        if (sample is { Model: null } && metadataStatus != RolloutModelMetadataStatus.Conflict &&
            files.Values.Any(file => file.ModelRecovery is { IsComplete: false } &&
                file.RecoveryPeers.TryGetValue(Key(sample), out var peer) && peer.IsCompatibleWith(sample)))
            metadataStatus = RolloutModelMetadataStatus.PendingHistory;
        if (sample is not null && sample.TimingReliability == OutputSpeedTimingReliability.UnverifiedItemLifecycle)
        {
            // Settings are a separate latest-thread preference, not an assertion
            // about the completed turn's request or the server-delivered tier.
            var settings = files.Values.Where(file => file.ThreadId == sample.ThreadId)
                .Select(file => file.AverageParser.ThreadSetting).ToArray();
            var latestSetting = settings.Where(value => value.RecordedAt.HasValue)
                .OrderByDescending(value => value.RecordedAt).FirstOrDefault();
            var known = latestSetting is not null && latestSetting.RecordedAt <= DateTimeOffset.UtcNow.AddMinutes(1) &&
                !settings.Any(value => value.BrokenBoundary) &&
                !settings.Any(value => value.RecordedAt == latestSetting.RecordedAt && value.Setting != latestSetting.Setting);
            sample = sample with { ThreadRequestedSetting = known ? latestSetting!.Setting : ThreadRequestedSetting.Unknown };
        }
        return new(sample, status, historyLimited, pending, errors, files.Count, bytesRead)
        { CanCatchUp = discoveryPending || files.Values.Any(file => !file.ReadFailed && (!file.Initialized || file.ForwardBacklog || file.History is not null || file.ModelRecovery is { IsComplete: false })),
            DiscoveryPending = discoveryPending,
            ForwardBacklogPending = files.Values.Any(file => !file.Initialized || file.ForwardBacklog),
            HistoryRecoveryPending = files.Values.Any(file => file.History is not null || file.ModelRecovery is { IsComplete: false }),
            DiscoveryEntriesVisited = discoveryEntriesVisited, DiscoveryDirectoriesVisited = discoveryDirectoriesVisited,
            ModelMetadataStatus = metadataStatus, ActivityAmbiguous = latestActivity is { ThreadId: null },
            TurnAverageDiagnostic = AverageDiagnostic(), ResponseRateDiagnostic = ResponseDiagnostic(),
            BoundaryReason = metadataStatus == RolloutModelMetadataStatus.Boundary && sample is not null
            ? boundaryReasons.TryGetValue(Key(sample), out var reason) ? reason : RolloutModelBoundaryReason.Unknown
            : RolloutModelBoundaryReason.None };
    }

    private ResponseRateDiagnostic ResponseDiagnostic()
    {
        var cursor = files.Values.Where(file => file.ResponseParser.HasOwnedSection &&
            (latestActivity is null || file.ThreadId == latestActivity.ThreadId))
            .OrderByDescending(file => file.ResponseParser.LastObservedAt).FirstOrDefault();
        return cursor?.ResponseParser.Diagnostic ?? new(ResponseRateReason.NotObserved, ResponseRateStartBoundary.NotObserved,
            ResponseRateEndBoundary.NotObserved, ResponseRateUsageKind.NotObserved, false, false, false, 0);
    }

    private IOrderedEnumerable<OutputSpeedSample> OrderedSamples(IEnumerable<OutputSpeedSample> values) => values
        .GroupBy(sample => sampleSources.TryGetValue(Key(sample), out var source) ? source.Path : Key(sample), PathComparer)
        .Select(group => group.OrderByDescending(sample => sampleSources.TryGetValue(Key(sample), out var source) ? source.Offset : 0).First())
        .GroupBy(sample => sample.ThreadId, StringComparer.Ordinal)
        .Select(group => group.OrderBy(sample => !(sampleSources.TryGetValue(Key(sample), out var source) &&
                files.ContainsKey(source.Path) && !IsArchive(source.Path)))
            .ThenByDescending(sample => sample.CompletedAt).ThenBy(sample => Key(sample), StringComparer.Ordinal).First())
        .OrderByDescending(sample => sample.CompletedAt).ThenBy(sample => sample.ThreadId, StringComparer.Ordinal)
        .ThenBy(sample => sampleSources.TryGetValue(Key(sample), out var source) && IsArchive(source.Path))
        .ThenBy(sample => sampleSources.TryGetValue(Key(sample), out var source) ? source.Path : "", PathComparer)
        .ThenByDescending(sample => sampleSources.TryGetValue(Key(sample), out var source) ? source.Offset : 0);

    private static bool IsArchive(string path) => path.IndexOf(Path.DirectorySeparatorChar + "archived_sessions" + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase) >= 0;

    private TurnAverageDiagnostic AverageDiagnostic()
    {
        if (latestActivity is { ThreadId: null }) return new(TurnAverageReason.ActivityAmbiguous);
        var candidates = files.Values.Where(file => file.AverageParser.HasOwnedSection &&
            (latestActivity is null || file.ThreadId == latestActivity.ThreadId)).ToArray();
        // Without an owning activity, only one verified thread may supply a diagnosis.
        if (latestActivity is null && candidates.Select(file => file.ThreadId).Distinct(StringComparer.Ordinal).Take(2).Count() > 1)
            return new(TurnAverageReason.IdentityMismatch);
        var file = candidates.Where(file => file.AverageParser.LastObservedAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            .OrderByDescending(file => file.AverageParser.LastObservedAt)
            .ThenByDescending(file => file.AverageParser.Diagnostic.Reason != TurnAverageReason.Ready).FirstOrDefault();
        if (file is null) return new();
        var diagnostic = file.AverageParser.Diagnostic;
        if (diagnostic.Reason == TurnAverageReason.Ready && file.AverageParser.CurrentTurnId is { } turnId &&
            invalidSamples.Contains(TurnKey(file.ThreadId, turnId)))
            return diagnostic with { Reason = TurnAverageReason.ConflictingEvidence, LastRejectReason = TurnAverageReason.ConflictingEvidence };
        return diagnostic;
    }

    private void Discover(CancellationToken token)
    {
        if (!discoveryPending)
        {
            discoveryErrors = false;
            foreach (var cursor in discovery) cursor.Dispose();
            discovery.Clear();
            foreach (var root in new[] { "sessions", "archived_sessions" })
                discovery.Add(new DiscoveryCursor(Path.Combine(home!, root)));
        }
        // Most discovered files are older than the bounded tracked set. Reuse
        // its oldest member within this discovery slice rather than allocating
        // and ordering the same set for every unchanged, rejected candidate.
        FileCursor? oldest = null;
        foreach (var cursor in discovery)
        {
            var entries = options.MaxDiscoveryEntries / 2;
            var directories = options.MaxDiscoveryDirectories / 2;
            while (!cursor.Complete && entries > 0 && directories > 0)
            {
                token.ThrowIfCancellationRequested();
                if (cursor.NextDirectory is { } directory)
                {
                    cursor.NextDirectory = null;
                    directories--; discoveryDirectoriesVisited++;
                    try
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                        {
                            if (cursor.Frames.Count >= 64) historyLimited = true;
                            else cursor.Frames.Push(Directory.EnumerateFileSystemEntries(directory).GetEnumerator());
                        }
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                    catch (Exception exception) when (IsFileError(exception)) { discoveryErrors = true; }
                }
                if (cursor.Frames.Count == 0) { cursor.Complete = true; break; }
                var frame = cursor.Frames.Peek();
                string entry;
                try
                {
                    if (!frame.MoveNext()) { frame.Dispose(); cursor.Frames.Pop(); continue; }
                    entry = frame.Current;
                }
                catch (Exception exception) when (IsFileError(exception))
                {
                    // Failed enumeration is retired; retrying an unadvanceable iterator
                    // without consuming a budget could otherwise spin forever.
                    frame.Dispose(); cursor.Frames.Pop(); discoveryErrors = true;
                    entries--; discoveryEntriesVisited++;
                    continue;
                }
                entries--; discoveryEntriesVisited++;
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0) { cursor.NextDirectory = entry; continue; }
                    if (!Path.GetFileName(entry).StartsWith("rollout-", StringComparison.Ordinal) ||
                        !string.Equals(Path.GetExtension(entry), ".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                    sawRolloutFiles = true;
                    if (ThreadIdFromPath(entry) is not { } threadId) continue;
                    var info = new FileInfo(entry);
                    if (files.TryGetValue(entry, out var existing))
                    {
                        if (existing.DiscoveredWriteUtc != info.LastWriteTimeUtc) oldest = null;
                        existing.DiscoveredWriteUtc = info.LastWriteTimeUtc;
                        continue;
                    }
                    if (files.Count >= options.MaxTrackedFiles)
                    {
                        historyLimited = true;
                        oldest ??= files.Values.OrderBy(file => file.DiscoveredWriteUtc).ThenByDescending(file => file.Path, PathComparer).First();
                        if (oldest.DiscoveredWriteUtc > info.LastWriteTimeUtc || oldest.DiscoveredWriteUtc == info.LastWriteTimeUtc &&
                            PathComparer.Compare(oldest.Path, entry) < 0) continue;
                        files.Remove(oldest.Path);
                        oldest = null;
                    }
                    files.Add(entry, new FileCursor(entry, threadId, options.MaxLineBytes) { DiscoveredWriteUtc = info.LastWriteTimeUtc });
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception exception) when (IsFileError(exception)) { discoveryErrors = true; }
            }
        }
        discoveryPending = discovery.Any(cursor => !cursor.Complete);
        if (discoveryPending) historyLimited = true;
    }

    public void Dispose()
    {
        gate.Wait();
        try
        {
            if (disposed) return;
            disposed = true;
            foreach (var cursor in discovery) cursor.Dispose();
            discovery.Clear();
        }
        finally { gate.Release(); }
    }

    private static string? ThreadIdFromPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length >= 36 && Guid.TryParseExact(name.Substring(name.Length - 36), "D", out var id)
            ? id.ToString("D") : null;
    }

    private void ReadFile(FileCursor file, ref int totalRemaining, CancellationToken token, ref bool pending)
    {
        file.ReadFailed = false;
        var info = new FileInfo(file.Path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { file.ReadFailed = true; return; }
        using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        var length = stream.Length;
        var identity = ReadIdentity(stream);
        var localRemaining = Math.Min(totalRemaining, options.MaxBytesPerFilePerPoll);
        var before = localRemaining;
        try
        {
            var prefixCount = (int)Math.Min(256, length);
            var prefix = Fingerprint(stream, 0, prefixCount, ref localRemaining);
            var changed = file.Initialized && (length < file.ObservedLength ||
                identity.HasValue && file.Identity.HasValue && identity.Value != file.Identity.Value ||
                info.CreationTimeUtc != file.CreationUtc && length <= file.ObservedLength ||
                (length <= file.ObservedLength && info.LastWriteTimeUtc != file.LastWriteUtc));
            if (file.Initialized && !changed)
            {
                // Compare exactly the old prefix length: ordinary short-file growth is not replacement.
                var oldPrefix = prefixCount == file.PrefixCount ? prefix
                    : Fingerprint(stream, 0, file.PrefixCount, ref localRemaining);
                changed = oldPrefix != file.Prefix || (file.AnchorCount != 0 &&
                    Fingerprint(stream, file.AnchorOffset, file.AnchorCount, ref localRemaining) != file.Anchor);
            }
            if (file.Initialized && !changed && file.Offset == length &&
                file.History is null && file.ModelRecovery is not { IsComplete: false })
            {
                // The prefix and previous end anchor were just verified, including
                // same-size replacements with preserved timestamps. There are no
                // new bytes or recovery work, so do not allocate a line buffer or
                // reread the unchanged anchor merely to store the same fingerprint.
                file.ObservedLength = length;
                file.Identity = identity;
                file.CreationUtc = info.CreationTimeUtc;
                file.LastWriteUtc = info.LastWriteTimeUtc;
                file.DiscoveredWriteUtc = info.LastWriteTimeUtc;
                file.Prefix = prefix;
                file.PrefixCount = prefixCount;
                file.ForwardBacklog = false;
                pending |= file.Projection.HasPending || file.SkipUntilNewline;
                return;
            }
            if (!file.Initialized || changed)
            {
                // Replaced/truncated contents no longer corroborate this thread's retained
                // history. Rebuild it from reliable boundaries instead of reviving old samples.
                if (changed)
                {
                    foreach (var key in samples.Where(pair => pair.Value.ThreadId == file.ThreadId)
                        .Select(pair => pair.Key).ToArray()) { samples.Remove(key); sampleSources.Remove(key); recoveredMetadata.Remove(key); metadataStatuses.Remove(key); boundaryReasons.Remove(key); }
                    foreach (var key in usages.Where(pair => pair.Value.Observation.ThreadId == file.ThreadId)
                        .Select(pair => pair.Key).ToArray()) usages.Remove(key);
                    foreach (var key in turnContexts.Keys.Where(key => key.StartsWith(file.ThreadId + "\n", StringComparison.Ordinal)).ToArray()) turnContexts.Remove(key);
                    foreach (var peer in files.Values.Where(peer => peer.ThreadId == file.ThreadId && !ReferenceEquals(peer, file)))
                    {
                        // An older copy's pending scan belongs to the replaced generation.
                        // It cannot later restore labels onto this response identity.
                        peer.History = null; peer.ModelRecovery = null; peer.RecoveryPeers.Clear();
                    }
                    var contextKeys = turnContextOrder.Where(turnContexts.ContainsKey).ToArray();
                    turnContextOrder.Clear();
                    foreach (var key in contextKeys) turnContextOrder.Enqueue(key);
                }
                file.Restart();
                file.Initialized = true;
                if (length > options.InitialTailBytes)
                {
                    historyLimited = true;
                    ReadFirstLine(stream, file, ref localRemaining, token);
                    file.Parser.ResetPending(); file.AverageParser.ResetPending(TurnAverageReason.ColdTail); file.ResponseParser.ResetPending();
                    file.Projection = new RolloutMetadataProjection(options.MaxLineBytes);
                    if (options.MaxModelHistoryBytes > options.InitialTailBytes)
                    {
                        var history = new FileCursor(file.Path, file.ThreadId, options.MaxLineBytes) { IsHistory = true };
                        var historyStart = Math.Max(0, length - options.MaxModelHistoryBytes);
                        if (historyStart > 0)
                        {
                            ReadFirstLine(stream, history, ref localRemaining, token);
                            history.AverageParser.ResetPending(TurnAverageReason.ColdTail); history.ResponseParser.ResetPending();
                            history.Offset = historyStart;
                            stream.Position = historyStart - 1; localRemaining--;
                            history.SkipUntilNewline = stream.ReadByte() != '\n';
                        }
                        file.History = history;
                        file.HistoryLimit = length; // Frozen historical end; never restart or shift this window.
                    }
                    file.Offset = Math.Max(file.Offset, length - options.InitialTailBytes);
                    // A tail offset may land inside UTF-8 or JSON. Discard through the next newline.
                    if (file.Offset > 0)
                    {
                        stream.Position = file.Offset - 1;
                        localRemaining--;
                        file.SkipUntilNewline = stream.ReadByte() != '\n';
                    }
                }
            }
            // Active forward traffic must not starve an already-pending historical lookup.
            // Leave room for at least one maximum-sized forward line and fingerprints.
            RecoverModel(file, stream, ref localRemaining, token,
                Math.Min(RolloutModelRecovery.MaximumWindowBytes, Math.Min(Math.Max(0, localRemaining - 512),
                    Math.Max(file.ModelRecovery?.MinimumBytesForProgress ?? 0, localRemaining - options.MaxLineBytes - 2048))));
            // Every pending cold scan receives a bounded share, while forward appends
            // retain at least half the local budget. No increasing per-poll thresholds.
            var historyReserve = file.History is null ? 0 : Math.Min(128 * 1024, Math.Max(0, (localRemaining - 1024) / 3));
            var forwardBudget = localRemaining - historyReserve;
            var forwardBefore = forwardBudget;
            ReadLines(stream, file, ref forwardBudget, token, stream.Length);
            localRemaining -= forwardBefore - forwardBudget;
            ReadHistory(stream, file, ref localRemaining, token);
            file.ObservedLength = length;
            file.Identity = identity;
            file.CreationUtc = info.CreationTimeUtc;
            file.LastWriteUtc = info.LastWriteTimeUtc;
            file.DiscoveredWriteUtc = info.LastWriteTimeUtc;
            file.Prefix = prefix;
            file.PrefixCount = prefixCount;
            file.AnchorCount = (int)Math.Min(256, file.Offset);
            file.AnchorOffset = file.Offset - file.AnchorCount;
            file.Anchor = Fingerprint(stream, file.AnchorOffset, file.AnchorCount, ref localRemaining);
            RecoverModel(file, stream, ref localRemaining, token);
            file.ForwardBacklog = file.Offset < stream.Length;
            pending |= file.ForwardBacklog || file.Projection.HasPending || file.SkipUntilNewline ||
                file.History is not null || file.ModelRecovery is { IsComplete: false };
        }
        finally { totalRemaining -= before - localRemaining; }
    }

    private void ReadFirstLine(FileStream stream, FileCursor file, ref int remaining, CancellationToken token)
    {
        stream.Position = 0;
        var line = new byte[options.MaxLineBytes];
        var count = 0;
        while (remaining > 512 && count < line.Length)
        {
            token.ThrowIfCancellationRequested();
            var value = stream.ReadByte();
            if (value < 0) break;
            remaining--;
            if (value == '\n')
            {
                Feed(file, line, count, stream.Position);
                file.Offset = stream.Position;
                Array.Clear(line, 0, line.Length);
                return;
            }
            line[count++] = (byte)value;
        }
        // No complete bounded metadata line: remain unbound until a later own-session marker.
        file.Parser.ResetPending(); file.AverageParser.ResetPending();
        Array.Clear(line, 0, line.Length);
    }

    private void ReadHistory(FileStream stream, FileCursor file, ref int remaining, CancellationToken token)
    {
        if (file.History is not { } history || remaining <= 512) return;
        // Startup history is one fixed, capped window ending at the original tail.
        // After that window, only bytes appended since startup need a catch-up pass.
        var target = Math.Min(file.Offset, file.HistoryLimit);
        ReadLines(stream, history, ref remaining, token, target);
        if (history.Offset < target) return;
        if (history.Offset == file.Offset)
        {
            // Legacy item lifecycle parsing and its model lookup stay on the
            // uninterrupted forward cursor. Replay only reconstructs the new metrics.
            file.AverageParser = history.AverageParser;
            file.ResponseParser = history.ResponseParser;
            file.Projection = history.Projection;
            file.SkipUntilNewline = history.SkipUntilNewline;
            file.History = null;
        }
        else if (history.Offset >= file.HistoryLimit)
        {
            // These are new forward bytes, not a wider history search. Never move
            // either cursor backward or reread the completed startup window.
            file.HistoryLimit = file.Offset;
        }
    }

    private void ReadLines(FileStream stream, FileCursor file, ref int remaining, CancellationToken token, long end)
    {
        if (remaining <= 512 || file.Offset >= end) return;
        stream.Position = file.Offset;
        var buffer = new byte[8192];
        try
        {
            while (remaining > 512 && file.Offset < end)
            {
                token.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, (int)Math.Min(Math.Min(buffer.Length, remaining - 512), end - file.Offset));
                if (read == 0) break;
                remaining -= read;
                for (var index = 0; index < read; index++)
                {
                    var value = buffer[index];
                    file.Offset++;
                    if (file.SkipUntilNewline)
                    {
                        if (value == '\n') file.SkipUntilNewline = false;
                    }
                    else if (value == '\n')
                    {
                        var line = file.Projection.Finish();
                        if (line is null)
                        {
                            historyLimited = true;
                            file.Parser.ResetPending(); file.AverageParser.ResetPending(file.Projection.FailureReason);
                            file.ResponseParser.ResetPending();
                        }
                        else { Feed(file, line, line.Length, file.Offset); Array.Clear(line, 0, line.Length); }
                        file.Projection = new RolloutMetadataProjection(options.MaxLineBytes);
                    }
                    else
                    {
                        var skipped = file.Projection.SkipPlainBody(buffer, index, read - index);
                        if (skipped == 0) file.Projection.Add(value);
                        else { index += skipped - 1; file.Offset += skipped - 1; }
                    }
                }
            }
        }
        finally { Array.Clear(buffer, 0, buffer.Length); }
    }

    private void Feed(FileCursor file, byte[] line, int count, long recordEnd)
    {
        OutputSpeedSample? emitted = null;
        OutputSpeedSample? average = null;
        OutputSpeedSample? responseRate = null;
        try
        {
            var start = count >= 3 && line[0] == 0xef && line[1] == 0xbb && line[2] == 0xbf ? 3 : 0;
            if (count > start && line[count - 1] == '\r') count--;
            var text = StrictUtf8.GetString(line, start, count - start);
            // Future clocks must not poison a parser watermark before sample filtering.
            if (IsFutureRecord(text))
            {
                file.Parser.ResetPending(); file.AverageParser.ResetPending(); file.ResponseParser.ResetPending();
                return;
            }
            emitted = file.IsHistory ? null : file.Parser.Feed(text);
            average = file.AverageParser.Feed(text);
            responseRate = file.ResponseParser.Feed(text);
        }
        catch (DecoderFallbackException) { file.Parser.ResetPending(); file.ResponseParser.ResetPending(); file.AverageParser.ResetPending(TurnAverageReason.InvalidUtf8); historyLimited = true; }
        if (file.AverageParser.TakeIdentityBoundary() | file.ResponseParser.TakeIdentityBoundary())
            foreach (var key in samples.Where(pair => pair.Value.ThreadId == file.ThreadId &&
                (!file.IsHistory || sampleSources.TryGetValue(pair.Key, out var source) && source.Path == file.Path && source.Offset <= recordEnd))
                .Select(pair => pair.Key).ToArray()) Invalidate(key);
        if (file.AverageParser.LatestStartedTurn is { } activity) RememberActivity(file.ThreadId, activity.StartedAt);
        foreach (var context in file.AverageParser.DrainContextObservations()) RememberTurnContext(file.ThreadId, context);
        var exactUsages = file.ResponseParser.DrainUsageObservations();
        foreach (var usage in file.Parser.DrainUsageObservations())
        {
            var exact = exactUsages.FirstOrDefault(value => value.ResponseId == usage.ResponseId && value.ThreadId == usage.ThreadId);
            // Lifecycle parser cancellation state is not the status of an earlier
            // completed response replayed after cancellation. Exact record status wins.
            RememberUsage(exact is null ? usage : usage with { Failed = exact.Failed });
        }
        foreach (var usage in exactUsages) RememberUsage(usage);
        foreach (var responseId in file.ResponseParser.DrainInvalidatedResponseIds()) Invalidate(file.ThreadId + "\nresponse-rate\n" + responseId);
        foreach (var responseId in file.Parser.DrainInvalidatedResponseIds())
            Invalidate(file.ThreadId + "\n" + responseId, includeResponseRate: false);
        RememberSample(emitted, file, recordEnd);
        if (!file.IsHistory) RememberSample(file.Parser.LatestSample, file, recordEnd);
        foreach (var turnId in file.AverageParser.DrainInvalidatedTurnIds()) Invalidate(TurnKey(file.ThreadId, turnId));
        RememberSample(average, file, recordEnd);
        RememberSample(responseRate, file, recordEnd);
        foreach (var response in file.Parser.DrainEffortConflictedResponseIds())
        {
            var key = file.ThreadId + "\n" + response;
            if (samples.TryGetValue(key, out var observed))
            {
                var model = observed.Model;
                if (model is null && recoveredMetadata.TryGetValue(key, out var metadata)) model = metadata.Model;
                if (model is not null) RememberRecoveredMetadata(key, model, null, effortConflict: true);
            }
        }
        if (emitted is not { Model: null, TurnId: not null } || !samples.ContainsKey(Key(emitted))) return;
        if (!file.Parser.CanRecoverModel)
        {
            SetMetadataStatus(emitted, RolloutModelMetadataStatus.Boundary, RolloutModelBoundaryReason.ParserBoundary);
            return;
        }
        if (file.RecoverySample?.TurnId != emitted.TurnId || file.RecoveryEpoch != file.Parser.ModelEpoch)
        {
            // A newer turn/generation supersedes this file's pending lookup. Do not leave
            // the older retained samples claiming that abandoned work is still pending.
            if (file.ModelRecovery is { IsComplete: false })
                foreach (var peer in file.RecoveryPeers.Values) SetMetadataStatus(peer, RolloutModelMetadataStatus.Boundary, RolloutModelBoundaryReason.SupersededRecovery);
            file.RecoverySample = emitted;
            file.RecoveryEpoch = file.Parser.ModelEpoch;
            file.RecoveryPeers.Clear();
            file.ModelRecovery = new RolloutModelRecovery(file.ThreadId, emitted.TurnId, emitted.StartedAt, recordEnd,
                minimumOffset: Math.Max(0, recordEnd - options.MaxModelHistoryBytes),
                maxLineBytes: Math.Min(options.MaxLineBytes, RolloutModelRecovery.MaximumLineBytes),
                maxToolCompletionBytes: Math.Min(RolloutModelRecovery.MaximumWindowBytes, options.MaxBytesPerFilePerPoll / 2));
        }
        if (file.ModelRecovery is not { } recovery) return;
        foreach (var key in file.RecoveryPeers.Keys.Where(key => !samples.ContainsKey(key)).ToArray()) file.RecoveryPeers.Remove(key);
        if (file.RecoveryPeers.Count < options.MaxRememberedSamples) file.RecoveryPeers[Key(emitted)] = emitted;
        SetMetadataStatus(emitted, recovery.Status, recovery.BoundaryReason);
        if (recovery.IsComplete) ApplyRecoveredMetadata(file, recovery);
    }

    private static bool IsFutureRecord(string text)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(text);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                document.RootElement.TryGetProperty("timestamp", out var timestamp) &&
                timestamp.ValueKind == System.Text.Json.JsonValueKind.String &&
                timestamp.TryGetDateTimeOffset(out var at) && at > DateTimeOffset.UtcNow.AddMinutes(1);
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private void RecoverModel(FileCursor file, FileStream stream, ref int remaining, CancellationToken token,
        int byteBudget = int.MaxValue)
    {
        var recovery = file.ModelRecovery;
        if (recovery is null || recovery.IsComplete) return;
        var available = Math.Min(remaining, byteBudget);
        while (!recovery.IsComplete && available >= recovery.MinimumBytesForProgress)
        {
            var used = recovery.Scan(stream, available, token);
            remaining -= used;
            available -= used;
            if (used == 0) break;
        }
        if (!recovery.IsComplete) return;
        ApplyRecoveredMetadata(file, recovery);
    }

    private void ApplyRecoveredMetadata(FileCursor file, RolloutModelRecovery recovery)
    {
        var candidate = file.RecoverySample;
        if (candidate is null) return;
        foreach (var peer in file.RecoveryPeers.Values)
        {
            if (!samples.TryGetValue(Key(peer), out var current) || !current.IsCompatibleWith(peer)) continue;
            if (recovery.ResultModel is not { } model)
            {
                if (recovery.Status == RolloutModelMetadataStatus.Conflict)
                {
                    recoveredMetadata[Key(peer)] = new(null, null, false);
                    metadataStatuses[Key(peer)] = RolloutModelMetadataStatus.Conflict;
                }
                else SetMetadataStatus(peer, recovery.Status, recovery.BoundaryReason);
                continue;
            }
            if (recovery.ContextAt > peer.StartedAt || peer.ThreadId != candidate.ThreadId || peer.TurnId != candidate.TurnId)
            {
                SetMetadataStatus(peer, RolloutModelMetadataStatus.Boundary, RolloutModelBoundaryReason.IncompatibleSample);
                continue;
            }
            RememberRecoveredMetadata(Key(peer), model, recovery.ResultReasoningEffort,
                recovery.Status == RolloutModelMetadataStatus.Conflict);
        }
        // Historical peers are immutable samples from this exact file/generation. A later
        // parser turn need not match for those peers, but restoring live state still must.
        if (recovery.ResultModel is { } restored)
            file.Parser.TryRestoreModel(candidate.TurnId!, restored, file.RecoveryEpoch, recovery.ResultReasoningEffort, recovery.ContextAt);
    }

    private void SetMetadataStatus(OutputSpeedSample sample, RolloutModelMetadataStatus status,
        RolloutModelBoundaryReason reason = RolloutModelBoundaryReason.None)
    {
        var key = Key(sample);
        if (!samples.TryGetValue(key, out var current) || !current.IsCompatibleWith(sample)) return;
        if (current.Model is not null || recoveredMetadata.ContainsKey(key)) return;
        // A concurrent copy may still be searching. Do not disguise a successful label,
        // and retain an explicit stop reason once that copy has finished.
        metadataStatuses[key] = status;
        if (status == RolloutModelMetadataStatus.Boundary) boundaryReasons[key] = reason == RolloutModelBoundaryReason.None ? RolloutModelBoundaryReason.Unknown : reason;
        else boundaryReasons.Remove(key);
    }

    private void RememberRecoveredMetadata(string key, string model, string? effort, bool effortConflict = false)
    {
        var modelConflict = samples.TryGetValue(key, out var sample) && sample.Model is not null && sample.Model != model;
        if (recoveredMetadata.TryGetValue(key, out var prior))
        {
            modelConflict |= prior.Model is null || prior.Model != model;
            effortConflict |= prior.EffortConflict || prior.ReasoningEffort is not null && effort is not null && prior.ReasoningEffort != effort;
            effort ??= prior.ReasoningEffort;
        }
        effortConflict |= sample?.ReasoningEffort is not null && effort is not null && sample.ReasoningEffort != effort;
        if (sample?.TimingReliability == OutputSpeedTimingReliability.TurnAverage && (modelConflict || effortConflict))
            { Invalidate(key); return; }
        recoveredMetadata[key] = new(modelConflict ? null : model, modelConflict || effortConflict ? null : effort, effortConflict);
        metadataStatuses[key] = modelConflict || effortConflict ? RolloutModelMetadataStatus.Conflict : RolloutModelMetadataStatus.Known;
    }

    private void RememberSample(OutputSpeedSample? current, FileCursor file, long recordEnd)
    {
        // A malformed/far-future log clock must not pin the most recent reading forever.
        // Also retain Feed's emitted sample so a later normal response can recover even
        // when this file's parser still has a future-dated LatestSample.
        if (current is null || current.CompletedAt > DateTimeOffset.UtcNow.AddMinutes(1)) return;
        RememberActivity(current.ThreadId, current.CompletedAt);
        var key = Key(current);
        // Scoped direct model labels can corroborate or contradict the same response
        // across metric types. Whole-turn backward-search ambiguity is not a claim
        // about a later response and must never be broadcast into this comparison.
        var rawKey = current.ThreadId + "\n" + current.ResponseId;
        var rateKey = current.ThreadId + "\nresponse-rate\n" + current.ResponseId;
        if (current.Model is not null)
        {
            foreach (var peerKey in new[] { rawKey, rateKey })
                if (samples.TryGetValue(peerKey, out var peer) && peer.Model is not null &&
                    peer.ThreadId == current.ThreadId && peer.ResponseId == current.ResponseId &&
                    peer.TurnId == current.TurnId && peer.OutputTokens == current.OutputTokens)
                {
                    if (peer.Model != current.Model) Invalidate(rateKey);
                    else if ((current.TimingReliability == OutputSpeedTimingReliability.ResponseRateEstimate || samples.ContainsKey(rateKey)) &&
                        peer.ReasoningEffort is not null && current.ReasoningEffort is not null && peer.ReasoningEffort != current.ReasoningEffort)
                        RememberRecoveredMetadata(rateKey, current.Model, null, effortConflict: true);
                }
        }
        if (invalidSamples.Contains(key)) return;
        if (current.TimingReliability == OutputSpeedTimingReliability.TurnAverage &&
            turnContexts.TryGetValue(key, out var context) && ContextContradicts(context, current)) { Invalidate(key); return; }
        if (current.TurnAverage is { } average)
            foreach (var response in average.Responses)
            {
                var responseKey = current.ThreadId + "\n" + response.ResponseId;
                if (invalidSamples.Contains(responseKey) || usages.TryGetValue(responseKey, out var usage) &&
                    (UsageContradicts(usage.Observation, current, response)))
                    { Invalidate(key); return; }
            }
        if (samples.TryGetValue(key, out var existing) && (!existing.IsCompatibleWith(current) ||
            current.TimingReliability == OutputSpeedTimingReliability.TurnAverage && existing.ReasoningEffort != current.ReasoningEffort)) { Invalidate(key); return; }
        if (existing is not null && current.TimingReliability is OutputSpeedTimingReliability.TurnAverage or OutputSpeedTimingReliability.ResponseRateEstimate &&
            existing.ThreadRequestedSetting != current.ThreadRequestedSetting)
            current = current with { ThreadRequestedSetting = ThreadRequestedSetting.Unknown };
        if (existing?.ReasoningEffort is not null && current.ReasoningEffort is not null &&
            existing.ReasoningEffort != current.ReasoningEffort && (existing.Model ?? current.Model) is { } model)
            RememberRecoveredMetadata(key, model, null, effortConflict: true);
        if (!sampleSources.TryGetValue(key, out var source) || IsArchive(source.Path) && !IsArchive(file.Path))
            sampleSources[key] = new(file.Path, recordEnd, observationOrder++);
        samples[key] = existing is { Model: not null } && current.Model is null ? existing : current;
        // Direct copies and recovered history share one bounded metadata cache. Missing
        // fields cannot erase verified labels, and a conflict is a permanent tombstone.
        if (current.Model is { } directModel) RememberRecoveredMetadata(key, directModel, current.ReasoningEffort);
        if (!metadataStatuses.ContainsKey(key)) metadataStatuses[key] = current.Model is null
            ? RolloutModelMetadataStatus.NotFound : RolloutModelMetadataStatus.Known;
        if (samples.Count > options.MaxRememberedSamples)
        {
            historyLimited = true;
            var protectedKeys = new HashSet<string>(samples.Values.Where(IsCompleteAverage)
                .GroupBy(sample => sample.ThreadId, StringComparer.Ordinal)
                .Select(group => Key(group.OrderByDescending(sample => sample.CompletedAt).First())), StringComparer.Ordinal);
            var newest = samples.Values.OrderByDescending(sample => sample.CompletedAt).ThenBy(sample => sample.ThreadId, StringComparer.Ordinal).First();
            protectedKeys.Add(Key(newest));
            // Pin one latest complete group inside, not in addition to, the existing
            // sample budget. Arbitrarily many unverified responses cannot evict it.
            var pinned = OrderedSamples(samples.Values.Where(IsCompleteResponse)).FirstOrDefault() ??
                OrderedSamples(samples.Values.Where(IsCompleteAverage)).FirstOrDefault();
            var oldest = samples.Values.OrderBy(sample => pinned is not null && Key(sample) == Key(pinned))
                .ThenBy(sample => protectedKeys.Contains(Key(sample)))
                .ThenBy(sample => sample.CompletedAt).ThenBy(sample => sample.ThreadId, StringComparer.Ordinal).First();
            samples.Remove(Key(oldest));
            sampleSources.Remove(Key(oldest));
            recoveredMetadata.Remove(Key(oldest));
            metadataStatuses.Remove(Key(oldest));
            boundaryReasons.Remove(Key(oldest));
        }
    }

    private static bool IsCompleteResponse(OutputSpeedSample sample) =>
        sample.TimingReliability == OutputSpeedTimingReliability.ResponseRateEstimate && sample.TokensPerSecond.HasValue;

    private static bool IsCompleteAverage(OutputSpeedSample sample) =>
        sample.TimingReliability == OutputSpeedTimingReliability.TurnAverage && sample.Model is not null && sample.TokensPerSecond.HasValue;

    private void RememberActivity(string threadId, DateTimeOffset at)
    {
        if (at > DateTimeOffset.UtcNow.AddMinutes(1)) return;
        if (latestActivity is null || at > latestActivity.At) latestActivity = new(threadId, at);
        else if (at == latestActivity.At && threadId != latestActivity.ThreadId)
            latestActivity = new(null, at); // A tie cannot prove a unique owning thread.
    }

    private void RememberTurnContext(string threadId, RolloutTurnContextObservation observation)
    {
        var key = TurnKey(threadId, observation.TurnId);
        if (!turnContexts.TryGetValue(key, out var prior)) turnContextOrder.Enqueue(key);
        var conflict = observation.Model is null || prior is not null && (prior.Model is null || prior.Model != observation.Model);
        var effortConflict = prior?.EffortConflict == true || prior?.ReasoningEffort is { } oldEffort &&
            observation.ReasoningEffort is { } newEffort && oldEffort != newEffort;
        var context = new RecoveredMetadata(conflict ? null : observation.Model,
            observation.ReasoningEffort ?? prior?.ReasoningEffort, effortConflict);
        turnContexts[key] = context;
        if (samples.TryGetValue(key, out var average) && ContextContradicts(context, average)) Invalidate(key);
        while (turnContextOrder.Count > options.MaxRememberedSamples) turnContexts.Remove(turnContextOrder.Dequeue());
    }

    private static bool ContextContradicts(RecoveredMetadata context, OutputSpeedSample sample) =>
        context.Model is null || context.Model != sample.Model || context.EffortConflict ||
        context.ReasoningEffort is not null && sample.ReasoningEffort is not null && context.ReasoningEffort != sample.ReasoningEffort;

    private void RememberUsage(OutputSpeedUsageObservation observation)
    {
        var key = observation.ThreadId + "\n" + observation.ResponseId;
        foreach (var average in samples.Values.Where(value => value.ThreadId == observation.ThreadId && value.TurnAverage is { } evidence &&
            evidence.Responses.Any(response => response.ResponseId == observation.ResponseId && UsageContradicts(observation, value, response))).ToArray())
            Invalidate(Key(average));
        if (observation.Failed) Invalidate(key);
        if (usages.TryGetValue(key, out var prior))
        {
            if (prior.Observation.TurnId != observation.TurnId ||
                prior.Observation.OutputTokens != observation.OutputTokens ||
                prior.Observation.Failed != observation.Failed ||
                prior.Observation.TurnOutputTokens is { } oldTotal && observation.TurnOutputTokens is { } newTotal && oldTotal != newTotal ||
                prior.Observation.ReasoningOutputTokens is { } oldReasoning && observation.ReasoningOutputTokens is { } newReasoning && oldReasoning != newReasoning)
                Invalidate(key);
            usages[key] = prior with { Observation = prior.Observation with
            {
                TurnOutputTokens = prior.Observation.TurnOutputTokens ?? observation.TurnOutputTokens,
                ReasoningOutputTokens = prior.Observation.ReasoningOutputTokens ?? observation.ReasoningOutputTokens
            } };
            return;
        }
        usages.Add(key, new RememberedUsage(observation, observationOrder++));
        if (usages.Count > 2 * options.MaxRememberedSamples)
        {
            historyLimited = true;
            // Keep the signatures for every retained sample, even if its duplicate in another
            // file lacks usable timing. Evict only older observations with no displayed candidate.
            var oldest = usages.Where(pair => !samples.ContainsKey(pair.Key))
                .OrderBy(pair => pair.Value.Order).First();
            usages.Remove(oldest.Key);
        }
    }

    private void Invalidate(string key, bool includeResponseRate = true)
    {
        // A turn average depends on every member response, including copies whose
        // own lifecycle timestamps are unavailable. Conflicts withdraw the average.
        foreach (var average in samples.Values.Where(value => value.TurnAverage is { } evidence &&
            evidence.Responses.Any(response => value.ThreadId + "\n" + response.ResponseId == key)).ToArray())
            Invalidate(Key(average));
        if (includeResponseRate && !key.Contains("\nresponse-rate\n") && !key.Contains("\nturn-average\n"))
        {
            var split = key.IndexOf('\n');
            if (split > 0) Invalidate(key.Substring(0, split) + "\nresponse-rate\n" + key.Substring(split + 1));
        }
        samples.Remove(key);
        sampleSources.Remove(key);
        recoveredMetadata.Remove(key);
        metadataStatuses.Remove(key);
        boundaryReasons.Remove(key);
        if (!invalidSamples.Add(key)) return;
        invalidOrder.Enqueue(key);
        while (invalidOrder.Count > options.MaxRememberedSamples)
        {
            historyLimited = true;
            invalidSamples.Remove(invalidOrder.Dequeue());
        }
    }

    private static bool UsageContradicts(OutputSpeedUsageObservation observation, OutputSpeedSample average, TurnResponseUsage response) =>
        observation.Failed || observation.TurnId != average.TurnId || observation.OutputTokens != response.OutputTokens ||
        observation.TurnOutputTokens is { } cumulative && cumulative != response.TurnOutputTokens ||
        observation.ReasoningOutputTokens is { } reasoning && response.ReasoningOutputTokens is { } expected && reasoning != expected;

    private static string TurnKey(string threadId, string turnId) => threadId + "\nturn-average\n" + turnId;
    private static string Key(OutputSpeedSample sample) => sample.TimingReliability == OutputSpeedTimingReliability.TurnAverage
        ? TurnKey(sample.ThreadId, sample.TurnId ?? sample.ResponseId) : sample.TimingReliability == OutputSpeedTimingReliability.ResponseRateEstimate
        ? sample.ThreadId + "\nresponse-rate\n" + sample.ResponseId : sample.ThreadId + "\n" + sample.ResponseId;

    private static ulong Fingerprint(FileStream stream, long offset, int count, ref int remaining)
    {
        if (count == 0) return 0;
        stream.Position = offset;
        var buffer = new byte[count];
        var read = stream.Read(buffer, 0, count);
        remaining -= read;
        var hash = 14695981039346656037UL;
        for (var index = 0; index < read; index++) hash = unchecked((hash ^ buffer[index]) * 1099511628211UL);
        Array.Clear(buffer, 0, buffer.Length);
        return hash ^ (ulong)read;
    }

    private static bool IsFileError(Exception exception) => exception is IOException ||
        exception is UnauthorizedAccessException || exception is SecurityException ||
        exception is ArgumentException || exception is NotSupportedException;

    // Windows file identity detects an atomic same-length replacement even when its producer
    // preserves creation/write times. Other platforms use bounded prefix/offset fingerprints
    // and metadata; detecting arbitrary in-place edits with all metadata preserved would require
    // rescanning history, which this incremental reader deliberately does not do.
    private static FileIdentity? ReadIdentity(FileStream stream)
    {
        if (Path.DirectorySeparatorChar != '\\') return null;
        return GetFileInformationByHandle(stream.SafeFileHandle, out var info)
            ? new FileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow)
            : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private readonly record struct FileIdentity(uint Volume, ulong Index);
    private sealed record RecoveredMetadata(string? Model, string? ReasoningEffort, bool EffortConflict);
    private sealed record RememberedUsage(OutputSpeedUsageObservation Observation, long Order);
    private sealed record LatestActivity(string? ThreadId, DateTimeOffset At);

    private sealed record SampleSource(string Path, long Offset, long Order);
    private sealed class DiscoveryCursor(string root) : IDisposable
    {
        public Stack<IEnumerator<string>> Frames { get; } = new();
        public string? NextDirectory { get; set; } = root;
        public bool Complete { get; set; }
        public void Dispose() { while (Frames.Count != 0) Frames.Pop().Dispose(); Complete = true; }
    }

    private sealed class FileCursor(string path, string threadId, int maximumLineBytes)
    {
        public string Path { get; } = path;
        public string ThreadId { get; } = threadId;
        public RolloutSpeedParser Parser { get; set; } = new(threadId);
        public TurnAverageParser AverageParser { get; set; } = new(threadId);
        public ResponseRateParser ResponseParser { get; set; } = new(threadId);
        public RolloutMetadataProjection Projection { get; set; } = new(maximumLineBytes);
        public FileCursor? History { get; set; }
        public long HistoryLimit { get; set; }
        public bool IsHistory { get; init; }
        public bool ForwardBacklog { get; set; }
        public bool ReadFailed { get; set; }
        public RolloutModelRecovery? ModelRecovery { get; set; }
        public OutputSpeedSample? RecoverySample { get; set; }
        public long RecoveryEpoch { get; set; }
        public Dictionary<string, OutputSpeedSample> RecoveryPeers { get; } = new(StringComparer.Ordinal);
        public bool Initialized { get; set; }
        public bool SkipUntilNewline { get; set; }
        public long Offset { get; set; }
        public long ObservedLength { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public DateTime DiscoveredWriteUtc { get; set; }
        public DateTime CreationUtc { get; set; }
        public FileIdentity? Identity { get; set; }
        public int PrefixCount { get; set; }
        public ulong Prefix { get; set; }
        public long AnchorOffset { get; set; }
        public int AnchorCount { get; set; }
        public ulong Anchor { get; set; }
        public void Restart()
        {
            Parser = new RolloutSpeedParser(ThreadId);
            AverageParser = new TurnAverageParser(ThreadId);
            ResponseParser = new ResponseRateParser(ThreadId);
            Projection = new RolloutMetadataProjection(maximumLineBytes);
            History = null;
            ForwardBacklog = false;
            ModelRecovery = null;
            RecoverySample = null;
            RecoveryPeers.Clear();
            Offset = 0;
            SkipUntilNewline = false;
        }
    }
}
