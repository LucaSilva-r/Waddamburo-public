using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class TaikoJudgementSessionTests
{
    [Fact]
    public void SimultaneousSecondHandCannotConsumeTheFollowingOrdinaryNote()
    {
        var session = createSession(
            new PlayableHitObject(TimeSpan.FromSeconds(1), PlayableNoteKind.Don),
            new PlayableHitObject(TimeSpan.FromSeconds(1.05), PlayableNoteKind.Don));
        session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1));
        Assert.Equal(TaikoInputResult.Ignored,
            session.SubmitInput(TaikoInputAction.RightDon, TimeSpan.FromSeconds(1)));
        Assert.False(session.IsJudged(1));
    }

    [Fact]
    public void PreRollAllowsEarlyInputOnAChartZeroNote()
    {
        var session = createSession(new PlayableHitObject(TimeSpan.Zero, PlayableNoteKind.Don));
        session.AdvanceTo(TimeSpan.FromSeconds(-3));
        session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromMilliseconds(-20));
        Assert.Equal(TaikoHitResult.Great, Assert.Single(session.CreateSnapshot()).Result);
    }

    [Fact]
    public void JudgementEventsIncludeTimeoutsAndStrongCompletionWithoutDuplicatingNotes()
    {
        var session = createSession(
            new PlayableHitObject(TimeSpan.FromSeconds(1), PlayableNoteKind.BigDon),
            new PlayableHitObject(TimeSpan.FromSeconds(2), PlayableNoteKind.Ka));
        var events = new List<TaikoNoteJudgement>();
        session.Judged += events.Add;
        session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1));
        session.SubmitInput(TaikoInputAction.RightDon, TimeSpan.FromSeconds(1.02));
        session.AdvanceTo(TimeSpan.FromSeconds(3));
        Assert.Equal(3, events.Count);
        Assert.True(events[1].StrongHitCompleted);
        Assert.Equal(events[0].NoteIndex, events[1].NoteIndex);
        Assert.Equal(TaikoHitResult.Miss, events[2].Result);
    }

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

    [Theory]
    [InlineData(1, true)]   // together: doubles for both
    [InlineData(30, true)]  // within the great window
    [InlineData(40, false)] // outside it: plain hits
    public void TwoPlayersDoubleAHandNoteOnlyWhenBothHitItTogether(int apartMs, bool doubled)
    {
        var note = new PlayableHitObject(TimeSpan.FromSeconds(1), PlayableNoteKind.BigDon, isHand: true);
        var left = createSession(partner: true, note);
        var right = createSession(partner: true, note);
        var link = new TaikoHandNoteLink(TimeSpan.FromMilliseconds(35));
        link.Add(left, [note]);
        link.Add(right, [note]);
        var strong = new List<string>();
        left.Judged += judgement => { if (judgement.StrongHitCompleted) strong.Add("left"); };
        right.Judged += judgement => { if (judgement.StrongHitCompleted) strong.Add("right"); };

        // One player's second hit on the same drum no longer completes it.
        left.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1));
        left.SubmitInput(TaikoInputAction.RightDon, TimeSpan.FromSeconds(1.005));
        right.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(apartMs));

        Assert.Equal(doubled ? ["left", "right"] : [], strong.Order());
    }

    private static TaikoJudgementSession createSession(params PlayableHitObject[] notes) => createSession(false, notes);

    private static TaikoJudgementSession createSession(bool partner, params PlayableHitObject[] notes)
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
        return new TaikoJudgementSession(chart, Windows, TimeSpan.FromMilliseconds(30), partner);
    }
}
