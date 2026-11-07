using Waddamburo.Catalog;

namespace Waddamburo.App.Gameplay;

/// <summary>
/// Sends each asset and chart request to the provider that owns it. A player's own modchart for a song
/// (<paramref name="modcharts"/>/&lt;source&gt;/&lt;song id&gt;.wddmod, <see cref="ChartModFile"/>) goes over the chart's: it only
/// changes what is drawn, so the chart and its scores stay the same whatever song it is.
/// </summary>
internal sealed class CatalogAssetRouter(IEnumerable<object> providers, string? modcharts = null) : ICatalogAssetResolver, IPlayableChartProvider
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

    public async ValueTask<PlayableChart> LoadChartAsync(ChartKey chart, CatalogAssetKey asset, CancellationToken cancellationToken = default)
    {
        if (!_charts.TryGetValue(asset.Provider, out var provider))
            throw new ArgumentException($"No provider '{asset.Provider}' loads charts.", nameof(asset));
        var loaded = await provider.LoadChartAsync(chart, asset, cancellationToken).ConfigureAwait(false);
        return ModchartPath(chart.Song) is { } path && File.Exists(path) && own(path, chart) is { } cues
            ? ChartModFile.Apply(loaded, cues) : loaded;
    }

    /// <summary>Where a player's own modchart for <paramref name="song"/> goes; null without a modchart folder.</summary>
    public string? ModchartPath(SongKey song) => modcharts is null ? null
        : Path.Combine(modcharts, song.Source.ToString().ToLowerInvariant(),
            string.Concat(song.StableId.Select(static c => Path.GetInvalidFileNameChars().Contains(c) || c is ':' ? '_' : c)) + ChartModFile.Extension);

    // A broken file is reported and the chart plays with its own effects. ponytail: printed, no UI list yet.
    private static IReadOnlyList<ChartModCue>? own(string path, ChartKey chart)
    {
        try
        {
            if (new FileInfo(path).Length > ChartModFile.MaxBytes)
                throw new InvalidDataException("The modchart file is too large.");
            return ChartModFile.Read(File.ReadAllBytes(path), ChartModFile.CourseOf(chart));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Modchart {path} not used: {exception.Message}");
            return null;
        }
    }
}
