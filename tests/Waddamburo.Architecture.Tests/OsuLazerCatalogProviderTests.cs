using Waddamburo.Catalog;
using Waddamburo.Providers.OsuLazer;

namespace Waddamburo.Architecture.Tests;

public sealed class OsuLazerCatalogProviderTests
{
    private static readonly string AudioHash = new('a', 64);

    private static OsuLazerBeatmap beatmap(string name, double stars, string audio = "song.ogg") => new(
        Guid.NewGuid(), name, "taiko", "Synthetic Song", "", "Fixture Artist", "", "Fixture Mapper", audio, -1,
        Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant().PadRight(64, '0'), name, stars, Hidden: false);

    private static SongCatalogContribution contribution(params OsuLazerBeatmap[] beatmaps) =>
        OsuLazerCatalogProvider.CreateContribution(new OsuLazerSnapshot(51,
            [new OsuLazerBeatmapSet(Guid.NewGuid(), false, [new OsuLazerFile("song.ogg", AudioHash)], beatmaps)]));

    [Fact]
    public void AllChartsSharingAudioStayInOneSongInStarOrder()
    {
        var songs = contribution(beatmap("Inner Oni", 6.3), beatmap("Kantan", 1.4), beatmap("Oni", 4.2),
            beatmap("Futsuu", 2.3), beatmap("Muzukashii", 3.4)).Songs;
        var song = Assert.Single(songs);
        Assert.Equal(["Kantan", "Futsuu", "Muzukashii", "Oni", "Inner Oni"], song.Charts.Select(static chart => chart.DifficultyName));
        Assert.Equal([TaikoCourse.Easy, TaikoCourse.Normal, TaikoCourse.Hard, TaikoCourse.Oni, TaikoCourse.Oni],
            song.Charts.Select(static chart => chart.Course!.Value));
        Assert.Equal(6.3, song.Charts[^1].StarRating);
        Assert.Equal("Fixture Artist", song.Subtitle);
        Assert.Equal($"file:{AudioHash}", songs[0].AudioAsset!.StableId);
    }

    [Fact]
    public void ALoneChartUsesOniJudgementAndKeepsItsSourceStars()
    {
        var chart = Assert.Single(Assert.Single(contribution(beatmap("Lv.450", 7.49)).Songs).Charts);
        Assert.Equal(TaikoCourse.Oni, chart.Course);
        Assert.Equal(7.49, chart.StarRating);
        Assert.Equal(7, chart.Level);
        Assert.Equal(10, OsuCourseLayout.Level(12.3));
        Assert.Equal(1, OsuCourseLayout.Level(0.4));
    }

    [Fact]
    public void ChartsWithAnotherAudioFileAreAnotherSongAndCollectionsBecomeCategories()
    {
        var oni = beatmap("Oni", 4.5);
        var other = beatmap("Oni", 4.5, "other.ogg");
        var result = OsuLazerCatalogProvider.CreateContribution(new OsuLazerSnapshot(51,
            [new OsuLazerBeatmapSet(Guid.NewGuid(), false,
                [new OsuLazerFile("song.ogg", AudioHash), new OsuLazerFile("other.ogg", new string('b', 64))], [oni, other])])
        {
            Collections = [new OsuLazerCollectionInfo(Guid.NewGuid(), "Favourites", [oni.Md5Hash, "missing"])],
        });
        Assert.Equal(2, result.Songs.Length);
        Assert.Equal(["All Songs", "Favourites"], result.Categories.Select(static category => category.Name));
        Assert.Single(result.Categories[1].Songs);
    }

    [Fact]
    public void TaikoBeatmapIsReadLikeLazer()
    {
        const string osu = """
            osu file format v14

            [General]
            Mode: 1

            [Difficulty]
            OverallDifficulty:5
            SliderMultiplier:1.4

            [TimingPoints]
            0,500,4,1,0,100,1,0
            2000,-50,4,1,0,100,0,1

            [HitObjects]
            256,192,0,1,0,0:0:0:0:
            256,192,500,1,2,0:0:0:0:
            256,192,1000,1,4,0:0:0:0:
            256,192,1500,1,12,0:0:0:0:
            256,192,2000,2,0,L|300:192,1,140
            256,192,3000,12,0,5000,0:0:0:0:
            """;
        var chart = OsuTaikoChartReader.Read(osu, new ChartKey(new SongKey(SongSourceKind.OsuLazer, "s"), "c"), 7);
        Assert.Equal([PlayableNoteKind.Don, PlayableNoteKind.Ka, PlayableNoteKind.BigDon, PlayableNoteKind.BigKa],
            chart.HitObjects.Select(static note => note.Kind));
        // 140 px at SV 2 and multiplier 1.4 is half a beat.
        Assert.Equal(new PlayableLongNote(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.25), PlayableLongNoteKind.Roll), chart.LongNotes[0]);
        Assert.Equal(PlayableLongNoteKind.Balloon, chart.LongNotes[1].Kind);
        Assert.Equal(16, chart.LongNotes[1].RequiredHits); // 2 s × 5 hits/s × 1.65
        Assert.Equal([1.0, 2.0], chart.ScrollPoints.Select(static point => point.Multiplier));
        Assert.Equal([false, true], chart.EffectPoints.Select(static point => point.IsGoGo));
        Assert.Equal(120, chart.TimingPoints[0].BeatsPerMinute);
        Assert.Equal(TimeSpan.Zero, chart.BarLines[0].Time);
        Assert.Equal(7, chart.Level);
    }
}
