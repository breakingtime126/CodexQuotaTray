using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexQuota.Core;

/// <summary>Why the selected response's exact historical model is known or unavailable.</summary>
public enum RolloutModelMetadataStatus { Known, PendingHistory, HistoryLimit, Boundary, NotFound, Conflict }

/// <summary>Fixed diagnostic labels only; never carries record content, identities, or file paths.</summary>
public enum RolloutModelBoundaryReason
{
    None,
    Unknown,
    TruncatedStream,
    IncompleteRead,
    IncompleteLine,
    LineTooLong,
    UnrecognizedLargeRecord,
    InvalidUtf8,
    InvalidJson,
    InvalidEnvelope,
    InvalidTimestamp,
    SessionMismatch,
    InvalidTurnIdentity,
    ThreadMismatch,
    MetadataAfterSample,
    InvalidModel,
    InvalidEventKind,
    InvalidSettings,
    ParserBoundary,
    SupersededRecovery,
    IncompatibleSample
}

/// <summary>
/// Searches backward for the exact historical context of an already validated sample.
/// Only offsets and bounded identity/model metadata survive a call; no rollout text is retained.
/// The caller must already have established ownership and discard this cursor if the file changes.
/// </summary>
public sealed class RolloutModelRecovery
{
    public const int MaximumWindowBytes = 256 * 1024;
    public const int MaximumLineBytes = 128 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string threadId;
    private readonly string turnId;
    private readonly DateTimeOffset sampleStartedAt;
    private readonly int maxLineBytes;
    private readonly int maxToolCompletionBytes;
    private long? partialLineEnd;
    private int pendingLineLength;
    private string? candidateModel;
    private string? candidateReasoningEffort;
    private bool effortConflict;
    private DateTimeOffset? candidateContextAt;
    private bool sawMatchingStart;
    private string? settingsModel;
    private string? settingsEffort;
    private DateTimeOffset? settingsAt;

    public RolloutModelRecovery(string threadId, string turnId, DateTimeOffset sampleStartedAt,
        long endOffset, int maxLineBytes = MaximumLineBytes, long minimumOffset = 0,
        int maxToolCompletionBytes = MaximumWindowBytes)
    {
        if (!IsLabel(threadId, 160)) throw new ArgumentException("A bounded rollout owner is required.", nameof(threadId));
        if (!IsLabel(turnId, 160)) throw new ArgumentException("An exact bounded turn ID is required.", nameof(turnId));
        if (endOffset < 0) throw new ArgumentOutOfRangeException(nameof(endOffset));
        if (minimumOffset < 0 || minimumOffset > endOffset) throw new ArgumentOutOfRangeException(nameof(minimumOffset));
        if (maxLineBytes < 1 || maxLineBytes > MaximumLineBytes) throw new ArgumentOutOfRangeException(nameof(maxLineBytes));
        if (maxToolCompletionBytes < maxLineBytes || maxToolCompletionBytes > MaximumWindowBytes)
            throw new ArgumentOutOfRangeException(nameof(maxToolCompletionBytes));
        this.maxToolCompletionBytes = maxToolCompletionBytes;
        this.threadId = threadId;
        this.turnId = turnId;
        this.sampleStartedAt = sampleStartedAt;
        this.maxLineBytes = maxLineBytes;
        EndOffset = endOffset;
        MinimumOffset = minimumOffset;
        Offset = endOffset;
        if (endOffset == minimumOffset) CompleteAtLimit();
    }

    public string? ResultModel { get; private set; }
    public DateTimeOffset? ContextAt { get; private set; }
    public string? ResultReasoningEffort { get; private set; }
    public RolloutModelMetadataStatus Status { get; private set; } = RolloutModelMetadataStatus.PendingHistory;
    public RolloutModelBoundaryReason BoundaryReason { get; private set; }
    public bool IsComplete { get; private set; }
    public long EndOffset { get; }
    public long MinimumOffset { get; }

    /// <summary>The backward cursor, never advanced into a record after the original seal.</summary>
    public long Offset { get; private set; }

    /// <summary>
    /// A split line is reread only when it can be parsed completely in one call. A caller
    /// with a smaller remaining poll budget can defer it without retaining partial text.
    /// </summary>
    public int MinimumBytesForProgress => IsComplete ? 0 : Math.Max(1, pendingLineLength);

    /// <summary>Returns actual bytes read, including bounded split-line rereads.</summary>
    public int Scan(FileStream stream, int byteBudget, CancellationToken cancellationToken = default) =>
        Scan((Stream)stream, byteBudget, cancellationToken);

    /// <summary>
    /// Consumes at most the supplied byte budget and 256 KiB per call. The stream must
    /// be readable and seekable; its position is unspecified afterward. The end offset
    /// must be immediately after a complete JSONL newline, never a partial EOF record.
    /// </summary>
    public int Scan(Stream stream, int byteBudget, CancellationToken cancellationToken = default)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead || !stream.CanSeek) throw new ArgumentException("A readable seekable stream is required.", nameof(stream));
        if (byteBudget < 0) throw new ArgumentOutOfRangeException(nameof(byteBudget));
        cancellationToken.ThrowIfCancellationRequested();
        if (IsComplete || byteBudget == 0) return 0;
        if (stream.Length < EndOffset) { StopBoundary(RolloutModelBoundaryReason.TruncatedStream); return 0; }
        var remaining = Math.Min(byteBudget, MaximumWindowBytes);
        var used = 0;

        while (!IsComplete && remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pendingLineLength > 0)
            {
                if (remaining < pendingLineLength) break;
                var line = new byte[pendingLineLength];
                try
                {
                    var read = ReadAt(stream, Offset, line, ref remaining, cancellationToken);
                    used += read;
                    if (read != line.Length) { StopBoundary(RolloutModelBoundaryReason.IncompleteRead); break; }
                    pendingLineLength = 0;
                    ReadRecord(line, 0, read);
                }
                finally { Array.Clear(line, 0, line.Length); }
                if (!IsComplete && Offset == MinimumOffset) CompleteAtLimit();
                continue;
            }

            // A split suffix needs only its preceding delimiter. Small framing reads
            // avoid rereading a whole scan window when its line is just a few bytes long.
            var count = (int)Math.Min(Math.Min(remaining, partialLineEnd.HasValue ? 4096 : MaximumWindowBytes),
                Offset - MinimumOffset);
            if (count == 0) { CompleteAtLimit(); break; }
            var start = Offset - count;
            var buffer = new byte[count];
            try
            {
                var read = ReadAt(stream, start, buffer, ref remaining, cancellationToken);
                used += read;
                if (read != count) { StopBoundary(RolloutModelBoundaryReason.IncompleteRead); break; }

                if (partialLineEnd is { } end)
                {
                    // Only the location of the split suffix was kept. Locate its start,
                    // then reread that one bounded line without keeping JSON across polls.
                    var newline = PreviousNewline(buffer, read - 1);
                    var lineStart = newline < 0 ? start : start + newline + 1;
                    if (end - lineStart > maxToolCompletionBytes) { StopBoundary(RolloutModelBoundaryReason.LineTooLong); break; }
                    Offset = lineStart;
                    if (newline < 0 && start != 0)
                    {
                        if (start == MinimumOffset) CompleteAtLimit();
                        continue;
                    }
                    partialLineEnd = null;
                    pendingLineLength = (int)(end - lineStart);
                    if (pendingLineLength == 0) StopBoundary(RolloutModelBoundaryReason.IncompleteLine);
                    continue;
                }

                // A broken/truncated seal boundary cannot establish historical identity.
                if (buffer[read - 1] != (byte)'\n') { StopBoundary(RolloutModelBoundaryReason.IncompleteLine); break; }
                var lineEnd = read - 1;
                while (!IsComplete)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var newline = PreviousNewline(buffer, lineEnd - 1);
                    var lineStart = newline + 1;
                    var length = lineEnd - lineStart;
                    if (length > maxToolCompletionBytes) { StopBoundary(RolloutModelBoundaryReason.LineTooLong); break; }
                    if (newline < 0 && start != 0)
                    {
                        partialLineEnd = start + lineEnd;
                        Offset = start;
                        if (start == MinimumOffset) CompleteAtLimit();
                        break;
                    }
                    Offset = start + lineStart;
                    ReadRecord(buffer, lineStart, length);
                    if (newline < 0 || Offset == MinimumOffset) { if (!IsComplete) CompleteAtLimit(); break; }
                    lineEnd = newline;
                }
            }
            finally { Array.Clear(buffer, 0, buffer.Length); }
        }
        return used;
    }

    private static int ReadAt(Stream stream, long offset, byte[] buffer, ref int remaining,
        CancellationToken cancellationToken)
    {
        stream.Position = offset;
        var total = 0;
        while (total < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            remaining -= read;
            total += read;
        }
        return total;
    }

    private static int PreviousNewline(byte[] buffer, int index)
    {
        for (; index >= 0; index--) if (buffer[index] == (byte)'\n') return index;
        return -1;
    }

    private void ReadRecord(byte[] bytes, int start, int length)
    {
        try
        {
            if (length >= 3 && bytes[start] == 0xef && bytes[start + 1] == 0xbb && bytes[start + 2] == 0xbf)
            {
                start += 3;
                length -= 3;
            }
            if (length > 0 && bytes[start + length - 1] == (byte)'\r') length--;
            using var document = JsonDocument.Parse(StrictUtf8.GetString(bytes, start, length));
            var record = document.RootElement;
            var kind = Label(record, "type", 64);
            if (kind is null || !Property(record, "payload", out var payload) ||
                payload.ValueKind != JsonValueKind.Object)
            {
                StopBoundary(RolloutModelBoundaryReason.InvalidEnvelope);
                return;
            }
            if (!ReadTimestamp(record, out var at))
            {
                StopBoundary(RolloutModelBoundaryReason.InvalidTimestamp);
                return;
            }
            if (length > maxLineBytes)
            {
                // A large tool result is not a model boundary. Keep the ordinary line
                // cap for metadata, but fully validate this narrow non-output shape
                // within the existing scan window before discarding its body.
                if (!IsToolCompletion(record, payload, kind)) StopBoundary(RolloutModelBoundaryReason.UnrecognizedLargeRecord);
                return;
            }
            if (kind == "session_meta")
            {
                if (Label(payload, "id", 160) == threadId) AcceptCandidate();
                else StopBoundary(RolloutModelBoundaryReason.SessionMismatch);
                return;
            }
            if (kind == "turn_context")
            {
                var contextTurn = Label(payload, "turn_id", 160);
                if (contextTurn is null)
                {
                    StopBoundary(RolloutModelBoundaryReason.InvalidTurnIdentity);
                    return;
                }
                if (!OptionalThreadMatches(payload))
                {
                    StopBoundary(RolloutModelBoundaryReason.ThreadMismatch);
                    return;
                }
                if (at > sampleStartedAt)
                {
                    StopBoundary(RolloutModelBoundaryReason.MetadataAfterSample);
                    return;
                }
                if (contextTurn != turnId) { AcceptCandidate(); return; }
                var model = Label(payload, "model", 128);
                if (model is null || candidateModel is not null && model != candidateModel)
                {
                    if (model is null) StopBoundary(RolloutModelBoundaryReason.InvalidModel);
                    else Stop(RolloutModelMetadataStatus.Conflict);
                    return;
                }
                var effort = HistoricalReasoningEffort.Read(payload);
                if (candidateModel is null)
                {
                    candidateModel = model;
                    candidateReasoningEffort = effort;
                    candidateContextAt = at;
                }
                else if (candidateReasoningEffort != effort)
                {
                    // Consistent model evidence does not make disagreeing effort labels safe.
                    candidateReasoningEffort = null;
                    effortConflict = true;
                }
                if (settingsModel is not null && settingsModel != model)
                {
                    Stop(RolloutModelMetadataStatus.Conflict);
                    return;
                }
                if (settingsModel is not null && settingsEffort != effort)
                {
                    candidateReasoningEffort = null;
                    effortConflict = true;
                }
                // A scoped applied-settings echo at this exact context timestamp
                // confirms the nearest snapshot. An unpaired frozen snapshot still
                // needs its earlier turn boundary, since it can conceal a change.
                if (sawMatchingStart || settingsAt == at) AcceptCandidate();
                return;
            }
            if (kind != "event_msg") return;
            var eventKind = Label(payload, "type", 64);
            if (eventKind is null) { StopBoundary(RolloutModelBoundaryReason.InvalidEventKind); return; }
            if (eventKind == "thread_settings_applied")
            {
                ReadSettings(payload, at);
                return;
            }
            if (eventKind == "task_started")
            {
                var startedTurn = Label(payload, "turn_id", 160);
                if (startedTurn is null) StopBoundary(RolloutModelBoundaryReason.InvalidTurnIdentity);
                else if (!OptionalThreadMatches(payload)) StopBoundary(RolloutModelBoundaryReason.ThreadMismatch);
                else if (at > sampleStartedAt) StopBoundary(RolloutModelBoundaryReason.MetadataAfterSample);
                else if (startedTurn != turnId || candidateModel is not null) AcceptCandidate();
                else sawMatchingStart = true;
            }
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            // A damaged line could hide an ownership/turn boundary. Never look past it.
            StopBoundary(exception is DecoderFallbackException ? RolloutModelBoundaryReason.InvalidUtf8 :
                RolloutModelBoundaryReason.InvalidJson);
        }
    }

    private bool IsToolCompletion(JsonElement record, JsonElement payload, string kind)
    {
        if (kind != "event_msg" || Label(payload, "type", 64) != "item_completed" ||
            Label(payload, "thread_id", 160) != threadId || Label(payload, "turn_id", 160) != turnId ||
            !Property(payload, "item", out var item) || item.ValueKind != JsonValueKind.Object ||
            Label(item, "type", 64) is not ("FileChange" or "CommandExecution")) return false;
        return UniqueProperties(record) && UniqueProperties(payload) && UniqueProperties(item);
    }

    private void ReadSettings(JsonElement payload, DateTimeOffset at)
    {
        if (Label(payload, "thread_id", 160) != threadId)
        {
            StopBoundary(RolloutModelBoundaryReason.ThreadMismatch);
            return;
        }
        if (at > sampleStartedAt)
        {
            StopBoundary(RolloutModelBoundaryReason.MetadataAfterSample);
            return;
        }
        if (!Property(payload, "thread_settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
        {
            StopBoundary(RolloutModelBoundaryReason.InvalidSettings);
            return;
        }
        if (Label(settings, "model", 128) is not { } model)
        {
            StopBoundary(RolloutModelBoundaryReason.InvalidModel);
            return;
        }
        if (settingsModel is not null && settingsModel != model || candidateModel is not null && candidateModel != model)
        {
            Stop(RolloutModelMetadataStatus.Conflict);
            return;
        }
        var effort = HistoricalReasoningEffort.Read(settings, settings: true);
        if (settingsModel is not null && settingsEffort != effort || candidateModel is not null && candidateReasoningEffort != effort)
        {
            candidateReasoningEffort = null;
            effortConflict = true;
        }
        settingsModel = model;
        settingsEffort = effort;
        settingsAt = at;
        if (candidateContextAt == at) AcceptCandidate();
    }

    private static bool UniqueProperties(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name)) return false;
        return true;
    }

    private void AcceptCandidate()
    {
        Stop(candidateModel is null ? RolloutModelMetadataStatus.NotFound :
            effortConflict ? RolloutModelMetadataStatus.Conflict : RolloutModelMetadataStatus.Known);
        ResultModel = candidateModel;
        ResultReasoningEffort = effortConflict ? null : candidateReasoningEffort;
        ContextAt = candidateContextAt;
    }

    private void CompleteAtLimit() => Stop(MinimumOffset > 0
        ? RolloutModelMetadataStatus.HistoryLimit : RolloutModelMetadataStatus.NotFound);

    private void StopBoundary(RolloutModelBoundaryReason reason)
    {
        BoundaryReason = reason;
        Stop(RolloutModelMetadataStatus.Boundary);
    }

    private void Stop(RolloutModelMetadataStatus status)
    {
        IsComplete = true;
        Status = status;
    }

    private bool OptionalThreadMatches(JsonElement payload) =>
        !payload.TryGetProperty("thread_id", out _) || Label(payload, "thread_id", 160) == threadId;

    private static bool ReadTimestamp(JsonElement record, out DateTimeOffset at)
    {
        at = default;
        var timestamp = Label(record, "timestamp", 64);
        if (timestamp is null) return false;
        var zone = timestamp.EndsWith("Z", StringComparison.Ordinal) ||
            timestamp.Length >= 6 && timestamp[timestamp.Length - 3] == ':' &&
            timestamp[timestamp.Length - 6] is '+' or '-';
        return zone && timestamp.IndexOf('T') > 0 && DateTimeOffset.TryParse(timestamp,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out at);
    }

    private static string? Label(JsonElement parent, string name, int limit)
    {
        if (!Property(parent, name, out var field) || field.ValueKind != JsonValueKind.String) return null;
        var value = field.GetString();
        return IsLabel(value, limit) ? value : null;
    }

    private static bool IsLabel(string? value, int limit) =>
        !string.IsNullOrWhiteSpace(value) && value!.Length <= limit && !value.Any(char.IsControl);

    private static bool Property(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Object) return false;
        var found = false;
        foreach (var property in parent.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            if (found) return false;
            value = property.Value;
            found = true;
        }
        return found;
    }
}
