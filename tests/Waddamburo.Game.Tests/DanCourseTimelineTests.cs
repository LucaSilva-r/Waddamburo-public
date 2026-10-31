using Waddamburo.Catalog;
using Waddamburo.Game.Dan;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scores;

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

    private static DanCourse exams(int bads) => new(1, "Test", "hash", [new("bads", [bads], [bads])],
        [new("a", 3, null, 1, []), new("b", 3, null, 1, []), new("c", 3, null, 1, [])]);

    [Fact]
    public void AReplaySplitsIntoOnePerSongOnItsOwnTime()
    {
        var course = merged();
        var replay = new TaikoReplay();
        replay.Add(TaikoInputAction.LeftDon, course.Songs[0].Zero + TimeSpan.FromSeconds(1));
        replay.Add(TaikoInputAction.LeftDon, course.Songs[1].Start + TimeSpan.FromSeconds(1));

        var replays = course.Split(replay);

        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(replays[0].Inputs).Time);
        Assert.Equal(course.Songs[1].Start + TimeSpan.FromSeconds(1) - course.Songs[1].Zero, Assert.Single(replays[1].Inputs).Time);
        Assert.Empty(replays[2].Inputs);
    }

    [Fact]
    public void ACourseScoresTheSameFromItsSplitReplays()
    {
        var course = merged();
        var windows = TaikoJudgementWindows.ForCourse(TaikoCourse.Hard);
        var replay = new TaikoReplay();
        foreach (var song in course.Songs)
            replay.Add(TaikoInputAction.LeftDon, song.Zero + TimeSpan.FromSeconds(1));

        var (total, songs, played, _) = PlayRescore.ScoreCourse(exams(10), merged(), course.Split(replay), windows, TimeSpan.FromMilliseconds(30));

        Assert.Equal(3, played);
        Assert.Equal(3, total.Goods);
        Assert.Equal(3, total.MaxCombo);
        Assert.Equal([1, 1, 1], songs.Select(static song => song.Goods));
    }

    [Fact]
    public void AFailedRequirementEndsTheCourseWithItsSong()
    {
        var course = merged();
        var windows = TaikoJudgementWindows.ForCourse(TaikoCourse.Hard);

        var (total, songs, played, level) = PlayRescore.ScoreCourse(exams(1), course, [], windows, TimeSpan.FromMilliseconds(30));

        Assert.Equal(1, played);
        Assert.Equal(0, level);
        Assert.Equal(1, total.Bads);
        Assert.Equal([1, 0, 0], songs.Select(static song => song.Bads));
    }

    [Fact]
    public void ACourseReportsItsProgressOnEachSongsOwnTime()
    {
        var seen = new List<(int Song, TimeSpan Time, int Bads)>();

        PlayRescore.ScoreCourse(exams(10), merged(), [], TaikoJudgementWindows.ForCourse(TaikoCourse.Hard), TimeSpan.FromMilliseconds(30),
            (song, time, at) => seen.Add((song, time, at.Bads)));

        Assert.Equal([0, 1, 2], seen.Select(static row => row.Song));
        Assert.Equal([1, 2, 3], seen.Select(static row => row.Bads));
        Assert.All(seen, static row => Assert.InRange(row.Time, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void ACourseWithNoFailurePlaysEverySong()
    {
        var (total, _, played, level) = PlayRescore.ScoreCourse(exams(10), merged(), [],
            TaikoJudgementWindows.ForCourse(TaikoCourse.Hard), TimeSpan.FromMilliseconds(30));

        Assert.Equal(3, played);
        Assert.Equal(3, total.Bads);
        Assert.Equal(2, level);
    }
}
