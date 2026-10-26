using System.Diagnostics;

namespace CodexQuota.Core;

/// <summary>Only the process/job created by this owner may be observed or terminated.</summary>
internal interface IOwnedQuotaProcess : IDisposable
{
    bool Start();
    bool HasOwnedProcess { get; }
    CliStartOperation StartOperation { get; }
    TextReader StandardOutput { get; }
    TextReader StandardError { get; }
    TextWriter StandardInput { get; }
    void RequestTermination();
    OwnedProcessExit ObserveExit();
}

internal sealed class OwnedQuotaProcess : IOwnedQuotaProcess
{
    private readonly FrameworkCliProcess? _windows;
    private readonly Process? _portable;
    private bool _started;
    public OwnedQuotaProcess(ProcessStartInfo start)
    {
        if (Environment.OSVersion.Platform == PlatformID.Win32NT) _windows = new(start);
        else _portable = new Process { StartInfo = start }; // Synthetic cross-platform protocol tests.
    }
    public bool Start() => _started = _windows?.Start() ?? _portable!.Start();
    public bool HasOwnedProcess => _windows?.HasOwnedProcess ?? _started;
    public CliStartOperation StartOperation => _windows?.StartOperation ?? CliStartOperation.ManagedProcessStart;
    public TextReader StandardOutput => _windows?.StandardOutput ?? _portable!.StandardOutput;
    public TextReader StandardError => _windows?.StandardError ?? _portable!.StandardError;
    public TextWriter StandardInput => _windows?.StandardInput ?? _portable!.StandardInput;
    public void RequestTermination()
    {
        if (_windows is not null) _windows.RequestOwnedTreeTermination();
#if !NETFRAMEWORK
        else if (_started && !_portable!.HasExited) _portable.Kill(entireProcessTree: true);
#endif
    }
    public OwnedProcessExit ObserveExit()
    {
        if (_windows is not null) return _windows.ObserveOwnedTreeExit();
        if (!_started) return new(true, true);
        var exited = _portable!.HasExited;
        return new(exited, exited, exited ? _portable.ExitCode : null);
    }
    public void Dispose() { _windows?.Dispose(); _portable?.Dispose(); }
}
