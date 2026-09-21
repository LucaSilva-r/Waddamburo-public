using System.Collections.Immutable;

namespace Waddamburo.Catalog;

public enum PlayableNoteKind
{
    Don,
    Ka,
    BigDon,
    BigKa,
}

public readonly record struct PlayableHitObject
{
    public PlayableHitObject(TimeSpan startTime, PlayableNoteKind kind)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(startTime, TimeSpan.Zero);
        StartTime = startTime;
        Kind = kind;
    }

    public TimeSpan StartTime { get; }
    public PlayableNoteKind Kind { get; }
    public bool IsStrong => Kind is PlayableNoteKind.BigDon or PlayableNoteKind.BigKa;
}

public readonly record struct ChartTimingPoint
{
    public ChartTimingPoint(TimeSpan time, double beatsPerMinute, int beatsPerMeasure, int beatUnit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(time, TimeSpan.Zero);
        if (!double.IsFinite(beatsPerMinute) || beatsPerMinute <= 0)
            throw new ArgumentOutOfRangeException(nameof(beatsPerMinute));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(beatsPerMeasure);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(beatUnit);
        Time = time;
        BeatsPerMinute = beatsPerMinute;
        BeatsPerMeasure = beatsPerMeasure;
        BeatUnit = beatUnit;
    }

    public TimeSpan Time { get; }
    public double BeatsPerMinute { get; }
    public int BeatsPerMeasure { get; }
    public int BeatUnit { get; }
}

public readonly record struct ChartScrollPoint
{
    public ChartScrollPoint(TimeSpan time, double multiplier)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(time, TimeSpan.Zero);
        if (!double.IsFinite(multiplier))
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        Time = time;
        Multiplier = multiplier;
    }

    public TimeSpan Time { get; }
    public double Multiplier { get; }
}

public readonly record struct ChartEffectPoint
{
    public ChartEffectPoint(TimeSpan time, bool isGoGo)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(time, TimeSpan.Zero);
        Time = time;
        IsGoGo = isGoGo;
    }

    public TimeSpan Time { get; }
    public bool IsGoGo { get; }
}

public readonly record struct ChartBarLine
{
    public ChartBarLine(TimeSpan time, bool isVisible)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(time, TimeSpan.Zero);
        Time = time;
        IsVisible = isVisible;
    }

    public TimeSpan Time { get; }
    public bool IsVisible { get; }
}

public sealed record PlayableChart
{
    public PlayableChart(
        ChartKey key,
        TimeSpan authoredOffset,
        TimeSpan duration,
        IEnumerable<PlayableHitObject> hitObjects,
        IEnumerable<ChartTimingPoint> timingPoints,
        IEnumerable<ChartScrollPoint> scrollPoints,
        IEnumerable<ChartEffectPoint> effectPoints,
        IEnumerable<ChartBarLine> barLines)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        Key = key;
        AuthoredOffset = authoredOffset;
        Duration = duration;
        HitObjects = ordered(hitObjects, static value => value.StartTime, nameof(hitObjects));
        TimingPoints = nonEmptyOrdered(timingPoints, static value => value.Time, nameof(timingPoints));
        ScrollPoints = nonEmptyOrdered(scrollPoints, static value => value.Time, nameof(scrollPoints));
        EffectPoints = nonEmptyOrdered(effectPoints, static value => value.Time, nameof(effectPoints));
        BarLines = ordered(barLines, static value => value.Time, nameof(barLines));
        if (!HitObjects.IsEmpty && HitObjects[^1].StartTime > duration)
            throw new ArgumentException("A hit object cannot occur after the chart duration.", nameof(duration));
    }

    public ChartKey Key { get; }
    public TimeSpan AuthoredOffset { get; }
    public TimeSpan Duration { get; }
    public ImmutableArray<PlayableHitObject> HitObjects { get; }
    public ImmutableArray<ChartTimingPoint> TimingPoints { get; }
    public ImmutableArray<ChartScrollPoint> ScrollPoints { get; }
    public ImmutableArray<ChartEffectPoint> EffectPoints { get; }
    public ImmutableArray<ChartBarLine> BarLines { get; }
    public int NoteCount => HitObjects.Length;

    private static ImmutableArray<T> nonEmptyOrdered<T>(
        IEnumerable<T> values,
        Func<T, TimeSpan> time,
        string parameterName)
    {
        var materialized = ordered(values, time, parameterName);
        if (materialized.IsEmpty)
            throw new ArgumentException("A control-point stream must contain an initial value.", parameterName);
        if (time(materialized[0]) != TimeSpan.Zero)
            throw new ArgumentException("A control-point stream must begin at chart time zero.", parameterName);
        return materialized;
    }

    private static ImmutableArray<T> ordered<T>(
        IEnumerable<T> values,
        Func<T, TimeSpan> time,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var materialized = values.ToImmutableArray();
        for (var index = 1; index < materialized.Length; index++)
        {
            if (time(materialized[index]) < time(materialized[index - 1]))
                throw new ArgumentException("Chart events must be ordered by time.", parameterName);
        }
        return materialized;
    }
}

public interface IPlayableChartProvider
{
    CatalogProviderId Id { get; }

    ValueTask<PlayableChart> LoadChartAsync(
        ChartKey chart,
        CatalogAssetKey asset,
        CancellationToken cancellationToken = default);
}
