namespace CodexQuota.Tray;

internal enum TrayIconIdentity { Guid, WindowId }
internal enum TrayShellOperation : uint { Add, Modify, Delete, SetFocus, SetVersion }
internal enum TrayRegistrationResult { Unchanged, RegisteredGuid, RegisteredWindowId, Failed }
internal enum TrayDiagnostic { TrayRegisteredGuid, TrayRegisteredWindowId, TrayRegistrationFailed, TrayRegistrationRetryExhausted, TrayVersion4Unavailable }

/// <summary>Shell calls are injected so registration failures can be tested without Explorer.</summary>
internal sealed class TrayIconRegistration
{
    private bool _identityEstablished;
    private bool _mayOwnIcon;
    public TrayIconIdentity Identity { get; private set; } = TrayIconIdentity.Guid;
    public bool IsAdded { get; private set; }
    public bool Version4 { get; private set; }

    public TrayRegistrationResult Update(Func<TrayShellOperation, TrayIconIdentity, bool> notify)
    {
        if (_mayOwnIcon && notify(TrayShellOperation.Modify, Identity))
        {
            var recovered = !IsAdded;
            IsAdded = true;
            return recovered ? RegisteredResult() : TrayRegistrationResult.Unchanged;
        }
        IsAdded = false;
        // Keep the accepted callback version while the old icon may still exist.
        // If Add failed because it remained present, a later Modify can recover it.
        if (!notify(TrayShellOperation.Add, Identity))
        {
            // Windows binds a GUID to an unsigned executable's full path. A moved
            // portable EXE may therefore be rejected although the process is healthy.
            // Try the supported HWND+ID identity only before our first success.
            // Once registered, never switch identities on a transient failure.
            if (_identityEstablished || Identity != TrayIconIdentity.Guid ||
                !notify(TrayShellOperation.Add, TrayIconIdentity.WindowId))
                return TrayRegistrationResult.Failed;
            Identity = TrayIconIdentity.WindowId;
        }

        IsAdded = true;
        _identityEstablished = true;
        _mayOwnIcon = true;
        Version4 = notify(TrayShellOperation.SetVersion, Identity);
        return RegisteredResult();
    }

    private TrayRegistrationResult RegisteredResult() => Identity == TrayIconIdentity.Guid
        ? TrayRegistrationResult.RegisteredGuid : TrayRegistrationResult.RegisteredWindowId;

    public void Forget()
    {
        // Explorer restart must retain an identity already accepted by the Shell.
        IsAdded = false;
        Version4 = false;
        _mayOwnIcon = false;
    }

    public void Remove(Func<TrayShellOperation, TrayIconIdentity, bool> notify)
    {
        // A failed Modify need not prove the old Shell icon disappeared. Attempt
        // cleanup of our accepted identity, but never delete an unregistered one.
        if (_mayOwnIcon) notify(TrayShellOperation.Delete, Identity);
        Forget();
    }
}

/// <summary>Initial attempt plus five timed retries; animation/refresh cannot bypass the gate.</summary>
internal sealed class TrayRegistrationRetry
{
    private static readonly int[] Delays = [1000, 2000, 5000, 10000, 30000];
    private int _failures;
    public bool IsWaiting { get; private set; }
    public bool IsExhausted { get; private set; }
    public bool CanAttempt => !IsWaiting && !IsExhausted;

    public int? Failed()
    {
        if (!CanAttempt) return null;
        if (_failures >= Delays.Length)
        {
            IsExhausted = true;
            return null;
        }
        IsWaiting = true;
        return Delays[_failures++];
    }

    public void TimerElapsed() => IsWaiting = false;
    public void Reset() { _failures = 0; IsWaiting = false; IsExhausted = false; }
}
