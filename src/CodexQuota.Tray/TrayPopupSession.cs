namespace CodexQuota.Tray;

/// <summary>
/// Identifies each activation of the tray menu. A queued deactivation from an earlier
/// activation must not dismiss a menu the user has already reopened.
/// </summary>
internal sealed class TrayPopupSession
{
    public int Generation { get; private set; }
    public bool IsActive { get; private set; }

    public void Begin()
    {
        Generation = Generation == int.MaxValue ? 1 : Generation + 1;
        IsActive = true;
    }

    public bool IsCurrent(int generation) => IsActive && generation == Generation;

    // Opening cancellation, Closed, disposal, and removal may all converge here.
    // Only the first completion should post cleanup or return Shell focus.
    public bool Complete()
    {
        if (!IsActive) return false;
        IsActive = false;
        return true;
    }
}
