namespace Waddamburo.Game.SongSelect;

/// <summary>Host-owned countdown using monotonic timestamps, independent of render ticks.</summary>
public sealed class SongSelectTimer(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _started;
    private TimeSpan _remaining;
    public bool IsRunning { get; private set; }

    public TimeSpan Remaining => !IsRunning ? _remaining
        : TimeSpan.FromTicks(Math.Max(0, (_remaining - _time.GetElapsedTime(_started)).Ticks));

    public bool IsTimeUp => IsRunning && Remaining == TimeSpan.Zero;

    public void Start(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        var duration = TimeSpan.FromSeconds(seconds);
        _remaining = duration;
        _started = _time.GetTimestamp();
        IsRunning = true;
    }

    public void Stop()
    {
        _remaining = Remaining;
        IsRunning = false;
    }
}
