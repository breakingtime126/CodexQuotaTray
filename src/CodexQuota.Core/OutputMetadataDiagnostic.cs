namespace CodexQuota.Core;

/// <summary>Formats an existing read result using only fixed labels and booleans; performs no reads.</summary>
public static class OutputMetadataDiagnostic
{
    public static string Format(RolloutSpeedReadResult? result, bool unavailable = false)
    {
        var sample = result?.Sample;
        var average = result?.TurnAverageDiagnostic ?? new();
        var response = result?.ResponseRateDiagnostic ?? new(ResponseRateReason.NotObserved, ResponseRateStartBoundary.NotObserved,
            ResponseRateEndBoundary.NotObserved, ResponseRateUsageKind.NotObserved, false, false, false, 0);
        return "modelMetadata status=" + (result is null ? "NotRead" : Label(result.Status)) +
            " samplePresent=" + Flag(sample is not null) +
            " modelPresent=" + Flag(!string.IsNullOrWhiteSpace(sample?.Model)) +
            " effortPresent=" + Flag(!string.IsNullOrWhiteSpace(sample?.ReasoningEffort)) +
            " metadataStatus=" + (result is null ? "NotRead" : Label(result.ModelMetadataStatus)) +
            " pendingWork=" + Flag(result?.PendingWork == true) +
            " readErrors=" + Flag(result?.HasReadErrors == true) +
            " historyLimited=" + Flag(result?.IsHistoryLimited == true) +
            " unavailable=" + Flag(unavailable) +
            " timingReliability=" + (sample is null ? "NoSample" : Label(sample.TimingReliability)) +
            " boundaryReason=" + (result is null ? "None" : Label(result.BoundaryReason)) +
            " metricScope=" + (sample?.TimingReliability == OutputSpeedTimingReliability.ResponseRateEstimate ? "ResponseEffectiveIncludingTtftAndLogDelay" :
                sample?.TimingReliability == OutputSpeedTimingReliability.TurnAverage ? "WholeTurnIncludingWaitsAndTools" : "NoTurnAverage") +
            " threadSetting=" + Label(sample?.ThreadRequestedSetting ?? ThreadRequestedSetting.Unknown) +
            " fastEvidence=ThreadRequestedSetting turnTierUnknown=true" +
            " rateAvailable=" + Flag(sample?.TokensPerSecond.HasValue == true) +
            " sampleSelection=" + (sample?.IsHistoricalOtherThread == true ? "HistoricalOtherThread" : sample?.IsRetainedTurnAverage == true ? "SameThreadCompletedTurn" : "Latest") +
            " fastSettingScope=" + (sample?.TimingReliability is OutputSpeedTimingReliability.TurnAverage or OutputSpeedTimingReliability.ResponseRateEstimate ? "CompletionSnapshot" : "LatestThreadSetting") +
            " activityAmbiguous=" + Flag(result?.ActivityAmbiguous == true) +
            " turnAverageReason=" + Label(average.Reason) + " lastRejectReason=" + Label(average.LastRejectReason) +
            " turnStartSeen=" + Flag(average.StartSeen) + " turnModelSeen=" + Flag(average.ModelSeen) +
            " usageRecordSeen=" + Flag(average.UsageSeen) + " verifiedResponseCount=" + Math.Max(0, Math.Min(512, average.ResponseCount)).ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " completionSeen=" + Flag(average.CompletionSeen) + " durationPresent=" + Flag(average.DurationPresent) +
            " responseRateReason=" + Label(response.Reason) + " responseStartSeen=" + Flag(response.StartSeen) +
            " responseModelSeen=" + Flag(response.ModelSeen) + " responseUsageSeen=" + Flag(response.UsageSeen) +
            " responseCount=" + Math.Max(0, Math.Min(512, response.ResponseCount)).ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " sampleStartBoundary=" + Label(sample?.ResponseRate?.StartBoundary ?? ResponseRateStartBoundary.NotObserved) +
            " sampleEndBoundary=" + Label(sample?.ResponseRate?.EndBoundary ?? ResponseRateEndBoundary.NotObserved) +
            " sampleUsageKind=" + Label(sample?.ResponseRate?.UsageKind ?? ResponseRateUsageKind.NotObserved) +
            " canCatchUp=" + Flag(result?.CanCatchUp == true) + " discoveryPending=" + Flag(result?.DiscoveryPending == true) +
            " forwardBacklog=" + Flag(result?.ForwardBacklogPending == true) + " historyRecovery=" + Flag(result?.HistoryRecoveryPending == true);
    }

    private static string Flag(bool value) => value ? "true" : "false";
    private static string Label<T>(T value) where T : struct => Enum.GetName(typeof(T), value) ?? "Unknown";
}
