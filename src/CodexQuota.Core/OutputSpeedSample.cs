using System.Text.Json;

namespace CodexQuota.Core;

// VerifiedGeneration requires independent evidence of the complete generation
// interval. Positive lifecycle duration or a plausible rate cannot establish it.
// No current rollout-only producer can set VerifiedGeneration.
public enum ThreadRequestedSetting { Unknown, Fast, Standard }
public sealed record ThreadRequestedSettingObservation(ThreadRequestedSetting Setting, DateTimeOffset? RecordedAt, bool BrokenBoundary = false);

public enum OutputSpeedTimingReliability { UnverifiedItemLifecycle, VerifiedGeneration, TurnAverage, ResponseRateEstimate }

public enum ResponseRateStartBoundary { NotObserved, TaskStarted, UserInput, PreviousResponseCompletion, ToolResult }
public enum ResponseRateEndBoundary { NotObserved, TokenUsageRecord }
public enum ResponseRateUsageKind { NotObserved, PerResponse, LegacyTokenCount }

/// <summary>Rollout boundaries for an effective response-rate estimate, including TTFT and log-writing delay.</summary>
public sealed record ResponseRateEvidence(ResponseRateStartBoundary StartBoundary,
    ResponseRateEndBoundary EndBoundary, ResponseRateUsageKind UsageKind)
{
    public bool IsComplete => (StartBoundary is ResponseRateStartBoundary.TaskStarted or ResponseRateStartBoundary.UserInput or
        ResponseRateStartBoundary.PreviousResponseCompletion or ResponseRateStartBoundary.ToolResult) &&
        EndBoundary == ResponseRateEndBoundary.TokenUsageRecord && UsageKind == ResponseRateUsageKind.PerResponse;
}

/// <summary>A completed response's metadata; lifecycle timestamps alone do not establish generation speed.</summary>
public sealed record OutputSpeedSample(
    string ThreadId,
    string ResponseId,
    string? Model,
    long OutputTokens,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt)
{
    public string? TurnId { get; init; }
    public string? ReasoningEffort { get; init; }
    public OutputSpeedTimingReliability TimingReliability { get; init; } = OutputSpeedTimingReliability.UnverifiedItemLifecycle;
    public TurnAverageEvidence? TurnAverage { get; init; }
    public ResponseRateEvidence? ResponseRate { get; init; }
    // Reader selection metadata for retained complete estimates (response or turn).
    // The legacy property name is retained; measurement and labels stay atomic.
    public bool IsRetainedTurnAverage { get; init; }
    // Historical whole sample selected without attributing it to current activity.
    public bool IsHistoricalOtherThread { get; init; }
    // TurnAverage and ResponseRateEstimate: completion-time thread preference snapshot. Other metadata:
    // latest explicit thread preference. Neither is the actual tier of this turn.
    public ThreadRequestedSetting ThreadRequestedSetting { get; init; } = ThreadRequestedSetting.Unknown;
    /// <summary>Outer timestamp of the matching per-response usage record, not generation completion.</summary>
    public DateTimeOffset? RecordedAt { get; init; }
    /// <summary>Observed source-boundary span; its meaning is given by TimingReliability.</summary>
    public TimeSpan ObservedInterval => CompletedAt - StartedAt;
    public TimeSpan? GenerationDuration => TimingReliability == OutputSpeedTimingReliability.VerifiedGeneration &&
        CompletedAt > StartedAt ? ObservedInterval : null;
    // output_tokens already includes reasoning tokens. Do not add them again.
    // Nullable at the data boundary, so another consumer cannot accidentally expose
    // a lifecycle-notification interval as model throughput.
    public double? TokensPerSecond => OutputTokens < 0 ? null : TimingReliability == OutputSpeedTimingReliability.ResponseRateEstimate
        ? ResponseRate is { IsComplete: true } && CompletedAt > StartedAt ? OutputTokens / ObservedInterval.TotalSeconds : null
        : TimingReliability == OutputSpeedTimingReliability.TurnAverage
        ? TurnAverage is { } average && average.DurationMilliseconds > 0 && average.HasTotal(OutputTokens)
            ? OutputTokens / (average.DurationMilliseconds / 1000d) : null
        : GenerationDuration is { } duration ? OutputTokens / duration.TotalSeconds : null;

    public bool IsCompatibleWith(OutputSpeedSample other) => ThreadId == other.ThreadId && ResponseId == other.ResponseId &&
        TurnId == other.TurnId && OutputTokens == other.OutputTokens && StartedAt == other.StartedAt && CompletedAt == other.CompletedAt &&
        TimingReliability == other.TimingReliability &&
        ResponseRate == other.ResponseRate &&
        (TurnAverage is null ? other.TurnAverage is null : other.TurnAverage is not null && TurnAverage.IsCompatibleWith(other.TurnAverage)) &&
        (Model is null || other.Model is null || Model == other.Model);
}

/// <summary>Scoped usage evidence for detecting contradictory copies without retaining payloads.</summary>
public sealed record OutputSpeedUsageObservation(
    string ThreadId,
    string ResponseId,
    string TurnId,
    long? OutputTokens,
    bool Failed)
{
    public long? TurnOutputTokens { get; init; }
    public long? ReasoningOutputTokens { get; init; }
}

/// <summary>Only explicit, consistent historical fields supply an effort; never a default.</summary>
internal static class HistoricalReasoningEffort
{
    public static bool IsSupported(string? value) => value is "none" or "minimal" or "low" or "medium" or
        "high" or "xhigh" or "max" or "ultra" or "persistent" or "custom";

    public static string? Read(JsonElement payload, bool settings = false)
    {
        string? result = null;
        var invalid = false;
        ReadField(payload, "effort", ref result, ref invalid);
        if (settings) ReadField(payload, "reasoning_effort", ref result, ref invalid);
        if (TryObject(payload, "collaboration_mode", ref invalid, out var mode) &&
            TryObject(mode, "settings", ref invalid, out var nestedSettings))
            ReadField(nestedSettings, "reasoning_effort", ref result, ref invalid);
        return invalid ? null : result;
    }

    private static bool TryObject(JsonElement parent, string name, ref bool invalid, out JsonElement result)
    {
        result = default;
        var count = 0;
        foreach (var field in parent.EnumerateObject())
            if (field.NameEquals(name)) { count++; result = field.Value; }
        if (count == 0) return false;
        if (count != 1 || result.ValueKind != JsonValueKind.Object) { invalid = true; return false; }
        return true;
    }

    private static void ReadField(JsonElement parent, string name, ref string? result, ref bool invalid)
    {
        var count = 0;
        foreach (var field in parent.EnumerateObject())
        {
            if (!field.NameEquals(name)) continue;
            count++;
            var value = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : null;
            if (!IsSupported(value) || result is not null && result != value) invalid = true;
            else result = value;
        }
        if (count > 1) invalid = true;
    }
}

/// <summary>Evidence for a whole-turn average, explicitly distinct from generation timing.</summary>
public sealed record TurnResponseUsage(string ResponseId, long OutputTokens, long? ReasoningOutputTokens, long TurnOutputTokens);

public sealed class TurnAverageEvidence
{
    public long DurationMilliseconds { get; }
    public IReadOnlyList<TurnResponseUsage> Responses { get; }
    public TurnAverageEvidence(long durationMilliseconds, IReadOnlyList<TurnResponseUsage> responses)
    {
        DurationMilliseconds = durationMilliseconds;
        Responses = Array.AsReadOnly(responses.ToArray());
    }
    public bool HasTotal(long expected)
    {
        if (Responses.Count == 0 || Responses.Count > 512 || DurationMilliseconds <= 0 ||
            DurationMilliseconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond) return false;
        long total = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var response in Responses)
            {
                if (response.OutputTokens < 0 || !seen.Add(response.ResponseId) ||
                    response.ReasoningOutputTokens is { } reasoning && (reasoning < 0 || reasoning > response.OutputTokens)) return false;
                total = checked(total + response.OutputTokens);
            }
        }
        catch (OverflowException) { return false; }
        if (total != expected) return false;
        long checkpoint = 0;
        var boundaries = new HashSet<long> { 0 };
        foreach (var response in Responses.Where(value => value.OutputTokens > 0).OrderBy(value => value.TurnOutputTokens))
        {
            checkpoint += response.OutputTokens; // The checked total above bounds this nonnegative partial sum.
            if (checkpoint != response.TurnOutputTokens) return false;
            boundaries.Add(checkpoint);
        }
        return Responses.Where(value => value.OutputTokens == 0).All(value => boundaries.Contains(value.TurnOutputTokens));
    }
    public bool IsCompatibleWith(TurnAverageEvidence other) => DurationMilliseconds == other.DurationMilliseconds &&
        Responses.SequenceEqual(other.Responses);
}
