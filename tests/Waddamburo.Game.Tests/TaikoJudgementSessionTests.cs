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
        Assert.True(events[2].TimedOut);
    }

    private static readonly TaikoJudgementWindows Windows = new(
        TimeSpan.FromMilliseconds(35),
        TimeSpan.FromMilliseconds(80),
        TimeSpan.FromMilliseconds(95));

    [Theory]
    [InlineData(TaikoCourse.Easy, 417083, 1084417, 1251250)]
    [InlineData(TaikoCourse.Normal, 417083, 1084417, 1251250)]
    [InlineData(TaikoCourse.Hard, 250250, 750750, 1084417)]
    [InlineData(TaikoCourse.Oni, 250250, 750750, 1084417)]
    [InlineData(TaikoCourse.Ura, 250250, 750750, 1084417)]
    public void CourseWindowsUseSpecifiedBoundaries(TaikoCourse course, long great, long good, long miss)
    {
        var windows = TaikoJudgementWindows.ForCourse(course);

        Assert.Equal(great, windows.Great.Ticks);
        Assert.Equal(good, windows.Good.Ticks);
        Assert.Equal(miss, windows.Miss.Ticks);
        Assert.Equal(TaikoHitResult.Great, windows.ResultFor(TimeSpan.FromTicks(-great)));
        Assert.Equal(TaikoHitResult.Good, windows.ResultFor(TimeSpan.FromTicks(great + 1)));
        Assert.Equal(TaikoHitResult.Good, windows.ResultFor(TimeSpan.FromTicks(-good)));
        Assert.Equal(TaikoHitResult.Miss, windows.ResultFor(TimeSpan.FromTicks(good + 1)));
        Assert.Equal(TaikoHitResult.Miss, windows.ResultFor(TimeSpan.FromTicks(-miss)));
        Assert.Null(windows.ResultFor(TimeSpan.FromTicks(miss + 1)));
    }

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
        Assert.False(judgement.TimedOut);
    }

    [Fact]
    public void WrongSurfaceIsIgnoredAndNoteCanStillBeHit()
    {
        var session = createSession(new PlayableHitObject(TimeSpan.FromSeconds(1), PlayableNoteKind.Ka));

        Assert.Equal(TaikoInputResult.Ignored,
            session.SubmitInput(TaikoInputAction.RightDon, TimeSpan.FromSeconds(1)));
        Assert.Null(Assert.Single(session.CreateSnapshot()).Result);
        Assert.Equal(0, session.NextNoteIndex);
        Assert.Equal(TaikoInputResult.Judged,
            session.SubmitInput(TaikoInputAction.LeftKa, TimeSpan.FromSeconds(1)));

        Assert.Equal(TaikoHitResult.Great, Assert.Single(session.CreateSnapshot()).Result);
        Assert.True(session.IsComplete);
    }

    [Fact]
    public void WrongSurfaceLeavesNoteToMissWhenItsWindowExpires()
    {
        var session = createSession(new PlayableHitObject(TimeSpan.FromSeconds(1), PlayableNoteKind.Ka));

        Assert.Equal(TaikoInputResult.Ignored,
            session.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1)));
        Assert.Equal(1, session.AdvanceTo(TimeSpan.FromSeconds(1.096)));

        Assert.Equal(TaikoHitResult.Miss, Assert.Single(session.CreateSnapshot()).Result);
        Assert.True(Assert.Single(session.CreateSnapshot()).TimedOut);
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
        Assert.True(session.IsMissed(0));
        Assert.False(session.IsMissed(1));
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
    [InlineData(0, 1, true)]    // together: doubles for both
    [InlineData(-30, 30, true)] // 60 ms apart, but both Great
    [InlineData(0, 40, false)]  // one Good: plain hits
    public void TwoPlayersDoubleAHandNoteOnlyWhenBothHitItGreat(int leftMs, int rightMs, bool doubled)
    {
        var note = new PlayableHitObject(TimeSpan.FromSeconds(1), PlayableNoteKind.BigDon, isHand: true);
        var left = createSession(partner: true, note);
        var right = createSession(partner: true, note);
        var link = new TaikoHandNoteLink();
        link.Add(left, [note]);
        link.Add(right, [note]);
        var strong = new List<string>();
        left.Judged += judgement => { if (judgement.StrongHitCompleted) strong.Add("left"); };
        right.Judged += judgement => { if (judgement.StrongHitCompleted) strong.Add("right"); };

        // One player's second hit on the same drum no longer completes it.
        var at = TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(leftMs);
        left.SubmitInput(TaikoInputAction.LeftDon, at);
        left.SubmitInput(TaikoInputAction.RightDon, at + TimeSpan.FromMilliseconds(5));
        right.SubmitInput(TaikoInputAction.LeftDon, TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(rightMs));

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
