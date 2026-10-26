using System.Globalization;
using System.Text.Json;

namespace CodexQuota.Core;

/// <summary>
/// Incrementally consumes one rollout in file order. Only identities, model labels,
/// integer counts and timing metadata survive Feed; content and tool arguments do not.
/// The owner must come from the rollout filename, not its possibly inherited header.
/// </summary>
public sealed class RolloutSpeedParser
{
    private const int MaximumIdentityLength = 160;
    private const int MaximumItems = 1024;
    private const int MaximumResponses = 512;
    private const int MaximumRecordCharacters = 1024 * 1024;
    private static readonly TimeSpan TimestampPrecision = TimeSpan.FromMilliseconds(1);
    private readonly string _threadId;
    private readonly List<OutputItem> _items = new();
    private readonly Dictionary<string, OutputItem> _itemsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ItemTiming> _timings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SeenResponse> _responses = new(StringComparer.Ordinal);
    private readonly Queue<string> _responseOrder = new();
    private readonly Queue<string> _invalidatedResponses = new();
    private readonly Queue<string> _effortConflictedResponses = new();
    private readonly Queue<OutputSpeedUsageObservation> _usageObservations = new();
    private bool _ownSection;
    private bool _explicitTurnStarted;
    private bool _turnHadOutput;
    private bool _boundary;
    private bool _damaged;
    private bool _cancelled;
    private bool _legacyBoundaryPending;
    private bool _hadOutputEvidence;
    private bool _contradictoryOutput;
    private string? _turnId;
    private string? _model;
    private string? _reasoningEffort;
    private DateTimeOffset? _modelContextAt;
    private bool _effortAmbiguous;
    private bool _modelAmbiguous;
    public long ModelEpoch { get; private set; }
    public bool CanRecoverModel => _ownSection && !_modelAmbiguous;

    /// <summary>Restore verified historical context only while the exact parser generation/turn is unchanged.</summary>
    public bool TryRestoreModel(string turnId, string model, long epoch, string? reasoningEffort = null, DateTimeOffset? contextAt = null)
    {
        if (!_ownSection || _turnId != turnId || ModelEpoch != epoch || _modelAmbiguous || _model is not null ||
            _damaged || _cancelled || !IsLabel(model, 128) ||
            reasoningEffort is not null && !HistoricalReasoningEffort.IsSupported(reasoningEffort)) return false;
        _model = model;
        _reasoningEffort = _effortAmbiguous ? null : reasoningEffort;
        _modelContextAt = contextAt;
        return true;
    }
    private DateTimeOffset? _turnStartedAt;

    public RolloutSpeedParser(string threadId)
    {
        if (!IsLabel(threadId, MaximumIdentityLength)) throw new ArgumentException("A bounded rollout owner is required.", nameof(threadId));
        _threadId = threadId;
    }

    /// <summary>
    /// Latest still-consistent response metadata from this file. Lifecycle timing
    /// is unverified and never yields numeric TPS. Inspect after each batch even when
    /// Feed returns null: a conflicting usage replay can withdraw an earlier sample.
    /// </summary>
    public OutputSpeedSample? LatestSample { get; private set; }

    /// <summary>Withdraw these response IDs from any cross-file cache, including older samples.</summary>
    public IReadOnlyList<string> DrainInvalidatedResponseIds()
    {
        var invalidated = _invalidatedResponses.ToArray();
        _invalidatedResponses.Clear();
        return invalidated;
    }

    /// <summary>Clear contradictory effort labels without withdrawing valid numeric measurements.</summary>
    public IReadOnlyList<string> DrainEffortConflictedResponseIds()
    {
        var conflicted = _effortConflictedResponses.ToArray();
        _effortConflictedResponses.Clear();
        return conflicted;
    }

    /// <summary>Usage remains useful conflict evidence even when its timing is incomplete.</summary>
    public IReadOnlyList<OutputSpeedUsageObservation> DrainUsageObservations()
    {
        var observations = _usageObservations.ToArray();
        _usageObservations.Clear();
        return observations;
    }

    /// <summary>Forget all observations, including a previously confirmed owner section.</summary>
    public void Reset()
    {
        ResetPending();
        _ownSection = false;
        _responses.Clear();
        _responseOrder.Clear();
        _invalidatedResponses.Clear();
        _effortConflictedResponses.Clear();
        _usageObservations.Clear();
        LatestSample = null;
    }

    /// <summary>
    /// Mark a skipped/truncated input region. Preserve confirmed owner and completed
    /// samples, but require a fresh turn or usage boundary before measuring again.
    /// </summary>
    public void ResetPending()
    {
        ModelEpoch++;
        ClearPending();
        _turnId = null;
        _turnStartedAt = null;
        _explicitTurnStarted = false;
        _turnHadOutput = false;
        _model = null;
        _reasoningEffort = null;
        _modelContextAt = null;
        _effortAmbiguous = false;
        _modelAmbiguous = false;
        _boundary = false;
        _damaged = true;
        _cancelled = false;
    }

    /// <summary>Return new consistent response metadata, or null. This does not verify generation timing.</summary>
    public OutputSpeedSample? Feed(string jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine) || jsonLine.Length > MaximumRecordCharacters)
        {
            Damage();
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(jsonLine);
            var record = document.RootElement;
            var kind = Label(record, "type", 64);
            if (kind is null || !Property(record, "payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
                !ReadTimestamp(record, out var at))
            {
                Damage();
                return null;
            }

            if (kind == "session_meta")
            {
                var owner = Label(payload, "id");
                ResetPending();
                _ownSection = owner == _threadId;
                return null;
            }
            var eventKind = Label(payload, "type", 64);
            if (kind is "response_item" or "event_msg" && eventKind is null)
            {
                Damage();
                return null;
            }
            if (kind == "event_msg" && eventKind == "thread_settings_applied" && Label(payload, "thread_id") == _threadId)
            {
                if (!_ownSection) ResetPending();
                _ownSection = true;
                // Persistent settings are not response model evidence. A mid-turn change
                // makes the frozen initial-turn model ambiguous until a new turn starts.
                if (_turnId is not null)
                {
                    var hasSettings = Property(payload, "thread_settings", out var settings) &&
                        settings.ValueKind == JsonValueKind.Object;
                    if (!hasSettings || Label(settings, "model", 128) != _model)
                    {
                        _model = null;
                        _reasoningEffort = null;
                        _modelAmbiguous = true;
                        _effortAmbiguous = true;
                        ModelEpoch++;
                    }
                    else if (HistoricalReasoningEffort.Read(settings, settings: true) != _reasoningEffort)
                    {
                        // A later frozen turn snapshot must not overwrite an effort change.
                        _reasoningEffort = null;
                        _effortAmbiguous = true;
                        ModelEpoch++;
                    }
                }
                return null;
            }
            // Forked rollouts can contain complete ancestor turns, including timings.
            if (!_ownSection) return null;

            if (kind == "turn_context")
            {
                if (!OptionalThreadMatches(payload)) { ResetPending(); return null; }
                var contextTurn = Label(payload, "turn_id");
                var unscoped = !payload.TryGetProperty("turn_id", out _) ||
                    Property(payload, "turn_id", out var turnField) && turnField.ValueKind == JsonValueKind.Null;
                if (contextTurn is null && unscoped && _explicitTurnStarted && !_turnHadOutput && !_damaged && !_cancelled &&
                    _turnStartedAt is { } started && at >= started) contextTurn = _turnId;
                if (contextTurn is null) { Damage(); return null; }
                BeginTurn(contextTurn, at);
                var model = Label(payload, "model", 128);
                if (!_modelAmbiguous && _items.Count != 0 && model != _model) _damaged = true;
                var effort = HistoricalReasoningEffort.Read(payload);
                if (_model != model || _reasoningEffort != effort) ModelEpoch++;
                if (_items.Count != 0 && effort != _reasoningEffort) _effortAmbiguous = true;
                _model = _modelAmbiguous ? null : model;
                _reasoningEffort = _modelAmbiguous || _effortAmbiguous ? null : effort;
                _modelContextAt = at;
            }
            else if (kind == "response_item")
            {
                if (eventKind == "message" && Label(payload, "role", 32) is null) { Damage(); return null; }
                if (eventKind is "reasoning" or "function_call" or "custom_tool_call" ||
                    eventKind == "message" && Label(payload, "role", 32) == "assistant")
                    AddOutput(payload, eventKind!, at);
                // Tool results and user/system messages never extend output timing.
            }
            else if (kind == "token_usage_record") return Seal(payload, at);
            else if (kind == "event_msg")
            {
                switch (eventKind)
                {
                    case "task_started":
                        if (!OptionalThreadMatches(payload)) ResetPending();
                        else
                        {
                            var startedTurn = Label(payload, "turn_id");
                            BeginTurn(startedTurn, at);
                            if (startedTurn is not null && !_damaged && !_cancelled) _explicitTurnStarted = true;
                        }
                        break;
                    case "item_completed": AddTiming(payload, at); break;
                    case "turn_aborted":
                    case "task_failed":
                    case "error":
                    case "stream_error":
                        Damage();
                        _cancelled = true;
                        break;
                    case "task_complete":
                        // A whole-turn duration is not evidence of generated-output time.
                        if (_items.Count != 0) Damage();
                        _boundary = false;
                        break;
                    case "token_count":
                        if (_items.Count != 0) _legacyBoundaryPending = true;
                        break;
                    // Legacy token_count is an unscoped snapshot, not a seal. In current
                    // logs it may precede the explicit per-response usage record.
                }
            }
            return null;
        }
        catch (JsonException)
        {
            Damage();
            return null;
        }
    }

    private void BeginTurn(string? turnId, DateTimeOffset at)
    {
        if (turnId is null) { Damage(); return; }
        if (_turnId == turnId) return;
        ClearPending();
        ModelEpoch++;
        _turnId = turnId;
        _turnStartedAt = at;
        _explicitTurnStarted = false;
        _turnHadOutput = false;
        _model = null;
        _reasoningEffort = null;
        _modelContextAt = null;
        _effortAmbiguous = false;
        _modelAmbiguous = false;
        _boundary = true;
        _damaged = false;
        _cancelled = false;
    }

    private void AddOutput(JsonElement payload, string kind, DateTimeOffset at)
    {
        // A legacy usage snapshot cannot seal a sample, but subsequent generated
        // output proves that an explicit response boundary was missed.
        if (_legacyBoundaryPending) Damage();
        _hadOutputEvidence = true;
        _turnHadOutput = true;
        var id = Label(payload, "id");
        if (id is null || IsFailed(payload)) { Damage(); return; }
        if (Property(payload, "internal_chat_message_metadata_passthrough", out var metadata) &&
            Label(metadata, "turn_id") is { } itemTurn && itemTurn != _turnId) _damaged = true;
        var item = new OutputItem(id, kind, at);
        if (_itemsById.TryGetValue(id, out var previous))
        {
            if (previous != item) _damaged = true;
            return;
        }
        if (_items.Count >= MaximumItems) { Damage(); return; }
        if (_items.Count > 0 && _items[_items.Count - 1].CompletedAt - at > TimestampPrecision) _damaged = true;
        _items.Add(item);
        _itemsById.Add(id, item);
    }

    private void AddTiming(JsonElement payload, DateTimeOffset at)
    {
        if (Label(payload, "thread_id") != _threadId) return;
        _hadOutputEvidence = true;
        if (!Property(payload, "item", out var item) || item.ValueKind != JsonValueKind.Object) { Damage(); return; }
        var kind = Label(item, "type", 64);
        // Tool ItemCompleted bounds describe execution, not generated arguments.
        if (kind is not ("Reasoning" or "AgentMessage")) return;
        var id = Label(item, "id");
        var turn = Label(payload, "turn_id");
        if (id is null || turn is null || turn != _turnId ||
            !ReadEpochMilliseconds(payload, "started_at_ms", out var start) ||
            !ReadEpochMilliseconds(payload, "completed_at_ms", out var end) || end < start ||
            end - at > TimestampPrecision || IsFailed(payload) || IsFailed(item))
        {
            Damage();
            return;
        }
        var timing = new ItemTiming(kind, start, end);
        if (_timings.TryGetValue(id, out var previous))
        {
            if (previous != timing) _damaged = true;
            return;
        }
        if (_timings.Count >= MaximumItems) { Damage(); return; }
        _timings.Add(id, timing);
    }

    private OutputSpeedSample? Seal(JsonElement payload, DateTimeOffset at)
    {
        var thread = Label(payload, "thread_id");
        var turn = Label(payload, "turn_id");
        var response = Label(payload, "response_id");
        if (thread != _threadId || response is null || turn is null)
        {
            Damage();
            return null;
        }
        long? tokens = null;
        if (Property(payload, "usage", out var usage) && Integer(usage, "output_tokens", out var count) && count >= 0)
        {
            // Reasoning is included in output, not added to it. When the breakdown
            // is present, contradictory values make the total untrustworthy.
            if (!usage.TryGetProperty("reasoning_output_tokens", out _) ||
                Integer(usage, "reasoning_output_tokens", out var reasoning) && reasoning >= 0 && reasoning <= count)
                tokens = count;
        }
        var failed = IsFailed(payload) || _cancelled;
        _usageObservations.Enqueue(new OutputSpeedUsageObservation(_threadId, response, turn, tokens, failed)
        {
            TurnOutputTokens = Property(payload, "turn_token_usage", out var cumulativeUsage) &&
                Integer(cumulativeUsage, "output_tokens", out var cumulativeOutput) ? cumulativeOutput : null,
            ReasoningOutputTokens = Property(payload, "usage", out var responseUsage) &&
                Integer(responseUsage, "reasoning_output_tokens", out var reasoningOutput) ? reasoningOutput : null
        });
        if (_usageObservations.Count > MaximumResponses) _usageObservations.Dequeue();

        OutputSpeedSample? sample = null;
        if (_boundary && !_damaged && !_cancelled && turn == _turnId && tokens.HasValue && !IsFailed(payload))
            sample = Measure(response, tokens.Value, at);

        if (_responses.TryGetValue(response, out var seen))
        {
            if (sample is not null && seen.Sample is not null && sample.IsCompatibleWith(seen.Sample))
            {
                var conflict = seen.EffortConflict || sample.ReasoningEffort is not null &&
                    seen.Sample.ReasoningEffort is not null && sample.ReasoningEffort != seen.Sample.ReasoningEffort;
                if (conflict && !seen.EffortConflict)
                {
                    _effortConflictedResponses.Enqueue(response);
                    if (_effortConflictedResponses.Count > MaximumResponses) _effortConflictedResponses.Dequeue();
                }
                var merged = seen.Sample with
                {
                    Model = seen.Sample.Model ?? sample.Model,
                    ReasoningEffort = conflict ? null : seen.Sample.ReasoningEffort ?? sample.ReasoningEffort
                };
                seen = seen with { Sample = merged, EffortConflict = conflict };
                _responses[response] = seen;
                RecomputeLatest();
            }
            // Identical snapshots are idempotent. Contradictory counts/identity or a
            // replay with different completed bounds withdraw the previous reading.
            if (seen.Tokens != tokens || seen.TurnId != turn || failed ||
                _hadOutputEvidence && (_damaged || _contradictoryOutput) ||
                sample is not null && seen.Sample is not null && !sample.IsCompatibleWith(seen.Sample))
            {
                _responses[response] = seen with { Sample = null, Invalidated = true };
                if (!seen.Invalidated)
                {
                    _invalidatedResponses.Enqueue(response);
                    if (_invalidatedResponses.Count > MaximumResponses) _invalidatedResponses.Dequeue();
                }
                RecomputeLatest();
            }
            sample = null;
        }
        else
        {
            _responses.Add(response, new SeenResponse(turn, tokens, sample, false));
            _responseOrder.Enqueue(response);
            if (sample is not null && (LatestSample is null || sample.CompletedAt > LatestSample.CompletedAt)) LatestSample = sample;
            if (_responseOrder.Count > MaximumResponses)
            {
                var removed = _responseOrder.Dequeue();
                _responses.Remove(removed);
                if (LatestSample?.ResponseId == removed) RecomputeLatest();
            }
        }

        ClearPending();
        // Explicit usage is also a reliable file-order boundary for the next
        // response. It never repairs a cancelled turn or supplies a model name.
        if (_turnId != turn)
        {
            _turnId = turn; _turnStartedAt = null; _explicitTurnStarted = false; _turnHadOutput = false; _model = null; _reasoningEffort = null;
            _modelContextAt = null; _modelAmbiguous = false; _effortAmbiguous = false; ModelEpoch++;
        }
        _boundary = !_cancelled;
        _damaged = false;
        return sample;
    }

    private OutputSpeedSample? Measure(string response, long tokens, DateTimeOffset sealedAt)
    {
        if (_items.Count == 0) return null;
        // A completion without its generated response_item may be an omitted
        // earlier/later output or a late replay. Never silently drop that timing
        // and divide the full response token count by a shorter partial interval.
        if (_timings.Keys.Any(id => !_itemsById.ContainsKey(id))) return RejectContradiction();
        var first = _items[0];
        if (first.Kind is "function_call" or "custom_tool_call") return RejectContradiction();
        if (!_timings.TryGetValue(first.Id, out var initial)) return null;
        if (initial.CompletedAt <= initial.StartedAt) return RejectContradiction();
        var start = initial.StartedAt;
        if (_turnStartedAt is { } turnStart && turnStart - start > TimestampPrecision) return RejectContradiction();
        var end = initial.CompletedAt;
        foreach (var item in _items)
        {
            if (item.CompletedAt - sealedAt > TimestampPrecision) return RejectContradiction();
            var itemEnd = item.CompletedAt;
            if (_timings.TryGetValue(item.Id, out var timing))
            {
                var expectedKind = item.Kind == "reasoning" ? "Reasoning" : item.Kind == "message" ? "AgentMessage" : null;
                if (expectedKind != timing.Kind || start - timing.StartedAt > TimestampPrecision ||
                    timing.CompletedAt - item.CompletedAt > TimestampPrecision) return RejectContradiction();
                itemEnd = timing.CompletedAt;
            }
            if (itemEnd < start || itemEnd - sealedAt > TimestampPrecision) return RejectContradiction();
            if (itemEnd > end) end = itemEnd;
        }
        if (end <= start) return RejectContradiction();
        var model = _modelContextAt is { } contextAt && contextAt > start ? null : _model;
        return new OutputSpeedSample(_threadId, response, model, tokens, start, end)
        {
            TurnId = _turnId, ReasoningEffort = model is null ? null : _reasoningEffort,
            RecordedAt = sealedAt,
            // Codex may synthesize a positive ItemStarted -> ItemCompleted span
            // while handling an already-completed output item. Completion also
            // awaits lifecycle contributors before reading its clock. Persisted
            // JSONL has no stream-start provenance to distinguish these paths.
            TimingReliability = OutputSpeedTimingReliability.UnverifiedItemLifecycle
        };
    }

    private void RecomputeLatest() => LatestSample = _responses.Values.Where(value => value.Sample is not null)
        .Select(value => value.Sample!).OrderByDescending(value => value.CompletedAt).FirstOrDefault();

    private void Damage()
    {
        ModelEpoch++;
        var hadOutput = _hadOutputEvidence;
        ClearPending();
        _hadOutputEvidence = hadOutput;
        _boundary = false;
        _damaged = true;
        _explicitTurnStarted = false;
        _model = null;
        _reasoningEffort = null;
        _modelContextAt = null;
    }

    private void ClearPending()
    {
        _items.Clear();
        _itemsById.Clear();
        _timings.Clear();
        _legacyBoundaryPending = false;
        _hadOutputEvidence = false;
        _contradictoryOutput = false;
    }

    private OutputSpeedSample? RejectContradiction()
    {
        _contradictoryOutput = true;
        return null;
    }

    private static bool IsFailed(JsonElement value) => Label(value, "status", 32) is "incomplete" or "cancelled" or "canceled" or "failed";

    private bool OptionalThreadMatches(JsonElement value)
    {
        // This field is absent in older turn records. If present, it is affirmative
        // identity evidence and must agree; a foreign context cannot change our turn.
        return !value.TryGetProperty("thread_id", out _) || Label(value, "thread_id") == _threadId;
    }

    private static bool ReadEpochMilliseconds(JsonElement parent, string name, out DateTimeOffset value)
    {
        value = default;
        if (!Integer(parent, name, out var milliseconds) || milliseconds <= 0 || milliseconds > 253402300799999L) return false;
        value = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        return true;
    }

    private static bool ReadTimestamp(JsonElement parent, out DateTimeOffset value)
    {
        value = default;
        var timestamp = Label(parent, "timestamp", 64);
        if (timestamp is null) return false;
        // DateTimeOffset.TryParse otherwise silently assumes the computer's zone.
        var zone = timestamp.EndsWith("Z", StringComparison.Ordinal) ||
            timestamp.Length >= 6 && timestamp[timestamp.Length - 3] == ':' &&
            timestamp[timestamp.Length - 6] is '+' or '-';
        return zone && timestamp.IndexOf('T') > 0 && DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out value);
    }

    private static bool Integer(JsonElement parent, string name, out long value)
    {
        value = default;
        return Property(parent, name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out value);
    }

    private static string? Label(JsonElement parent, string name, int limit = MaximumIdentityLength)
    {
        if (!Property(parent, name, out var field) || field.ValueKind != JsonValueKind.String) return null;
        var value = field.GetString();
        return IsLabel(value, limit) ? value : null;
    }

    private static bool IsLabel(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value!.Length <= limit &&
        !value!.Any(char.IsControl);

    // Reject duplicate identity/count fields instead of silently trusting the last one.
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

    private sealed record OutputItem(string Id, string Kind, DateTimeOffset CompletedAt);
    private sealed record ItemTiming(string Kind, DateTimeOffset StartedAt, DateTimeOffset CompletedAt);
    private sealed record SeenResponse(string TurnId, long? Tokens, OutputSpeedSample? Sample, bool Invalidated, bool EffortConflict = false);
}
