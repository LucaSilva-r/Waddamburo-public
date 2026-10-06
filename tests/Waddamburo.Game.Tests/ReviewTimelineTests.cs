using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class ReviewTimelineTests
{
    private static readonly TaikoJudgementWindows Windows = new(
        TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(108));
    private static readonly TimeSpan Strong = TimeSpan.FromMilliseconds(30);

    private static TimeSpan ms(double value) => TimeSpan.FromMilliseconds(value);

    private static PlayableChart chart() => new(
        new ChartKey(new SongKey(SongSourceKind.Tja, "song"), "chart"),
        TimeSpan.Zero,
        TimeSpan.FromSeconds(4),
        [
            new PlayableHitObject(TimeSpan.FromSeconds(1), PlayableNoteKind.Don),
            new PlayableHitObject(TimeSpan.FromSeconds(1.5), PlayableNoteKind.BigKa),
            new PlayableHitObject(TimeSpan.FromSeconds(2), PlayableNoteKind.Don),
            new PlayableHitObject(TimeSpan.FromSeconds(3), PlayableNoteKind.Don),
        ],
        [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4)],
        [new ChartScrollPoint(TimeSpan.Zero, 1)],
        [new ChartEffectPoint(TimeSpan.Zero, false)],
        [new ChartBarLine(TimeSpan.Zero, true)]);

    // A Great, a late Good on the big ka (both rims), the third Don left to pass, a Great on the last.
    private static readonly TaikoReplayInput[] Inputs =
    [
        new(TaikoInputAction.LeftDon, ms(1_005)),
        new(TaikoInputAction.LeftKa, ms(1_550)), new(TaikoInputAction.RightKa, ms(1_560)),
        new(TaikoInputAction.RightDon, ms(3_000)),
    ];

    [Fact]
    public void TheBakedPlayEndsAsTheLiveOneDoes()
    {
        var timeline = ReviewTimeline.Build(chart(), TaikoCourse.Oni, Windows, Strong, Inputs);

        var live = new TaikoJudgementSession(chart(), Windows, Strong);
        var score = new TaikoScore(chart());
        var gauge = new TaikoSoulGauge(TaikoCourse.Oni, chart().Level, chart().NoteCount);
        live.Judged += judgement =>
        {
            score.Apply(judgement, live.CurrentTime);
            gauge.Apply(judgement);
        };
        foreach (var input in Inputs)
            live.SubmitInput(input.Action, input.Time);
        live.AdvanceTo(TimeSpan.FromSeconds(5));

        var last = timeline.StateAt(TimeSpan.FromSeconds(10))!.Value;
        Assert.Equal((score.State, gauge.Value), (last.Score, last.Gauge));
        Assert.Equal([TaikoHitResult.Great, TaikoHitResult.Good, TaikoHitResult.Miss, TaikoHitResult.Great],
            Enumerable.Range(0, 4).Select(note => timeline.ResultAt(note, TimeSpan.FromSeconds(10))));
    }

    [Fact]
    public void ThePlayheadShowsTheStateOfItsMoment()
    {
        var timeline = ReviewTimeline.Build(chart(), TaikoCourse.Oni, Windows, Strong, Inputs);

        Assert.Null(timeline.StateAt(ms(900)));
        Assert.Equal(1, timeline.StateAt(ms(1_200))!.Value.Score.Combo);
        Assert.Equal(2, timeline.StateAt(ms(1_900))!.Value.Score.Combo);
        // The unhit Don leaves (and breaks the combo) when its Bad window closes, 108 ms after it.
        Assert.False(timeline.IsJudged(2, ms(2_100)));
        Assert.Equal((true, TaikoHitResult.Miss, 0), (timeline.IsJudged(2, ms(2_108)), timeline.ResultAt(2, ms(2_108)),
            timeline.StateAt(ms(2_108))!.Value.Score.Combo));
    }

    [Fact]
    public void ATrainingAttemptJudgesOnlyItsOwnStretch()
    {
        // Played from 1.4 s until 2.5 s: the Don before it and the notes after it are never judged.
        var timeline = ReviewTimeline.Build(chart(), TaikoCourse.Oni, Windows, Strong, Inputs, from: ms(1_400), until: ms(2_500));

        Assert.Equal([null, TaikoHitResult.Good, TaikoHitResult.Miss, null],
            Enumerable.Range(0, 4).Select(note => timeline.ResultAt(note, TimeSpan.FromSeconds(10))));
        Assert.Equal(0, timeline.StateAt(TimeSpan.FromSeconds(10))!.Value.Score.Combo);

        // Live, the skipped Don is gone (shown as done) and raises nothing.
        var live = new TaikoJudgementSession(chart(), Windows, Strong);
        var judged = 0;
        live.Judged += _ => judged++;
        live.SkipTo(ms(1_400));
        live.AdvanceTo(ms(1_400));
        Assert.Equal((true, false, 0), (live.IsJudged(0), live.IsMissed(0), judged));
    }

    [Fact]
    public void PlayingOnRaisesWhatItPassesAndAJumpRaisesNothing()
    {
        var source = new ReviewJudgementSource(ReviewTimeline.Build(chart(), TaikoCourse.Oni, Windows, Strong, Inputs));
        var seen = new List<(int Note, bool Strong)>();
        source.Judged += judgement => seen.Add((judgement.NoteIndex, judgement.StrongHitCompleted));

        source.Play(ms(1_600));
        Assert.Equal([(0, false), (1, false), (1, true)], seen);
        source.Jump(ms(500));
        source.Jump(ms(2_500));
        Assert.Equal(3, seen.Count);
        Assert.True(source.IsMissed(2));
        source.Play(ms(3_100));
        Assert.Equal((3, false), seen[^1]);
        Assert.Equal(4, seen.Count);
    }

    [Fact]
    public void PlayingOnSoundsTheHitsPassedOnceEvenAfterJumps()
    {
        var source = new ReviewJudgementSource(ReviewTimeline.Build(chart(), TaikoCourse.Oni, Windows, Strong, Inputs));
        var hits = new List<TaikoInputAction>();
        source.Hit += hits.Add;

        source.Play(ms(1_555)); // the Don and the first rim of the big ka
        Assert.Equal([TaikoInputAction.LeftDon, TaikoInputAction.LeftKa], hits);
        source.Jump(ms(1_010)); // back, after the Don
        source.Play(ms(1_600)); // the hits from there sound again
        Assert.Equal([TaikoInputAction.LeftKa, TaikoInputAction.RightKa], hits[2..]);
        source.Jump(ms(2_500)); // past the end of what was heard: nothing in between sounds
        source.Play(ms(3_100));
        Assert.Equal(TaikoInputAction.RightDon, Assert.Single(hits[4..]));
    }
}
