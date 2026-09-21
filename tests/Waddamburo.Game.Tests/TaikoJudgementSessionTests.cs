using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class TaikoJudgementSessionTests
{
    private static readonly TaikoJudgementWindows Windows = new(
        TimeSpan.FromMilliseconds(35),
        TimeSpan.FromMilliseconds(80),
        TimeSpan.FromMilliseconds(95));

    [Theory]
    [InlineData(-35, TaikoHitResult.Great)]
    [InlineData(35, TaikoHitResult.Great)]
    [InlineData(36, TaikoHitResult.Good)]
    [InlineData(81, TaikoHitResult.Miss)]
    public void InputIsJudgedFromAbsoluteOffset(int milliseconds, TaikoHitResult expected)
    {
        var session = createSession(new PlayableHitObject(TimeSpan.FromSeconds(1), PlayableNoteKind.Don));

        var input = session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(milliseconds));

        var judgement = Assert.Single(session.CreateSnapshot());
        Assert.Equal(TaikoInputResult.Judged, input);
        Assert.Equal(expected, judgement.Result);
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), judgement.Offset);
    }

    [Fact]
    public void WrongSurfaceConsumesAnInWindowNoteAsMiss()
    {
        var session = createSession(new PlayableHitObject(TimeSpan.FromSeconds(1), PlayableNoteKind.Ka));

        session.SubmitInput(TaikoInputAction.RightDon, TimeSpan.FromSeconds(1));

        Assert.Equal(TaikoHitResult.Miss, Assert.Single(session.CreateSnapshot()).Result);
        Assert.True(session.IsComplete);
    }

    [Fact]
    public void AdvanceProducesMissesAfterTheWindowExpires()
    {
        var session = createSession(
            new PlayableHitObject(TimeSpan.FromSeconds(1), PlayableNoteKind.Don),
            new PlayableHitObject(TimeSpan.FromSeconds(2), PlayableNoteKind.Ka));

        var missed = session.AdvanceTo(TimeSpan.FromSeconds(1.096));

        Assert.Equal(1, missed);
        Assert.Equal(TaikoHitResult.Miss, session.CreateSnapshot()[0].Result);
        Assert.Null(session.CreateSnapshot()[1].Result);
    }

    [Fact]
    public void StrongHitRequiresTheOtherSideWithinItsOwnWindow()
    {
        var session = createSession(new PlayableHitObject(TimeSpan.FromSeconds(1), PlayableNoteKind.BigDon));

        Assert.Equal(TaikoInputResult.Judged,
            session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1)));
        Assert.Equal(TaikoInputResult.Ignored,
            session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1.010)));
        Assert.Equal(TaikoInputResult.StrongHitCompleted,
            session.SubmitInput(TaikoInputAction.RightDon, TimeSpan.FromSeconds(1.020)));

        var judgement = Assert.Single(session.CreateSnapshot());
        Assert.Equal(TaikoHitResult.Great, judgement.Result);
        Assert.True(judgement.StrongHitCompleted);
    }

    [Fact]
    public void TimeCannotMoveBackwards()
    {
        var session = createSession(new PlayableHitObject(TimeSpan.FromSeconds(1), PlayableNoteKind.Don));
        session.AdvanceTo(TimeSpan.FromSeconds(0.5));

        Assert.Throws<ArgumentOutOfRangeException>(() => session.AdvanceTo(TimeSpan.FromSeconds(0.4)));
    }

    private static TaikoJudgementSession createSession(params PlayableHitObject[] notes)
    {
        var song = new SongKey(SongSourceKind.Tja, "song");
        var chart = new PlayableChart(
            new ChartKey(song, "chart"),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(3),
            notes,
            [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4)],
            [new ChartScrollPoint(TimeSpan.Zero, 1)],
            [new ChartEffectPoint(TimeSpan.Zero, false)],
            [new ChartBarLine(TimeSpan.Zero, true)]);
        return new TaikoJudgementSession(chart, Windows, TimeSpan.FromMilliseconds(30));
    }
}
