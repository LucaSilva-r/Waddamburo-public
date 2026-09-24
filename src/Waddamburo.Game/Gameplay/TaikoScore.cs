using Waddamburo.Catalog;

namespace Waddamburo.Game.Gameplay;

/// <summary>One-player score accumulated from accepted gameplay events.</summary>
public sealed class TaikoScore
{
    private readonly PlayableChart _chart;
    private readonly int _base;
    private readonly int _step;
    private int _pendingStrongIndex = -1;
    private long _pendingStrongPoints;

    public TaikoScore(PlayableChart chart)
    {
        _chart = chart ?? throw new ArgumentNullException(nameof(chart));
        if (chart.ScoreInit is null && chart.ScoreDiff is > int.MaxValue / 4)
            throw new ArgumentOutOfRangeException(nameof(chart), "SCOREDIFF is too large to derive SCOREINIT.");
        var estimated = chart.ScoreInit is null && chart.ScoreDiff is null ? estimateRates(chart) : (Init: 0, Diff: 0);
        _base = chart.ScoreInit ?? (chart.ScoreDiff is { } diff
            ? checked((int)((((long)diff * 4 + 5) / 10) * 10)) : estimated.Init);
        _step = chart.ScoreDiff ?? (chart.ScoreInit is { } init ? init / 4 : estimated.Diff);
        ArgumentOutOfRangeException.ThrowIfNegative(_base);
        ArgumentOutOfRangeException.ThrowIfNegative(_step);
    }

    public long Value { get; private set; }
    public int Combo { get; private set; }
    public int RollHits { get; private set; }
    public int BalloonHits { get; private set; }

    public bool Apply(TaikoNoteJudgement judgement, TimeSpan time)
    {
        var previous = Value;
        if (judgement.StrongHitCompleted)
        {
            if (judgement.NoteIndex == _pendingStrongIndex)
                Value += _pendingStrongPoints;
            _pendingStrongIndex = -1;
            _pendingStrongPoints = 0;
            return Value != previous;
        }

        if (judgement.Result is not { } result) return false;
        if (result == TaikoHitResult.Miss)
        {
            Combo = 0;
            return false;
        }

        // Gen 3 uses four score steps after 10, 30, 50 and 100 combo.
        var step = Combo >= 100 ? 8 : Combo >= 50 ? 4 : Combo >= 30 ? 2 : Combo >= 10 ? 1 : 0;
        var basic = ((long)_base + (long)_step * step) / 10 * 10;
        var points = result == TaikoHitResult.Good ? basic / 20 * 10 : basic;
        if (isGoGo(time)) points = truncateToTen(points * 6, 5);
        Value += points;
        if (judgement.HitObject.IsStrong)
        {
            _pendingStrongIndex = judgement.NoteIndex;
            _pendingStrongPoints = points;
        }
        Combo++;
        if (Combo % 100 == 0)
            Value += 10000;
        return Value != previous;
    }

    public bool Apply(TaikoLongNoteProgress progress, TimeSpan time)
    {
        var previous = Value;
        if (progress.Note.IsBalloon) BalloonHits++;
        else RollHits++;
        var goGo = isGoGo(progress.Note.IsBalloon ? progress.Note.StartTime : time);
        var points = progress.Note.IsBalloon
            ? progress.IsPopped ? 5000L : 300L
            : progress.Note.Kind == PlayableLongNoteKind.BigRoll ? 200L : 100L;
        Value += goGo ? points * 6 / 5 : points;
        return Value != previous;
    }

    private bool isGoGo(TimeSpan time)
    {
        var active = false;
        foreach (var point in _chart.EffectPoints)
        {
            if (point.Time > time) break;
            active = point.IsGoGo;
        }
        return active;
    }

    private static long truncateToTen(long numerator, long denominator) =>
        numerator / (denominator * 10) * 10;

    private static (int Init, int Diff) estimateRates(PlayableChart chart)
    {
        if (chart.NoteCount == 0) return (1000, 100);
        // When a chart omits both headers, choose an approximate million-point
        // all-Great ceiling with a 4:1 initial-score-to-difference ratio.
        decimal units = 0;
        var effectIndex = 0;
        var goGo = false;
        for (var index = 0; index < chart.NoteCount; index++)
        {
            var note = chart.HitObjects[index];
            while (effectIndex < chart.EffectPoints.Length && chart.EffectPoints[effectIndex].Time <= note.StartTime)
                goGo = chart.EffectPoints[effectIndex++].IsGoGo;
            var step = index >= 100 ? 8 : index >= 50 ? 4 : index >= 30 ? 2 : index >= 10 ? 1 : 0;
            units += (4 + step) * (note.IsStrong ? 2 : 1) * (goGo ? 1.2m : 1m);
        }
        var budget = Math.Max(10000, 1000000 - 10000 * (chart.NoteCount / 100));
        var diff = Math.Max(1, (int)Math.Round(budget / units, MidpointRounding.AwayFromZero));
        return ((diff * 4 + 5) / 10 * 10, diff);
    }
}
