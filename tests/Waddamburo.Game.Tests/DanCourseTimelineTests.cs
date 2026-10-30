using Waddamburo.Catalog;
using Waddamburo.Game.Dan;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class DanCourseTimelineTests
{
    private static PlayableChart chart(string id, int seconds, int level = 5) => new(
        new ChartKey(new SongKey(SongSourceKind.Stock, id), "m"), TimeSpan.Zero, TimeSpan.FromSeconds(seconds),
        [new PlayableHitObject(TimeSpan.FromSeconds(1), PlayableNoteKind.Don)],
        [new ChartTimingPoint(TimeSpan.Zero, 120, 4, 4)],
        [new ChartScrollPoint(TimeSpan.Zero, 1)],
        [new ChartEffectPoint(TimeSpan.Zero, false)],
        [new ChartBarLine(TimeSpan.Zero, true)])
    { Level = level };

    private static DanCourseTimeline merged() => DanCourseTimeline.Merge(
        [(chart("a", 10), TaikoCourse.Hard), (chart("b", 20), TaikoCourse.Hard), (chart("c", 30), TaikoCourse.Oni)]);

    [Fact]
    public void SongsFollowEachOtherAfterTheirCards()
    {
        var course = merged();
        var card = DanCourseTimeline.CardLeadIn;

        Assert.Equal(card, course.Songs[0].Zero);
        Assert.Equal(card + TimeSpan.FromSeconds(10), course.Songs[1].Start);
        Assert.Equal(card * 2 + TimeSpan.FromSeconds(10), course.Songs[1].Zero);
        Assert.Equal(card * 3 + TimeSpan.FromSeconds(60), course.Chart.Duration);
    }

    [Fact]
    public void EverySongsNotesKeepTheirPlaceInItsSong()
    {
        var course = merged();

        Assert.Equal(course.Songs.Select(static song => song.Zero + TimeSpan.FromSeconds(1)),
            course.Chart.HitObjects.Select(static note => note.StartTime));
        Assert.Equal(TimeSpan.Zero, course.Chart.TimingPoints[0].Time);
    }

    [Fact]
    public void ControlPointsAreFoundByTimeTheLastOfATimeWinning()
    {
        var points = new[] { new ChartTimingPoint(TimeSpan.Zero, 100, 4, 4), new ChartTimingPoint(TimeSpan.FromSeconds(5), 150, 4, 4),
            new ChartTimingPoint(TimeSpan.FromSeconds(5), 200, 4, 4), new ChartTimingPoint(TimeSpan.FromSeconds(9), 300, 4, 4) };
        var scrolls = new[] { new ChartScrollPoint(TimeSpan.Zero, 1), new ChartScrollPoint(TimeSpan.FromSeconds(1), 2), new ChartScrollPoint(TimeSpan.FromSeconds(3), 3) };
        var test = new PlayableChart(new ChartKey(new SongKey(SongSourceKind.Stock, "a"), "m"), TimeSpan.Zero, TimeSpan.FromSeconds(10),
            [new PlayableHitObject(TimeSpan.FromSeconds(1), PlayableNoteKind.Don)], points, scrolls,
            [new ChartEffectPoint(TimeSpan.Zero, false)], [new ChartBarLine(TimeSpan.Zero, true)]);

        Assert.Equal(100, test.BeatsPerMinuteAt(TimeSpan.FromSeconds(4)));
        Assert.Equal(200, test.BeatsPerMinuteAt(TimeSpan.FromSeconds(5)));
        Assert.Equal(300, test.BeatsPerMinuteAt(TimeSpan.FromSeconds(20)));
        Assert.Equal(1, test.ScrollAt(TimeSpan.Zero));
        Assert.Equal(2, test.ScrollAt(TimeSpan.FromSeconds(2)));
        Assert.Equal(3, test.ScrollAt(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void GaugeUsesAThirdOfEachSongsAmountsRoundedDown()
    {
        var normal = new TaikoSoulGauge(TaikoCourse.Hard, 5, 1);
        var great = new TaikoNoteJudgement(0, new PlayableHitObject(TimeSpan.FromSeconds(7), PlayableNoteKind.Don), TaikoHitResult.Great, null, false);
        normal.Apply(great);
        var course = merged();

        var gauge = new TaikoSoulGauge(course.Chart.Segments);
        gauge.Apply(great);

        Assert.Equal(normal.Value / 3, gauge.Value);
    }
}
