using System.Text;
using System.Text.Json;

namespace CodexQuota.Core;

/// <summary>
/// A bounded, incremental JSONL projector. Text/tool bodies are validated and discarded as
/// they arrive; only metadata is retained, including across a partial writer flush. Unknown
/// oversized metadata and malformed discarded bodies fail closed at the record boundary.
/// </summary>
internal sealed class RolloutMetadataProjection
{
    private const int MaximumDepth = 64;
    private const int MaximumKeyBytes = 256;
    private const int MaximumScalarBytes = 128;
    // Persisted RolloutItemWire variants, codex-rs/history/src/rollout_payload.rs.
    // Neutral envelopes remain neutral even when they contain large non-metric bodies.
    private static readonly HashSet<string> RolloutKinds = new(StringComparer.Ordinal)
    {
        "session_meta", "turn_context", "response_item", "event_msg", "token_usage_record",
        "compacted", "world_state", "retained_context", "security_risk_score",
        "inter_agent_communication", "inter_agent_communication_metadata", "realtime_item"
    };
    private static readonly HashSet<string> Metadata = new(StringComparer.Ordinal)
    {
        "timestamp", "type", "payload", "id", "thread_id", "turn_id", "response_id", "call_id", "role",
        "status", "error", "item", "model", "effort", "reasoning_effort", "service_tier", "thread_settings",
        "collaboration_mode", "settings", "usage", "turn_token_usage", "output_tokens", "reasoning_output_tokens",
        "started_at_ms", "completed_at_ms", "duration_ms", "internal_chat_message_metadata_passthrough"
    };
    private readonly int maximumBytes;
    private readonly List<byte> projected = new();
    private readonly List<byte> key = new();
    private readonly List<byte> scalar = new();
    private readonly List<Frame> stack = new();
    private bool rejected, rootComplete, inString, propertyString, escaped, stringSuppressed, scalarSuppressed;
    private bool keyNeedsDecode;
    private int stringBytes;
    private int unicodeValue;
    private bool pendingHighSurrogate;
    private int unicodeDigits, utf8Remaining, utf8Minimum = 0x80, utf8Maximum = 0xbf;
    private bool scalarActive;
    private long count;
    private int bomRemaining;
    public bool HasPending => count != 0;
    public bool IsRejected => rejected;
    public TurnAverageReason FailureReason { get; private set; } = TurnAverageReason.InvalidRecord;

    public RolloutMetadataProjection(int maximumBytes) => this.maximumBytes = Math.Min(maximumBytes, 64 * 1024);

    public void Add(byte value)
    {
        count++;
        if (rejected) return;
        if (count == 1 && value == 0xef) { bomRemaining = 2; return; }
        if (bomRemaining != 0)
        {
            if (value != (bomRemaining == 2 ? 0xbb : 0xbf)) rejected = true;
            bomRemaining--; return;
        }
        if (inString)
        {
            StringByte(value);
            return;
        }
        if (scalarActive)
        {
            if (value is (byte)',' or (byte)'}' or (byte)']' || White(value))
            {
                EndScalar();
                if (rejected) return;
            }
            else
            {
                if (scalar.Count == MaximumScalarBytes) { rejected = true; FailureReason = TurnAverageReason.OversizedRecord; return; }
                scalar.Add(value);
                if (!scalarSuppressed) Emit(value);
                return;
            }
        }
        if (White(value)) return;
        if (stack.Count == 0)
        {
            if (rootComplete) { rejected = true; return; }
            BeginValue(value, false);
            return;
        }
        var frame = stack[stack.Count - 1];
        if (frame.Object)
        {
            switch (frame.State)
            {
                case 0:
                case 4:
                    if (value == '}' && frame.State == 0) { EndContainer(value); return; }
                    if (value != '"') { rejected = true; return; }
                    BeginString(true, frame.Suppressed);
                    return;
                case 1:
                    if (value != ':') { rejected = true; return; }
                    if (!frame.Suppressed) Emit(value);
                    frame.State = 2;
                    return;
                case 2: BeginValue(value, frame.Suppressed || frame.DropValue); return;
                case 3:
                    if (value == '}') { EndContainer(value); return; }
                    if (value != ',') { rejected = true; return; }
                    if (!frame.Suppressed) Emit(value);
                    frame.State = 4;
                    return;
            }
        }
        else
        {
            if (frame.State is 0 or 2)
            {
                if (value == ']' && frame.State == 0) { EndContainer(value); return; }
                BeginValue(value, frame.Suppressed);
            }
            else if (value == ']') EndContainer(value);
            else if (value == ',') { if (!frame.Suppressed) Emit(value); frame.State = 2; }
            else rejected = true;
        }
    }

    public int SkipPlainBody(byte[] buffer, int offset, int length)
    {
        if (!rejected && (!inString || !stringSuppressed || propertyString || escaped || unicodeDigits != 0 ||
            utf8Remaining != 0 || pendingHighSurrogate)) return 0;
        var end = offset + length;
        var cursor = offset;
        if (rejected)
        {
            while (cursor < end && buffer[cursor] != '\n') cursor++;
        }
        else
        {
            // This is the hot path for large tool results. Only plain ASCII string
            // characters are skipped; escapes, UTF8, controls and quotes return to
            // the full validator. No body bytes are copied or allocated.
            while (cursor < end && buffer[cursor] >= 0x20 && buffer[cursor] < 0x80 &&
                buffer[cursor] != '"' && buffer[cursor] != '\\') cursor++;
        }
        count += cursor - offset;
        return cursor - offset;
    }

    public byte[]? Finish()
    {
        if (utf8Remaining != 0) FailureReason = TurnAverageReason.InvalidUtf8;
        if (scalarActive) EndScalar();
        if (rejected || bomRemaining != 0 || inString || stack.Count != 0 || !rootComplete) return null;
        var result = projected.ToArray();
        try
        {
            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            if (!Unique(root, "type", out var kind) || !Unique(root, "payload", out var payload) ||
                kind.ValueKind != JsonValueKind.String || payload.ValueKind != JsonValueKind.Object ||
                !RolloutKinds.Contains(kind.GetString()!)) return null;
            if (kind.GetString() is "response_item" or "event_msg")
            {
                if (!Unique(payload, "type", out var subtype) || subtype.ValueKind != JsonValueKind.String) return null;
                if (kind.GetString() == "event_msg" && subtype.GetString() is "item_completed" or "item_started")
                    if (!Unique(payload, "item", out var item) || item.ValueKind != JsonValueKind.Object ||
                        !Unique(item, "type", out var itemType) || itemType.ValueKind != JsonValueKind.String) return null;
            }
            return result;
        }
        catch (JsonException) { return null; }
    }

    private void BeginValue(byte value, bool suppress)
    {
        // Preserve the property but replace only its body value with null. Descendants
        // are parsed with the same strict grammar and never copied into metadata.
        var parentSuppressed = stack.Count != 0 && stack[stack.Count - 1].Suppressed;
        var errorPresence = !parentSuppressed && stack.Count != 0 && stack[stack.Count - 1].PropertyName == "error";
        suppress |= errorPresence;
        if (suppress && !parentSuppressed)
            foreach (var item in errorPresence && value != 'n' ? TrueBytes : NullBytes) Emit(item);
        if (value is (byte)'{' or (byte)'[')
        {
            if (stack.Count == MaximumDepth) { rejected = true; FailureReason = TurnAverageReason.OversizedRecord; return; }
            if (!suppress) Emit(value);
            var parent = stack.Count == 0 ? null : stack[stack.Count - 1];
            var location = parent is null ? Location.Root : parent.Location == Location.Root && parent.PropertyName == "payload"
                ? Location.Payload : parent.Location == Location.Payload && parent.PropertyName == "item" ? Location.Item : Location.Other;
            stack.Add(new Frame(value == '{', suppress, location));
        }
        else if (value == '"') BeginString(false, suppress);
        else if (value is (byte)'t' or (byte)'f' or (byte)'n' or (byte)'-' || value >= '0' && value <= '9')
        {
            scalarActive = true;
            scalarSuppressed = suppress;
            scalar.Clear(); scalar.Add(value);
            if (!suppress) Emit(value);
        }
        else rejected = true;
    }

    private void BeginString(bool property, bool suppress)
    {
        inString = true; propertyString = property; stringSuppressed = suppress; stringBytes = 0;
        escaped = false; unicodeDigits = 0; utf8Remaining = 0; pendingHighSurrogate = false;
        if (property) { key.Clear(); if (!suppress) key.Add((byte)'"'); keyNeedsDecode = false; }
        if (!suppress) Emit((byte)'"');
    }

    private void StringByte(byte value)
    {
        if (!stringSuppressed && ++stringBytes > 512) { rejected = true; FailureReason = TurnAverageReason.OversizedRecord; return; }
        if (propertyString && !stringSuppressed)
        {
            if (key.Count >= MaximumKeyBytes) { rejected = true; FailureReason = TurnAverageReason.OversizedRecord; return; }
            key.Add(value);
            keyNeedsDecode |= value == '\\' || value >= 0x80;
        }
        if (!stringSuppressed) Emit(value);
        if (utf8Remaining != 0)
        {
            if (value < utf8Minimum || value > utf8Maximum) { rejected = true; FailureReason = TurnAverageReason.InvalidUtf8; return; }
            utf8Remaining--; utf8Minimum = 0x80; utf8Maximum = 0xbf;
            return;
        }
        if (unicodeDigits != 0)
        {
            if (!(value >= '0' && value <= '9' || value >= 'a' && value <= 'f' || value >= 'A' && value <= 'F')) rejected = true;
            unicodeValue = (unicodeValue << 4) | (value <= '9' ? value - '0' : (value | 0x20) - 'a' + 10);
            unicodeDigits--;
            if (unicodeDigits == 0)
            {
                if (pendingHighSurrogate) { if (unicodeValue < 0xdc00 || unicodeValue > 0xdfff) rejected = true; pendingHighSurrogate = false; }
                else if (unicodeValue >= 0xd800 && unicodeValue <= 0xdbff) pendingHighSurrogate = true;
                else if (unicodeValue >= 0xdc00 && unicodeValue <= 0xdfff) rejected = true;
            }
            return;
        }
        if (escaped)
        {
            escaped = false;
            if (pendingHighSurrogate && value != 'u') { rejected = true; return; }
            if (value == 'u') { unicodeDigits = 4; unicodeValue = 0; }
            else if (!(value is (byte)'"' or (byte)'\\' or (byte)'/' or (byte)'b' or (byte)'f' or (byte)'n' or (byte)'r' or (byte)'t')) rejected = true;
            return;
        }
        if (pendingHighSurrogate && value != '\\') { rejected = true; return; }
        if (value < 0x20) { rejected = true; return; }
        if (value == '\\') { escaped = true; return; }
        if (value == '"')
        {
            inString = false;
            if (propertyString)
            {
                if (stringSuppressed)
                {
                    var suppressed = stack[stack.Count - 1];
                    suppressed.PropertyName = null; suppressed.DropValue = false; suppressed.State = 1;
                    key.Clear(); return;
                }
                try
                {
                    var bytes = key.ToArray();
                    var name = keyNeedsDecode ? JsonSerializer.Deserialize<string>(bytes) : Encoding.UTF8.GetString(bytes, 1, bytes.Length - 2);
                    var frame = stack[stack.Count - 1];
                    frame.PropertyName = name;
                    // Field names such as summary/content/output mean different things
                    // in different official records. Only metric metadata is retained;
                    // every other value is fully validated and discarded, irrespective
                    // of its length or its spelling. Never promote nested body fields.
                    frame.DropValue = !frame.Suppressed && name is not null && !Metadata.Contains(name);
                    frame.State = 1;
                }
                catch (JsonException) { rejected = true; }
                catch (InvalidOperationException) { rejected = true; }
                key.Clear();
            }
            else CompleteValue();
            return;
        }
        if (value < 0x80) return;
        if (value >= 0xc2 && value <= 0xdf) utf8Remaining = 1;
        else if (value >= 0xe0 && value <= 0xef)
        {
            utf8Remaining = 2;
            utf8Minimum = value == 0xe0 ? 0xa0 : 0x80;
            utf8Maximum = value == 0xed ? 0x9f : 0xbf;
        }
        else if (value >= 0xf0 && value <= 0xf4)
        {
            utf8Remaining = 3;
            utf8Minimum = value == 0xf0 ? 0x90 : 0x80;
            utf8Maximum = value == 0xf4 ? 0x8f : 0xbf;
        }
        else { rejected = true; FailureReason = TurnAverageReason.InvalidUtf8; }
    }

    private void EndScalar()
    {
        scalarActive = false;
        try
        {
            using var document = JsonDocument.Parse(scalar.ToArray());
            if (document.RootElement.ValueKind is not (JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) rejected = true;
        }
        catch (JsonException) { rejected = true; }
        scalar.Clear();
        CompleteValue();
    }

    private void EndContainer(byte value)
    {
        var frame = stack[stack.Count - 1];
        if (!frame.Suppressed) Emit(value);
        stack.RemoveAt(stack.Count - 1);
        CompleteValue();
    }

    private void CompleteValue()
    {
        if (stack.Count == 0) rootComplete = true;
        else { var frame = stack[stack.Count - 1]; frame.State = frame.Object ? 3 : 1; frame.DropValue = false; }
    }

    private void Emit(byte value)
    {
        if (projected.Count >= maximumBytes) { rejected = true; FailureReason = TurnAverageReason.OversizedRecord; return; }
        projected.Add(value);
    }

    private static readonly byte[] NullBytes = Encoding.ASCII.GetBytes("null");
    private static readonly byte[] TrueBytes = Encoding.ASCII.GetBytes("true");
    private static bool White(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r';
    private static bool Unique(JsonElement value, string name, out JsonElement found)
    {
        found = default; var seen = false;
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in value.EnumerateObject())
            if (property.NameEquals(name)) { if (seen) return false; seen = true; found = property.Value; }
        return seen;
    }

    private enum Location { Root, Payload, Item, Other }
    private sealed class Frame(bool isObject, bool suppressed, Location location)
    {
        public bool Object { get; } = isObject;
        public bool Suppressed { get; } = suppressed;
        public Location Location { get; } = location;
        public string? PropertyName { get; set; }
        public int State { get; set; }
        public bool DropValue { get; set; }
    }
}
