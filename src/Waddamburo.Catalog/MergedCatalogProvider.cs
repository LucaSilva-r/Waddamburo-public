namespace Waddamburo.Catalog;

/// <summary>
/// Several installs of one kind of song (the game's own data from different releases, several Nijiiro dumps) listed as
/// one library. The sources come newest first, and each share this provider's id: a song several of them have comes
/// from the first (the newest version of it), and its assets are read from that source. Categories with the same key
/// are one folder, in the order the newest source lists them, then the songs only older ones have.
/// </summary>
public sealed class MergedCatalogProvider : ISongCatalogProvider, ICatalogAssetResolver, IPlayableChartProvider, IChartModFileProvider, IChartUploadPolicyProvider
{
    private readonly ISongCatalogProvider[] _sources;
    // Each listed song's assets, by stable id, to the source it was taken from.
    private Dictionary<string, ISongCatalogProvider> _owners = new(StringComparer.Ordinal);

    /// <param name="sources">Newest first; every one with this provider's id and source kind.</param>
    public MergedCatalogProvider(IReadOnlyList<ISongCatalogProvider> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
            throw new ArgumentException("A merged library needs a source.", nameof(sources));
        if (sources.Any(source => source.Id != sources[0].Id || source.Source != sources[0].Source
                || source is not (ICatalogAssetResolver and IPlayableChartProvider)))
            throw new ArgumentException("Merged sources share one id and source kind, and resolve their own assets.", nameof(sources));
        _sources = [.. sources];
        Id = sources[0].Id;
        Source = sources[0].Source;
    }

    public CatalogProviderId Id { get; }

    public SongSourceKind Source { get; }

    public IReadOnlyList<ISongCatalogProvider> Sources => _sources;

    public async ValueTask<SongCatalogContribution> ScanAsync(IProgress<CatalogScanProgress>? progress, CancellationToken cancellationToken)
    {
        var songs = new List<SongDescriptor>();
        var owners = new Dictionary<string, ISongCatalogProvider>(StringComparer.Ordinal);
        var listed = new HashSet<SongKey>();
        var placed = new HashSet<SongKey>();
        var categories = new List<(SongCategoryDescriptor First, List<SongKey> Songs)>();
        var diagnostics = new List<CatalogDiagnostic>();
        for (var sourceIndex = 0; sourceIndex < _sources.Length; sourceIndex++)
        {
            var source = _sources[sourceIndex];
            var part = sourceIndex;
            var aggregate = progress is null ? null : new SourceProgress(progress, Id, part, _sources.Length);
            SongCatalogContribution contribution;
            try
            {
                contribution = await source.ScanAsync(aggregate, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
                or System.Xml.XmlException or System.Text.Json.JsonException)
            {
                // One unreadable install leaves the others.
                diagnostics.Add(new CatalogDiagnostic(CatalogDiagnosticSeverity.Warning, "MERGED_SOURCE_FAILED", exception.Message, Id));
                progress?.Report(new CatalogScanProgress(Id, "scan", (part + 1) * 1000, _sources.Length * 1000));
                continue;
            }
            progress?.Report(new CatalogScanProgress(Id, "scan", (part + 1) * 1000, _sources.Length * 1000));
            diagnostics.AddRange(contribution.Diagnostics);
            foreach (var song in contribution.Songs)
            {
                if (!listed.Add(song.Key))
                    continue;
                songs.Add(song);
                foreach (var asset in assets(song))
                    owners[asset.StableId] = source;
            }
            foreach (var category in contribution.Categories)
            {
                var index = categories.FindIndex(existing => existing.First.Key == category.Key);
                if (index < 0)
                    categories.Add((category, []));
                // A song an older install files elsewhere stays where the newest one put it.
                categories[index < 0 ? ^1 : index].Songs.AddRange(category.Songs.Where(placed.Add));
            }
        }
        if (songs.Count == 0 && diagnostics.Any(static diagnostic => diagnostic.Code == "MERGED_SOURCE_FAILED"))
            throw new InvalidDataException(diagnostics.First(static diagnostic => diagnostic.Code == "MERGED_SOURCE_FAILED").Message);
        _owners = owners;
        return new SongCatalogContribution(Id, Source, songs,
            categories.Where(static category => category.Songs.Count > 0).Select(static category =>
                new SongCategoryDescriptor(category.First.Key, category.First.Name, category.First.SortOrder, category.Songs)
                {
                    Genre = category.First.Genre,
                    Colour = category.First.Colour,
                    OutlineColour = category.First.OutlineColour,
                    Description = category.First.Description,
                    SpineImage = category.First.SpineImage,
                    HeaderImage = category.First.HeaderImage,
                    BoxImage = category.First.BoxImage,
                }),
            diagnostics);
    }

    private sealed class SourceProgress(IProgress<CatalogScanProgress> target, CatalogProviderId id, int part, int count)
        : IProgress<CatalogScanProgress>
    {
        public void Report(CatalogScanProgress value)
        {
            var fraction = value.Total is > 0 ? Math.Clamp((double)value.Completed / value.Total.Value, 0, 1) : 0;
            target.Report(new CatalogScanProgress(id, value.Stage, part * 1000 + (int)(fraction * 1000), count * 1000));
        }
    }

    public ValueTask<Stream> OpenReadAsync(CatalogAssetKey asset, CancellationToken cancellationToken = default) =>
        ((ICatalogAssetResolver)owner(asset)).OpenReadAsync(asset, cancellationToken);

    public ValueTask<PlayableChart> LoadChartAsync(ChartKey chart, CatalogAssetKey asset, CancellationToken cancellationToken = default) =>
        ((IPlayableChartProvider)owner(asset)).LoadChartAsync(chart, asset, cancellationToken);

    public string? ModchartPath(CatalogAssetKey chartAsset) =>
        (owner(chartAsset) as IChartModFileProvider)?.ModchartPath(chartAsset);

    public string? ChartPath(CatalogAssetKey chartAsset) =>
        (owner(chartAsset) as IChartModFileProvider)?.ChartPath(chartAsset);

    public bool UploadBlocked(CatalogAssetKey chartAsset) =>
        (owner(chartAsset) as IChartUploadPolicyProvider)?.UploadBlocked(chartAsset) ?? false;

    private ISongCatalogProvider owner(CatalogAssetKey asset) => _owners.TryGetValue(asset.StableId, out var source)
        ? source : throw new ArgumentException($"No merged source has '{asset.StableId}'.", nameof(asset));

    private static IEnumerable<CatalogAssetKey> assets(SongDescriptor song) =>
    [
        .. song.AudioAsset is { } audio ? [audio] : Array.Empty<CatalogAssetKey>(),
        .. song.WaiwaiComposition is { } composition ? [composition] : Array.Empty<CatalogAssetKey>(),
        .. song.Charts.SelectMany(static chart => chart.DuetChartAssets.Prepend(chart.ChartAsset)),
    ];
}
