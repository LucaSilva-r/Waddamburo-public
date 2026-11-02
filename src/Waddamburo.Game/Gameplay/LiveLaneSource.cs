using Waddamburo.Catalog;

namespace Waddamburo.Game.Gameplay;

/// <summary>
/// The other player's lane as it's shown online: in time with this player's own song, whatever the network's delay.
/// Their hits come in late (<see cref="RemoteLaneFeed"/>); a shadow session (<see cref="Shadow"/>) judges them at the times
/// they were hit as they arrive, so its score, combo and gauge are exactly theirs, just late. The lane shows each of its
/// notes, as it reaches the judgement line, with the next judgement the shadow has made; with none yet (the first notes,
/// or a lag spike) the note is a placeholder, hit with no judgement of its own, and the judgements that come later go on
/// the notes after it. So the lane runs a few notes behind in judgements, never in time.
/// </summary>
public sealed class LiveLaneSource : ITaikoJudgementSource
{
    private static readonly TimeSpan LongNoteGrace = TimeSpan.FromMilliseconds(500);

    private readonly PlayableChart _chart;
    private readonly RemoteLaneFeed _feed;
    private readonly Queue<TaikoNoteJudgement> _arrived = new();
    private readonly bool[] _shown;
    private readonly bool[] _missed;
    private int _next;

    public LiveLaneSource(PlayableChart chart, TaikoJudgementSession shadow, RemoteLaneFeed feed)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(shadow);
        ArgumentNullException.ThrowIfNull(feed);
        _chart = chart;
        Shadow = shadow;
        _feed = feed;
        _shown = new bool[chart.HitObjects.Length];
        _missed = new bool[chart.HitObjects.Length];
        shadow.Judged += judgement =>
        {
            if (!judgement.StrongHitCompleted)
                _arrived.Enqueue(judgement);
        };
        // A roll's or balloon's hits show on it while it's on screen; late ones only count (on the shadow).
        shadow.LongNoteHit += progress =>
        {
            if (CurrentTime >= progress.Note.StartTime && CurrentTime <= progress.Note.EndTime + LongNoteGrace)
                LongNoteHit?.Invoke(progress);
        };
    }

    /// <summary>Their play, judged as they played it: the lane's score, combo, gauge and results come from it.</summary>
    public TaikoJudgementSession Shadow { get; }

    /// <summary>The judgements arrived but not shown yet: how many notes behind the lane's judgements run.</summary>
    public int Behind => _arrived.Count;

    public TimeSpan CurrentTime { get; private set; } = TimeSpan.MinValue;

    public ChartBranch Branch => Shadow.Branch;

    public event Action<TaikoNoteJudgement>? Judged;

    public event Action<TaikoLongNoteProgress>? LongNoteHit;

    public bool IsJudged(int index) => (uint)index < (uint)_shown.Length && _shown[index];

    public bool IsMissed(int index) => (uint)index < (uint)_missed.Length && _missed[index];

    public ChartBranch BranchAt(TimeSpan time) => Shadow.BranchAt(time);

    public TaikoLongNoteProgress GetLongNoteProgress(int index) => Shadow.GetLongNoteProgress(index);

    /// <summary>
    /// This player's song is at <paramref name="localTime"/>: the shadow takes every hit that has arrived, and the notes
    /// reaching the line now are shown. <paramref name="hit"/> is told of their drum hits (each note's own, for the drum's
    /// look) and of their hits that judged no note as those arrive (a stray hit, a roll's: late, but nothing else shows
    /// them), <paramref name="arrived"/> of each of theirs that arrived (with its own time).
    /// </summary>
    public void Advance(TimeSpan localTime, Action<TaikoInputAction>? hit = null, Action<TaikoInputAction, TimeSpan>? arrived = null)
    {
        _feed.Apply(Shadow, localTime, arrived, hit);
        CurrentTime = localTime;
        var notes = _chart.HitObjects;
        for (; _next < notes.Length && notes[_next].StartTime <= localTime; _next++)
        {
            var note = notes[_next];
            if (note.Branch is { } route && route != Shadow.BranchAt(note.StartTime))
                continue;
            _shown[_next] = true;
            if (_arrived.TryDequeue(out var judgement))
            {
                _missed[_next] = judgement.Result == TaikoHitResult.Miss;
                Judged?.Invoke(judgement with { NoteIndex = _next, HitObject = note });
                if (judgement.Result == TaikoHitResult.Miss)
                    continue;
            }
            else
                Judged?.Invoke(new TaikoNoteJudgement(_next, note, TaikoHitResult.Great, TimeSpan.Zero, StrongHitCompleted: false) { Placeholder = true });
            hit?.Invoke(note.Kind is PlayableNoteKind.Ka or PlayableNoteKind.BigKa ? TaikoInputAction.LeftKa : TaikoInputAction.LeftDon);
        }
    }
}
