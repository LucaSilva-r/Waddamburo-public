using System.Text;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.Tja.Tests;

public sealed class TjaCatalogProviderTests
{
    [Fact]
    public async Task FolderLibraryPublishesMetadataCoursesAndReadableAudio()
    {
        using var library = new TemporaryLibrary();
        library.WriteBytes("Pop/audio/song.ogg", [1, 2, 3, 4]);
        library.WriteText(
            "Pop/song.tja",
            """
            TITLE:Synthetic title
            TITLEJA:合成曲
            TITLEEN:Synthetic Song
            SUBTITLE:--Fixture subtitle
            ARTIST:Fixture Artist
            BPM:120
            WAVE:audio\song.ogg
            DEMOSTART:12.5
            COURSE:Easy
            LEVEL:2
            #START
            1000,
            #END
            COURSE:Oni
            LEVEL:8
            #START
            1000,
            #END
            """);
        var provider = new TjaCatalogProvider(library.Path);

        var contribution = await provider.ScanAsync(null, CancellationToken.None);

        var song = Assert.Single(contribution.Songs);
        Assert.Equal("合成曲", song.Title.Primary);
        Assert.Equal("Synthetic Song", song.Title.English);
        Assert.Equal("Fixture subtitle", song.Subtitle);
        Assert.Equal("Fixture Artist", song.Artist);
        Assert.Equal(TimeSpan.FromSeconds(12.5), song.PreviewStart);
        Assert.Collection(
            song.Charts,
            chart =>
            {
                Assert.Equal(TaikoCourse.Easy, chart.Course);
                Assert.Equal(2, chart.Level);
            },
            chart =>
            {
                Assert.Equal(TaikoCourse.Oni, chart.Course);
                Assert.Equal(8, chart.Level);
            });
        var category = Assert.Single(contribution.Categories);
        Assert.Equal("Pop", category.Name);
        Assert.Equal(song.Key, Assert.Single(category.Songs));
        Assert.DoesNotContain("song.ogg", song.AudioAsset!.StableId, StringComparison.Ordinal);

        await using var audio = await provider.OpenReadAsync(song.AudioAsset);
        using var memory = new MemoryStream();
        await audio.CopyToAsync(memory);
        Assert.Equal([1, 2, 3, 4], memory.ToArray());
    }

    [Fact]
    public async Task ShiftJisAndUppercaseExtensionAreSupported()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var library = new TemporaryLibrary();
        var text = "TITLE:テスト曲\nGENRE:ゲームミュージック\nBPM:120\nCOURSE:3\nLEVEL:7\n#START\n1000,\n#END\n";
        library.WriteBytes("song.TJA", Encoding.GetEncoding(932).GetBytes(text));
        var provider = new TjaCatalogProvider(library.Path);

        var contribution = await provider.ScanAsync(null, CancellationToken.None);

        Assert.Equal("テスト曲", Assert.Single(contribution.Songs).Title.Primary);
        Assert.Equal("ゲームミュージック", Assert.Single(contribution.Categories).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Utf16ByteOrdersAreSupported(bool bigEndian)
    {
        using var library = new TemporaryLibrary();
        var encoding = new UnicodeEncoding(bigEndian, byteOrderMark: true, throwOnInvalidBytes: true);
        var text = "TITLE:UTF-16 song\nBPM:120\nCOURSE:Easy\nLEVEL:1\n#START\n1000,\n#END\n";
        library.WriteBytes("song.tja", [.. encoding.GetPreamble(), .. encoding.GetBytes(text)]);
        var provider = new TjaCatalogProvider(library.Path);

        var contribution = await provider.ScanAsync(null, CancellationToken.None);

        Assert.Equal("UTF-16 song", Assert.Single(contribution.Songs).Title.Primary);
    }

    [Fact]
    public async Task PlayerAliasesLevelsAndNegativePreviewAreNormalized()
    {
        using var library = new TemporaryLibrary();
        library.WriteText(
            "Duet/song.tja",
            "TITLE:Duet\nBPM:120\nDEMOSTART:-2\nCOURSE:Oni\nLEVEL:99\n#START 1P\n1000,\n#END\n#START 2P\n1000,\n#END\n");
        var provider = new TjaCatalogProvider(library.Path);

        var contribution = await provider.ScanAsync(null, CancellationToken.None);

        var song = Assert.Single(contribution.Songs);
        Assert.Equal(TimeSpan.Zero, song.PreviewStart);
        Assert.Collection(
            song.Charts,
            chart =>
            {
                Assert.Equal("Oni (P1)", chart.DifficultyName);
                Assert.Equal(10, chart.Level);
            },
            chart => Assert.Equal("Oni (P2)", chart.DifficultyName));
    }

    [Fact]
    public async Task BadFilesAndMissingAudioDoNotDiscardHealthySongs()
    {
        using var library = new TemporaryLibrary();
        library.WriteText("Good/healthy.tja", "TITLE:Healthy\nBPM:120\nCOURSE:Oni\n#START\n1000,\n#END\n");
        library.WriteText("Bad/no-chart.tja", "TITLE:No chart\nBPM:120\nWAVE:missing.ogg\n");
        library.WriteText("Bad/no-course.tja", "TITLE:No course\nBPM:120\n#START\n1000,\n#END\n");
        library.WriteBytes("Bad/oversized.tja", new byte[65]);
        var provider = new TjaCatalogProvider(
            library.Path,
            options: new TjaProviderOptions { MaximumChartBytes = 64 });

        var contribution = await provider.ScanAsync(null, CancellationToken.None);

        Assert.Equal("Healthy", Assert.Single(contribution.Songs).Title.Primary);
        Assert.Contains(contribution.Diagnostics, diagnostic => diagnostic.Code == "TJA_AUDIO_UNSPECIFIED");
        Assert.Contains(contribution.Diagnostics, diagnostic => diagnostic.Code == "TJA_NO_SUPPORTED_CHARTS");
        Assert.Contains(contribution.Diagnostics, diagnostic => diagnostic.Code == "TJA_FILE_TOO_LARGE");
    }

    [Fact]
    public async Task MovingLibraryDoesNotChangeStableIdentities()
    {
        using var first = new TemporaryLibrary();
        using var second = new TemporaryLibrary();
        const string chart = "TITLE:Movable\nBPM:120\nWAVE:song.ogg\nCOURSE:Hard\nLEVEL:5\n#START\n1000,\n#END\n";
        foreach (var library in new[] { first, second })
        {
            library.WriteText("Genre/chart.tja", chart);
            library.WriteBytes("Genre/song.ogg", [9, 8, 7]);
        }
        var id = new CatalogProviderId("portable-tja");

        var firstContribution = await new TjaCatalogProvider(first.Path, id).ScanAsync(null, CancellationToken.None);
        var secondContribution = await new TjaCatalogProvider(second.Path, id).ScanAsync(null, CancellationToken.None);

        var firstSong = Assert.Single(firstContribution.Songs);
        var secondSong = Assert.Single(secondContribution.Songs);
        Assert.Equal(firstSong.Key, secondSong.Key);
        Assert.Equal(Assert.Single(firstSong.Charts).Key, Assert.Single(secondSong.Charts).Key);
        Assert.Equal(firstSong.AudioAsset, secondSong.AudioAsset);
        Assert.Equal(Assert.Single(firstContribution.Categories).Key, Assert.Single(secondContribution.Categories).Key);
    }

    [Fact]
    public async Task ResolverRejectsForeignAndEscapingAssetKeys()
    {
        using var library = new TemporaryLibrary();
        var provider = new TjaCatalogProvider(library.Path);
        var encodedEscape = Convert.ToBase64String(Encoding.UTF8.GetBytes("../outside.ogg"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await provider.OpenReadAsync(new CatalogAssetKey(new CatalogProviderId("foreign"), "relative:eA")));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await provider.OpenReadAsync(new CatalogAssetKey(provider.Id, $"relative:{encodedEscape}")));
    }

    [Fact]
    public async Task ConcreteProviderPublishesThroughGlobalCatalog()
    {
        using var library = new TemporaryLibrary();
        library.WriteText(
            "Game/song.tja",
            "TITLE:Catalog song\nBPM:120\nCOURSE:Normal\nLEVEL:4\n#START\n1000,\n#END\n");
        var provider = new TjaCatalogProvider(library.Path);
        using var catalog = new GlobalSongCatalog([provider]);

        var snapshot = await catalog.RefreshAsync();

        Assert.Equal(1, snapshot.Revision);
        Assert.Equal("Catalog song", Assert.Single(snapshot.Songs).Value.Title.Primary);
        Assert.True(Assert.Single(snapshot.Providers).Succeeded);
        Assert.Equal("Game", Assert.Single(snapshot.Categories).Name);
    }

    private sealed class TemporaryLibrary : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("waddamburo-tja-");

        public string Path => _directory.FullName;

        public void WriteText(string relativePath, string content) =>
            WriteBytes(relativePath, new UTF8Encoding(false).GetBytes(content));

        public void WriteBytes(string relativePath, byte[] content)
        {
            var path = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
