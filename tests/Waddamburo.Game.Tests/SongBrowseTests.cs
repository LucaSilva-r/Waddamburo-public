using Waddamburo.Catalog;
using Waddamburo.Game.SongSelect;

namespace Waddamburo.Game.Tests;

public sealed class SongBrowseTests
{
    private static SongDescriptor song(string title, string artist = "Artist", int[]? levels = null, double bpm = 120,
        int daysAgo = 0)
    {
        var key = new SongKey(SongSourceKind.OsuLazer, title + Guid.NewGuid().ToString("N"));
        var charts = (levels ?? [5]).Select((level, index) => new SongChartDescriptor(new ChartKey(key, $"c{index}"), $"D{index}",
            new CatalogAssetKey(new CatalogProviderId("osu"), $"c{index}"), (TaikoCourse)index, level));
        return new SongDescriptor(key, new SongTitle(title), artist, charts)
        {
            Bpm = bpm,
            DateAdded = DateTimeOffset.UnixEpoch.AddDays(100 - daysAgo),
        };
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(100);

    [Fact]
    public void TitleGroupsByFirstLetterLikeOsuAndSortsInside()
    {
        var folders = new SongBrowse(SongGroupMode.Title, SongSortMode.Title)
            .Folders([song("banana"), song("Apple"), song("4 Seasons"), song("さくら"), song("avocado")], [], Now);
        Assert.Equal(["0-9", "A", "B", "Other"], folders.Select(static folder => folder.Name));
        Assert.Equal(["Apple", "avocado"], folders[1].Songs.Select(static song => song.Title.Primary));
    }

    [Fact]
    public void DifficultyListsASongUnderEveryStarLevelItHolds()
    {
        var folders = new SongBrowse(SongGroupMode.Difficulty).Folders([song("a", levels: [2, 5]), song("b", levels: [5])], [], Now);
        Assert.Equal(["★2", "★5"], folders.Select(static folder => folder.Name));
        Assert.Equal(2, folders[1].Songs.Count);
    }

    [Fact]
    public void BigFoldersSplitIntoNumberedPartsUnderTheCountLabelsLimit()
    {
        var songs = Enumerable.Range(0, SongBrowse.MaxFolderSongs + 5).Select(index => song($"s{index:0000}")).ToArray();
        var folders = new SongBrowse(SongGroupMode.All).Folders(songs, [], Now);
        Assert.Equal(["All Songs 1/2", "All Songs 2/2"], folders.Select(static folder => folder.Name));
        Assert.Equal(SongBrowse.MaxFolderSongs, folders[0].Songs.Count);
    }

    [Fact]
    public void BpmAndDateAddedUseOsuBands()
    {
        var songs = new[] { song("slow", bpm: 55, daysAgo: 0), song("fast", bpm: 181, daysAgo: 40) };
        Assert.Equal(["Under 60 BPM", "180-190 BPM"],
            new SongBrowse(SongGroupMode.Bpm).Folders(songs, [], Now).Select(static folder => folder.Name));
        Assert.Equal(["Today", "1 months ago"],
            new SongBrowse(SongGroupMode.DateAdded).Folders(songs, [], Now).Select(static folder => folder.Name));
    }

    [Fact]
    public void SearchNeedsEveryWordInTitleArtistOrMapper()
    {
        var target = song("Senbonzakura", artist: "Kurousa-P") with { Creator = "mapper" };
        Assert.True(SongBrowse.Matches(target, "senbon kurousa"));
        Assert.True(SongBrowse.Matches(target, "MAPPER"));
        Assert.False(SongBrowse.Matches(target, "senbon other"));
        Assert.False(SongBrowse.Matches(target, "   "));
    }

    [Fact]
    public void SearchFiltersLikeOsu()
    {
        // Courses by index: easy 3, normal 5, hard 7, oni 9; no ura.
        var target = song("Senbonzakura", artist: "Kurousa-P", levels: [3, 5, 7, 9], bpm: 154)
            with { Creator = "mapper", Length = TimeSpan.FromSeconds(125) };
        Assert.True(SongBrowse.Matches(target, "stars>=9"));
        Assert.False(SongBrowse.Matches(target, "stars>9"));
        Assert.True(SongBrowse.Matches(target, "oni=9 hard<=7 easy<4"));
        Assert.False(SongBrowse.Matches(target, "ura>0"));
        Assert.False(SongBrowse.Matches(target, "ura<5")); // a course filter needs the course
        Assert.False(SongBrowse.Matches(target, "ura=0"));
        Assert.True(SongBrowse.Matches(target, "bpm>150 length<2:30 length>=125"));
        Assert.True(SongBrowse.Matches(target, "senbon artist=kurousa mapper:MAP"));
        Assert.False(SongBrowse.Matches(target, "senbon artist=other"));
        Assert.False(SongBrowse.Matches(target, "title=other stars>1"));
        // The test songs are osu!lazer's.
        Assert.True(SongBrowse.Matches(target, "source=osu stars>=9"));
        Assert.True(SongBrowse.Matches(target, "source=tja,lazer"));
        Assert.False(SongBrowse.Matches(target, "source=tja,nijiiro"));
        // Not a filter: a word to find (re:zero's colon, an unknown key).
        Assert.True(SongBrowse.Matches(song("Re:Zero"), "re:zero"));
    }

    [Fact]
    public void SearchFiltersByThePlayersCrowns()
    {
        // Courses by index: easy cleared, normal full combo, hard Dondaful, oni not cleared.
        var target = song("Senbonzakura", levels: [3, 5, 7, 9]);
        Scores.TaikoCrown[] byCourse = [Scores.TaikoCrown.Clear, Scores.TaikoCrown.FullCombo, Scores.TaikoCrown.Dondaful, Scores.TaikoCrown.None];
        Scores.TaikoCrown crownOf(SongChartDescriptor chart) => byCourse[(int)chart.Course!.Value];
        Assert.True(SongBrowse.Matches(target, "oni=notcleared", crownOf));
        Assert.False(SongBrowse.Matches(target, "oni>=cleared", crownOf));
        Assert.True(SongBrowse.Matches(target, "hard=dondaful normal=fc easy=cleared", crownOf));
        Assert.True(SongBrowse.Matches(target, "crown=dondaful", crownOf));
        Assert.True(SongBrowse.Matches(target, "crown>=fc oni>=9", crownOf));
        Assert.False(SongBrowse.Matches(target, "normal=dondaful", crownOf));
        // Without crowns (no player) a crown token is just a word.
        Assert.False(SongBrowse.Matches(target, "oni=notcleared"));
    }

    [Fact]
    public void CyclingWrapsAround()
    {
        Assert.Equal(SongGroupMode.All, new SongBrowse(SongGroupMode.Length).NextGroup().Group);
        Assert.Equal(SongSortMode.Artist, new SongBrowse().NextSort().Sort);
    }
}
