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
    public PlayableHitObject(TimeSpan startTime, PlayableNoteKind kind, bool isHand = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(startTime, TimeSpan.Zero);
        if (isHand && kind is not (PlayableNoteKind.BigDon or PlayableNoteKind.BigKa))
            throw new ArgumentException("Only big notes can be hand notes.", nameof(isHand));
        StartTime = startTime;
        Kind = kind;
        IsHand = isHand;
    }

    public TimeSpan StartTime { get; }
    public PlayableNoteKind Kind { get; }

    /// <summary>
    /// A hand-in-hand big note (fumen 0xB/0xD, TJA A/B): played like a big note; with two players
    /// the game draws it with feet.
    /// </summary>
    public bool IsHand { get; }

    /// <summary>A Waiwai synchro note: both players play it in a together section (drawn as onp_synchro_*).</summary>
    public bool IsSynchro { get; init; }

    /// <summary>
    /// A note inside a quick run, sung do / ko / katsu rather than don / ka (fumen types 2, 3, 5).
    /// Waiwai never makes these synchro notes (traced session12-notes).
    /// </summary>
    public bool InRun { get; init; }

    /// <summary>A run don sung ko (fumen type 3). Presentation only: not part of the chart hash.</summary>
    public bool IsKo { get; init; }

    /// <summary>A Waiwai rare (heart) note: played as its note, drawn with the collabo's rare art.</summary>
    public bool IsRare { get; init; }

    /// <summary>The 譜面分岐 route this note belongs to; null: played on every route.</summary>
    public ChartBranch? Branch { get; init; }
    public bool IsStrong => Kind is PlayableNoteKind.BigDon or PlayableNoteKind.BigKa;
}

public enum PlayableLongNoteKind { Roll, BigRoll, Balloon, Kusudama }

public readonly record struct PlayableLongNote
{
    public PlayableLongNote(TimeSpan startTime, TimeSpan endTime, PlayableLongNoteKind kind, int requiredHits = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(startTime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(endTime, startTime);
        ArgumentOutOfRangeException.ThrowIfNegative(requiredHits);
        if (kind is PlayableLongNoteKind.Balloon or PlayableLongNoteKind.Kusudama && requiredHits == 0)
            throw new ArgumentOutOfRangeException(nameof(requiredHits));
        StartTime = startTime;
        EndTime = endTime;
        Kind = kind;
        RequiredHits = requiredHits;
    }
    public TimeSpan StartTime { get; }
    public TimeSpan EndTime { get; }
    public PlayableLongNoteKind Kind { get; }
    public int RequiredHits { get; }

    /// <summary>The 譜面分岐 route this note belongs to; null: played on every route.</summary>
    public ChartBranch? Branch { get; init; }
    public bool IsBalloon => Kind is PlayableLongNoteKind.Balloon or PlayableLongNoteKind.Kusudama;
}

/// <summary>The three 譜面分岐 routes: 普通, 玄人 and 達人 (lane_hit's BRANCH_NORMAL/KUROUTO/TATSUJIN).</summary>
public enum ChartBranch : byte { Normal, Expert, Master }

/// <summary>
/// One route's requirement at a check: branch points at least <see cref="Master"/> go to master, at least
/// <see cref="Expert"/> to expert, else normal. -1 (fumen) on either: the route stays as it is.
/// </summary>
public readonly record struct ChartBranchThreshold(int Expert, int Master)
{
    public static readonly ChartBranchThreshold Stay = new(-1, -1);

    public ChartBranch Next(ChartBranch current, int points) => Expert < 0 || Master < 0 ? current
        : points >= Master ? ChartBranch.Master : points >= Expert ? ChartBranch.Expert : ChartBranch.Normal;
}

/// <summary>
/// A 譜面分岐 check. At <see cref="Time"/> (the measure before the branch, drawn with a yellow bar line) the
/// branch points earned since the previous check pick the route played from <see cref="Start"/>, by the
/// threshold row of the route being played.
/// </summary>
public readonly record struct ChartBranchCheck(TimeSpan Time, TimeSpan Start,
    ChartBranchThreshold FromNormal, ChartBranchThreshold FromExpert, ChartBranchThreshold FromMaster)
{
    public ChartBranchThreshold For(ChartBranch current) => current switch
    {
        ChartBranch.Normal => FromNormal,
        ChartBranch.Expert => FromExpert,
        _ => FromMaster,
    };
}

/// <summary>Branch points per judgement (the fumen header's; Green's usual 20/10/0/1/20/10/1/30/30).</summary>
public readonly record struct ChartBranchPoints(int Great, int Good, int Bad, int Roll,
    int BigGreat, int BigGood, int BigRoll, int Balloon, int Kusudama)
{
    public static readonly ChartBranchPoints Default = new(20, 10, 0, 1, 20, 10, 1, 30, 30);
}

/// <summary>A chart's 譜面分岐: its checks in time order and what each judgement is worth.</summary>
public sealed record ChartBranching(ImmutableArray<ChartBranchCheck> Checks, ChartBranchPoints Points);

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
        IEnumerable<ChartBarLine> barLines,
        IEnumerable<PlayableLongNote>? longNotes = null)
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
        LongNotes = ordered(longNotes ?? [], static value => value.StartTime, nameof(longNotes));
        if (LongNotes.Any(note => note.EndTime > duration))
            throw new ArgumentException("A long note cannot end after the chart duration.", nameof(longNotes));
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
    public ImmutableArray<PlayableLongNote> LongNotes { get; }
    public int NoteCount => HitObjects.Length;
    /// <summary>Authored star rating, when the source has one.</summary>
    public int? Level { get; init; }
    /// <summary>Optional TJA base score and ten-combo increment.</summary>
    public int? ScoreInit { get; init; }
    public int? ScoreDiff { get; init; }

    /// <summary>
    /// Played with the 真打 (shin-uchi) option: every Great is worth this, a Good half of it (rounded up to
    /// ten), with no combo growth, 100-combo bonus or Go-Go factor. Null: the normal scoring.
    /// </summary>
    public int? ShinuchiBase { get; init; }

    /// <summary>
    /// The chart's 譜面分岐, when it has one: its notes then carry <see cref="PlayableHitObject.Branch"/>
    /// and only the route being played is judged and drawn (<c>TaikoJudgementSession.BranchAt</c>).
    /// Timing, scroll, Go-Go and bar lines are the normal route's.
    /// </summary>
    public ChartBranching? Branching { get; init; }

    /// <summary>Whether a note tagged <paramref name="branch"/> plays on <paramref name="route"/>.</summary>
    public static bool IsOn(ChartBranch? branch, ChartBranch route) => branch is null || branch == route;

    /// <summary>
    /// The chart as one route, without branching: what per-chart figures (soul gauge note count, score
    /// rates, 真打 base) are worked out from. A chart without branching is returned as it is.
    /// </summary>
    public PlayableChart OnRoute(ChartBranch route) => Branching is null ? this
        : new PlayableChart(Key, AuthoredOffset, Duration, HitObjects.Where(note => IsOn(note.Branch, route)),
            TimingPoints, ScrollPoints, EffectPoints, BarLines, LongNotes.Where(note => IsOn(note.Branch, route)))
        {
            Level = Level,
            ScoreInit = ScoreInit,
            ScoreDiff = ScoreDiff,
            ShinuchiBase = ShinuchiBase,
        };

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
