using System.Globalization;
using System.Text.Json;

namespace CodexQuota.Core;

public enum ResponseRateReason
{
    NotObserved, Ready, AwaitingUsage, IdentityMismatch, MissingStart, MissingModel,
    InvalidRecord, InvalidUsage, InvalidDuration, ConflictingUsage, MixedModel,
    Cancelled, ReadGap, LegacyUsageUnsupported
}

/// <summary>Bounded labels and counts only. No content, account information, or raw identifiers.</summary>
public sealed record ResponseRateDiagnostic(ResponseRateReason Reason,
    ResponseRateStartBoundary StartBoundary, ResponseRateEndBoundary EndBoundary,
    ResponseRateUsageKind UsageKind, bool StartSeen, bool ModelSeen, bool UsageSeen, int ResponseCount);

/// <summary>
/// One file's effective response-rate estimates from scoped per-response usage.
/// Rollout write timestamps bound the estimate; they are not request dispatch or
/// token-generation timestamps. No body text or tool arguments survive Feed.
/// </summary>
public sealed class ResponseRateParser
{
    private const int MaximumResponses = 512;
    private const int MaximumRecordCharacters = 1024 * 1024;
    private readonly string threadId;
    private readonly Dictionary<string, SeenUsage> responses = new(StringComparer.Ordinal);
    private readonly Queue<string> responseOrder = new();
    private readonly Queue<string> invalidated = new();
    private readonly Queue<OutputSpeedUsageObservation> observations = new();
    private bool ownSection, identityBoundary, frozen, mixedModel, cancelled, exactSeen, coldGap;
    private string? turnId, model, effort;
    private DateTimeOffset? startAt, pendingNextAt, lastCompletionAt, taskStartedAt, modelContextAt;
    private ResponseRateStartBoundary startBoundary, pendingNextBoundary, diagnosticStartBoundary;
    private ResponseRateReason reason;
    private ResponseRateEndBoundary endBoundary;
    private ResponseRateUsageKind usageKind;
    private bool startSeen, modelSeen, usageSeen;

    public ResponseRateParser(string threadId)
    {
        if (!ValidLabel(threadId, 160)) throw new ArgumentException("A bounded rollout owner is required.", nameof(threadId));
        this.threadId = threadId;
    }

    public OutputSpeedSample? LatestSample { get; private set; }
    public string? CurrentTurnId => turnId;
    public DateTimeOffset? LastObservedAt { get; private set; }
    public bool HasOwnedSection => ownSection;
    public bool HasPendingResponseActivity => reason == ResponseRateReason.AwaitingUsage;
    public ThreadRequestedSettingObservation ThreadSetting { get; private set; } = new(ThreadRequestedSetting.Unknown, null);
    public ResponseRateDiagnostic Diagnostic => new(reason, diagnosticStartBoundary, endBoundary, usageKind,
        startSeen, modelSeen, usageSeen, responses.Count);

    public IReadOnlyList<string> DrainInvalidatedResponseIds()
    {
        var result = invalidated.ToArray(); invalidated.Clear(); return result;
    }

    public IReadOnlyList<OutputSpeedUsageObservation> DrainUsageObservations()
    {
        var result = observations.ToArray(); observations.Clear(); return result;
    }

    public bool TakeIdentityBoundary() { var result = identityBoundary; identityBoundary = false; return result; }

    /// <summary>A skipped region loses context and in-flight timing, never a completed historical sample.</summary>
    public void ResetPending()
    {
        ClearTurn();
        coldGap = true;
        ThreadSetting = new(ThreadRequestedSetting.Unknown, null, BrokenBoundary: true);
        reason = ResponseRateReason.ReadGap;
    }

    public void Reset()
    {
        ResetPending(); ownSection = identityBoundary = exactSeen = coldGap = false;
        responses.Clear(); responseOrder.Clear(); invalidated.Clear(); observations.Clear();
        LatestSample = null; LastObservedAt = lastCompletionAt = null;
        reason = ResponseRateReason.NotObserved;
    }

    public OutputSpeedSample? Feed(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaximumRecordCharacters)
            { Damage(); return null; }
        try
        {
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            var kind = Label(record, "type", 64);
            if (kind is null || !Property(record, "payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
                !Timestamp(record, out var at)) { Damage(); return null; }
            if (kind == "session_meta")
            {
                var owner = Label(payload, "id");
                if (ownSection && owner != threadId) identityBoundary = true;
                ClearTurn();
                ownSection = owner == threadId;
                ThreadSetting = new(ThreadRequestedSetting.Unknown, null);
                reason = ownSection ? ResponseRateReason.NotObserved : ResponseRateReason.IdentityMismatch;
                LastObservedAt = at;
                return null;
            }
            if (!ownSection) { reason = ResponseRateReason.IdentityMismatch; return null; }
            if (LastObservedAt is null || at > LastObservedAt) LastObservedAt = at;
            if (kind == "token_usage_record") return ReadUsage(payload, at);
            var eventKind = Label(payload, "type", 64);
            if (kind is "event_msg" or "response_item" && eventKind is null) { Damage(); return null; }
            if (!OptionalThreadMatches(payload)) { reason = ResponseRateReason.IdentityMismatch; return null; }
            if (kind == "turn_context") { ReadContext(payload, at); return null; }
            if (kind == "event_msg")
            {
                if (eventKind == "thread_settings_applied") { ReadSettings(payload, at); return null; }
                if (eventKind == "task_started") { StartTurn(payload, at); return null; }
                if (!OptionalTurnMatches(payload)) return null;
                if (eventKind is "turn_aborted" or "task_failed")
                {
                    // A later failed turn does not undo a response which already completed.
                    ClearWindow(); cancelled = true; reason = ResponseRateReason.Cancelled; return null;
                }
                if (eventKind is "error" or "stream_error")
                {
                    // A stream retry can still complete later in the same turn.
                    // The damaged request loses its start; subsequent exact
                    // completion or an explicit task start can resynchronize.
                    ClearWindow(); coldGap = true;
                    if (taskStartedAt is null) modelContextAt = null;
                    reason = ResponseRateReason.InvalidRecord; return null;
                }
                if (eventKind == "task_complete") { ClearWindow(); cancelled = true; return null; }
                if (eventKind == "token_count")
                {
                    // Official exact usage precedes this delayed snapshot (and tool waits).
                    // Legacy snapshots have no response ID and cannot double-count a response.
                    if (!exactSeen) { reason = ResponseRateReason.LegacyUsageUnsupported; usageKind = ResponseRateUsageKind.LegacyTokenCount; }
                    return null;
                }
                if (eventKind == "user_message" && turnId is not null && !cancelled)
                    ObserveInput(at, ResponseRateStartBoundary.UserInput);
                return null; // Item lifecycle events do not prove a generation interval.
            }
            if (kind == "response_item" && turnId is not null && !cancelled && OptionalTurnMatches(payload))
            {
                if (payload.TryGetProperty("internal_chat_message_metadata_passthrough", out _))
                {
                    if (!Property(payload, "internal_chat_message_metadata_passthrough", out var metadata) ||
                        metadata.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) { Damage(); return null; }
                    if (metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("turn_id", out _))
                    {
                        if (!Property(metadata, "turn_id", out var scopedTurn)) { Damage(); return null; }
                        if (scopedTurn.ValueKind != JsonValueKind.Null)
                        {
                            var itemTurn = Label(metadata, "turn_id");
                            if (itemTurn is null) { Damage(); return null; }
                            if (itemTurn != turnId) { reason = ResponseRateReason.IdentityMismatch; return null; }
                        }
                    }
                }
                if (eventKind is "function_call_output" or "custom_tool_call_output" or "tool_search_output")
                    ObserveInput(at, ResponseRateStartBoundary.ToolResult);
                else if (eventKind == "agent_message" || eventKind == "message" && Label(payload, "role", 32) is "user" or "developer" or "system")
                    ObserveInput(at, ResponseRateStartBoundary.UserInput);
                else if (eventKind is "reasoning" or "function_call" or "custom_tool_call" or "local_shell_call" or
                    "tool_search_call" or "web_search_call" or "image_generation_call" ||
                    eventKind == "message" && Label(payload, "role", 32) == "assistant")
                {
                    // Freeze the pre-output input boundary. A parallel tool's later
                    // result belongs to a following request, never this response's start.
                    frozen = true;
                    reason = ResponseRateReason.AwaitingUsage;
                }
            }
            return null;
        }
        catch (JsonException) { Damage(); return null; }
    }

    private void StartTurn(JsonElement payload, DateTimeOffset at)
    {
        var id = Label(payload, "turn_id");
        if (id is null) { Damage(); return; }
        if (turnId == id && taskStartedAt is { } previous)
        {
            if (previous != at) { ClearWindow(); reason = ResponseRateReason.InvalidRecord; }
            return;
        }
        // Some compatible histories write explicit scoped context before start.
        // Preserve only pristine same-turn metadata; context never supplies time.
        var preserveContext = turnId == id && taskStartedAt is null && !usageSeen && !frozen && !cancelled &&
            model is not null && modelContextAt is { } contextAt && contextAt <= at;
        var priorModel = preserveContext ? model : null;
        var priorEffort = preserveContext ? effort : null;
        ClearTurn(); coldGap = false; turnId = id; taskStartedAt = at; model = priorModel; effort = priorEffort;
        modelSeen = model is not null;
        startAt = at; startBoundary = diagnosticStartBoundary = ResponseRateStartBoundary.TaskStarted; startSeen = true;
        reason = ResponseRateReason.AwaitingUsage;
    }

    private void ReadContext(JsonElement payload, DateTimeOffset at)
    {
        var contextTurn = Label(payload, "turn_id");
        var unscoped = !payload.TryGetProperty("turn_id", out _) || Property(payload, "turn_id", out var field) && field.ValueKind == JsonValueKind.Null;
        if (contextTurn is null && (!unscoped || turnId is null)) { Damage(); return; }
        if (contextTurn is not null && contextTurn != turnId) { ClearTurn(); turnId = contextTurn; }
        var nextModel = Label(payload, "model", 128);
        var nextEffort = HistoricalReasoningEffort.Read(payload);
        if (frozen && (nextModel != model || nextEffort != effort)) mixedModel = true;
        model = nextModel; effort = nextEffort; modelSeen = model is not null;
        modelContextAt = contextTurn is not null ? at : null;
        if (nextModel is null) reason = ResponseRateReason.MissingModel;
    }

    private void ReadSettings(JsonElement payload, DateTimeOffset at)
    {
        // Settings can label Fast, but never establish this file's identity or model.
        if (Label(payload, "thread_id") != threadId) return;
        var hasSettings = Property(payload, "thread_settings", out var settings) && settings.ValueKind == JsonValueKind.Object;
        var tier = hasSettings ? Label(settings, "service_tier", 32) : null;
        ThreadSetting = new(tier switch
        {
            "priority" or "fast" => ThreadRequestedSetting.Fast,
            "default" or "standard" or "off" => ThreadRequestedSetting.Standard,
            _ => ThreadRequestedSetting.Unknown
        }, at);
    }

    private OutputSpeedSample? ReadUsage(JsonElement payload, DateTimeOffset at)
    {
        var owner = Label(payload, "thread_id");
        var id = Label(payload, "response_id");
        var usageTurn = Label(payload, "turn_id");
        if (owner != threadId) { reason = ResponseRateReason.IdentityMismatch; return null; }
        if (id is null || usageTurn is null)
        {
            ClearWindow(); coldGap = true; if (taskStartedAt is null) modelContextAt = null;
            reason = ResponseRateReason.IdentityMismatch; return null;
        }
        var hasCount = Property(payload, "usage", out var usage) && ReadCount(usage, out _, out _);
        long count = 0; long? reasoning = null;
        if (hasCount) ReadCount(usage, out count, out reasoning);
        var failed = !hasCount || Failed(payload);
        var observation = new OutputSpeedUsageObservation(threadId, id, usageTurn, hasCount ? count : null, failed)
        {
            ReasoningOutputTokens = reasoning,
            TurnOutputTokens = Property(payload, "turn_token_usage", out var cumulative) &&
                Integer(cumulative, "output_tokens", out var cumulativeCount) && cumulativeCount >= 0 ? cumulativeCount : null
        };
        observations.Enqueue(observation); Trim(observations);
        // Resolve every replay before touching pending input or response state.
        if (responses.TryGetValue(id, out var previous))
        {
            if (Contradicts(previous.Observation, observation))
            {
                previous.Invalid = true; Invalidate(id); reason = ResponseRateReason.ConflictingUsage;
            }
            else previous.Observation = previous.Observation with
            {
                ReasoningOutputTokens = previous.Observation.ReasoningOutputTokens ?? observation.ReasoningOutputTokens,
                TurnOutputTokens = previous.Observation.TurnOutputTokens ?? observation.TurnOutputTokens
            };
            return null;
        }
        if (turnId is not null && usageTurn != turnId) { reason = ResponseRateReason.IdentityMismatch; return null; }
        // Old replay beyond the bounded cache cannot become a fresh request or
        // move a baseline backwards. A clock rollback affects only that response.
        if (lastCompletionAt is { } watermark && at < watermark)
            { reason = ResponseRateReason.InvalidDuration; return null; }
        Remember(id, observation);
        exactSeen = usageSeen = true; usageKind = ResponseRateUsageKind.PerResponse;
        endBoundary = ResponseRateEndBoundary.TokenUsageRecord;
        if (turnId is null) turnId = usageTurn;
        var responseStart = startAt; var boundary = startBoundary; diagnosticStartBoundary = boundary;
        startSeen = responseStart.HasValue; modelSeen = model is not null;
        var responseModel = model; var responseEffort = effort;
        var reject = cancelled ? ResponseRateReason.Cancelled : failed ? ResponseRateReason.InvalidUsage :
            mixedModel ? ResponseRateReason.MixedModel : responseStart is null ? ResponseRateReason.MissingStart :
            at <= responseStart ? ResponseRateReason.InvalidDuration : responseModel is null ? ResponseRateReason.MissingModel : ResponseRateReason.Ready;
        if (!cancelled)
        {
            // Timing validity is independent of the primary rejection label:
            // a bad count or mixed model cannot make a backward timestamp safe.
            if (responseStart is { } knownStart && at <= knownStart)
            {
                ClearWindow();
                if (at < knownStart) coldGap = true;
            }
            else CompleteWindow(at);
        }
        reason = reject;
        if (failed) { responses[id].Invalid = true; Invalidate(id); }
        if (reject != ResponseRateReason.Ready) return null;
        var setting = ThreadSetting.RecordedAt is { } settingAt && settingAt <= at && !ThreadSetting.BrokenBoundary
            ? ThreadSetting.Setting : ThreadRequestedSetting.Unknown;
        var sample = new OutputSpeedSample(threadId, id, responseModel, count, responseStart!.Value, at)
        {
            TurnId = usageTurn, ReasoningEffort = responseEffort, RecordedAt = at, ThreadRequestedSetting = setting,
            TimingReliability = OutputSpeedTimingReliability.ResponseRateEstimate,
            ResponseRate = new(boundary, ResponseRateEndBoundary.TokenUsageRecord, ResponseRateUsageKind.PerResponse)
        };
        LatestSample = sample;
        return sample;
    }

    private void ObserveInput(DateTimeOffset at, ResponseRateStartBoundary boundary)
    {
        // A skipped region may hide an already-running response. Its parallel
        // tool result is not proof that a fresh request started here. First see
        // an explicit task start or one exact response completion to synchronize.
        if (coldGap) { reason = ResponseRateReason.MissingStart; return; }
        if (lastCompletionAt is { } completed && at < completed) return;
        if (frozen)
        {
            if (pendingNextAt is null || at >= pendingNextAt)
                { pendingNextAt = at; pendingNextBoundary = boundary; reason = ResponseRateReason.AwaitingUsage; }
        }
        else if (startAt is null || at >= startAt)
            { startAt = at; startBoundary = diagnosticStartBoundary = boundary; startSeen = true; reason = ResponseRateReason.AwaitingUsage; }
    }

    private void CompleteWindow(DateTimeOffset at)
    {
        // Early parallel tool results must not pull the next request back before
        // the preceding response completed. Later explicit inputs can advance it.
        var next = pendingNextAt is { } pending && pending > at ? pending : at;
        var boundary = next > at ? pendingNextBoundary : ResponseRateStartBoundary.PreviousResponseCompletion;
        ClearWindow(); startAt = next; startBoundary = boundary;
        coldGap = false;
        if (lastCompletionAt is null || at > lastCompletionAt) lastCompletionAt = at;
    }

    private void Remember(string id, OutputSpeedUsageObservation observation)
    {
        responses.Add(id, new(observation)); responseOrder.Enqueue(id);
        while (responseOrder.Count > MaximumResponses) responses.Remove(responseOrder.Dequeue());
    }

    private void Invalidate(string id)
    {
        invalidated.Enqueue(id); Trim(invalidated);
        if (LatestSample?.ResponseId == id) LatestSample = null;
    }

    private void Damage()
    {
        ResetPending(); reason = ResponseRateReason.InvalidRecord;
    }

    private void ClearWindow()
    {
        startAt = pendingNextAt = null; startBoundary = pendingNextBoundary = ResponseRateStartBoundary.NotObserved;
        frozen = mixedModel = false;
    }

    private void ClearTurn()
    {
        ClearWindow(); turnId = model = effort = null; taskStartedAt = lastCompletionAt = modelContextAt = null; cancelled = false;
        startSeen = modelSeen = usageSeen = false;
        diagnosticStartBoundary = ResponseRateStartBoundary.NotObserved;
        endBoundary = ResponseRateEndBoundary.NotObserved; usageKind = ResponseRateUsageKind.NotObserved;
    }

    private bool OptionalThreadMatches(JsonElement value) => !value.TryGetProperty("thread_id", out _) || Label(value, "thread_id") == threadId;
    private bool OptionalTurnMatches(JsonElement value) => !value.TryGetProperty("turn_id", out _) || Label(value, "turn_id") == turnId;
    private static bool Failed(JsonElement value) => value.TryGetProperty("status", out _) && Label(value, "status", 32) is not ("completed" or "success");
    private static void Trim<T>(Queue<T> queue) { while (queue.Count > MaximumResponses) queue.Dequeue(); }
    private static bool Contradicts(OutputSpeedUsageObservation first, OutputSpeedUsageObservation second) =>
        first.ThreadId != second.ThreadId || first.TurnId != second.TurnId || first.ResponseId != second.ResponseId ||
        first.OutputTokens != second.OutputTokens || first.Failed != second.Failed ||
        first.ReasoningOutputTokens is { } oldReasoning && second.ReasoningOutputTokens is { } newReasoning && oldReasoning != newReasoning ||
        first.TurnOutputTokens is { } oldTotal && second.TurnOutputTokens is { } newTotal && oldTotal != newTotal;

    private static bool ReadCount(JsonElement value, out long count, out long? reasoning)
    {
        reasoning = null;
        if (!Integer(value, "output_tokens", out count) || count < 0) return false;
        if (!value.TryGetProperty("reasoning_output_tokens", out _)) return true;
        if (!Integer(value, "reasoning_output_tokens", out var inner) || inner < 0 || inner > count) return false;
        reasoning = inner; return true;
    }

    private static bool Timestamp(JsonElement value, out DateTimeOffset at)
    {
        at = default; var text = Label(value, "timestamp", 64);
        return text is not null && text.IndexOf('T') > 0 &&
            (text.EndsWith("Z", StringComparison.Ordinal) || text.Length >= 6 && text[text.Length - 3] == ':' && text[text.Length - 6] is '+' or '-') &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out at);
    }

    private static string? Label(JsonElement value, string name, int limit = 160)
    {
        if (!Property(value, name, out var field) || field.ValueKind != JsonValueKind.String) return null;
        var result = field.GetString(); return ValidLabel(result, limit) ? result : null;
    }
    private static bool ValidLabel(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value!.Length <= limit && !value.Any(char.IsControl);
    private static bool Integer(JsonElement value, string name, out long result)
    {
        result = 0; return Property(value, name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out result);
    }
    private static bool Property(JsonElement value, string name, out JsonElement result)
    {
        result = default;
        if (value.ValueKind != JsonValueKind.Object) return false;
        var found = false;
        foreach (var property in value.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            if (found) return false;
            result = property.Value; found = true;
        }
        return found;
    }
    private sealed class SeenUsage(OutputSpeedUsageObservation observation)
    {
        public OutputSpeedUsageObservation Observation { get; set; } = observation;
        public bool Invalid { get; set; }
    }
}
