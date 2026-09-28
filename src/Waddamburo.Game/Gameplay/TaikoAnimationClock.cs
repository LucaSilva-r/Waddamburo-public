using System.Collections.Immutable;
using Waddamburo.Catalog;

namespace Waddamburo.Game.Gameplay;

/// <summary>
/// Integrates chart tempo into presentation frames. Traced in the original game (session19-21): the
/// beat-synced movies' frame step is 5340 / BPM against 60 per 60 Hz tick (44.5 at 120 BPM, 50.38 at
/// 106, 25.19 at 212), i.e. BPM / 89 frames per tick = 3600 / 89 (about 40.4) frames per beat.
/// </summary>
public sealed class TaikoAnimationClock
{
    /// <summary>Frames per second per BPM: (BPM / 89) frames per tick at 60 ticks a second.</summary>
    private const double FramesPerSecondPerBpm = 60d / 89;

    private readonly ImmutableArray<ChartTimingPoint> _points;
    private readonly double[] _frames;
    private double? _previous;
    private double _remainder;

    public TaikoAnimationClock(ImmutableArray<ChartTimingPoint> points)
    {
        if (points.IsDefaultOrEmpty) throw new ArgumentException("Timing points are required.", nameof(points));
        _points = points;
        _frames = new double[points.Length];
        for (var i = 1; i < points.Length; i++)
            _frames[i] = _frames[i - 1] + (points[i].Time - points[i - 1].Time).TotalSeconds
                * points[i - 1].BeatsPerMinute * FramesPerSecondPerBpm;
    }

    public double Position(TimeSpan time)
    {
        var low = 0;
        var high = _points.Length;
        while (low + 1 < high)
        {
            var middle = (low + high) / 2;
            if (_points[middle].Time <= time) low = middle;
            else high = middle;
        }
        return _frames[low] + (time - _points[low].Time).TotalSeconds * _points[low].BeatsPerMinute * FramesPerSecondPerBpm;
    }

    public int FramesToAdvance { get; private set; }
    public float Interpolation => (float)_remainder;

    public double Advance(TimeSpan time)
    {
        var position = Position(time);
        var delta = _previous is { } previous ? Math.Max(0, position - previous) : 0;
        // Repeated/backwards source-position samples must not replay animation time.
        _previous = Math.Max(_previous ?? position, position);
        _remainder += delta;
        FramesToAdvance = checked((int)Math.Floor(_remainder + 1e-9));
        _remainder = Math.Max(0, _remainder - FramesToAdvance);
        return delta;
    }
}
