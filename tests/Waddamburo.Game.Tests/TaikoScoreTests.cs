using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class TaikoScoreTests
{
    [Fact]
    public void JudgementsUseComboStepsAndStrongCompletionOnlyOnce()
    {
        var score = new TaikoScore(chart() with { ScoreInit = 1000, ScoreDiff = 100 });
        for (var i = 0; i < 10; i++)
            score.Apply(judgement(i, TaikoHitResult.Great), TimeSpan.Zero);
        Assert.Equal(10000, score.Value);
        score.Apply(judgement(10, TaikoHitResult.Good), TimeSpan.Zero);
        Assert.Equal(10550, score.Value);
        score.Apply(judgement(11, TaikoHitResult.Great, big: true), TimeSpan.Zero);
        Assert.Equal(11650, score.Value);
        score.Apply(judgement(11, TaikoHitResult.Great, big: true, completed: true), TimeSpan.Zero);
        Assert.Equal(12750, score.Value);
        score.Apply(judgement(12, TaikoHitResult.Miss), TimeSpan.Zero);
        score.Apply(judgement(13, TaikoHitResult.Great), TimeSpan.Zero);
        Assert.Equal(13750, score.Value);
        Assert.Equal(1, score.Combo);
    }

    [Fact]
    public void BalloonPopReplacesFinalHitAndUsesGoGoAtNoteStart()
    {
        var score = new TaikoScore(chart(goGo: true));
        var roll = new PlayableLongNote(TimeSpan.Zero, TimeSpan.FromSeconds(3), PlayableLongNoteKind.BigRoll);
        var balloon = new PlayableLongNote(TimeSpan.Zero, TimeSpan.FromSeconds(3), PlayableLongNoteKind.Balloon, 2);
        score.Apply(new TaikoLongNoteProgress(0, roll, 1, false), TimeSpan.FromSeconds(1));
        score.Apply(new TaikoLongNoteProgress(0, roll, 2, false), TimeSpan.FromSeconds(2));
        score.Apply(new TaikoLongNoteProgress(1, balloon, 1, false), TimeSpan.FromSeconds(2));
        score.Apply(new TaikoLongNoteProgress(1, balloon, 2, true), TimeSpan.FromSeconds(2));
        Assert.Equal(5740, score.Value); // 200 + 240 roll; 300 + 5000 balloon.
        Assert.Equal(2, score.RollHits);
        Assert.Equal(2, score.BalloonHits);
        Assert.Equal(0, score.Combo);
    }

    [Fact]
    public void ComboBonusOccursAtEachHundredAndBaseStopsGrowing()
    {
        var score = new TaikoScore(chart() with { ScoreInit = 1000, ScoreDiff = 100 });
        for (var i = 0; i < 100; i++)
            score.Apply(judgement(i, TaikoHitResult.Great), TimeSpan.Zero);
        Assert.Equal(100, score.Combo);
        Assert.Equal(136000, score.Value);
        score.Apply(judgement(100, TaikoHitResult.Great), TimeSpan.Zero);
        Assert.Equal(137800, score.Value);
    }

    [Fact]
    public void GenThreeStepsAndTruncationKeepEveryAwardOnTens()
    {
        var score = new TaikoScore(chart(goGo: true) with { ScoreInit = 420, ScoreDiff = 98 });
        var expected = new (int Combo, long Award)[]
        {
            (0, 420), (9, 420), (10, 510), (29, 510), (30, 610),
            (49, 610), (50, 810), (99, 10810), (100, 1200),
        };
        foreach (var (combo, award) in expected)
        {
            var test = new TaikoScore(chart() with { ScoreInit = 420, ScoreDiff = 98 });
            for (var i = 0; i < combo; i++) test.Apply(judgement(i, TaikoHitResult.Great), TimeSpan.Zero);
            var before = test.Value;
            test.Apply(judgement(combo, TaikoHitResult.Great), TimeSpan.Zero);
            Assert.Equal(award, test.Value - before);
        }
        score.Apply(judgement(0, TaikoHitResult.Good), TimeSpan.FromSeconds(2));
        Assert.Equal(250, score.Value); // 420 / 2 = 210, Go-Go makes 252, truncated to 250.
        Assert.Equal(0, score.Value % 10);
    }

    [Fact]
    public void MissingScoreHeadersScaleWithChartSize()
    {
        var shortChart = chartWithNotes(50);
        var longChart = chartWithNotes(500);
        var shortScore = new TaikoScore(shortChart);
        var longScore = new TaikoScore(longChart);
        shortScore.Apply(judgement(0, TaikoHitResult.Great), TimeSpan.Zero);
        longScore.Apply(judgement(0, TaikoHitResult.Great), TimeSpan.Zero);
        Assert.True(shortScore.Value > longScore.Value);
        for (var i = 1; i < shortChart.NoteCount; i++)
            shortScore.Apply(judgement(i, TaikoHitResult.Great), TimeSpan.Zero);
        for (var i = 1; i < longChart.NoteCount; i++)
            longScore.Apply(judgement(i, TaikoHitResult.Great), TimeSpan.Zero);
        Assert.InRange(shortScore.Value, 900000, 1100000);
        Assert.InRange(longScore.Value, 900000, 1100000);
    }

    [Fact]
    public void GoodAndGoGoAwardsTruncateToTensButComboBonusDoesNotGrow()
    {
        var score = new TaikoScore(chart(goGo: true) with { ScoreInit = 510, ScoreDiff = 0 });
        for (var i = 0; i < 100; i++)
            score.Apply(judgement(i, TaikoHitResult.Great), TimeSpan.FromSeconds(2));
        Assert.Equal(71000, score.Value); // 100 * 610 + fixed 10000.
        score.Apply(judgement(100, TaikoHitResult.Good), TimeSpan.Zero);
        Assert.Equal(71250, score.Value); // 510 / 2 truncates to 250.
    }

    [Fact]
    public void GreenGoodGoGoAndStrongGoodUseTruncatedIntermediateAwards()
    {
        var normal = new TaikoScore(chart(goGo: true) with { ScoreInit = 330, ScoreDiff = 0 });
        normal.Apply(judgement(0, TaikoHitResult.Great), TimeSpan.FromSeconds(2));
        normal.Apply(judgement(1, TaikoHitResult.Good), TimeSpan.FromSeconds(2));
        Assert.Equal(580, normal.Value); // 390 + 190, not 400 + 200.

        var strong = new TaikoScore(chart(goGo: true) with { ScoreInit = 330, ScoreDiff = 0 });
        strong.Apply(judgement(0, TaikoHitResult.Good, big: true), TimeSpan.FromSeconds(2));
        strong.Apply(judgement(0, TaikoHitResult.Good, big: true, completed: true), TimeSpan.FromSeconds(2));
        Assert.Equal(380, strong.Value); // (floor10(330 / 2) then floor10(×1.2)) × 2.
    }

    [Fact]
    public void BalloonKeepsItsStartingGoGoFactorAcrossTheBoundary()
    {
        var timed = chart(goGo: true, goGoEnd: true);
        var score = new TaikoScore(timed);
        var before = new PlayableLongNote(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), PlayableLongNoteKind.Balloon, 2);
        var during = new PlayableLongNote(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), PlayableLongNoteKind.Balloon, 2);
        score.Apply(new TaikoLongNoteProgress(0, before, 1, false), TimeSpan.FromSeconds(2.5));
        score.Apply(new TaikoLongNoteProgress(0, before, 2, true), TimeSpan.FromSeconds(2.6));
        score.Apply(new TaikoLongNoteProgress(1, during, 1, false), TimeSpan.FromSeconds(3.1));
        score.Apply(new TaikoLongNoteProgress(1, during, 2, true), TimeSpan.FromSeconds(3.2));
        Assert.Equal(11660, score.Value); // (300 + 5000) + (360 + 6000).
    }

    private static TaikoNoteJudgement judgement(int index, TaikoHitResult result,
        bool big = false, bool completed = false) =>
        new(index, new PlayableHitObject(TimeSpan.Zero, big ? PlayableNoteKind.BigDon : PlayableNoteKind.Don),
            result, TimeSpan.Zero, completed);

    private static PlayableChart chart(bool goGo = false, bool goGoEnd = false) => new(
        new ChartKey(new SongKey(SongSourceKind.Tja, "synthetic"), "score"),
        TimeSpan.Zero, TimeSpan.FromSeconds(4), [],
        [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4)],
        [new ChartScrollPoint(TimeSpan.Zero, 1)],
        goGo ? goGoEnd
            ? [new ChartEffectPoint(TimeSpan.Zero, false),
                new ChartEffectPoint(TimeSpan.FromSeconds(2), true),
                new ChartEffectPoint(TimeSpan.FromSeconds(3), false)]
            : [new ChartEffectPoint(TimeSpan.Zero, false),
                new ChartEffectPoint(TimeSpan.FromSeconds(2), true)]
            : [new ChartEffectPoint(TimeSpan.Zero, false)], []);

    private static PlayableChart chartWithNotes(int count) => new(
        new ChartKey(new SongKey(SongSourceKind.Tja, "synthetic"), "estimated"),
        TimeSpan.Zero, TimeSpan.FromSeconds(4),
        Enumerable.Range(0, count).Select(_ => new PlayableHitObject(TimeSpan.Zero, PlayableNoteKind.Don)),
        [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4)],
        [new ChartScrollPoint(TimeSpan.Zero, 1)],
        [new ChartEffectPoint(TimeSpan.Zero, false)], []);
}
