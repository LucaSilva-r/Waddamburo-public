using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class TaikoBranchingTests
{
    private static readonly TaikoJudgementWindows Windows = new(
        TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(108));
    private static readonly TimeSpan Strong = TimeSpan.FromMilliseconds(30);

    private static TimeSpan s(double value) => TimeSpan.FromSeconds(value);

    // 2 s measures. Two Dons before the check at the 2 s bar line; one Great goes to expert, two to master,
    // from the branch at 4 s: a Don on normal, a Ka on expert, a big Don on master, then a Don on every route.
    private static PlayableChart chart() => new(
        new ChartKey(new SongKey(SongSourceKind.Tja, "song"), "chart"),
        TimeSpan.Zero,
        s(8),
        [
            new PlayableHitObject(s(0.5), PlayableNoteKind.Don),
            new PlayableHitObject(s(1), PlayableNoteKind.Don),
            new PlayableHitObject(s(4.5), PlayableNoteKind.Don) { Branch = ChartBranch.Normal },
            new PlayableHitObject(s(4.5), PlayableNoteKind.Ka) { Branch = ChartBranch.Expert },
            new PlayableHitObject(s(4.5), PlayableNoteKind.BigDon) { Branch = ChartBranch.Master },
            new PlayableHitObject(s(6.5), PlayableNoteKind.Don),
        ],
        [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4)],
        [new ChartScrollPoint(TimeSpan.Zero, 1)],
        [new ChartEffectPoint(TimeSpan.Zero, false)],
        [new ChartBarLine(s(0), true), new ChartBarLine(s(2), true), new ChartBarLine(s(4), true), new ChartBarLine(s(6), true)])
    {
        Branching = new ChartBranching(
            [new ChartBranchCheck(s(2), s(4), new(20, 40), new(20, 40), new(20, 40))], ChartBranchPoints.Default),
    };

    [Theory]
    [InlineData(0, ChartBranch.Normal, 2)]
    [InlineData(1, ChartBranch.Expert, 3)]
    [InlineData(2, ChartBranch.Master, 4)]
    public void GreatsBeforeTheCheckPickTheRouteAndOnlyItsNotesAreJudged(int greats, ChartBranch expected, int judgedNote)
    {
        var session = new TaikoJudgementSession(chart(), Windows, Strong);
        var picked = new List<ChartBranch>();
        session.BranchChecked += (_, route) => picked.Add(route);
        var judged = new List<int>();
        session.Judged += judgement => judged.Add(judgement.NoteIndex);
        foreach (var time in new[] { 0.5, 1 }.Take(greats))
            session.SubmitInput(TaikoInputAction.LeftDon, s(time));

        session.AdvanceTo(s(2));
        Assert.Equal([expected], picked);
        Assert.Equal(expected, session.Branch);
        Assert.Equal(ChartBranch.Normal, session.BranchAt(s(3.9))); // the measure before the branch is still normal
        Assert.Equal(expected, session.BranchAt(s(4.5)));

        session.AdvanceTo(s(8));
        // Only the route's note at 4.5 s was judged (missed); the two off it went by.
        Assert.Equal([judgedNote, 5], judged.Where(index => index >= 2));
    }

    [Fact]
    public void AnInputJudgesTheRoutesNoteNotAnotherRoutesAtTheSameTime()
    {
        var session = new TaikoJudgementSession(chart(), Windows, Strong);
        session.SubmitInput(TaikoInputAction.LeftDon, s(0.5));
        session.SubmitInput(TaikoInputAction.LeftDon, s(1));
        session.AdvanceTo(s(4));
        TaikoNoteJudgement? hit = null;
        session.Judged += judgement => hit ??= judgement;
        // Master: the big Don takes the hit; the normal Don at the same time is off the route.
        Assert.Equal(TaikoInputResult.Judged, session.SubmitInput(TaikoInputAction.LeftDon, s(4.5)));
        Assert.Equal(4, hit!.Value.NoteIndex);
    }

    [Fact]
    public void ACheckIsJudgedOneFullMeasureBeforeItsRouteHoweverShortTheBarBefore()
    {
        // 120 BPM: a 4/4 measure takes 2 s. The stored check says the bar line 0.5 s before the route.
        var chart = new PlayableChart(new ChartKey(new SongKey(SongSourceKind.Tja, "song"), "chart"), TimeSpan.Zero, s(8),
            [new PlayableHitObject(s(1), PlayableNoteKind.Don), new PlayableHitObject(s(3), PlayableNoteKind.Don)],
            [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4)], [new ChartScrollPoint(TimeSpan.Zero, 1)],
            [new ChartEffectPoint(TimeSpan.Zero, false)], [new ChartBarLine(TimeSpan.Zero, true)])
        {
            Branching = new ChartBranching([new ChartBranchCheck(s(4.5), s(5), new(1, 1), new(1, 1), new(1, 1))],
                new ChartBranchPoints(1, 1, 0, 0, 1, 1, 0, 0, 0)),
        };
        var session = new TaikoJudgementSession(chart, new TaikoJudgementWindows(TimeSpan.FromMilliseconds(25),
            TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(108)), TimeSpan.FromMilliseconds(30));
        var judged = new List<ChartBranchCheck>();
        session.BranchChecked += (check, _) => judged.Add(check);

        session.AdvanceTo(s(2.9));
        Assert.Empty(judged);
        session.AdvanceTo(s(3));
        Assert.Equal(s(3), Assert.Single(judged).Time);
        Assert.Equal(s(4.5), chart.Branching!.Checks[0].Time); // what the chart's hash reads
    }

    [Fact]
    public void AReviewedPlayShowsTheRoutesItsChecksPicked()
    {
        TaikoReplayInput[] inputs = [new(TaikoInputAction.LeftDon, s(0.5)), new(TaikoInputAction.LeftDon, s(1))];
        var source = new ReviewJudgementSource(ReviewTimeline.Build(chart(), TaikoCourse.Oni, Windows, Strong, inputs));
        source.Jump(s(1.5));
        Assert.Equal(ChartBranch.Normal, source.Branch);
        Assert.Equal(ChartBranch.Normal, source.BranchAt(s(4.5))); // not checked yet at the playhead
        source.Jump(s(2.5));
        Assert.Equal(ChartBranch.Master, source.Branch);
        Assert.Equal(ChartBranch.Normal, source.BranchAt(s(3)));
        Assert.Equal(ChartBranch.Master, source.BranchAt(s(4.5)));
    }

    [Fact]
    public void RoutesWithTheSameNotesChangeRouteButHashAsNoBranching()
    {
        var plain = chart() with { Branching = null };
        var shown = plain with { Branching = new ChartBranching([new ChartBranchCheck(s(2), s(4), new(0, 0), new(0, 0), new(0, 0))], ChartBranchPoints.Default) { SameNotes = true } };
        Assert.Equal(ChartHash.Compute(plain, TaikoCourse.Oni), ChartHash.Compute(shown, TaikoCourse.Oni));

        var session = new TaikoJudgementSession(shown, Windows, Strong);
        session.AdvanceTo(s(2));
        Assert.Equal(ChartBranch.Master, session.Branch);
    }

    [Fact]
    public void BranchingIsPartOfTheChartHashAndReadsBack()
    {
        var branched = chart();
        var bytes = ChartHash.Serialize(branched, TaikoCourse.Oni);
        var (read, _) = ChartHash.Deserialize(bytes, SongSourceKind.Tja);
        Assert.Equal(bytes, ChartHash.Serialize(read, TaikoCourse.Oni));
        Assert.Equal(branched.Branching!.Checks.ToArray(), read.Branching!.Checks.ToArray());
        Assert.Equal(ChartBranch.Master, read.HitObjects[4].Branch);
        Assert.Null(read.HitObjects[5].Branch);
        // Without branching the same notes serialize as before (no trailer).
        var plain = branched with { Branching = null };
        Assert.True(ChartHash.Serialize(plain, TaikoCourse.Oni).Length < bytes.Length);
    }

    [Fact]
    public void PerChartFiguresComeFromOneRoute()
    {
        var normal = chart().OnRoute(ChartBranch.Normal);
        Assert.Null(normal.Branching);
        Assert.Equal(4, normal.NoteCount);
        Assert.Equal(PlayableNoteKind.BigDon, chart().OnRoute(ChartBranch.Master).HitObjects[2].Kind);
    }
}
