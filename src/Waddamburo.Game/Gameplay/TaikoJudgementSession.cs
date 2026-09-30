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
    LongNoteHit,
    BalloonPopped,
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

    // TimeSpan ticks preserve the four-decimal millisecond boundaries exactly (10,000 ticks/ms).
    public static TaikoJudgementWindows ForCourse(TaikoCourse course) => course switch
    {
        TaikoCourse.Easy or TaikoCourse.Normal => new(
            TimeSpan.FromTicks(417083), TimeSpan.FromTicks(1084417), TimeSpan.FromTicks(1251250)),
        TaikoCourse.Hard or TaikoCourse.Oni or TaikoCourse.Ura => new(
            TimeSpan.FromTicks(250250), TimeSpan.FromTicks(750750), TimeSpan.FromTicks(1084417)),
        _ => throw new ArgumentOutOfRangeException(nameof(course)),
    };

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

public readonly record struct TaikoLongNoteProgress(int NoteIndex, PlayableLongNote Note, int Hits, bool IsPopped)
{
    public TaikoInputAction? LastAction { get; init; }
}

public readonly record struct TaikoNoteJudgement(
    int NoteIndex,
    PlayableHitObject HitObject,
    TaikoHitResult? Result,
    TimeSpan? Offset,
    bool StrongHitCompleted,
    bool TimedOut = false);

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
    private readonly bool[] _timedOut;
    private readonly bool[] _strongHits;
    private readonly int[] _longHits;
    private int _nextLongIndex;
    private TimeSpan _currentTime = TimeSpan.MinValue;
    private int _nextNoteIndex;
    private PendingStrongHit? _pendingStrong;
    private TimeSpan? _lastJudgedInputTime;
    private readonly bool _partnerHandNotes;

    /// <summary>A hand note was hit Great (two-player partner matching).</summary>
    public event Action<int>? HandNoteHit;

    /// <param name="chart">The playable chart whose notes are judged.</param>
    /// <param name="windows">The timing windows for Great, Good, and Miss results.</param>
    /// <param name="strongSecondHitWindow">Maximum time between hits for a strong note.</param>
    /// <param name="partnerHandNotes">Two players: a hand note's strong bonus comes from the partner
    /// (<see cref="TaikoHandNoteLink"/>), not from this player's second hit.</param>
    public TaikoJudgementSession(
        PlayableChart chart,
        TaikoJudgementWindows windows,
        TimeSpan strongSecondHitWindow,
        bool partnerHandNotes = false)
    {
        _partnerHandNotes = partnerHandNotes;
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentOutOfRangeException.ThrowIfLessThan(strongSecondHitWindow, TimeSpan.Zero);
        _chart = chart;
        _windows = windows;
        _strongSecondHitWindow = strongSecondHitWindow;
        _results = new TaikoHitResult?[chart.NoteCount];
        _offsets = new TimeSpan?[chart.NoteCount];
        _timedOut = new bool[chart.NoteCount];
        _strongHits = new bool[chart.NoteCount];
        _longHits = new int[chart.LongNotes.Length];
    }

    public TimeSpan CurrentTime => _currentTime;
    public int NextNoteIndex => _nextNoteIndex;
    public bool IsComplete => _nextNoteIndex == _chart.NoteCount && _nextLongIndex == _chart.LongNotes.Length;

    public event Action<TaikoNoteJudgement>? Judged;
    public event Action<TaikoLongNoteProgress>? LongNoteHit;

    public TaikoLongNoteProgress GetLongNoteProgress(int index)
    {
        var note = _chart.LongNotes[index];
        return new(index, note, _longHits[index], note.IsBalloon && _longHits[index] >= note.RequiredHits);
    }

    public bool IsJudged(int index) => _results[index] is not null;

    public bool IsMissed(int index) => _results[index] == TaikoHitResult.Miss;

    public int AdvanceTo(TimeSpan time)
    {
        ensureMonotonic(time);
        _currentTime = time;
        expireStrongHit(time);
        while (_nextLongIndex < _chart.LongNotes.Length
               && (time >= _chart.LongNotes[_nextLongIndex].EndTime || GetLongNoteProgress(_nextLongIndex).IsPopped))
            _nextLongIndex++;
        var missed = 0;
        while (_nextNoteIndex < _chart.NoteCount
               && time - _chart.HitObjects[_nextNoteIndex].StartTime > _windows.Miss)
        {
            judge(_nextNoteIndex, TaikoHitResult.Miss, time - _chart.HitObjects[_nextNoteIndex].StartTime,
                timedOut: true);
            _nextNoteIndex++;
            missed++;
            skipJudged();
        }
        return missed;
    }

    private void skipJudged()
    {
        while (_nextNoteIndex < _chart.NoteCount && _results[_nextNoteIndex] is not null)
            _nextNoteIndex++;
    }

    public TaikoInputResult SubmitInput(TaikoInputAction action, TimeSpan time)
    {
        AdvanceTo(time);
        if (tryCompleteStrongHit(action, time))
            return TaikoInputResult.StrongHitCompleted;
        if (_lastJudgedInputTime == time)
            return TaikoInputResult.Ignored;
        if (_nextNoteIndex >= _chart.NoteCount)
            return hitLongNote(action, time);

        // The input judges the nearest note of its colour inside the window, so an unhit note of
        // the other colour (or a late one) does not hold up the rest of a stream; skipped notes
        // stay hittable until their window ends. Wrong colour alone is still ignored.
        // ponytail: nearest-note choice is ours, not measured on Green (scoring.md research item 1).
        var judgedIndex = -1;
        var anyInWindow = false;
        for (var index = _nextNoteIndex; index < _chart.NoteCount; index++)
        {
            var candidateOffset = time - _chart.HitObjects[index].StartTime;
            if (candidateOffset < -_windows.Miss)
                break;
            if (_results[index] is not null)
                continue;
            anyInWindow = true;
            if (matches(action, _chart.HitObjects[index].Kind)
                && (judgedIndex < 0 || candidateOffset.Duration() < (time - _chart.HitObjects[judgedIndex].StartTime).Duration()))
                judgedIndex = index;
        }
        if (!anyInWindow)
            return hitLongNote(action, time);
        if (judgedIndex < 0)
            return TaikoInputResult.Ignored;

        var hitObject = _chart.HitObjects[judgedIndex];
        var offset = time - hitObject.StartTime;
        var result = _windows.ResultFor(offset)!;
        _lastJudgedInputTime = time;
        judge(judgedIndex, result.Value, offset);
        skipJudged();
        if (hitObject.IsHand && _partnerHandNotes)
        {
            if (result == TaikoHitResult.Great)
                HandNoteHit?.Invoke(judgedIndex);
        }
        else if (result != TaikoHitResult.Miss && hitObject.IsStrong)
            _pendingStrong = new PendingStrongHit(judgedIndex, action, time);
        return TaikoInputResult.Judged;
    }

    /// <summary>Completes a hit note's strong bonus from outside (the partner hit the same hand note).</summary>
    public void CompleteStrongHit(int noteIndex)
    {
        if (_strongHits[noteIndex] || _results[noteIndex] is null or TaikoHitResult.Miss)
            return;
        _strongHits[noteIndex] = true;
        Judged?.Invoke(new TaikoNoteJudgement(noteIndex, _chart.HitObjects[noteIndex],
            _results[noteIndex], _offsets[noteIndex], true));
    }

    private TaikoInputResult hitLongNote(TaikoInputAction action, TimeSpan time)
    {
        if (_nextLongIndex >= _chart.LongNotes.Length) return TaikoInputResult.Ignored;
        var note = _chart.LongNotes[_nextLongIndex];
        if (time < note.StartTime || time >= note.EndTime || (note.IsBalloon && !isDon(action)))
            return TaikoInputResult.Ignored;
        _longHits[_nextLongIndex]++;
        var progress = GetLongNoteProgress(_nextLongIndex) with { LastAction = action };
        LongNoteHit?.Invoke(progress);
        if (progress.IsPopped) _nextLongIndex++;
        return progress.IsPopped ? TaikoInputResult.BalloonPopped : TaikoInputResult.LongNoteHit;
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
                _strongHits[index],
                _timedOut[index]));
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
        Judged?.Invoke(new TaikoNoteJudgement(pending.NoteIndex, _chart.HitObjects[pending.NoteIndex],
            _results[pending.NoteIndex], _offsets[pending.NoteIndex], true));
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

    private void judge(int index, TaikoHitResult result, TimeSpan offset, bool timedOut = false)
    {
        _results[index] = result;
        _offsets[index] = offset;
        _timedOut[index] = timedOut;
        Judged?.Invoke(new TaikoNoteJudgement(index, _chart.HitObjects[index], result, offset, false, timedOut));
    }

    private void ensureMonotonic(TimeSpan time)
    {
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
