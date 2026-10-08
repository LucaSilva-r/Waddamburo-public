using Waddamburo.Catalog;
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
        var gauge = new TaikoSoulGauge(course, played.Level, played.NoteCount);
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
            RollMax = TaikoJudgementSession.MaxRollHits(played),
        };
    }
}
