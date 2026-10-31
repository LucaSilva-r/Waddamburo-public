using Waddamburo.Catalog;
using Waddamburo.Game.Dan;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Scores;

/// <summary>
/// A stored play scored again from its replay under the current rules (<see cref="PlayRecord.CurrentScoringVersion"/>):
/// what a score server keeps instead of the numbers the client sent.
/// </summary>
public static class PlayRescore
{
    /// <param name="chart">The chart as stored (<see cref="ChartHash.Deserialize"/>), before the play's options.</param>
    /// <param name="course">The chart's course (its soul gauge's rates).</param>
    /// <param name="options">The play's options (random notes, 真打 scoring...).</param>
    /// <param name="seed">The random options' seed (null: none drawn).</param>
    /// <param name="replay">The play's drum inputs.</param>
    /// <param name="windows">The judgement windows the game uses for it (osu! charts play with Oni's).</param>
    /// <param name="strongSecondHitWindow">How soon a big note's second hit must follow the first.</param>
    public static TaikoPlayResult Score(PlayableChart chart, TaikoCourse course, TaikoPlayOptions options, int? seed,
        TaikoReplay replay, TaikoJudgementWindows windows, TimeSpan strongSecondHitWindow)
    {
        ArgumentNullException.ThrowIfNull(replay);
        var played = options.Apply(chart, seed ?? 0);
        // As a solo lane plays it (TaikoGameplayPresentation): the score and gauge follow each judgement
        // at the session's time, the counts skip a big note's second hit.
        // ponytail: a duet's hand notes take their bonus from the partner, whose inputs a solo replay lacks.
        var session = new TaikoJudgementSession(played, windows, strongSecondHitWindow);
        var score = new TaikoScore(played);
        var gauge = new TaikoSoulGauge(course, played.Level, played.OnRoute(ChartBranch.Normal).NoteCount);
        int great = 0, good = 0, miss = 0, maxCombo = 0;
        session.Judged += judgement =>
        {
            score.Apply(judgement, session.CurrentTime);
            gauge.Apply(judgement);
            if (judgement.StrongHitCompleted || judgement.Result is not { } result)
                return;
            if (result == TaikoHitResult.Great) great++;
            else if (result == TaikoHitResult.Good) good++;
            else miss++;
            maxCombo = Math.Max(maxCombo, score.Combo);
        };
        session.LongNoteHit += progress => score.Apply(progress, session.CurrentTime);
        foreach (var input in replay.Inputs.OrderBy(static input => input.Time))
            session.SubmitInput(input.Action, input.Time < session.CurrentTime ? session.CurrentTime : input.Time);
        session.AdvanceTo(TimeSpan.MaxValue);
        return new TaikoPlayResult(course, score.Value, great, good, miss, maxCombo, score.RollHits,
            gauge.FilledSegments, gauge.State != TaikoGaugeState.BelowClear)
        {
            Options = options,
            RollMax = Enum.GetValues<ChartBranch>().Max(route => TaikoJudgementSession.MaxRollHits(played.OnRoute(route))),
        };
    }

    /// <summary>
    /// A dan course played again from its songs' replays, as one run on a timeline of the scorer's own: combo and
    /// gauge carry across, the requirements are checked as it goes, and a failed one ends the course with the
    /// song being played. A song without inputs is all misses, so one cannot be skipped. No scoring option applies.
    /// </summary>
    /// <param name="course">The course's requirements.</param>
    /// <param name="timeline">The charts as stored.</param>
    /// <param name="replays">Each song's drum inputs, on its own time.</param>
    /// <param name="windows">The first song's windows, which the whole course plays with.</param>
    /// <param name="strongSecondHitWindow">How soon a big note's second hit must follow the first.</param>
    /// <param name="onProgress">Told the play so far after each judgement and roll hit: the song, its time and the numbers.</param>
    public static (DanProgress Total, IReadOnlyList<DanSongCounts> Songs, int SongsPlayed, int Level) ScoreCourse(DanCourse course,
        DanCourseTimeline timeline, IReadOnlyList<TaikoReplay> replays, TaikoJudgementWindows windows, TimeSpan strongSecondHitWindow,
        Action<int, TimeSpan, DanProgress>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(course);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(replays);
        var session = new TaikoJudgementSession(timeline.Chart, windows, strongSecondHitWindow);
        var score = new TaikoScore(timeline.Chart);
        var gauge = new TaikoSoulGauge(timeline.Chart.Segments);
        int great = 0, good = 0, miss = 0, maxCombo = 0;
        var playing = 0;
        session.Judged += judgement =>
        {
            score.Apply(judgement, session.CurrentTime);
            gauge.Apply(judgement);
            if (!judgement.StrongHitCompleted && judgement.Result is { } result)
            {
                if (result == TaikoHitResult.Great) great++;
                else if (result == TaikoHitResult.Good) good++;
                else miss++;
                maxCombo = Math.Max(maxCombo, score.Combo);
            }
            // A note left alone is judged when the session next moves: it counts from when its window closed.
            report(judgement.TimedOut ? judgement.HitObject.StartTime + windows.Miss : session.CurrentTime);
        };
        session.LongNoteHit += hit =>
        {
            score.Apply(hit, session.CurrentTime);
            report(session.CurrentTime);
        };
        DanProgress progress() => new(gauge.Value * 100 / TaikoSoulGauge.Max, great, good, miss, maxCombo,
            score.RollHits + score.BalloonHits, score.Value);
        void report(TimeSpan time) => onProgress?.Invoke(playing, time - timeline.Songs[playing].Zero, progress());
        var exams = new DanExams(course);
        var ends = new List<DanProgress>();
        var failed = false;
        for (var song = 0; song < timeline.Songs.Count && !failed; song++)
        {
            playing = song;
            var begun = ends.Count == 0 ? default : ends[^1];
            // Up to the next song's first possible note, past every late hit and miss of this one's.
            var zero = timeline.Songs[song].Zero;
            var end = song + 1 < timeline.Songs.Count ? timeline.Songs[song + 1].Zero - TimeSpan.FromMilliseconds(1) : TimeSpan.MaxValue;
            foreach (var input in (song < replays.Count ? replays[song].Inputs : []).OrderBy(static input => input.Time))
            {
                var time = zero + input.Time;
                if (time >= end)
                    break;
                session.SubmitInput(input.Action, time < session.CurrentTime ? session.CurrentTime : time);
                failed |= exams.Requirements.Any(requirement => !exams.StillPossible(requirement, progress(), song, begun));
            }
            session.AdvanceTo(end);
            failed |= exams.Requirements.Any(requirement => !exams.StillPossible(requirement, progress(), song, begun))
                || exams.MissedOnSong(progress(), song, begun);
            ends.Add(progress());
        }
        var counts = new List<DanSongCounts>();
        var before = default(DanProgress);
        for (var song = 0; song < timeline.Songs.Count; song++)
        {
            var at = song < ends.Count ? ends[song] : before;
            counts.Add(new(at.Goods - before.Goods, at.Oks - before.Oks, at.Bads - before.Bads, at.Rolls - before.Rolls));
            before = at;
        }
        var total = progress();
        return (total, counts, ends.Count, failed ? 0 : DanCourseResult.LevelOf(course, total, ends));
    }
}
