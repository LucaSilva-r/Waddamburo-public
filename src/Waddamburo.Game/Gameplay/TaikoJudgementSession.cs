using System.Collections.Immutable;
using Waddamburo.Catalog;

namespace Waddamburo.Game.Gameplay;

public enum TaikoInputAction
{
    LeftDon,
    RightDon,
    LeftKa,
    RightKa,
}

public enum TaikoHitResult
{
    Good,
    Great,
    Miss,
}

public enum TaikoInputResult
{
    Ignored,
    Judged,
    StrongHitCompleted,
}

public readonly record struct TaikoJudgementWindows
{
    public TaikoJudgementWindows(TimeSpan great, TimeSpan good, TimeSpan miss)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(great, TimeSpan.Zero);
        if (good < great)
            throw new ArgumentOutOfRangeException(nameof(good), "The Good window cannot be smaller than Great.");
        if (miss < good)
            throw new ArgumentOutOfRangeException(nameof(miss), "The Miss window cannot be smaller than Good.");
        Great = great;
        Good = good;
        Miss = miss;
    }

    public TimeSpan Great { get; }
    public TimeSpan Good { get; }
    public TimeSpan Miss { get; }

    public TaikoHitResult? ResultFor(TimeSpan offset)
    {
        var magnitude = offset.Duration();
        if (magnitude <= Great)
            return TaikoHitResult.Great;
        if (magnitude <= Good)
            return TaikoHitResult.Good;
        if (magnitude <= Miss)
            return TaikoHitResult.Miss;
        return null;
    }
}

public readonly record struct TaikoNoteJudgement(
    int NoteIndex,
    PlayableHitObject HitObject,
    TaikoHitResult? Result,
    TimeSpan? Offset,
    bool StrongHitCompleted);

/// <summary>
/// Deterministic Taiko judgement state. The caller owns the gameplay clock and supplies
/// every timestamp; rendering and audio playback never advance this session implicitly.
/// </summary>
public sealed class TaikoJudgementSession
{
    private readonly PlayableChart _chart;
    private readonly TaikoJudgementWindows _windows;
    private readonly TimeSpan _strongSecondHitWindow;
    private readonly TaikoHitResult?[] _results;
    private readonly TimeSpan?[] _offsets;
    private readonly bool[] _strongHits;
    private TimeSpan _currentTime;
    private int _nextNoteIndex;
    private PendingStrongHit? _pendingStrong;

    public TaikoJudgementSession(
        PlayableChart chart,
        TaikoJudgementWindows windows,
        TimeSpan strongSecondHitWindow)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentOutOfRangeException.ThrowIfLessThan(strongSecondHitWindow, TimeSpan.Zero);
        _chart = chart;
        _windows = windows;
        _strongSecondHitWindow = strongSecondHitWindow;
        _results = new TaikoHitResult?[chart.NoteCount];
        _offsets = new TimeSpan?[chart.NoteCount];
        _strongHits = new bool[chart.NoteCount];
    }

    public TimeSpan CurrentTime => _currentTime;
    public int NextNoteIndex => _nextNoteIndex;
    public bool IsComplete => _nextNoteIndex == _chart.NoteCount;

    public int AdvanceTo(TimeSpan time)
    {
        ensureMonotonic(time);
        _currentTime = time;
        expireStrongHit(time);
        var missed = 0;
        while (_nextNoteIndex < _chart.NoteCount
               && time - _chart.HitObjects[_nextNoteIndex].StartTime > _windows.Miss)
        {
            judge(_nextNoteIndex, TaikoHitResult.Miss, time - _chart.HitObjects[_nextNoteIndex].StartTime);
            _nextNoteIndex++;
            missed++;
        }
        return missed;
    }

    public TaikoInputResult SubmitInput(TaikoInputAction action, TimeSpan time)
    {
        AdvanceTo(time);
        if (tryCompleteStrongHit(action, time))
            return TaikoInputResult.StrongHitCompleted;
        if (_nextNoteIndex >= _chart.NoteCount)
            return TaikoInputResult.Ignored;

        var hitObject = _chart.HitObjects[_nextNoteIndex];
        var offset = time - hitObject.StartTime;
        var result = _windows.ResultFor(offset);
        if (result is null)
            return TaikoInputResult.Ignored;
        if (!matches(action, hitObject.Kind))
            result = TaikoHitResult.Miss;

        var judgedIndex = _nextNoteIndex++;
        judge(judgedIndex, result.Value, offset);
        if (result != TaikoHitResult.Miss && hitObject.IsStrong)
            _pendingStrong = new PendingStrongHit(judgedIndex, action, time);
        return TaikoInputResult.Judged;
    }

    public ImmutableArray<TaikoNoteJudgement> CreateSnapshot()
    {
        var snapshot = ImmutableArray.CreateBuilder<TaikoNoteJudgement>(_chart.NoteCount);
        for (var index = 0; index < _chart.NoteCount; index++)
        {
            snapshot.Add(new TaikoNoteJudgement(
                index,
                _chart.HitObjects[index],
                _results[index],
                _offsets[index],
                _strongHits[index]));
        }
        return snapshot.MoveToImmutable();
    }

    private bool tryCompleteStrongHit(TaikoInputAction action, TimeSpan time)
    {
        if (_pendingStrong is not { } pending
            || time - pending.FirstInputTime > _strongSecondHitWindow
            || action == pending.FirstAction
            || !sameSurface(action, pending.FirstAction))
        {
            return false;
        }
        _strongHits[pending.NoteIndex] = true;
        _pendingStrong = null;
        return true;
    }

    private void expireStrongHit(TimeSpan time)
    {
        if (_pendingStrong is { } pending
            && time - pending.FirstInputTime > _strongSecondHitWindow)
        {
            _pendingStrong = null;
        }
    }

    private void judge(int index, TaikoHitResult result, TimeSpan offset)
    {
        _results[index] = result;
        _offsets[index] = offset;
    }

    private void ensureMonotonic(TimeSpan time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(time, TimeSpan.Zero);
        if (time < _currentTime)
            throw new ArgumentOutOfRangeException(nameof(time), "Gameplay time cannot move backwards.");
    }

    private static bool matches(TaikoInputAction action, PlayableNoteKind kind) =>
        isDon(action) == (kind is PlayableNoteKind.Don or PlayableNoteKind.BigDon);

    private static bool sameSurface(TaikoInputAction first, TaikoInputAction second) =>
        isDon(first) == isDon(second);

    private static bool isDon(TaikoInputAction action) =>
        action is TaikoInputAction.LeftDon or TaikoInputAction.RightDon;

    private readonly record struct PendingStrongHit(
        int NoteIndex,
        TaikoInputAction FirstAction,
        TimeSpan FirstInputTime);
}
