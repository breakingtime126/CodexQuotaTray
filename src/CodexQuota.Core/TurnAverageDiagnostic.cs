namespace CodexQuota.Core;

// Fixed labels only. Diagnostics never retain payload text, paths, models or IDs.
public enum TurnAverageReason
{
    NotObserved, Ready, AwaitingUsage, AwaitingCompletion, MissingStart, MissingModel,
    MissingUsage, MissingCumulativeUsage, InvalidUsage, UsageMismatch, UnsealedOutput,
    MissingDuration, InvalidDuration, TurnFailed, TurnCancelled, MixedModelOrEffort,
    InvalidRecord, ItemStatusRejected, ReadGap, ColdTail, OversizedRecord, InvalidUtf8, IdentityMismatch,
    ResponseLimit, ActivityAmbiguous, ConflictingEvidence
}

public sealed record TurnAverageDiagnostic(TurnAverageReason Reason = TurnAverageReason.NotObserved,
    TurnAverageReason LastRejectReason = TurnAverageReason.NotObserved, bool StartSeen = false,
    bool ModelSeen = false, bool UsageSeen = false, int ResponseCount = 0,
    bool CompletionSeen = false, bool DurationPresent = false);
