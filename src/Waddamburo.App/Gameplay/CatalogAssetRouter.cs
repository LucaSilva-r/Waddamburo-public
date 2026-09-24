using Waddamburo.Catalog;

/// <summary>Sends each asset and chart request to the provider that owns it.</summary>
internal sealed class CatalogAssetRouter(IEnumerable<object> providers) : ICatalogAssetResolver, IPlayableChartProvider
{
    private readonly Dictionary<CatalogProviderId, ICatalogAssetResolver> _resolvers =
        providers.OfType<ICatalogAssetResolver>().ToDictionary(static provider => provider.Id);
    private readonly Dictionary<CatalogProviderId, IPlayableChartProvider> _charts =
        providers.OfType<IPlayableChartProvider>().ToDictionary(static provider => provider.Id);

    public CatalogProviderId Id { get; } = new("router");

    public bool Resolves(CatalogAssetKey asset) => _resolvers.ContainsKey(asset.Provider);

    public ValueTask<Stream> OpenReadAsync(CatalogAssetKey asset, CancellationToken cancellationToken = default) =>
        _resolvers.TryGetValue(asset.Provider, out var resolver)
            ? resolver.OpenReadAsync(asset, cancellationToken)
            : throw new ArgumentException($"No provider '{asset.Provider}' resolves assets.", nameof(asset));

    public ValueTask<PlayableChart> LoadChartAsync(ChartKey chart, CatalogAssetKey asset, CancellationToken cancellationToken = default) =>
        _charts.TryGetValue(asset.Provider, out var provider)
            ? provider.LoadChartAsync(chart, asset, cancellationToken)
            : throw new ArgumentException($"No provider '{asset.Provider}' loads charts.", nameof(asset));
}
