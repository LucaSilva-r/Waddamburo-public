using Waddamburo.Catalog;
using Waddamburo.Providers.OsuLazer;

namespace Waddamburo.Architecture.Tests;

public sealed class OsuLazerCatalogProviderTests
{
    [Fact]
    public void SnapshotIsConvertedWithoutRealmOrFilesystemAccess()
    {
        var setId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var chartId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var snapshot = new OsuLazerSnapshot(
            OsuLazerRealmReader.SupportedSchemaVersion,
            [new OsuLazerBeatmapSet(
                setId,
                10,
                DateTimeOffset.UnixEpoch,
                "set-hash",
                false,
                [new OsuLazerFile("song.ogg", "audio-hash")],
                [new OsuLazerBeatmap(
                    chartId,
                    20,
                    "Fixture Oni",
                    "taiko",
                    "Synthetic Song",
                    "",
                    "Fixture Artist",
                    "",
                    "Fixture Mapper",
                    "song.ogg",
                    "background.png",
                    "chart-hash",
                    "synthetic-md5",
                    123_000,
                    180)])]);

        var contribution = OsuLazerCatalogProvider.CreateContribution(snapshot);

        var song = Assert.Single(contribution.Songs);
        Assert.Equal(new SongKey(SongSourceKind.OsuLazer, "set-hash"), song.Key);
        Assert.Equal("Synthetic Song", song.Title.Primary);
        Assert.Equal("Fixture Artist", song.Artist);
        Assert.Equal("file:audio-hash", song.AudioAsset!.StableId);
        Assert.Equal("chart-hash", Assert.Single(song.Charts).Key.StableId);
        Assert.Equal(song.Key, Assert.Single(Assert.Single(contribution.Categories).Songs));
    }
}
