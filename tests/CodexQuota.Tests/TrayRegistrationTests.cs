using CodexQuota.Tray;

internal static class TrayRegistrationTests
{
    public static void Register(Action<string, Action> test)
    {
        test("tray registration: stable GUID remains preferred and preserves identity", () =>
        {
            var state = new TrayIconRegistration();
            var calls = new List<(TrayShellOperation, TrayIconIdentity)>();
            bool Notify(TrayShellOperation op, TrayIconIdentity id) { calls.Add((op, id)); return true; }
            Check.Equal(TrayRegistrationResult.RegisteredGuid, state.Update(Notify));
            Check.True(state.IsAdded && state.Version4);
            Check.Equal(2, calls.Count);
            Check.Equal((TrayShellOperation.Add, TrayIconIdentity.Guid), calls[0]);
            Check.Equal((TrayShellOperation.SetVersion, TrayIconIdentity.Guid), calls[1]);
            Check.Equal(TrayRegistrationResult.Unchanged, state.Update(Notify));
            Check.Equal((TrayShellOperation.Modify, TrayIconIdentity.Guid), calls[2]);
        });

        test("tray registration: rejected GUID recovers via window identity rather than invisible process", () =>
        {
            var state = new TrayIconRegistration();
            var calls = new List<(TrayShellOperation, TrayIconIdentity)>();
            bool Notify(TrayShellOperation op, TrayIconIdentity id)
            {
                calls.Add((op, id));
                return id == TrayIconIdentity.WindowId;
            }
            Check.Equal(TrayRegistrationResult.RegisteredWindowId, state.Update(Notify));
            Check.True(state.IsAdded && state.Version4);
            Check.Equal(TrayIconIdentity.WindowId, state.Identity);
            Check.Equal(3, calls.Count);
            Check.Equal((TrayShellOperation.Add, TrayIconIdentity.Guid), calls[0]);
            Check.Equal((TrayShellOperation.Add, TrayIconIdentity.WindowId), calls[1]);
            Check.Equal((TrayShellOperation.SetVersion, TrayIconIdentity.WindowId), calls[2]);
        });

        test("tray registration: failure of both identities never claims registration", () =>
        {
            var state = new TrayIconRegistration();
            var calls = new List<(TrayShellOperation, TrayIconIdentity)>();
            Check.Equal(TrayRegistrationResult.Failed, state.Update((op, id) => { calls.Add((op, id)); return false; }));
            Check.False(state.IsAdded);
            Check.False(state.Version4);
            Check.Equal(TrayIconIdentity.Guid, state.Identity);
            Check.Equal(2, calls.Count);
            Check.True(calls.All(c => c.Item1 == TrayShellOperation.Add));
        });

        test("tray registration: fallback identity persists for modify delete and Explorer restart", () =>
        {
            var state = new TrayIconRegistration();
            state.Update((_, id) => id == TrayIconIdentity.WindowId);
            var calls = new List<(TrayShellOperation, TrayIconIdentity)>();
            bool Notify(TrayShellOperation op, TrayIconIdentity id) { calls.Add((op, id)); return true; }
            Check.Equal(TrayRegistrationResult.Unchanged, state.Update(Notify));
            state.Forget();
            Check.Equal(TrayRegistrationResult.RegisteredWindowId, state.Update(Notify));
            state.Remove(Notify);
            state.Remove(Notify);
            Check.Equal(4, calls.Count);
            Check.True(calls.All(c => c.Item2 == TrayIconIdentity.WindowId));
            Check.Equal(TrayShellOperation.Delete, calls[3].Item1);
            Check.False(state.IsAdded);
        });

        test("tray registration: temporary loss never changes a previously accepted GUID", () =>
        {
            var state = new TrayIconRegistration();
            state.Update((_, _) => true);
            var calls = new List<(TrayShellOperation, TrayIconIdentity)>();
            Check.Equal(TrayRegistrationResult.Failed, state.Update((op, id) => { calls.Add((op, id)); return false; }));
            Check.Equal(2, calls.Count); // Modify, then Add. No second identity/duplicate icon.
            Check.True(calls.All(c => c.Item2 == TrayIconIdentity.Guid));
            state.Forget();
            Check.Equal(TrayRegistrationResult.RegisteredGuid, state.Update((_, _) => true));
        });

        test("tray registration: failure then later GUID success does not unnecessarily use fallback", () =>
        {
            var state = new TrayIconRegistration();
            state.Update((_, _) => false);
            Check.Equal(TrayRegistrationResult.RegisteredGuid, state.Update((_, _) => true));
            Check.Equal(TrayIconIdentity.Guid, state.Identity);
        });

        foreach (var identity in new[] { TrayIconIdentity.Guid, TrayIconIdentity.WindowId })
            test($"tray registration: transient modify/add failure can recover existing {identity} icon", () =>
            {
                var state = new TrayIconRegistration();
                state.Update((_, id) => id == identity);
                Check.Equal(TrayRegistrationResult.Failed, state.Update((_, _) => false));
                Check.False(state.IsAdded);
                var calls = new List<TrayShellOperation>();
                var recovered = state.Update((op, id) => { Check.Equal(identity, id); calls.Add(op); return op == TrayShellOperation.Modify; });
                Check.Equal(identity == TrayIconIdentity.Guid ? TrayRegistrationResult.RegisteredGuid : TrayRegistrationResult.RegisteredWindowId, recovered);
                Check.True(state.IsAdded && state.Version4);
                Check.Equal(1, calls.Count);
                Check.Equal(TrayShellOperation.Modify, calls[0]);
            });

        test("tray registration: every re-add sets callback version again", () =>
        {
            var state = new TrayIconRegistration();
            var versionCalls = 0;
            for (var i = 0; i < 50; i++)
            {
                state.Forget();
                state.Update((op, _) => { if (op == TrayShellOperation.SetVersion) versionCalls++; return true; });
            }
            Check.Equal(50, versionCalls);
        });

        test("tray registration: unsupported callback version still has a real icon", () =>
        {
            var state = new TrayIconRegistration();
            Check.Equal(TrayRegistrationResult.RegisteredGuid, state.Update((op, _) => op != TrayShellOperation.SetVersion));
            Check.True(state.IsAdded);
            Check.False(state.Version4);
        });

        test("tray registration: failed add never triggers delete on dispose", () =>
        {
            var state = new TrayIconRegistration();
            state.Update((_, _) => false);
            var deletes = 0;
            state.Remove((_, _) => { deletes++; return true; });
            Check.Equal(0, deletes);
        });

        test("tray registration: accepted identity gets cleanup even after a failed update", () =>
        {
            var state = new TrayIconRegistration();
            state.Update((_, _) => true);
            state.Update((_, _) => false);
            var deletes = 0;
            state.Remove((op, id) => { Check.Equal(TrayShellOperation.Delete, op); Check.Equal(TrayIconIdentity.Guid, id); deletes++; return true; });
            state.Remove((_, _) => { deletes++; return true; });
            Check.Equal(1, deletes);
        });

        test("tray registration: Explorer loss clears stale ownership without discarding selected identity", () =>
        {
            var state = new TrayIconRegistration();
            state.Update((_, id) => id == TrayIconIdentity.WindowId);
            state.Forget();
            var deletes = 0;
            state.Remove((_, _) => { deletes++; return true; });
            Check.Equal(0, deletes);
            Check.Equal(TrayIconIdentity.WindowId, state.Identity);
        });

        test("tray registration retry: finite backoff is gated between timer ticks", () =>
        {
            var retry = new TrayRegistrationRetry();
            foreach (var delay in new[] { 1000, 2000, 5000, 10000, 30000 })
            {
                Check.True(retry.CanAttempt);
                Check.Equal((int?)delay, retry.Failed());
                Check.False(retry.CanAttempt);
                Check.True(retry.IsWaiting);
                for (var frame = 0; frame < 30; frame++) Check.Null(retry.Failed());
                retry.TimerElapsed();
            }
            Check.Null(retry.Failed());
            Check.True(retry.IsExhausted);
            Check.False(retry.CanAttempt);
            retry.TimerElapsed();
            Check.False(retry.CanAttempt);
        });

        test("tray registration retry: success removal or Explorer reset cancels exhaustion", () =>
        {
            var retry = new TrayRegistrationRetry();
            for (var i = 0; i < 6; i++) { retry.Failed(); retry.TimerElapsed(); }
            Check.True(retry.IsExhausted);
            retry.Reset();
            Check.True(retry.CanAttempt);
            Check.False(retry.IsWaiting);
            Check.False(retry.IsExhausted);
            Check.Equal((int?)1000, retry.Failed());
            retry.Reset();
            Check.True(retry.CanAttempt);
        });
    }
}
