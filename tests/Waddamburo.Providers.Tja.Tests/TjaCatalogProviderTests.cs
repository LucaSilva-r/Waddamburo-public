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
    public async Task DifficultyFilesSharingAudioBecomeOneBrowserSong()
    {
        using var library = new TemporaryLibrary();
        library.WriteBytes("Anime/Grouped/audio.ogg", [1, 2, 3]);
        library.WriteText(
            "Anime/Grouped/easy.tja",
            "TITLE:Grouped song\nBPM:120\nWAVE:audio.ogg\nCOURSE:Easy\nLEVEL:2\n#START\n1000,\n#END\n");
        library.WriteText(
            "Anime/Grouped/oni.tja",
            "TITLE:Grouped song\nBPM:120\nWAVE:audio.ogg\nCOURSE:Oni\nLEVEL:8\n#START\n1000,\n#END\n");
        var provider = new TjaCatalogProvider(library.Path);

        var contribution = await provider.ScanAsync(null, CancellationToken.None);

        var song = Assert.Single(contribution.Songs);
        Assert.Equal("Grouped song", song.Title.Primary);
        Assert.Equal([TaikoCourse.Easy, TaikoCourse.Oni], song.Charts.Select(static chart => chart.Course));
        Assert.Equal(song.Key, Assert.Single(Assert.Single(contribution.Categories).Songs));
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

    [Fact]
    public async Task PlayableChartLoadsTheSelectedSectionWithAbsoluteTimelineEvents()
    {
        using var library = new TemporaryLibrary();
        library.WriteText(
            "Game/timeline.tja",
            """
            TITLE:Timeline
            BPM:120
            OFFSET:-0.25
            COURSE:Easy
            LEVEL:2
            #START
            1000,
            #END
            COURSE:Oni
            LEVEL:8
            #START
            3000,
            #BPMCHANGE 240
            #MEASURE 3/4
            #SCROLL 2
            #GOGOSTART
            0100,
            #DELAY 0.5
            #BARLINEOFF
            0010,
            #END
            """);
        var provider = new TjaCatalogProvider(library.Path);
        var contribution = await provider.ScanAsync(null, CancellationToken.None);
        var descriptor = Assert.Single(contribution.Songs).Charts.Single(chart => chart.Course == TaikoCourse.Oni);

        var chart = await provider.LoadChartAsync(descriptor.Key, descriptor.ChartAsset);

        Assert.Equal(TimeSpan.FromSeconds(-0.25), chart.AuthoredOffset);
        Assert.Equal(TimeSpan.FromSeconds(4), chart.Duration);
        Assert.Collection(
            chart.HitObjects,
            note =>
            {
                Assert.Equal(TimeSpan.Zero, note.StartTime);
                Assert.Equal(PlayableNoteKind.BigDon, note.Kind);
                Assert.True(note.IsStrong);
            },
            note =>
            {
                Assert.Equal(TimeSpan.FromSeconds(2.1875), note.StartTime);
                Assert.Equal(PlayableNoteKind.Don, note.Kind);
            },
            note =>
            {
                Assert.Equal(TimeSpan.FromSeconds(3.625), note.StartTime);
                Assert.Equal(PlayableNoteKind.Don, note.Kind);
            });
        Assert.Collection(
            chart.TimingPoints,
            point => Assert.Equal((TimeSpan.Zero, 120d, 4, 4),
                (point.Time, point.BeatsPerMinute, point.BeatsPerMeasure, point.BeatUnit)),
            point => Assert.Equal((TimeSpan.FromSeconds(2), 240d, 3, 4),
                (point.Time, point.BeatsPerMinute, point.BeatsPerMeasure, point.BeatUnit)));
        Assert.Collection(
            chart.ScrollPoints,
            point => Assert.Equal((TimeSpan.Zero, 1d), (point.Time, point.Multiplier)),
            point => Assert.Equal((TimeSpan.FromSeconds(2), 2d), (point.Time, point.Multiplier)));
        Assert.Collection(
            chart.EffectPoints,
            point => Assert.Equal((TimeSpan.Zero, false), (point.Time, point.IsGoGo)),
            point => Assert.Equal((TimeSpan.FromSeconds(2), true), (point.Time, point.IsGoGo)));
        Assert.Collection(
            chart.BarLines,
            line => Assert.Equal((TimeSpan.Zero, true), (line.Time, line.IsVisible)),
            line => Assert.Equal((TimeSpan.FromSeconds(2), true), (line.Time, line.IsVisible)),
            line => Assert.Equal((TimeSpan.FromSeconds(3.25), false), (line.Time, line.IsVisible)));

        await using var stream = await provider.OpenReadAsync(descriptor.ChartAsset);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public async Task PlayableChartRejectsAFileChangedAfterCatalogScan()
    {
        using var library = new TemporaryLibrary();
        const string path = "Game/song.tja";
        library.WriteText(path, "TITLE:Song\nBPM:120\nCOURSE:Oni\n#START\n1000,\n#END\n");
        var provider = new TjaCatalogProvider(library.Path);
        var contribution = await provider.ScanAsync(null, CancellationToken.None);
        var descriptor = Assert.Single(Assert.Single(contribution.Songs).Charts);
        library.WriteText(path, "TITLE:Changed\nBPM:120\nCOURSE:Oni\n#START\n2000,\n#END\n");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await provider.LoadChartAsync(descriptor.Key, descriptor.ChartAsset));

        Assert.Contains("rescan", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayableChartRetainsLongNotesWithoutChangingTapTiming()
    {
        using var library = new TemporaryLibrary();
        library.WriteText(
            "Game/song.tja",
            "TITLE:Roll\nBPM:120\nCOURSE:Oni\n#START\n1508,\n2639,\n#END\n");
        var provider = new TjaCatalogProvider(library.Path);
        var contribution = await provider.ScanAsync(null, CancellationToken.None);
        var descriptor = Assert.Single(Assert.Single(contribution.Songs).Charts);

        var chart = await provider.LoadChartAsync(descriptor.Key, descriptor.ChartAsset);

        Assert.Equal(TimeSpan.FromSeconds(4), chart.Duration);
        Assert.Equal(3, chart.LongNotes.Length);
        Assert.Equal(TimeSpan.FromSeconds(0.5), chart.LongNotes[0].StartTime);
        Assert.Equal(TimeSpan.FromSeconds(1.5), chart.LongNotes[0].EndTime);
        Assert.Equal(PlayableLongNoteKind.BigRoll, chart.LongNotes[1].Kind);
        Assert.Equal(TimeSpan.FromSeconds(4), chart.LongNotes[2].EndTime);
        Assert.Collection(
            chart.HitObjects,
            note => Assert.Equal((TimeSpan.Zero, PlayableNoteKind.Don), (note.StartTime, note.Kind)),
            note => Assert.Equal((TimeSpan.FromSeconds(2), PlayableNoteKind.Ka), (note.StartTime, note.Kind)),
            note => Assert.Equal((TimeSpan.FromSeconds(3), PlayableNoteKind.BigDon), (note.StartTime, note.Kind)));
    }

    [Fact]
    public async Task PlayableChartRetainsCourseScoreHeaders()
    {
        var chart = await loadSynthetic("1000,", "SCOREINIT:1200,1600\nSCOREDIFF:240\n");
        Assert.Equal(1200, chart.ScoreInit);
        Assert.Equal(240, chart.ScoreDiff);
    }

    [Fact]
    public async Task ScoreHeadersDoNotLeakBetweenCourses()
    {
        using var library = new TemporaryLibrary();
        library.WriteText("synthetic.tja", "TITLE:Synthetic\nBPM:120\nCOURSE:Easy\nSCOREINIT:900\nSCOREDIFF:90\n#START\n1000,\n#END\nCOURSE:Oni\n#START\n1000,\n#END\n");
        var provider = new TjaCatalogProvider(library.Path);
        var contribution = await provider.ScanAsync(null, CancellationToken.None);
        var charts = Assert.Single(contribution.Songs).Charts;
        var easy = await provider.LoadChartAsync(charts[0].Key, charts[0].ChartAsset);
        var oni = await provider.LoadChartAsync(charts[1].Key, charts[1].ChartAsset);
        Assert.Equal(900, easy.ScoreInit);
        Assert.Equal(90, easy.ScoreDiff);
        Assert.Null(oni.ScoreInit);
        Assert.Null(oni.ScoreDiff);
    }

    [Fact]
    public async Task CommandsWithinMeasuresPreserveSubdivisionsAndTheirExactPositions()
    {
        var chart = await loadSynthetic("10\n#BPMCHANGE 240\n#SCROLL 2\n#GOGOSTART\n1\n#DELAY 0.25\n2,\n#SECTION\n#LEVELHOLD\n#SENOTECHANGE 1\n#LYRIC synthetic\n1,");
        Assert.Equal([0d, 1d, 1.5d, 1.75d], chart.HitObjects.Select(n => n.StartTime.TotalSeconds));
        Assert.Equal(2.75, chart.Duration.TotalSeconds);
        Assert.Equal(TimeSpan.FromSeconds(1), chart.TimingPoints[1].Time);
        Assert.Equal(TimeSpan.FromSeconds(1), chart.ScrollPoints[1].Time);
        Assert.True(chart.EffectPoints[1].IsGoGo);
        Assert.Equal(TimeSpan.FromSeconds(1.75), chart.BarLines[1].Time);
    }

    [Fact]
    public async Task BranchesPlayNormalOnlyAndDoNotLeakTimingFromOtherRoutes()
    {
        var chart = await loadSynthetic("#SECTION\n#BRANCHSTART p,50,80\n#N\n1,\n#E\n#BPMCHANGE 999\n2,\n#M\n#DELAY 10\n3,\n#BRANCHEND\n4,");
        Assert.Equal(new[] { PlayableNoteKind.Don, PlayableNoteKind.BigKa }, chart.HitObjects.Select(n => n.Kind));
        Assert.Equal(TimeSpan.FromSeconds(2), chart.HitObjects[1].StartTime);
        Assert.Equal(TimeSpan.FromSeconds(4), chart.Duration);
        Assert.Single(chart.TimingPoints);
    }

    [Fact]
    public async Task ConsecutiveAndEmptyBranchesAndFallbackRoutesLoad()
    {
        var chart = await loadSynthetic("#BRANCHSTART p,0,0\n#BRANCHEND\n#BRANCHSTART p,50,80\n#N\n1,\n#E\n2,\n#BRANCHSTART p,-1,-1\n#M\n3,");
        Assert.Equal(new[] { PlayableNoteKind.Don, PlayableNoteKind.BigDon }, chart.HitObjects.Select(n => n.Kind));
        Assert.Equal(TimeSpan.FromSeconds(4), chart.Duration);
    }

    [Fact]
    public async Task LongNotesSpanMeasuresAndConsumeBalloonQuotas()
    {
        var chart = await loadSynthetic("50,\n08,\n7080,\n9090,\n6080,", "BALLOON:3,5\n");
        Assert.Empty(chart.HitObjects);
        Assert.Equal(4, chart.LongNotes.Length);
        Assert.Equal(TimeSpan.FromSeconds(3), chart.LongNotes[0].EndTime);
        Assert.Equal(3, chart.LongNotes[1].RequiredHits);
        Assert.Equal(5, chart.LongNotes[2].RequiredHits);
        Assert.Equal(TimeSpan.FromSeconds(7), chart.LongNotes[2].EndTime);
        Assert.Equal(PlayableLongNoteKind.BigRoll, chart.LongNotes[3].Kind);
    }

    [Theory]
    [InlineData("", 3)]
    [InlineData("BALLOONNOR:8\n", 8)]
    public async Task SkippedBranchesStillConsumeTheirGlobalBalloonEntries(string branchHeader, int expectedHits)
    {
        var chart = await loadSynthetic("#BRANCHSTART p,50,80\n#E\n7080,\n#N\n7080,\n#M\n7080,\n#BRANCHEND\n7080,", "BALLOON:2,3,4,5\n" + branchHeader);
        Assert.Equal(expectedHits, chart.LongNotes[0].RequiredHits);
        Assert.Equal(branchHeader.Length == 0 ? 5 : 2, chart.LongNotes[1].RequiredHits);
        Assert.Equal(TimeSpan.FromSeconds(4), chart.Duration);
    }

    [Fact]
    public async Task PartnerNotesUseSinglePlayerBigNoteSemantics()
    {
        var chart = await loadSynthetic("ABab,");
        Assert.Equal(new[] { PlayableNoteKind.BigDon, PlayableNoteKind.BigKa,
            PlayableNoteKind.BigDon, PlayableNoteKind.BigKa }, chart.HitObjects.Select(n => n.Kind));
    }

    [Fact]
    public async Task EmptyMeasuresAndTrailingCommandsRetainDuration()
    {
        var chart = await loadSynthetic("#MEASURE 3/4\n,\n#DELAY 0.5\n#BARLINEOFF\n,\n#DELAY 0.25");
        Assert.Equal(TimeSpan.FromSeconds(3.75), chart.Duration);
        Assert.False(chart.BarLines[1].IsVisible);
    }

    [Theory]
    [InlineData("#UNKNOWN\n1,", typeof(NotSupportedException))]
    [InlineData("C,", typeof(NotSupportedException))]
    [InlineData("1", typeof(InvalidDataException))]
    [InlineData("#BPMCHANGE 0\n1,", typeof(InvalidDataException))]
    [InlineData("#MEASURE 0/4\n1,", typeof(InvalidDataException))]
    [InlineData("#DELAY -1\n1,", typeof(InvalidDataException))]
    public async Task MalformedAndGimmickChartsStillReportAnExplicitFailure(string body, Type exception)
    {
        await Assert.ThrowsAsync(exception, async () => await loadSynthetic(body));
    }

    [Fact]
    public async Task LongNotesCountTowardsTheSafetyLimit()
    {
        using var library = new TemporaryLibrary();
        library.WriteText("limit.tja", "TITLE:Limit\nBPM:120\nCOURSE:Oni\n#START\n50805080,\n#END");
        var provider = new TjaCatalogProvider(library.Path, options: new TjaProviderOptions { MaximumNotes = 1 });
        var contribution = await provider.ScanAsync(null, CancellationToken.None);
        var descriptor = Assert.Single(Assert.Single(contribution.Songs).Charts);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await provider.LoadChartAsync(descriptor.Key, descriptor.ChartAsset));
    }

    private static async Task<PlayableChart> loadSynthetic(string body, string headers = "")
    {
        using var library = new TemporaryLibrary();
        library.WriteText("synthetic.tja", "TITLE:Synthetic\nBPM:120\nCOURSE:Oni\n" + headers + "#START\n" + body + "\n#END");
        var provider = new TjaCatalogProvider(library.Path);
        var contribution = await provider.ScanAsync(null, CancellationToken.None);
        var descriptor = Assert.Single(Assert.Single(contribution.Songs).Charts);
        return await provider.LoadChartAsync(descriptor.Key, descriptor.ChartAsset);
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
