namespace Waddamburo.Platform.Sdl.Media;

/// <summary>Interpolates block-sized audio progress using a monotonic clock.</summary>
public sealed class AudioPlaybackClock
{
    private TimeSpan? _timestamp;
    private TimeSpan _position;
    private TimeSpan _consumed;
    private TimeSpan _lastProgress;

    public TimeSpan Update(TimeSpan consumed, TimeSpan hardwareLatency, TimeSpan timestamp)
    {
        // Queue consumption is a noisy reference, not the animation clock. Allow
        // one buffer of uncertainty and correct drift at at most 1% playback speed (5% past 5 ms).
        var earliest = consumed - hardwareLatency;
        if (_timestamp is not { } previous)
        {
            _position = earliest;
            _lastProgress = timestamp;
        }
        else
        {
            if (consumed > _consumed)
                _lastProgress = timestamp;
            var elapsed = timestamp > previous ? timestamp - previous : TimeSpan.Zero;
            var stallTimeout = TimeSpan.FromSeconds(Math.Max(0.1, hardwareLatency.TotalSeconds * 4));
            if (consumed > TimeSpan.Zero && timestamp - _lastProgress <= stallTimeout)
            {
                var predicted = _position + elapsed;
                var error = predicted < earliest ? earliest - predicted
                    : predicted > consumed ? consumed - predicted : TimeSpan.Zero;
                // Off by more than a buffer (audio that slipped: a late WASAPI exclusive period), 5% so it is caught
                // within a fraction of a second; buffer-sized jitter and drift stay at 1%, too slow to see.
                var rate = Math.Abs(error.TotalMilliseconds) > Math.Max(5, hardwareLatency.TotalMilliseconds) ? 0.05 : 0.01;
                var correction = TimeSpan.FromSeconds(Math.Clamp(error.TotalSeconds,
                    -elapsed.TotalSeconds * rate, elapsed.TotalSeconds * rate));
                _position = predicted + correction;
            }
        }
        _consumed = consumed;
        _timestamp = timestamp;
        return _position > TimeSpan.Zero ? _position : TimeSpan.Zero;
    }
}
