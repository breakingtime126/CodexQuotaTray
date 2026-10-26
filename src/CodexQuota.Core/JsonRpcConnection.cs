using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace CodexQuota.Core;

public sealed class RpcException : Exception
{
    public int? Code { get; }
    public bool IsRateLimited { get; }
    public CliErrorKind ErrorKind { get; }
    // Never propagate server-provided text: errors may contain account details or credentials.
    public RpcException(int? code = null, bool isRateLimited = false, CliErrorKind errorKind = CliErrorKind.None)
        : base("Codex app-server returned an RPC error.")
    { Code = code; IsRateLimited = isRateLimited; ErrorKind = errorKind; }

    internal static CliErrorKind ClassifyError(int? code, string message)
    {
        // Official quota endpoint messages; these require no account/read request.
        // https://github.com/openai/codex/blob/main/codex-rs/app-server/tests/suite/v2/rate_limits.rs
        if (code == -32600) return message switch
        {
            "codex account authentication required to read rate limits" => CliErrorKind.QuotaAuthenticationRequired,
            "chatgpt authentication required to read rate limits" => CliErrorKind.QuotaChatGptRequired,
            _ => CliErrorKind.None
        };
        if (code != -32603) return CliErrorKind.None;
        // Exact fixed messages, emitted without dynamic values by the official backend:
        // https://github.com/openai/codex/blob/rust-v0.159.2/codex-rs/app-server/src/request_processors/account_processor/workspace_routing.rs
        // Non-401 upstream failures lose their HTTP status here. Never infer 403.
        return message switch
        {
            "workspace routing discovery unauthorized (401)" => CliErrorKind.RoutingUnauthorized401,
            "workspace routing discovery failed" => CliErrorKind.RoutingDiscoveryFailed,
            "workspace routing discovery timed out" => CliErrorKind.RoutingDiscoveryTimeout,
            _ => CliErrorKind.None
        };
    }
}

/// <summary>Newline-delimited JSON-RPC: independent response routing, notifications, bounded input.</summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Task _pump;
    private Task _activeWrite = Task.CompletedTask;
    private Task _activeRead = Task.CompletedTask;
    private int _readerDisposed, _writerDisposed;
    private long _nextId;
    private int _disposed;
    private volatile Exception? _terminalError;
    public const int MaximumLineLength = 1_048_576;

    public JsonRpcConnection(TextReader reader, TextWriter writer)
    {
        _reader = reader;
        _writer = writer;
        _pump = PumpAsync();
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(JsonRpcConnection));
        if (_terminalError is { } terminal) throw new IOException("Codex app-server connection closed.", terminal);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, _lifetime.Token);
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        // Close the race between the initial liveness check and insertion into the pending map.
        if (_terminalError is { } failure) completion.TrySetException(failure);
#if NETFRAMEWORK
        using var registration = linked.Token.Register(() => completion.TrySetCanceled());
#else
        using var registration = linked.Token.Register(() => completion.TrySetCanceled(linked.Token));
#endif
        try
        {
            await SendAsync(new { id, method, @params = parameters }, linked.Token).ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Codex app-server request timed out.");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(JsonRpcConnection));
        return SendAsync(new { method, @params = parameters }, cancellationToken);
    }

    private async Task SendAsync(object message, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        token = linked.Token;
        var json = JsonSerializer.Serialize(message);
        await _writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
#if NETFRAMEWORK
            token.ThrowIfCancellationRequested();
            _activeWrite = _writer.WriteLineAsync(json);
            await AsyncCompatibility.WaitAsync(_activeWrite, token).ConfigureAwait(false);
            _activeWrite = _writer.FlushAsync();
            await AsyncCompatibility.WaitAsync(_activeWrite, token).ConfigureAwait(false);
#else
            _activeWrite = _writer.WriteLineAsync(json.AsMemory(), token);
            await _activeWrite.ConfigureAwait(false);
            _activeWrite = _writer.FlushAsync(token);
            await _activeWrite.ConfigureAwait(false);
#endif
        }
#if NETFRAMEWORK
        catch (OperationCanceledException)
        {
            // A Framework pipe cannot cancel its write directly. Never start a subsequent
            // write over an abandoned operation: the process owner will close the connection.
            _lifetime.Cancel();
            throw;
        }
#endif
        finally { _writeGate.Release(); }
    }

    private async Task PumpAsync()
    {
        try
        {
            var chunk = new char[4096];
            var line = new StringBuilder();
            while (true)
            {
                var count = await AsyncCompatibility.ReadAsync(_reader, chunk, _lifetime.Token,
                    operation => _activeRead = operation).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("Codex app-server closed stdout.");
                for (var i = 0; i < count; i++)
                {
                    if (chunk[i] == '\n')
                    {
                        if (line.Length > 0) await RouteAsync(line.ToString()).ConfigureAwait(false);
                        line.Clear();
                    }
                    else if (chunk[i] != '\r')
                    {
                        if (line.Length >= MaximumLineLength) throw new InvalidDataException("RPC line exceeded limit.");
                        line.Append(chunk[i]);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            _terminalError = exception;
            foreach (var request in _pending.Values) request.TrySetException(exception);
        }
    }

    private async Task RouteAsync(string line)
    {
        using var document = JsonDocument.Parse(line);
        var message = document.RootElement;
        if (message.ValueKind != JsonValueKind.Object) throw new InvalidDataException("RPC object expected.");
        if (message.TryGetProperty("method", out _))
        {
            // Unsolicited server requests must never cause approvals, login, tools, or model turns.
            if (message.TryGetProperty("id", out var requestId))
                await SendAsync(new { id = requestId.Clone(), error = new { code = -32601, message = "Read-only quota client; method unsupported." } }, _lifetime.Token).ConfigureAwait(false);
            return; // Notifications cannot be mistaken for request results.
        }
        if (!message.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number ||
            !idElement.TryGetInt64(out var id) || !_pending.TryGetValue(id, out var pending)) return;
        if (message.TryGetProperty("error", out var error))
        {
            int? code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c) &&
                c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var number) ? number : null;
            var rateLimited = code == 429;
            var errorKind = CliErrorKind.None;
            if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String)
            {
                // Interpret only a coarse status in memory; never copy server text into an exception/log.
                var messageText = text.GetString() ?? "";
                errorKind = RpcException.ClassifyError(code, messageText);
                rateLimited |= System.Text.RegularExpressions.Regex.IsMatch(messageText, @"\b429\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
                    messageText.IndexOf("Too Many Requests", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            pending.TrySetException(new RpcException(code, rateLimited, errorKind));
        }
        else if (message.TryGetProperty("result", out var result)) pending.TrySetResult(result.Clone());
        else pending.TrySetException(new InvalidDataException("RPC result missing."));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        // Join the pump before disposing StreamWriter: it may be replying to a server request.
        // The caller kills its child process first; bounds also protect custom/misbehaving streams.
        try { await AsyncCompatibility.WaitAsync(_pump, TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException) { }
        finally { if (_activeRead.IsCompleted) DisposeReader(); }

        var acquired = false;
        try
        {
            acquired = await _writeGate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
#if NETFRAMEWORK
            if (acquired)
            {
                try { await AsyncCompatibility.WaitAsync(_activeWrite, TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException) { }
                if (_activeWrite.IsCompleted) DisposeWriter();
            }
#else
            if (acquired && _activeWrite.IsCompleted) DisposeWriter();
#endif
            // If a broken stream ignores cancellation, don't dispose it mid-write; the process owner
            // still closes its pipe handles. Never block the tray's Exit on a third-party process.
        }
        finally
        {
            if (acquired) _writeGate.Release();
            // These small managed synchronization objects are collected with the connection.
            // Keeping them alive avoids racing late cancellation continuations on broken streams.
        }
    }

    // Cancellation may complete a Framework waiting wrapper before its underlying pipe I/O.
    // The process owner must retain the pipes and lease until these actual operations finish.
    internal bool IsIoQuiescent => _pump.IsCompleted && _activeRead.IsCompleted &&
        _activeWrite.IsCompleted && _writeGate.CurrentCount == 1;

    internal bool TryFinishCleanup()
    {
        if (Volatile.Read(ref _disposed) == 0 || !IsIoQuiescent) return false;
        DisposeReader();
        DisposeWriter();
        return true;
    }

    private void DisposeReader()
    { if (Interlocked.Exchange(ref _readerDisposed, 1) == 0) DisposeStreamSafely(_reader); }
    private void DisposeWriter()
    { if (Interlocked.Exchange(ref _writerDisposed, 1) == 0) DisposeStreamSafely(_writer); }

    private static void DisposeStreamSafely(IDisposable stream)
    {
        try { stream.Dispose(); }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { }
    }
}
