namespace CodexQuota.Core;

public enum BackendCleanupState { Pending, Confirmed }

/// <summary>A read may have valid data while its exclusively owned backend is still retiring.</summary>
public sealed record BackendCleanupProgress(QuotaDataSource Source, BackendCleanupState State,
    QuotaRead? Read, DateTimeOffset? ReadAt, long ElapsedMilliseconds);

internal sealed record BackendCleanupOptions(TimeSpan PendingAfter, TimeSpan InitialPoll, TimeSpan MaximumPoll)
{
    internal static BackendCleanupOptions Default { get; } =
        new(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(30));
}

internal sealed record OwnedProcessExit(bool RootExited, bool TreeExited, int? ExitCode = null);
