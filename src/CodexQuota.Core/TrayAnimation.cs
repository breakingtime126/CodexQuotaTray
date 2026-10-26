namespace CodexQuota.Core;

public readonly record struct TrayAnimationFrame(
    double? RingPercent, double SweepPhase, long Key, bool IsActive, int NextDelayMilliseconds);

/// <summary>Finite, event-driven ring motion; no timer or background work lives in this class.</summary>
public sealed class TrayAnimation
{
    public const int FrameIntervalMilliseconds = 67; // At most ~15 FPS.
    public const int TransitionMilliseconds = 201; // Three 67 ms steps, roughly 200 ms.
    public const int SweepMilliseconds = 420;
    private bool _active;
    private double _started;
    private double _from;
    private int _target;
    private int _duration;
    private long _generation;

    public bool Start(ReadStatus status, int? previousPercent, int? currentPercent,
        bool stale, double nowMilliseconds, bool enabled)
    {
        if (!enabled || status != ReadStatus.Ready || stale || currentPercent is null ||
            !IsFinite(nowMilliseconds) || currentPercent < 0 || currentPercent > 100)
        { Stop(); return false; }
        var priorFrame = Sample(previousPercent, false, nowMilliseconds);
        _from = priorFrame.IsActive && priorFrame.RingPercent is { } visible ? visible :
            previousPercent is { } previous && previous >= 0 && previous <= 100 ? previous : currentPercent.Value;
        _target = currentPercent.Value;
        _started = nowMilliseconds;
        _duration = _target == 0 ? TransitionMilliseconds : SweepMilliseconds;
        // An unchanged empty ring has nothing to highlight. Do not spin a pretend quota arc.
        _active = _target != 0 || Math.Abs(_from - _target) > 0.001;
        _generation = _generation == long.MaxValue / 16 ? 1 : _generation + 1;
        return _active;
    }

    public TrayAnimationFrame Sample(int? currentPercent, bool stale, double nowMilliseconds)
    {
        if (!_active || stale || currentPercent != _target || !IsFinite(nowMilliseconds))
        { Stop(); return Static(currentPercent); }
        var elapsed = Math.Max(0, nowMilliseconds - _started);
        if (elapsed >= _duration) { Stop(); return Static(currentPercent); }
        // Repeated UI events in the same frame interval do not regenerate or re-send the icon.
        var index = (int)(elapsed / FrameIntervalMilliseconds);
        var sampledTime = index * FrameIntervalMilliseconds;
        var t = Math.Min(1, sampledTime / (double)TransitionMilliseconds);
        var ease = 1 - Math.Pow(1 - t, 3);
        var ring = _from + (_target - _from) * ease;
        var sweep = _target == 0 ? double.NaN : sampledTime / (double)SweepMilliseconds;
        var delay = Math.Max(1, Math.Min(FrameIntervalMilliseconds, (int)Math.Ceiling(_duration - elapsed)));
        return new(ring, sweep, _generation * 16 + index + 1, true, delay);
    }

    public void Stop() => _active = false;
    private static TrayAnimationFrame Static(int? percent) => new(percent, double.NaN, 0, false, 0);
    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
