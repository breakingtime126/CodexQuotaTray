using System.Globalization;
using System.Text.Json;

namespace CodexQuota.Core;

public sealed record RolloutTurnActivity(string TurnId, DateTimeOffset StartedAt);
public sealed record RolloutTurnContextObservation(string TurnId, string? Model, string? ReasoningEffort);

/// <summary>
/// A separate, fail-closed metric: scoped output tokens / official whole-turn duration.
/// Never uses item lifecycle timestamps or subtracts TTFT. Only bounded metadata survives Feed.
/// </summary>
public sealed class TurnAverageParser
{
    private const int MaximumResponses = 512;
    private readonly string threadId;
    private readonly Queue<string> invalidated = new();
    private readonly Queue<RolloutTurnContextObservation> contexts = new();
    private bool identityBoundary;
    private bool ownSection;
    private TurnState? turn;
    private TurnAverageReason reason;
    private TurnAverageReason lastReject;
    private bool startSeen, modelSeen, usageSeen, completionSeen, durationPresent;
    private int responseCount;
    public ThreadRequestedSettingObservation ThreadSetting { get; private set; } = new(ThreadRequestedSetting.Unknown, null);
    public RolloutTurnActivity? LatestStartedTurn { get; private set; }
    public DateTimeOffset? LastObservedAt { get; private set; }
    public string? CurrentTurnId => turn?.Id;
    public bool HasOwnedSection => ownSection;
    public TurnAverageDiagnostic Diagnostic => new(reason != TurnAverageReason.NotObserved ? reason :
        !ownSection ? TurnAverageReason.IdentityMismatch : turn is null ? TurnAverageReason.MissingStart :
        turn.Finished ? TurnAverageReason.Ready : turn.Model is null ? TurnAverageReason.MissingModel :
        turn.Usages.Count == 0 ? TurnAverageReason.AwaitingUsage : TurnAverageReason.AwaitingCompletion,
        lastReject, startSeen, modelSeen, usageSeen, responseCount, completionSeen, durationPresent);

    public TurnAverageParser(string threadId) => this.threadId = threadId;

    public void ResetPending(TurnAverageReason cause = TurnAverageReason.ReadGap)
    {
        if (turn is { Finished: false }) turn.Invalid = true;
        turn = null;
        reason = cause;
        lastReject = cause;
        ThreadSetting = new(ThreadRequestedSetting.Unknown, null, BrokenBoundary: true);
    }

    public IReadOnlyList<string> DrainInvalidatedTurnIds()
    {
        var result = invalidated.ToArray();
        invalidated.Clear();
        return result;
    }

    public IReadOnlyList<RolloutTurnContextObservation> DrainContextObservations()
    {
        var result = contexts.ToArray(); contexts.Clear(); return result;
    }

    public bool TakeIdentityBoundary()
    {
        var result = identityBoundary; identityBoundary = false; return result;
    }

    public OutputSpeedSample? Feed(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 1024 * 1024) { Damage(); return null; }
        try
        {
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            var kind = Label(record, "type", 64);
            if (kind is null || !Property(record, "payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
                !Timestamp(record, out var at)) { Damage(); return null; }
            if (kind == "session_meta")
            {
                if (ownSection && Label(payload, "id") != threadId) identityBoundary = true;
                ResetPending();
                LatestStartedTurn = null;
                ownSection = Label(payload, "id") == threadId;
                ThreadSetting = new(ThreadRequestedSetting.Unknown, null);
                ClearDiagnostic();
                LastObservedAt = at;
                return null;
            }
            var eventKind = Label(payload, "type", 64);
            if (kind is "event_msg" or "response_item" && eventKind is null) { Damage(); return null; }
            if (kind == "event_msg" && eventKind == "thread_settings_applied")
            {
                if (Label(payload, "thread_id") != threadId) { if (ownSection) Damage(); return null; }
                if (!ownSection) { ResetPending(); ownSection = true; }
                LastObservedAt = at;
                var hasSettings = Property(payload, "thread_settings", out var threadSettings) && threadSettings.ValueKind == JsonValueKind.Object;
                var tier = hasSettings ? Label(threadSettings, "service_tier", 32) : null;
                ThreadSetting = new(tier switch
                {
                    "priority" or "fast" => ThreadRequestedSetting.Fast,
                    "default" or "standard" or "off" => ThreadRequestedSetting.Standard,
                    _ => ThreadRequestedSetting.Unknown
                }, at);
                if (turn is { Finished: false } current)
                {
                    if (!Property(payload, "thread_settings", out var settings) ||
                        Label(settings, "model", 128) != current.Model ||
                        HistoricalReasoningEffort.Read(settings, settings: true) != current.Effort) Reject(current.Id, TurnAverageReason.MixedModelOrEffort);
                }
                return null;
            }
            if (!ownSection) return null;
            LastObservedAt = at;
            // Explicit evidence can target an earlier completed turn, including
            // an archive containing no active turn. Never cancel a different turn.
            if (kind == "event_msg" && eventKind is "turn_aborted" or "task_failed" or "error" or "stream_error")
            {
                if (!ThreadMatches(payload)) { Damage(); return null; }
                var target = Label(payload, "turn_id");
                var failure = eventKind == "turn_aborted" ? TurnAverageReason.TurnCancelled : TurnAverageReason.TurnFailed;
                if (target is not null) Reject(target, failure);
                else if (payload.TryGetProperty("turn_id", out _)) Damage();
                else if (turn is { Finished: false }) Reject(turn.Id, failure);
                return null;
            }
            if (kind == "turn_context" && ThreadMatches(payload) && Label(payload, "turn_id") is { } contextId)
            {
                contexts.Enqueue(new(contextId, Label(payload, "model", 128), HistoricalReasoningEffort.Read(payload)));
                if (contexts.Count > MaximumResponses) contexts.Dequeue();
            }
            if (kind == "event_msg" && eventKind == "task_started")
            {
                var id = Label(payload, "turn_id");
                if (id is null || !ThreadMatches(payload)) { Damage(); return null; }
                if (turn?.Id == id)
                {
                    if (turn.StartedAt != at || turn.Finished) Reject(id);
                    return null;
                }
                turn = new TurnState(id, at);
                LatestStartedTurn = new(id, at);
                ClearDiagnostic(); startSeen = true;
                return null;
            }
            if (kind == "token_usage_record" && Label(payload, "thread_id") == threadId &&
                Label(payload, "turn_id") is { } usageTurn && (turn is null || usageTurn == turn.Id)) usageSeen = true;
            if (kind == "event_msg" && eventKind == "task_complete" && ThreadMatches(payload) &&
                Label(payload, "turn_id") is { } completedTurn && (turn is null || completedTurn == turn.Id))
            {
                completionSeen = true;
                durationPresent = payload.TryGetProperty("duration_ms", out var observedDuration) && observedDuration.ValueKind != JsonValueKind.Null;
            }
            if (turn is null) return null; // A cold tail cannot reconstruct an unseen turn start.
            if (at < turn.StartedAt) { Damage(); return null; }
            if (kind == "turn_context")
            {
                var contextTurn = Label(payload, "turn_id");
                var unscoped = !payload.TryGetProperty("turn_id", out _) ||
                    Property(payload, "turn_id", out var turnField) && turnField.ValueKind == JsonValueKind.Null;
                // Official legacy contexts may omit their optional turn ID. Bind
                // only before output to the explicit, continuously observed start.
                if (!ThreadMatches(payload) || contextTurn != turn.Id &&
                    !(unscoped && !turn.Invalid && !turn.Finished && !turn.PendingOutput && turn.Usages.Count == 0))
                    { Damage(); return null; }
                var model = Label(payload, "model", 128);
                var effort = HistoricalReasoningEffort.Read(payload);
                if (model is null || turn.Model is null && (turn.PendingOutput || turn.Usages.Count != 0) ||
                    turn.Model is not null && (turn.Model != model || turn.Effort != effort)) { Reject(turn.Id, TurnAverageReason.MixedModelOrEffort); return null; }
                turn.Model = model;
                turn.Effort = effort;
                modelSeen = true;
            }
            else if (kind == "response_item")
            {
                if (Failed(payload)) { Damage(TurnAverageReason.TurnFailed); return null; }
                if (eventKind == "reasoning" || eventKind!.EndsWith("_call", StringComparison.Ordinal) ||
                    eventKind == "message" && Label(payload, "role", 32) == "assistant")
                    turn.PendingOutput = true;
            }
            else if (kind == "token_usage_record") ReadUsage(payload, at);
            else if (kind == "event_msg")
            {
                switch (eventKind)
                {
                    case "item_completed":
                        if (!ThreadMatches(payload) || Label(payload, "turn_id") != turn.Id) { Damage(); break; }
                        if (!Property(payload, "item", out var item) || item.ValueKind != JsonValueKind.Object)
                            { Damage(); break; }
                        if (Failed(payload)) { Damage(TurnAverageReason.TurnFailed); break; }
                        if (Failed(item) && !IsTerminalToolOutcome(item)) { Damage(TurnAverageReason.ItemStatusRejected); break; }
                        if (Label(item, "type", 64) is "Reasoning" or "AgentMessage") turn.PendingOutput = true;
                        break;
                    case "task_complete": return Complete(payload, at);
                }
            }
            return null;
        }
        catch (JsonException) { Damage(); return null; }
    }

    private void ReadUsage(JsonElement payload, DateTimeOffset at)
    {
        var current = turn!;
        var id = Label(payload, "response_id");
        if (Label(payload, "thread_id") != threadId || Label(payload, "turn_id") != current.Id || id is null)
            { Reject(current.Id, TurnAverageReason.IdentityMismatch); return; }
        if (!Property(payload, "usage", out var usage)) { Reject(current.Id, TurnAverageReason.MissingUsage); return; }
        if (Failed(payload) || !ReadCount(usage, out var count, out var reasoning))
            { Reject(current.Id, TurnAverageReason.InvalidUsage); return; }
        if (!Property(payload, "turn_token_usage", out var cumulative))
            { Reject(current.Id, TurnAverageReason.MissingCumulativeUsage); return; }
        if (!ReadCount(cumulative, out var cumulativeCount, out _)) { Reject(current.Id, TurnAverageReason.InvalidUsage); return; }
        var observation = new TurnResponseUsage(id, count, reasoning, cumulativeCount);
        if (current.Usages.TryGetValue(id, out var previous))
        {
            if (previous != observation || current.PendingOutput) Reject(current.Id, TurnAverageReason.UsageMismatch);
            // A repeated usage snapshot is not a second response.
            return;
        }
        if (current.Finished) { Reject(current.Id, TurnAverageReason.UsageMismatch); return; }
        if (current.Model is null) { Reject(current.Id, TurnAverageReason.MissingModel); return; }
        if (current.Usages.Count >= MaximumResponses) { Reject(current.Id, TurnAverageReason.ResponseLimit); return; }
        long total;
        try { total = checked(current.OutputTokens + count); }
        catch (OverflowException) { Reject(current.Id, TurnAverageReason.InvalidUsage); return; }
        // Remote compaction can persist usage without ordinary response items.
        // Its complete scoped ledger is still output included in whole-turn time.
        // Require the official same-turn cumulative checkpoint as independent
        // coverage evidence. Missing checkpoints never silently downgrade the metric.
        if (cumulativeCount != total)
            { Reject(current.Id, TurnAverageReason.UsageMismatch); return; }
        current.Usages.Add(id, observation);
        responseCount = current.Usages.Count;
        current.OutputTokens = total;
        current.PendingOutput = false;
        // A later record can have an earlier wall-clock timestamp. Keep the
        // maximum so rollback cannot hide usage recorded after completion.
        if (at > current.LastUsageAt) current.LastUsageAt = at;
    }

    private OutputSpeedSample? Complete(JsonElement payload, DateTimeOffset at)
    {
        var current = turn!;
        var id = Label(payload, "turn_id");
        if (id != current.Id || !ThreadMatches(payload))
        {
            if (id is not null) Reject(id, TurnAverageReason.IdentityMismatch);
            Damage(TurnAverageReason.IdentityMismatch);
            return null;
        }
        if (current.Invalid) { Reject(id, reason); return null; } // Preserve the cause and existing withdrawal timing.
        if (Failed(payload) || payload.TryGetProperty("error", out _) &&
            (!Property(payload, "error", out var error) || error.ValueKind != JsonValueKind.Null))
            { Reject(id, TurnAverageReason.TurnFailed); return null; }
        if (current.Model is null) { Reject(id, TurnAverageReason.MissingModel); return null; }
        if (current.Usages.Count == 0) { Reject(id, TurnAverageReason.MissingUsage); return null; }
        if (current.PendingOutput) { Reject(id, TurnAverageReason.UnsealedOutput); return null; }
        if (current.LastUsageAt > at) { Reject(id, TurnAverageReason.InvalidRecord); return null; }
        if (!payload.TryGetProperty("duration_ms", out var durationField) || durationField.ValueKind == JsonValueKind.Null)
            { Reject(id, TurnAverageReason.MissingDuration); return null; }
        if (!Integer(payload, "duration_ms", out var duration) || duration <= 0 || duration > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond)
            { Reject(id, TurnAverageReason.InvalidDuration); return null; }
        if (current.Finished && (current.Duration != duration || current.CompletedAt != at)) { Reject(id, TurnAverageReason.UsageMismatch); return null; }
        if (!current.Finished) current.CompletionSetting = ThreadSetting.RecordedAt is { } settingAt && settingAt <= at && !ThreadSetting.BrokenBoundary
            ? ThreadSetting.Setting : ThreadRequestedSetting.Unknown;
        current.Finished = true;
        current.Duration = duration;
        current.CompletedAt = at;
        reason = TurnAverageReason.Ready;
        return new OutputSpeedSample(threadId, id, current.Model, current.OutputTokens, current.StartedAt, at)
        {
            TurnId = id, ReasoningEffort = current.Effort, RecordedAt = at,
            ThreadRequestedSetting = current.CompletionSetting,
            TimingReliability = OutputSpeedTimingReliability.TurnAverage,
            TurnAverage = new TurnAverageEvidence(duration, current.Usages.Values.OrderBy(value => value.ResponseId, StringComparer.Ordinal).ToArray())
        };
    }

    private void Damage(TurnAverageReason cause = TurnAverageReason.InvalidRecord)
    {
        if (turn is { Finished: false })
        {
            if (!turn.Invalid) reason = cause;
            turn.Invalid = true;
        }
        else reason = cause;
        lastReject = cause;
        ThreadSetting = new(ThreadRequestedSetting.Unknown, null, BrokenBoundary: true);
    }
    private void Reject(string id, TurnAverageReason cause = TurnAverageReason.InvalidRecord)
    {
        lastReject = cause;
        if (turn?.Id == id)
        {
            if (!turn.Invalid) reason = cause;
            turn.Invalid = true;
        }
        invalidated.Enqueue(id);
        if (invalidated.Count > MaximumResponses) invalidated.Dequeue();
    }
    private void ClearDiagnostic()
    {
        reason = lastReject = TurnAverageReason.NotObserved;
        startSeen = modelSeen = usageSeen = completionSeen = durationPresent = false;
        responseCount = 0;
    }
    private static bool IsTerminalToolOutcome(JsonElement item)
    {
        var type = Label(item, "type", 64);
        var status = Label(item, "status", 32);
        return (type is "CommandExecution" or "FileChange") && (status is "failed" or "declined") ||
            (type is "DynamicToolCall" or "McpToolCall") && status == "failed";
    }
    private bool ThreadMatches(JsonElement value) => !value.TryGetProperty("thread_id", out _) || Label(value, "thread_id") == threadId;
    private static bool Failed(JsonElement value) => value.TryGetProperty("status", out _) && Label(value, "status", 32) is not ("completed" or "success");
    private static bool ReadCount(JsonElement value, out long count, out long? reasoning)
    {
        reasoning = null;
        if (!Integer(value, "output_tokens", out count) || count < 0) return false;
        if (!value.TryGetProperty("reasoning_output_tokens", out _)) return true;
        if (!Integer(value, "reasoning_output_tokens", out var inner) || inner < 0 || inner > count) return false;
        reasoning = inner;
        return true;
    }
    private static bool Timestamp(JsonElement value, out DateTimeOffset at)
    {
        at = default;
        var text = Label(value, "timestamp", 64);
        return text is not null && text.IndexOf('T') > 0 &&
            (text.EndsWith("Z", StringComparison.Ordinal) || text.Length >= 6 && text[text.Length - 3] == ':' && text[text.Length - 6] is '+' or '-') &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out at);
    }
    private static string? Label(JsonElement value, string name, int limit = 160)
    {
        if (!Property(value, name, out var field) || field.ValueKind != JsonValueKind.String) return null;
        var result = field.GetString();
        return !string.IsNullOrWhiteSpace(result) && result!.Length <= limit && !result.Any(char.IsControl) ? result : null;
    }
    private static bool Integer(JsonElement value, string name, out long result)
    {
        result = 0;
        return Property(value, name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out result);
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
    private sealed class TurnState(string id, DateTimeOffset startedAt)
    {
        public string Id { get; } = id;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public string? Model { get; set; }
        public string? Effort { get; set; }
        public bool Invalid { get; set; }
        public bool PendingOutput { get; set; }
        public bool Finished { get; set; }
        public long Duration { get; set; }
        public DateTimeOffset CompletedAt { get; set; }
        public DateTimeOffset LastUsageAt { get; set; }
        public ThreadRequestedSetting CompletionSetting { get; set; }
        public long OutputTokens { get; set; }
        public Dictionary<string, TurnResponseUsage> Usages { get; } = new(StringComparer.Ordinal);
    }
}
