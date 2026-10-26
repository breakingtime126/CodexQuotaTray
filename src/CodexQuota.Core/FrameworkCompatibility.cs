#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    // C# records/init compile to ordinary CLR metadata; Framework lacks this marker type.
    internal static class IsExternalInit { }
}
#endif

namespace CodexQuota.Core
{
    internal static class AsyncCompatibility
    {
        public static async Task WaitAsync(Task task, TimeSpan timeout)
        {
#if NETFRAMEWORK
            using var timeoutSource = new CancellationTokenSource(timeout);
            try { await WaitAsync(task, timeoutSource.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            { throw new TimeoutException("Operation timed out."); }
#else
            await task.WaitAsync(timeout).ConfigureAwait(false);
#endif
        }

        public static async Task WaitAsync(Task task, CancellationToken token)
        {
#if NETFRAMEWORK
            if (task.IsCompleted) { await task.ConfigureAwait(false); return; }
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetCanceled()))
            {
                if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
                {
                    // Cancellation bounds the caller, while killing the owned process closes the
                    // actual pipe operation. Observe any eventual fault from an abandoned operation.
                    _ = task.ContinueWith(completed => { _ = completed.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    token.ThrowIfCancellationRequested();
                }
                await task.ConfigureAwait(false);
            }
#else
            await task.WaitAsync(token).ConfigureAwait(false);
#endif
        }

        public static async Task<int> ReadAsync(TextReader reader, char[] buffer, CancellationToken token,
            Action<Task>? observeOperation = null)
        {
#if NETFRAMEWORK
            token.ThrowIfCancellationRequested();
            var read = reader.ReadAsync(buffer, 0, buffer.Length);
            observeOperation?.Invoke(read);
            await WaitAsync(read, token).ConfigureAwait(false);
            return await read.ConfigureAwait(false);
#else
            var read = reader.ReadAsync(buffer.AsMemory(), token).AsTask();
            observeOperation?.Invoke(read);
            return await read.ConfigureAwait(false);
#endif
        }
    }
}
