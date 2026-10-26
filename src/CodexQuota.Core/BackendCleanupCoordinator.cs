using System.ComponentModel;
using System.Diagnostics;

namespace CodexQuota.Core;

internal sealed record BackendCleanupEvidence(bool RootExited, bool TreeExited, bool PipesCompleted,
    int? ExitCode = null, int? Code = null);

/// <summary>
/// Retains one backend's ownership until it is safe to release the request lease. An expired
/// fast-path budget is a pending state, never permission to start another backend.
/// </summary>
internal static class BackendCleanupCoordinator
{
    internal static async Task<BackendCleanupEvidence> WaitAsync(IOwnedQuotaProcess process,
        JsonRpcConnection? rpc, Task stderr, CancellationToken stop, BackendCleanupOptions options,
        Action<BackendCleanupEvidence, long> pending)
    {
        var elapsed = Stopwatch.StartNew();
        var announced = false;
        var delay = options.InitialPoll;
        Task? disposeRpc = null;
        var evidence = new BackendCleanupEvidence(false, false, false);
        while (true)
        {
            if (stop.IsCancellationRequested) throw new BackendCleanupUnconfirmedException();
            int? terminationCode = null;
            try { process.RequestTermination(); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or
                IOException or NotSupportedException or UnauthorizedAccessException)
            {
                terminationCode = error is Win32Exception native ? native.NativeErrorCode : error.HResult;
            }
            try
            {
                var exit = process.ObserveExit();
                // Even if a termination request failed, an independent owned-handle/Job
                // observation can prove that the already-exiting backend is now gone.
                evidence = new(exit.RootExited, exit.TreeExited, false, exit.ExitCode, terminationCode);
                if (exit.RootExited && exit.TreeExited)
                {
                    disposeRpc ??= rpc?.DisposeAsync().AsTask() ?? Task.CompletedTask;
                    if (disposeRpc.IsCompleted)
                    {
                        await disposeRpc.ConfigureAwait(false);
                        var pipesCompleted = stderr.IsCompleted && (rpc?.TryFinishCleanup() ?? true);
                        if (pipesCompleted)
                        {
                            try { await stderr.ConfigureAwait(false); }
                            catch (Exception error) when (error is IOException or OperationCanceledException or
                                ObjectDisposedException or InvalidOperationException) { }
                            return evidence with { PipesCompleted = true };
                        }
                    }
                }
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or
                IOException or NotSupportedException or UnauthorizedAccessException)
            {
                // A failed observation is not evidence of exit. Retry only the retained owner.
                evidence = evidence with { Code = error is Win32Exception native ? native.NativeErrorCode : error.HResult };
            }
            if (!announced && elapsed.Elapsed >= options.PendingAfter)
            {
                announced = true;
                pending(evidence, elapsed.ElapsedMilliseconds);
                delay = TimeSpan.FromMilliseconds(Math.Min(1000, options.MaximumPoll.TotalMilliseconds));
            }
            try { await Task.Delay(delay, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw new BackendCleanupUnconfirmedException(); }
            if (announced)
                delay = TimeSpan.FromMilliseconds(Math.Min(options.MaximumPoll.TotalMilliseconds,
                    Math.Max(1, delay.TotalMilliseconds * 2)));
        }
    }
}
