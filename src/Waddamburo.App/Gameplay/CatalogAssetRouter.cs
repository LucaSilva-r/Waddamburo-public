using Waddamburo.Catalog;

namespace Waddamburo.App.Gameplay;

/// <summary>Sends each asset and chart request to the provider that owns it.</summary>
internal sealed class CatalogAssetRouter(IEnumerable<object> providers) : ICatalogAssetResolver, IPlayableChartProvider
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<CatalogProviderId, ICatalogAssetResolver> _resolvers =
        new(providers.OfType<ICatalogAssetResolver>().ToDictionary(static provider => provider.Id));
    private readonly System.Collections.Concurrent.ConcurrentDictionary<CatalogProviderId, IPlayableChartProvider> _charts =
        new(providers.OfType<IPlayableChartProvider>().ToDictionary(static provider => provider.Id));

    public CatalogProviderId Id { get; } = new("router");

    public bool Resolves(CatalogAssetKey asset) => _resolvers.ContainsKey(asset.Provider);

    public void Register(ICatalogAssetResolver resolver, IPlayableChartProvider charts)
    {
        if (resolver.Id != charts.Id) throw new ArgumentException("Asset and chart providers must match.");
        _resolvers[resolver.Id] = resolver; _charts[charts.Id] = charts;
    }

    public string? ModchartPath(CatalogAssetKey asset) => _charts.TryGetValue(asset.Provider, out var provider)
        ? (provider as IChartModFileProvider)?.ModchartPath(asset) : null;

    public string? ChartPath(CatalogAssetKey asset) => _charts.TryGetValue(asset.Provider, out var provider)
        ? (provider as IChartModFileProvider)?.ChartPath(asset) : null;

    public ValueTask<Stream> OpenReadAsync(CatalogAssetKey asset, CancellationToken cancellationToken = default) =>
        _resolvers.TryGetValue(asset.Provider, out var resolver)
            ? resolver.OpenReadAsync(asset, cancellationToken)
            : throw new ArgumentException($"No provider '{asset.Provider}' resolves assets.", nameof(asset));

    public ValueTask<PlayableChart> LoadChartAsync(ChartKey chart, CatalogAssetKey asset, CancellationToken cancellationToken = default) =>
        _charts.TryGetValue(asset.Provider, out var provider)
            ? provider.LoadChartAsync(chart, asset, cancellationToken)
            : throw new ArgumentException($"No provider '{asset.Provider}' loads charts.", nameof(asset));
}
