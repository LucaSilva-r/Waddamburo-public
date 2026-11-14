namespace Waddamburo.Catalog.Tests;

public sealed class MergedCatalogProviderTests
{
    private static readonly CatalogProviderId Stock = new("stock");

    [Fact]
    public async Task TheNewestInstallsVersionOfASongWinsAndOlderOnesAddWhatItLacks()
    {
        var green = new Install("green", ("pop", ["shared", "green-only"]));
        var blue = new Install("blue", ("pop", ["shared", "blue-only"]), ("anime", ["shared", "anime-only"]));
        var merged = new MergedCatalogProvider([green, blue]);

        var contribution = await merged.ScanAsync(null, CancellationToken.None);

        Assert.Equal(["shared", "green-only", "blue-only", "anime-only"], contribution.Songs.Select(static song => song.Key.StableId));
        Assert.Equal("green", contribution.Songs[0].Title.Primary);
        Assert.Equal(["shared", "green-only", "blue-only"], contribution.Categories[0].Songs.Select(static song => song.StableId));
        // The shared song stays in the newest install's folder only.
        Assert.Equal(["anime-only"], contribution.Categories[1].Songs.Select(static song => song.StableId));

        Assert.Equal("green", await read(merged, "chart:shared"));
        Assert.Equal("blue", await read(merged, "chart:blue-only"));
    }

    [Fact]
    public async Task ProgressIncludesEveryInstallBeforeReportingCompletion()
    {
        var progress = new Reports();
        await new MergedCatalogProvider([new Install("new"), new Install("old")]).ScanAsync(progress, default);
        Assert.Equal([0, 500, 1000, 1000, 1000, 1500, 2000, 2000], progress.Values.Select(value => value.Completed));
        Assert.All(progress.Values, value => Assert.Equal(2000, value.Total));
    }

    private sealed class Reports : IProgress<CatalogScanProgress>
    {
        public List<CatalogScanProgress> Values { get; } = [];
        public void Report(CatalogScanProgress value) => Values.Add(value);
    }

    private static async Task<string> read(MergedCatalogProvider merged, string asset)
    {
        using var reader = new StreamReader(await merged.OpenReadAsync(new CatalogAssetKey(Stock, asset)));
        return await reader.ReadToEndAsync();
    }

    // One release's data: its folders' songs, each titled and with assets reading as the release's name.
    private sealed class Install(string name, params (string Genre, string[] Songs)[] folders)
        : ISongCatalogProvider, ICatalogAssetResolver, IPlayableChartProvider
    {
        public CatalogProviderId Id => Stock;

        public SongSourceKind Source => SongSourceKind.Stock;

        public ValueTask<SongCatalogContribution> ScanAsync(IProgress<CatalogScanProgress>? progress, CancellationToken cancellationToken)
        {
            progress?.Report(new CatalogScanProgress(Id, "discover", 0, null));
            progress?.Report(new CatalogScanProgress(Id, "inspect", 1, 2));
            progress?.Report(new CatalogScanProgress(Id, "complete", 2, 2));
            var songs = folders.SelectMany(static folder => folder.Songs).Distinct().Select(id =>
            {
                var key = new SongKey(SongSourceKind.Stock, id);
                return new SongDescriptor(key, new SongTitle(name), null,
                    [new SongChartDescriptor(new ChartKey(key, "oni"), "Oni", new CatalogAssetKey(Stock, $"chart:{id}"), TaikoCourse.Oni)]);
            });
            return ValueTask.FromResult(new SongCatalogContribution(Stock, SongSourceKind.Stock, songs,
                folders.Select((folder, index) => new SongCategoryDescriptor(new CategoryKey(SongSourceKind.Stock, folder.Genre), folder.Genre,
                    index, folder.Songs.Select(static id => new SongKey(SongSourceKind.Stock, id))))));
        }

        public ValueTask<Stream> OpenReadAsync(CatalogAssetKey asset, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(name)));

        public ValueTask<PlayableChart> LoadChartAsync(ChartKey chart, CatalogAssetKey asset, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
