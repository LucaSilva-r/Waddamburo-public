using System.Collections.Immutable;
using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Editor;

/// <summary>Practice owns the clock and rebuilds its attempt when seeking; the editor only requests moves.</summary>
public interface IEffectsEditorTransport
{
    ReviewClock Clock { get; }
    TimeSpan? LoopStart { get; }
    TimeSpan? LoopEnd { get; }
    void Seek(TimeSpan position);
    void MarkLoopStart();
    void MarkLoopEnd();
    void ClearLoop();
    /// <summary>The scene's movies stand still (their bounds stop moving under the gizmos); chart time goes on.</summary>
    bool FreezeAnimations { get; set; }
}

/// <summary>Read-only data shared by timeline, graph, and inspector. No GPU or file access.</summary>
public sealed class EffectPreview
{
    private readonly Dictionary<string, ChartModEase[]> _changes;
    public PlayableChart Chart { get; }
    public ChartEffects BeatMap { get; }
    public ImmutableDictionary<string, ImmutableArray<ChartModKeyframe>> Tracks { get; }
    public ImmutableArray<double> Bars { get; }

    /// <param name="chart">The chart as previewed, its effects applied.</param>
    /// <param name="keyframes">The editor document's tracks; changes without one are shown from their timed segments.</param>
    public EffectPreview(PlayableChart chart, IReadOnlyDictionary<string, ImmutableArray<ChartModKeyframe>>? keyframes = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        Chart = chart;
        BeatMap = chart.Effects.Beats.IsEmpty ? new() { Beats = ChartModFile.BeatsOf(chart.TimingPoints) } : chart.Effects;
        _changes = chart.Effects.Mods.GroupBy(c => c.Target + "." + c.Property)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Start).ToArray());
        var tracks = ImmutableDictionary.CreateBuilder<string, ImmutableArray<ChartModKeyframe>>(StringComparer.Ordinal);
        if (keyframes is not null) tracks.AddRange(keyframes);
        // Charts authored with TJA SET/EASE have timed segments rather than file keyframes.
        // Display the segment starts and ends, preserving discontinuities at the same beat.
        foreach (var (key, changes) in _changes)
        {
            if (tracks.ContainsKey(key)) continue;
            var keys = new List<ChartModKeyframe>();
            foreach (var c in changes)
            {
                if (c.Length > TimeSpan.Zero)
                    keys.Add(new(BeatAt(c.Start), ChartMods.ValueAt(changes, c.Start, Default(key))));
                keys.Add(new(BeatAt(c.Start + c.Length), c.Value, c.Ease, c.Curve));
            }
            tracks[key] = [.. keys.OrderBy(k => k.Beat)];
        }
        Tracks = tracks.ToImmutable();
        Bars = [.. chart.BarLines.Where(b => b.IsVisible).Select(b => BeatAt(b.Time)).Distinct().Order()];
    }

    public double BeatAt(TimeSpan time) => BeatMap.BeatAt(time);
    public TimeSpan TimeAt(double beat) => BeatMap.TimeAtBeat(beat);
    public double ValueAt(string key, double beat) => ValueAt(key, TimeAt(beat));
    public double ValueAt(string key, TimeSpan time) => _changes.TryGetValue(key, out var changes)
        ? ChartMods.ValueAt(changes, time, Default(key)) : Default(key);
    private static double Default(string key) => ChartMods.Default(key[(key.IndexOf('.') + 1)..]);

    public double MeasureAt(TimeSpan time)
    {
        var timing = Chart.TimingPoints.LastOrDefault(p => p.Time <= time, Chart.TimingPoints[0]);
        return timing.BeatsPerMeasure * 4d / timing.BeatUnit;
    }
}

/// <summary>A beat-space viewport; zoom preserves the beat underneath the pointer.</summary>
public sealed class BeatView
{
    public double Start { get; private set; }
    public double Span { get; private set; } = 64;
    public double BeatAt(double fraction) => Start + fraction * Span;
    public double FractionAt(double beat) => (beat - Start) / Span;
    public void Frame(double first, double last)
    {
        if (!double.IsFinite(first) || !double.IsFinite(last)) throw new ArgumentOutOfRangeException(nameof(first));
        var span = Math.Clamp(last - first, 4, 1e6);
        Start = Math.Clamp(first, -1e9, 1e9) - span * .08;
        Span = span * 1.16;
    }
    public void Pan(double fractions) => Start = Math.Clamp(Start - fractions * Span, -1e9, 1e9);
    public void Zoom(double wheel, double fraction)
    {
        var at = BeatAt(fraction);
        Span = Math.Clamp(Span * Math.Exp(-Math.Clamp(wheel, -8, 8) * .2), .25, 1e6);
        Start = Math.Clamp(at - fraction * Span, -1e9, 1e9);
    }
    public double GridStep(double pixels) => Math.Pow(2, Math.Ceiling(Math.Log2(Span * 55 / Math.Max(1, pixels))));
}

/// <summary>Normalizes values before fitting the graph so extreme finite effects cannot overflow screen coordinates.</summary>
public readonly record struct GraphScale(double Scale, double Min, double Max)
{
    public static GraphScale Fit(IEnumerable<double> values)
    {
        var finite = values.Where(double.IsFinite).ToArray();
        var scale = Math.Max(1, finite.Select(Math.Abs).DefaultIfEmpty(1).Max());
        var min = finite.Select(v => v / scale).DefaultIfEmpty(-1).Min();
        var max = finite.Select(v => v / scale).DefaultIfEmpty(1).Max();
        var pad = Math.Max(1e-6, Math.Max(Math.Abs(min) * .05, (max - min) * .12));
        return new(scale, min - pad, max + pad);
    }
    public double Fraction(double value) => (value / Scale - Min) / (Max - Min);
    public double ValueAt(double fraction) => Math.Clamp((Min + (Max - Min) * fraction) * Scale, -double.MaxValue, double.MaxValue);
}
