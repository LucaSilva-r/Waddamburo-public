using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class TaikoLongNoteTests
{
    [Theory]
    [InlineData(PlayableLongNoteKind.Roll)]
    [InlineData(PlayableLongNoteKind.BigRoll)]
    public void RollsAcceptBothSurfacesOnlyWithinTheirDuration(PlayableLongNoteKind kind)
    {
        var session = create(kind);
        Assert.Equal(TaikoInputResult.Ignored, session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(0.99)));
        Assert.Equal(TaikoInputResult.LongNoteHit, session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1)));
        Assert.Equal(TaikoInputResult.LongNoteHit, session.SubmitInput(TaikoInputAction.RightKa, TimeSpan.FromSeconds(1)));
        Assert.Equal(2, session.GetLongNoteProgress(0).Hits);
        Assert.False(session.IsComplete);
        Assert.Equal(TaikoInputResult.Ignored, session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(2)));
        Assert.True(session.IsComplete);
        Assert.Empty(session.CreateSnapshot());
    }

    [Theory]
    [InlineData(PlayableLongNoteKind.Balloon)]
    [InlineData(PlayableLongNoteKind.Kusudama)]
    public void BalloonsRequireDonHitsAndPopAtTheQuota(PlayableLongNoteKind kind)
    {
        var session = create(kind, 2);
        var changes = new List<TaikoLongNoteProgress>();
        session.LongNoteHit += changes.Add;
        Assert.Equal(TaikoInputResult.Ignored, session.SubmitInput(TaikoInputAction.LeftKa, TimeSpan.FromSeconds(1)));
        Assert.Equal(TaikoInputResult.LongNoteHit, session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1.1)));
        Assert.Equal(TaikoInputResult.BalloonPopped, session.SubmitInput(TaikoInputAction.RightDon, TimeSpan.FromSeconds(1.2)));
        Assert.Equal(TaikoInputResult.Ignored, session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1.3)));
        Assert.Equal(2, changes.Count);
        Assert.True(changes[1].IsPopped);
        Assert.Equal(TaikoInputAction.RightDon, changes[1].LastAction);
        Assert.True(session.IsComplete);
    }

    [Fact]
    public void UnhitLongNotesExpireWithoutComboMisses()
    {
        var session = create(PlayableLongNoteKind.Balloon, 3);
        var judgements = new List<TaikoNoteJudgement>();
        session.Judged += judgements.Add;
        Assert.Equal(0, session.AdvanceTo(TimeSpan.FromSeconds(3)));
        Assert.Empty(judgements);
        Assert.True(session.IsComplete);
        Assert.False(session.GetLongNoteProgress(0).IsPopped);
    }

    [Fact]
    public void TapAfterRollIsStillJudgedNormally()
    {
        var session = create(PlayableLongNoteKind.Roll, tap: true);
        Assert.Equal(TaikoInputResult.LongNoteHit, session.SubmitInput(TaikoInputAction.RightKa, TimeSpan.FromSeconds(1.5)));
        Assert.Equal(TaikoInputResult.Judged, session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(2.5)));
        Assert.Equal(TaikoHitResult.Great, Assert.Single(session.CreateSnapshot()).Result);
        Assert.True(session.IsComplete);
    }

    private static TaikoJudgementSession create(PlayableLongNoteKind kind, int hits = 0, bool tap = false)
    {
        var key = new ChartKey(new SongKey(SongSourceKind.Tja, "synthetic"), "long");
        var chart = new PlayableChart(key, TimeSpan.Zero, TimeSpan.FromSeconds(3),
            tap ? [new PlayableHitObject(TimeSpan.FromSeconds(2.5), PlayableNoteKind.Don)] : [],
            [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4)],
            [new ChartScrollPoint(TimeSpan.Zero, 1)], [new ChartEffectPoint(TimeSpan.Zero, false)], [],
            [new PlayableLongNote(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), kind, hits)]);
        return new TaikoJudgementSession(chart, new(TimeSpan.FromMilliseconds(35), TimeSpan.FromMilliseconds(80),
            TimeSpan.FromMilliseconds(95)), TimeSpan.FromMilliseconds(30));
    }
}
