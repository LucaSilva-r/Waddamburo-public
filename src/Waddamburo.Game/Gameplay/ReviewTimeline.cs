using System.Collections.Immutable;
using Waddamburo.Catalog;

namespace Waddamburo.Game.Gameplay;

/// <summary>A play's state just after one of its judgements (or roll/balloon hits): what a seek restores.</summary>
public readonly record struct ReviewState(TimeSpan Time, TaikoScoreState Score, int Gauge);

/// <summary>One judgement or roll/balloon hit of a baked play, at the time it showed.</summary>
public readonly record struct ReviewEvent(TimeSpan Time, TaikoNoteJudgement? Judgement, TaikoLongNoteProgress? LongHit, ReviewState After);

/// <summary>
/// A whole play run once through the real game logic (judgement, score, soul gauge) and kept: every
/// judgement and roll hit with its time, and the state after each. A reviewed play is shown from it
/// at any playhead, so a seek restores the baked state instead of playing the song again.
/// </summary>
public sealed class ReviewTimeline
{
    private readonly TimeSpan?[] _judgedAt;
    private readonly TaikoHitResult?[] _results;
    private readonly TimeSpan[][] _longHits;

    private ReviewTimeline(PlayableChart chart, ImmutableArray<ReviewEvent> events, ImmutableArray<TaikoReplayInput> inputs)
    {
        Chart = chart;
        Events = events;
        Inputs = inputs;
        _judgedAt = new TimeSpan?[chart.NoteCount];
        _results = new TaikoHitResult?[chart.NoteCount];
        var longHits = Enumerable.Range(0, chart.LongNotes.Length).Select(static _ => new List<TimeSpan>()).ToArray();
        foreach (var item in events)
        {
            if (item.Judgement is { StrongHitCompleted: false } judgement)
                (_judgedAt[judgement.NoteIndex], _results[judgement.NoteIndex]) = (item.Time, judgement.Result);
            if (item.LongHit is { } hit)
                longHits[hit.NoteIndex].Add(item.Time);
        }
        _longHits = [.. longHits.Select(static hits => hits.ToArray())];
    }

    public PlayableChart Chart { get; }

    /// <summary>In time order.</summary>
    public ImmutableArray<ReviewEvent> Events { get; }

    /// <summary>The drum hits played, in time order (their sounds and drum animation).</summary>
    public ImmutableArray<TaikoReplayInput> Inputs { get; }

    /// <summary>
    /// Plays <paramref name="inputs"/> (in time order) over <paramref name="chart"/> with the same rules as
    /// a live lane, then times out whatever is left. A training attempt played only
    /// [<paramref name="from"/>, <paramref name="until"/>): nothing before or after it is judged.
    /// </summary>
    public static ReviewTimeline Build(PlayableChart chart, TaikoCourse course, TaikoJudgementWindows windows,
        TimeSpan strongSecondHitWindow, IEnumerable<TaikoReplayInput> inputs, TimeSpan? from = null, TimeSpan? until = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(inputs);
        ImmutableArray<TaikoReplayInput> played = [.. inputs.Where(input => (from is null || input.Time >= from) && (until is null || input.Time < until))
            .OrderBy(static input => input.Time)];
        var session = new TaikoJudgementSession(chart, windows, strongSecondHitWindow);
        if (from is { } start)
            session.SkipTo(start);
        var score = new TaikoScore(chart);
        var gauge = new TaikoSoulGauge(course, chart.Level, chart.NoteCount);
        var events = new List<ReviewEvent>();
        ReviewState state(TimeSpan time) => new(time, score.State, gauge.Value);
        session.Judged += judgement =>
        {
            // As a lane applies them (TaikoGameplayPresentation): the score by the session's time.
            score.Apply(judgement, session.CurrentTime);
            gauge.Apply(judgement);
            // A note that passed unhit left when its Bad window closed, whenever the session noticed.
            var time = judgement.TimedOut ? judgement.HitObject.StartTime + windows.Miss : session.CurrentTime;
            events.Add(new ReviewEvent(time, judgement, null, state(time)));
        };
        session.LongNoteHit += progress =>
        {
            score.Apply(progress, session.CurrentTime);
            events.Add(new ReviewEvent(session.CurrentTime, null, progress, state(session.CurrentTime)));
        };
        foreach (var input in played)
            session.SubmitInput(input.Action, input.Time < session.CurrentTime ? session.CurrentTime : input.Time);
        var end = until ?? chart.HitObjects.Select(static note => note.StartTime).Concat(chart.LongNotes.Select(static note => note.EndTime))
            .DefaultIfEmpty(TimeSpan.Zero).Max() + windows.Miss + TimeSpan.FromSeconds(1);
        if (end > session.CurrentTime)
            session.AdvanceTo(end);
        // Stable: same-time events keep the order they happened in.
        return new ReviewTimeline(chart, [.. events.Select(static (item, index) => (item, index))
            .OrderBy(static pair => pair.item.Time).ThenBy(static pair => pair.index).Select(static pair => pair.item)], played);
    }

    /// <summary>The state at <paramref name="time"/>: after the last event at or before it (null: before the first).</summary>
    public ReviewState? StateAt(TimeSpan time)
    {
        var index = LastEventAt(time);
        return index < 0 ? null : Events[index].After;
    }

    /// <summary>The index of the last event at or before <paramref name="time"/>, or -1.</summary>
    public int LastEventAt(TimeSpan time)
    {
        int low = 0, high = Events.Length - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (Events[middle].Time <= time)
                (found, low) = (middle, middle + 1);
            else
                high = middle - 1;
        }
        return found;
    }

    public bool IsJudged(int note, TimeSpan time) => _judgedAt[note] is { } at && at <= time;

    public TaikoHitResult? ResultAt(int note, TimeSpan time) => IsJudged(note, time) ? _results[note] : null;

    /// <summary>Hits on roll/balloon <paramref name="index"/> at or before <paramref name="time"/>.</summary>
    public int LongHitsAt(int index, TimeSpan time)
    {
        var hits = _longHits[index];
        int low = 0, high = hits.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (hits[middle] <= time) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}

/// <summary>
/// A baked play read at a playhead, as the lane's presentation reads a live session. Playing on
/// (<see cref="Play"/>) raises the judgements and roll hits it passes, so the lane shows them as it
/// would live; a jump (<see cref="Jump"/>) raises nothing: the lane restores <see cref="ReviewTimeline.StateAt"/>.
/// </summary>
public sealed class ReviewJudgementSource(ReviewTimeline timeline) : ITaikoJudgementSource
{
    // The first input after the playhead (inputs are in time order).
    private int _nextInput;

    public ReviewTimeline Timeline { get; } = timeline;

    public TimeSpan CurrentTime { get; private set; } = TimeSpan.MinValue;

    public event Action<TaikoNoteJudgement>? Judged;

    public event Action<TaikoLongNoteProgress>? LongNoteHit;

    /// <summary>A drum hit the playhead passed while playing on.</summary>
    public event Action<TaikoInputAction>? Hit;

    public bool IsJudged(int index) => Timeline.IsJudged(index, CurrentTime);

    public bool IsMissed(int index) => Timeline.ResultAt(index, CurrentTime) == TaikoHitResult.Miss;

    public TaikoLongNoteProgress GetLongNoteProgress(int index)
    {
        var note = Timeline.Chart.LongNotes[index];
        var hits = Timeline.LongHitsAt(index, CurrentTime);
        return new(index, note, hits, note.IsBalloon && hits >= note.RequiredHits);
    }

    /// <summary>Plays on to <paramref name="time"/>, raising every event passed (a time before the playhead is a <see cref="Jump"/>).</summary>
    public void Play(TimeSpan time)
    {
        if (time < CurrentTime)
        {
            Jump(time);
            return;
        }
        var from = Timeline.LastEventAt(CurrentTime) + 1;
        var to = Timeline.LastEventAt(time);
        CurrentTime = time;
        var inputs = Timeline.Inputs;
        for (; _nextInput < inputs.Length && inputs[_nextInput].Time <= time; _nextInput++)
            Hit?.Invoke(inputs[_nextInput].Action);
        for (var index = from; index <= to; index++)
        {
            var item = Timeline.Events[index];
            if (item.Judgement is { } judgement)
                Judged?.Invoke(judgement);
            if (item.LongHit is { } hit)
                LongNoteHit?.Invoke(hit);
        }
    }

    public void Jump(TimeSpan time)
    {
        CurrentTime = time;
        var inputs = Timeline.Inputs;
        int low = 0, high = inputs.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (inputs[middle].Time <= time) low = middle + 1;
            else high = middle;
        }
        _nextInput = low;
    }
}
