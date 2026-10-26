using CodexQuota.Core;

internal static class OutputMetadataDiagnosticTests
{
    private const string NoAverageDiagnostic = " turnAverageReason=NotObserved lastRejectReason=NotObserved turnStartSeen=false turnModelSeen=false usageRecordSeen=false verifiedResponseCount=0 completionSeen=false durationPresent=false responseRateReason=NotObserved responseStartSeen=false responseModelSeen=false responseUsageSeen=false responseCount=0 sampleStartBoundary=NotObserved sampleEndBoundary=NotObserved sampleUsageKind=NotObserved canCatchUp=false discoveryPending=false forwardBacklog=false historyRecovery=false";
    public static void Register(Action<string, Action> test)
    {
        test("model diagnostic: before first read has explicit fixed absence labels", () =>
            Check.Equal("modelMetadata status=NotRead samplePresent=false modelPresent=false effortPresent=false " +
                "metadataStatus=NotRead pendingWork=false readErrors=false historyLimited=false unavailable=false " +
                "timingReliability=NoSample boundaryReason=None metricScope=NoTurnAverage threadSetting=Unknown fastEvidence=ThreadRequestedSetting turnTierUnknown=true rateAvailable=false sampleSelection=Latest fastSettingScope=LatestThreadSetting activityAmbiguous=false" + NoAverageDiagnostic, OutputMetadataDiagnostic.Format(null)));

        test("model diagnostic: known metadata remains present with unverified timing", () =>
        {
            var result = Result(Sample());
            Check.Null(result.Sample!.TokensPerSecond);
            Check.Equal("modelMetadata status=Ready samplePresent=true modelPresent=true effortPresent=true " +
                "metadataStatus=Known pendingWork=false readErrors=false historyLimited=false unavailable=false " +
                "timingReliability=UnverifiedItemLifecycle boundaryReason=None metricScope=NoTurnAverage threadSetting=Unknown fastEvidence=ThreadRequestedSetting turnTierUnknown=true rateAvailable=false sampleSelection=Latest fastSettingScope=LatestThreadSetting activityAmbiguous=false" + NoAverageDiagnostic, OutputMetadataDiagnostic.Format(result));
        });

        test("model diagnostic: distinguishes all metadata recovery outcomes", () =>
        {
            foreach (var status in Enum.GetValues<RolloutModelMetadataStatus>())
            {
                var result = Result(Sample() with { Model = null, ReasoningEffort = null }) with
                    { ModelMetadataStatus = status };
                var diagnostic = OutputMetadataDiagnostic.Format(result);
                Check.Contains("samplePresent=true modelPresent=false effortPresent=false", diagnostic);
                Check.Contains("metadataStatus=" + status + " ", diagnostic);
                Check.Contains("timingReliability=UnverifiedItemLifecycle", diagnostic);
            }
        });

        test("model diagnostic: preserves every read status without inventing a sample", () =>
        {
            foreach (var status in Enum.GetValues<RolloutSpeedReadStatus>())
            {
                var diagnostic = OutputMetadataDiagnostic.Format(new(null, status));
                Check.Contains("status=" + status + " ", diagnostic);
                Check.Contains("samplePresent=false modelPresent=false effortPresent=false", diagnostic);
                Check.Contains("timingReliability=NoSample", diagnostic);
            }
        });

        test("model diagnostic: caller unavailable and read flags are independent", () =>
        {
            foreach (var pending in new[] { false, true })
            foreach (var errors in new[] { false, true })
            foreach (var limited in new[] { false, true })
            foreach (var unavailable in new[] { false, true })
            {
                var result = Result(Sample()) with
                    { PendingWork = pending, HasReadErrors = errors, IsHistoryLimited = limited };
                var diagnostic = OutputMetadataDiagnostic.Format(result, unavailable);
                Check.Contains("pendingWork=" + Flag(pending) + " readErrors=" + Flag(errors) +
                    " historyLimited=" + Flag(limited) + " unavailable=" + Flag(unavailable) + " ", diagnostic);
                Check.Contains("status=Ready samplePresent=true modelPresent=true effortPresent=true", diagnostic);
            }
            Check.Contains("status=NotRead", OutputMetadataDiagnostic.Format(null, unavailable: true));
            Check.Contains("unavailable=true", OutputMetadataDiagnostic.Format(null, unavailable: true));
        });

        test("model diagnostic: null empty and whitespace metadata are absent", () =>
        {
            foreach (var absent in new string?[] { null, "", " \r\n\t" })
            {
                var diagnostic = OutputMetadataDiagnostic.Format(Result(Sample() with
                    { Model = absent, ReasoningEffort = absent }));
                Check.Contains("samplePresent=true modelPresent=false effortPresent=false", diagnostic);
            }
        });

        test("model diagnostic: unsupported enum values use fixed Unknown labels", () =>
        {
            var result = Result(Sample() with { TimingReliability = (OutputSpeedTimingReliability)8675309 }) with
                { Status = (RolloutSpeedReadStatus)8675309, ModelMetadataStatus = (RolloutModelMetadataStatus)8675309,
                    TurnAverageDiagnostic = new((TurnAverageReason)8675309, (TurnAverageReason)8675309, ResponseCount: int.MaxValue) };
            var diagnostic = OutputMetadataDiagnostic.Format(result);
            Check.Contains("status=Unknown ", diagnostic);
            Check.Contains("metadataStatus=Unknown ", diagnostic);
            Check.Contains("timingReliability=Unknown", diagnostic);
            Check.Contains("turnAverageReason=Unknown lastRejectReason=Unknown", diagnostic);
            Check.Contains("verifiedResponseCount=512", diagnostic);
            Check.False(diagnostic.Contains("8675309", StringComparison.Ordinal));
        });

        test("model diagnostic: payload values identifiers timestamps and counts never enter output", () =>
        {
            var reference = Result(Sample());
            var sensitive = new OutputSpeedSample("private-thread", "private-response", "private-model\nforged=true",
                987654321, DateTimeOffset.MinValue, DateTimeOffset.MaxValue)
            {
                TurnId = "private-turn", ReasoningEffort = "C:\\private\\raw-log", RecordedAt = DateTimeOffset.MaxValue
            };
            var altered = Result(sensitive) with { BytesRead = int.MaxValue, TrackedFileCount = int.MaxValue };
            // Equality of the complete diagnostic also rules out accidental future fields.
            Check.Equal(OutputMetadataDiagnostic.Format(reference), OutputMetadataDiagnostic.Format(altered));
        });

        test("model diagnostic: formatting is repeatable and does not mutate metadata or timing", () =>
        {
            var sample = Sample() with { TimingReliability = OutputSpeedTimingReliability.VerifiedGeneration };
            var result = Result(sample);
            var before = result with { Sample = sample with { } };
            var diagnostic = OutputMetadataDiagnostic.Format(result);
            Check.Contains("timingReliability=VerifiedGeneration", diagnostic);
            Check.Equal(diagnostic, OutputMetadataDiagnostic.Format(result));
            Check.Equal(before, result);
            Check.Equal(50d, result.Sample!.TokensPerSecond);
        });
    }

    private static string Flag(bool value) => value ? "true" : "false";
    private static OutputSpeedSample Sample() => new("thread", "response", "gpt-6.1-sol", 100,
        new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 12, 12, 0, 2, TimeSpan.Zero)) { ReasoningEffort = "medium" };
    private static RolloutSpeedReadResult Result(OutputSpeedSample sample) => new(sample, RolloutSpeedReadStatus.Ready)
        { ModelMetadataStatus = RolloutModelMetadataStatus.Known };
}
