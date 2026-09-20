namespace Waddamburo.Catalog.Tests;

public sealed class GlobalSongCatalogTests
{
    [Fact]
    public void NullProviderIsRejectedAtCompositionBoundary()
    {
        Assert.Throws<ArgumentException>(() => new GlobalSongCatalog([null!]));
    }

    [Fact]
    public async Task FourSourceKindsPublishOneDeterministicSnapshot()
    {
        var providers = new[]
        {
            provider("z-osu", SongSourceKind.OsuLazer, "osu-song"),
            provider("a-stock", SongSourceKind.Stock, "stock-song"),
            provider("n-nijiiro", SongSourceKind.Nijiiro, "nijiiro-song"),
            provider("t-tja", SongSourceKind.Tja, "tja-song"),
        };
        using var catalog = new GlobalSongCatalog(providers);

        var snapshot = await catalog.RefreshAsync();

        Assert.Equal(1, snapshot.Revision);
        Assert.Equal(4, snapshot.Songs.Count);
        Assert.Equal(4, snapshot.Categories.Length);
        Assert.Equal(
            [SongSourceKind.Stock, SongSourceKind.Nijiiro, SongSourceKind.Tja, SongSourceKind.OsuLazer],
            snapshot.Providers.Select(static status => status.Source));
        Assert.All(snapshot.Providers, static status => Assert.True(status.Succeeded));
        Assert.Same(snapshot, catalog.Current);
    }

    [Fact]
    public async Task FailedProviderDoesNotDiscardHealthyContributions()
    {
        var good = provider("stock", SongSourceKind.Stock, "song");
        var failed = new DelegateProvider(
            "tja",
            SongSourceKind.Tja,
            (_, _) => throw new IOException("synthetic scan failure"));
        using var catalog = new GlobalSongCatalog([good, failed]);

        var snapshot = await catalog.RefreshAsync();

        Assert.Single(snapshot.Songs);
        Assert.True(snapshot.Providers.Single(status => status.Provider == good.Id).Succeeded);
        Assert.False(snapshot.Providers.Single(status => status.Provider == failed.Id).Succeeded);
        var diagnostic = Assert.Single(snapshot.Diagnostics);
        Assert.Equal("CATALOG_PROVIDER_FAILED", diagnostic.Code);
    }

    [Fact]
    public async Task StableKeyCollisionRejectsTheLaterProviderContribution()
    {
        var first = provider("a-first", SongSourceKind.Tja, "same-song");
        var second = provider("b-second", SongSourceKind.Tja, "same-song");
        using var catalog = new GlobalSongCatalog([second, first]);

        var snapshot = await catalog.RefreshAsync();

        Assert.Single(snapshot.Songs);
        Assert.True(snapshot.Providers.Single(status => status.Provider == first.Id).Succeeded);
        Assert.False(snapshot.Providers.Single(status => status.Provider == second.Id).Succeeded);
        Assert.Contains(snapshot.Diagnostics, diagnostic =>
            diagnostic.Provider == second.Id && diagnostic.Code == "CATALOG_KEY_COLLISION");
    }

    [Fact]
    public async Task CancelledRefreshDoesNotReplaceThePublishedSnapshot()
    {
        var provider = GlobalSongCatalogTests.provider("stock", SongSourceKind.Stock, "first");
        using var catalog = new GlobalSongCatalog([provider]);
        var first = await catalog.RefreshAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await catalog.RefreshAsync(cancellationToken: cancellation.Token));

        Assert.Same(first, catalog.Current);
        Assert.Equal(1, catalog.Current.Revision);
    }

    [Fact]
    public void ContributionRejectsCategoriesThatReferenceForeignSongs()
    {
        var provider = new CatalogProviderId("stock");
        Assert.Throws<ArgumentException>(() => new SongCatalogContribution(
            provider,
            SongSourceKind.Stock,
            [song(provider, SongSourceKind.Stock, "one")],
            [new SongCategoryDescriptor(
                new CategoryKey(SongSourceKind.Stock, "all"),
                "All",
                0,
                [new SongKey(SongSourceKind.Stock, "missing")])]));
    }

    [Fact]
    public void ContributionRejectsAssetsOwnedByAnotherProvider()
    {
        var provider = new CatalogProviderId("stock");
        var foreign = new CatalogProviderId("foreign");
        var key = new SongKey(SongSourceKind.Stock, "one");

        Assert.Throws<ArgumentException>(() => new SongCatalogContribution(
            provider,
            SongSourceKind.Stock,
            [new SongDescriptor(
                key,
                new SongTitle("One"),
                null,
                [new SongChartDescriptor(
                    new ChartKey(key, "oni"),
                    "Oni",
                    new CatalogAssetKey(foreign, "chart"))])],
            []));
    }

    private static DelegateProvider provider(string id, SongSourceKind source, string songId)
    {
        var providerId = new CatalogProviderId(id);
        return new DelegateProvider(
            id,
            source,
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var descriptor = song(providerId, source, songId);
                return ValueTask.FromResult(new SongCatalogContribution(
                    providerId,
                    source,
                    [descriptor],
                    [new SongCategoryDescriptor(
                        new CategoryKey(source, "all"),
                        $"{source} songs",
                        (int)source,
                        [descriptor.Key])]));
            });
    }

    private static SongDescriptor song(CatalogProviderId provider, SongSourceKind source, string stableId)
    {
        var key = new SongKey(source, stableId);
        return new SongDescriptor(
            key,
            new SongTitle(stableId),
            null,
            [new SongChartDescriptor(
                new ChartKey(key, "oni"),
                "Oni",
                new CatalogAssetKey(provider, $"chart:{stableId}"),
                TaikoCourse.Oni)]);
    }

    private sealed class DelegateProvider : ISongCatalogProvider
    {
        private readonly Func<IProgress<CatalogScanProgress>?, CancellationToken, ValueTask<SongCatalogContribution>> _scan;

        public DelegateProvider(
            string id,
            SongSourceKind source,
            Func<IProgress<CatalogScanProgress>?, CancellationToken, ValueTask<SongCatalogContribution>> scan)
        {
            Id = new CatalogProviderId(id);
            Source = source;
            _scan = scan;
        }

        public CatalogProviderId Id { get; }

        public SongSourceKind Source { get; }

        public ValueTask<SongCatalogContribution> ScanAsync(
            IProgress<CatalogScanProgress>? progress,
            CancellationToken cancellationToken) => _scan(progress, cancellationToken);
    }
}
