using System.Collections.Immutable;

namespace Waddamburo.Catalog;

/// <summary>From <see cref="Time"/> the chart's beat count is <see cref="Beat"/>, going on at <see cref="BeatsPerSecond"/> (0 in a #DELAY).</summary>
public readonly record struct ChartBeatPoint(TimeSpan Time, double Beat, double BeatsPerSecond);

/// <summary>
/// TJA <c>#JPOSSCROLL</c>: from <see cref="Time"/> the hit target slides by (<see cref="X"/>, <see cref="Y"/>) stage
/// pixels over <see cref="Duration"/>, already cut short where the next move starts. Drawing only.
/// </summary>
public readonly record struct ChartJudgeMove(TimeSpan Time, TimeSpan Duration, double X, double Y);

/// <summary>
/// What a chart draws beyond its notes: the hit target's moves, the beat count that #HBSCROLL/#BMSCROLL scroll by, and
/// modchart changes. Drawing only (no judgement or score reads it), so it stays out of <see cref="ChartHash"/>; a song
/// package carries it in <see cref="ChartDrawing"/>. Every value is a function of chart time alone.
/// </summary>
public sealed record ChartEffects
{
    /// <summary>A chart without any (almost all of them).</summary>
    public static readonly ChartEffects None = new();

    public bool IsEmpty => JudgeMoves.IsEmpty && Beats.IsEmpty && Mods.IsEmpty;

    /// <summary>Authored file keyframes retained for local editing; drawing blocks only need compiled mods.</summary>
    public ImmutableDictionary<string, ImmutableArray<ChartModKeyframe>> ModTracks { get; init; } =
        ImmutableDictionary<string, ImmutableArray<ChartModKeyframe>>.Empty;

    /// <summary>Modchart changes (TJA #EASE/#SET) in start order (<see cref="ChartMods"/>).</summary>
    public ImmutableArray<ChartModEase> Mods { get; init; } = [];

    /// <summary>The beat count over time (for <see cref="ChartScrollPoint.ByBeat"/>), in time order; empty when no point scrolls by beat.</summary>
    public ImmutableArray<ChartBeatPoint> Beats { get; init; } = [];

    /// <summary>The chart's beat count at <paramref name="time"/> (<see cref="Beats"/>; 0 without them).</summary>
    public double BeatAt(TimeSpan time)
    {
        var (low, high) = (0, Beats.Length);
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (Beats[middle].Time <= time) low = middle + 1;
            else high = middle;
        }
        if (Beats.IsEmpty) return 0;
        var point = Beats[Math.Max(low - 1, 0)];
        return point.Beat + (time - point.Time).TotalSeconds * point.BeatsPerSecond;
    }

    /// <summary>When the beat count reaches <paramref name="beat"/> (one a #DELAY stops at: the delay's end).</summary>
    public TimeSpan TimeAtBeat(double beat)
    {
        if (Beats.IsEmpty) return TimeSpan.Zero;
        var index = 0;
        while (index + 1 < Beats.Length && Beats[index + 1].Beat <= beat)
            index++;
        var point = Beats[index];
        var seconds = point.BeatsPerSecond > 0 ? (beat - point.Beat) / point.BeatsPerSecond : 0;
        return point.Time + TimeSpan.FromSeconds(Math.Clamp(seconds, -1e6, 1e6));
    }

    /// <summary>The hit target's moves (TJA #JPOSSCROLL), in time order.</summary>
    public ImmutableArray<ChartJudgeMove> JudgeMoves { get; init; } = [];

    /// <summary>How far the hit target has moved from its place at <paramref name="time"/>.</summary>
    // ponytail: walks every move per call; charts have a handful. Binary search if one has thousands.
    public (double X, double Y) JudgeOffsetAt(TimeSpan time)
    {
        var (x, y) = (0d, 0d);
        foreach (var move in JudgeMoves)
        {
            if (time <= move.Time) break;
            var done = move.Duration <= TimeSpan.Zero ? 1 : Math.Min(1, (time - move.Time) / move.Duration);
            (x, y) = (x + move.X * done, y + move.Y * done);
        }
        return (x, y);
    }
}
