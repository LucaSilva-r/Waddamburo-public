using Waddamburo.Catalog;

namespace Waddamburo.App.Gameplay;

/// <summary>Sends each asset and chart request to the provider that owns it.</summary>
internal sealed class CatalogAssetRouter(IEnumerable<object> providers) : ICatalogAssetResolver, IPlayableChartProvider
{
    private readonly Dictionary<CatalogProviderId, ICatalogAssetResolver> _resolvers =
        providers.OfType<ICatalogAssetResolver>().ToDictionary(static provider => provider.Id);
    private readonly Dictionary<CatalogProviderId, IPlayableChartProvider> _charts =
        providers.OfType<IPlayableChartProvider>().ToDictionary(static provider => provider.Id);

    // The effects editor's TJA opened from outside the libraries: one slot, replaced by the next one opened.
    private volatile EditorSource? _editor;
    private sealed record EditorSource(ICatalogAssetResolver Assets, IPlayableChartProvider Charts);

    public CatalogProviderId Id { get; } = new("router");

    /// <summary>Resolves the effects editor's outside TJA (its audio, chart and modchart paths) in place of the last one.</summary>
    public void UseEditorSource(ICatalogAssetResolver assets, IPlayableChartProvider charts)
    {
        if (assets.Id != charts.Id) throw new ArgumentException("Asset and chart providers must match.");
        _editor = new(assets, charts);
    }

    public bool Resolves(CatalogAssetKey asset) => resolver(asset) is not null;

    public string? ModchartPath(CatalogAssetKey asset) => (charts(asset) as IChartModFileProvider)?.ModchartPath(asset);

    public string? ChartPath(CatalogAssetKey asset) => (charts(asset) as IChartModFileProvider)?.ChartPath(asset);

    public bool UploadBlocked(CatalogAssetKey asset)
    {
        try
        {
            return (charts(asset) as IChartUploadPolicyProvider)?.UploadBlocked(asset) ?? false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true; // A privacy check that cannot resolve its chart must not permit an upload.
        }
    }

    public ValueTask<Stream> OpenReadAsync(CatalogAssetKey asset, CancellationToken cancellationToken = default) =>
        resolver(asset) is { } owner
            ? owner.OpenReadAsync(asset, cancellationToken)
            : throw new ArgumentException($"No provider '{asset.Provider}' resolves assets.", nameof(asset));

    public ValueTask<PlayableChart> LoadChartAsync(ChartKey chart, CatalogAssetKey asset, CancellationToken cancellationToken = default) =>
        charts(asset) is { } owner
            ? owner.LoadChartAsync(chart, asset, cancellationToken)
            : throw new ArgumentException($"No provider '{asset.Provider}' loads charts.", nameof(asset));

    private ICatalogAssetResolver? resolver(CatalogAssetKey asset) =>
        _editor is { } editor && editor.Assets.Id == asset.Provider ? editor.Assets : _resolvers.GetValueOrDefault(asset.Provider);

    private IPlayableChartProvider? charts(CatalogAssetKey asset) =>
        _editor is { } editor && editor.Charts.Id == asset.Provider ? editor.Charts : _charts.GetValueOrDefault(asset.Provider);
}
