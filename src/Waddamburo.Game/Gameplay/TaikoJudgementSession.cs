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
    /// <summary>A roll, balloon or kusudama hit in a 1/60 s frame that already counted one: taken, not scored.</summary>
    RollHitCapped,
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
/// What the lane's presentation reads of a play's judgements: the live session's, or a reviewed play's
/// baked timeline at its playhead (<see cref="ReviewJudgementSource"/>).
/// </summary>
public interface ITaikoJudgementSource
{
    TimeSpan CurrentTime { get; }

    bool IsJudged(int index);

    bool IsMissed(int index);

    TaikoLongNoteProgress GetLongNoteProgress(int index);

    /// <summary>
    /// The 譜面分岐 route played at chart time <paramref name="time"/> as known now (normal before the first
    /// check; a section not checked yet follows the route being played). Notes off it are neither judged nor drawn.
    /// </summary>
    ChartBranch BranchAt(TimeSpan time);

    /// <summary>The route the last check passed picked (what the lane shows; its notes start at the check's Start).</summary>
    ChartBranch Branch { get; }

    event Action<TaikoNoteJudgement>? Judged;

    event Action<TaikoLongNoteProgress>? LongNoteHit;
}

/// <summary>
/// Deterministic Taiko judgement state. The caller owns the gameplay clock and supplies
/// every timestamp; rendering and audio playback never advance this session implicitly.
/// </summary>
public sealed class TaikoJudgementSession : ITaikoJudgementSource
{
    private readonly PlayableChart _chart;
    private readonly TaikoJudgementWindows _windows;
    private readonly TimeSpan _strongSecondHitWindow;
    private readonly TaikoHitResult?[] _results;
    private readonly TimeSpan?[] _offsets;
    private readonly bool[] _timedOut;
    private readonly bool[] _strongHits;
    private readonly bool[] _skipped;
    private readonly int[] _longHits;
    private readonly long[] _lastRollFrame;
    private int _nextLongIndex;
    private TimeSpan _currentTime = TimeSpan.MinValue;
    private int _nextNoteIndex;
    private PendingStrongHit? _pendingStrong;
    private TimeSpan? _lastJudgedInputTime;
    private readonly bool _partnerHandNotes;
    // 譜面分岐: the route picked at each check passed (from its Start), the next check, the points since the last.
    private readonly List<(TimeSpan Start, ChartBranch Route)> _routes = [];
    private int _nextCheck;
    private int _branchPoints;
    private TimeSpan _pointsSince;

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
        _skipped = new bool[chart.NoteCount];
        _longHits = new int[chart.LongNotes.Length];
        _lastRollFrame = new long[chart.LongNotes.Length];
        Array.Fill(_lastRollFrame, -1L);
    }

    public TimeSpan CurrentTime => _currentTime;
    public int NextNoteIndex => _nextNoteIndex;
    public bool IsComplete => _nextNoteIndex == _chart.NoteCount && _nextLongIndex == _chart.LongNotes.Length;

    public event Action<TaikoNoteJudgement>? Judged;
    public event Action<TaikoLongNoteProgress>? LongNoteHit;

    /// <summary>A 譜面分岐 check picked its route (raised at the check, also when the route stays).</summary>
    public event Action<ChartBranchCheck, ChartBranch>? BranchChecked;

    /// <summary>The route being played now (the last check's pick).</summary>
    public ChartBranch Branch => _routes.Count == 0 ? ChartBranch.Normal : _routes[^1].Route;

    /// <summary>Branch points earned since the last check.</summary>
    public int BranchPoints => _branchPoints;

    public ChartBranch BranchAt(TimeSpan time)
    {
        for (var index = _routes.Count - 1; index >= 0; index--)
            if (_routes[index].Start <= time)
                return _routes[index].Route;
        return ChartBranch.Normal;
    }

    private bool onRoute(int index) =>
        PlayableChart.IsOn(_chart.HitObjects[index].Branch, BranchAt(_chart.HitObjects[index].StartTime));

    private bool longOnRoute(int index) =>
        PlayableChart.IsOn(_chart.LongNotes[index].Branch, BranchAt(_chart.LongNotes[index].StartTime));

    /// <summary>
    /// The most hits a chart's drumrolls can count (the 1/60 s frames they last; balloons and kusudama
    /// are not counted).
    /// </summary>
    public static int MaxRollHits(PlayableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        // A hit counts at time < EndTime, in frame (time - StartTime) * 60 s: frames 0 up to the one before the end.
        return chart.LongNotes.Where(static note => !note.IsBalloon)
            .Sum(static note => (int)(((note.EndTime - note.StartTime).Ticks * 60 + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond));
    }

    public TaikoLongNoteProgress GetLongNoteProgress(int index)
    {
        var note = _chart.LongNotes[index];
        return new(index, note, _longHits[index], note.IsBalloon && _longHits[index] >= note.RequiredHits);
    }

    public bool IsJudged(int index) => _results[index] is not null || _skipped[index];

    public bool IsMissed(int index) => _results[index] == TaikoHitResult.Miss;

    /// <summary>
    /// Starts judging at <paramref name="time"/> (training from a point in the song): the notes before it and
    /// the rolls over by then are passed by, with no judgement and nothing raised; they show as gone.
    /// </summary>
    public void SkipTo(TimeSpan time)
    {
        ensureMonotonic(time);
        _currentTime = time;
        for (; _nextNoteIndex < _chart.NoteCount && _chart.HitObjects[_nextNoteIndex].StartTime < time; _nextNoteIndex++)
            _skipped[_nextNoteIndex] = true;
        while (_nextLongIndex < _chart.LongNotes.Length && _chart.LongNotes[_nextLongIndex].EndTime <= time)
            _nextLongIndex++;
        // ponytail: the checks skipped are not judged, so training from mid-song plays the normal route.
        while (_chart.Branching is { } branching && _nextCheck < branching.Checks.Length && branching.Checks[_nextCheck].Time <= time)
            _nextCheck++;
        _pointsSince = time;
    }

    public int AdvanceTo(TimeSpan time)
    {
        ensureMonotonic(time);
        _currentTime = time;
        expireStrongHit(time);
        var missed = 0;
        while (_nextNoteIndex < _chart.NoteCount
               && time - _chart.HitObjects[_nextNoteIndex].StartTime > _windows.Miss)
        {
            // A note off the route played just goes by (its section was checked a measure before it).
            if (onRoute(_nextNoteIndex))
            {
                judge(_nextNoteIndex, TaikoHitResult.Miss, time - _chart.HitObjects[_nextNoteIndex].StartTime,
                    timedOut: true);
                missed++;
            }
            _nextNoteIndex++;
            skipJudged();
        }
        checkBranches(time);
        while (_nextLongIndex < _chart.LongNotes.Length
               && (time >= _chart.LongNotes[_nextLongIndex].EndTime || GetLongNoteProgress(_nextLongIndex).IsPopped
                   || time >= _chart.LongNotes[_nextLongIndex].StartTime && !longOnRoute(_nextLongIndex)))
            _nextLongIndex++;
        return missed;
    }

    // The checks reached by now pick their routes from the points since the previous one. A note hit
    // after a check it came before counts for neither (ponytail: when Green counts a late hit is not measured).
    private void checkBranches(TimeSpan time)
    {
        if (_chart.Branching is not { } branching)
            return;
        for (; _nextCheck < branching.Checks.Length && branching.Checks[_nextCheck].Time <= time; _nextCheck++)
        {
            var check = branching.Checks[_nextCheck];
            var route = check.For(Branch).Next(Branch, _branchPoints);
            _routes.Add((check.Start, route));
            _branchPoints = 0;
            _pointsSince = check.Time;
            BranchChecked?.Invoke(check, route);
        }
    }

    private void addBranchPoints(int points, TimeSpan noteTime)
    {
        if (_chart.Branching is not null && noteTime >= _pointsSince)
            _branchPoints += points;
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

        // The input judges the oldest unjudged note of its colour inside the Good window, else the
        // oldest one inside the Bad window: a late hit stays on its own note instead of taking the
        // next one (which left the skipped note to miss). An active roll, balloon or kusudama takes
        // the input unless a note older than it can still be hit Good, so drumming a roll up to its
        // end never gives the following note an early Bad. Wrong colour alone is ignored.
        // ponytail: OpenTaiko's order (GetChipToJudge); the arcade's own is not measured on Green.
        int goodIndex = -1, badIndex = -1;
        for (var index = _nextNoteIndex; index < _chart.NoteCount; index++)
        {
            var candidateOffset = time - _chart.HitObjects[index].StartTime;
            if (candidateOffset < -_windows.Miss)
                break;
            if (_results[index] is not null || !matches(action, _chart.HitObjects[index].Kind) || !onRoute(index))
                continue;
            if (candidateOffset.Duration() <= _windows.Good)
            {
                goodIndex = index;
                break;
            }
            if (badIndex < 0)
                badIndex = index;
        }
        if (goodIndex < 0 || activeLongNoteStart(action, time) is { } longStart
            && _chart.HitObjects[goodIndex].StartTime >= longStart)
        {
            var longHit = hitLongNote(action, time);
            if (longHit != TaikoInputResult.Ignored)
                return longHit;
        }
        var judgedIndex = goodIndex >= 0 ? goodIndex : badIndex;
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

    // The start of the roll, balloon or kusudama under way at this time that takes this input.
    private TimeSpan? activeLongNoteStart(TaikoInputAction action, TimeSpan time)
    {
        if (_nextLongIndex >= _chart.LongNotes.Length) return null;
        var note = _chart.LongNotes[_nextLongIndex];
        return time >= note.StartTime && time < note.EndTime && (!note.IsBalloon || isDon(action)) ? note.StartTime : null;
    }

    private TaikoInputResult hitLongNote(TaikoInputAction action, TimeSpan time)
    {
        if (_nextLongIndex >= _chart.LongNotes.Length) return TaikoInputResult.Ignored;
        var note = _chart.LongNotes[_nextLongIndex];
        if (time < note.StartTime || time >= note.EndTime || (note.IsBalloon && !isDon(action)))
            return TaikoInputResult.Ignored;
        // Rolls, balloons and kusudama count at most 60 hits a second of song time (Green's top speed),
        // at any training speed: 1/60 s frames from the note's start, the first hit in each frame
        // scores, the rest are taken but not counted.
        var frame = (time - note.StartTime).Ticks * 60 / TimeSpan.TicksPerSecond;
        if (frame == _lastRollFrame[_nextLongIndex])
            return TaikoInputResult.RollHitCapped;
        _lastRollFrame[_nextLongIndex] = frame;
        _longHits[_nextLongIndex]++;
        var progress = GetLongNoteProgress(_nextLongIndex) with { LastAction = action };
        if (_chart.Branching is { Points: var points })
            addBranchPoints(note.Kind switch
            {
                PlayableLongNoteKind.Roll => points.Roll,
                PlayableLongNoteKind.BigRoll => points.BigRoll,
                // ponytail: a balloon's points come once, when it bursts (tja2fumen counts them once a balloon).
                PlayableLongNoteKind.Balloon => progress.IsPopped ? points.Balloon : 0,
                _ => progress.IsPopped ? points.Kusudama : 0,
            }, time);
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
        if (_chart.Branching is { Points: var points })
        {
            var strong = _chart.HitObjects[index].IsStrong;
            addBranchPoints(result switch
            {
                TaikoHitResult.Great => strong ? points.BigGreat : points.Great,
                TaikoHitResult.Good => strong ? points.BigGood : points.Good,
                _ => points.Bad,
            }, _chart.HitObjects[index].StartTime);
        }
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
