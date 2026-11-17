using System.Collections.Immutable;
using Waddamburo.Catalog;

namespace Waddamburo.App.Sync;

/// <summary>
/// One source's received songs (<see cref="SongStore"/>), as the sender's game listed them: same song keys, chart ids
/// and categories. Charts load from their canonical bytes and drawing data, music is the sender's file.
/// </summary>
public sealed class StoredSongsProvider(SongStore store, SongSourceKind source, IReadOnlyList<StoredSong> rows, CatalogProviderId? owner = null)
    : ISongCatalogProvider, ICatalogAssetResolver, IPlayableChartProvider, IChartUploadPolicyProvider
{
    /// <summary>Its assets' keys start with this: under a library's id (<see cref="OverlayCatalogProvider"/>), they are told apart by it.</summary>
    public const string Prefix = "received|";

    public CatalogProviderId Id { get; } = owner ?? new($"received-{source.ToString().ToLowerInvariant()}");

    /// <summary>The same songs with their assets owned by <paramref name="library"/> (which hands them back).</summary>
    public StoredSongsProvider OwnedBy(CatalogProviderId library) => new(store, source, rows, library);

    public SongSourceKind Source => source;

    /// <summary>A provider per source among the store's rows.</summary>
    public static List<StoredSongsProvider> For(SongStore store) =>
        [.. store.Load().GroupBy(static row => row.Source).Select(group => new StoredSongsProvider(store, group.Key, [.. group]))];

    public ValueTask<SongCatalogContribution> ScanAsync(IProgress<CatalogScanProgress>? progress, CancellationToken cancellationToken)
    {
        var songs = rows.Select(song).ToImmutableArray();
        var categories = rows.GroupBy(static row => row.Category.Id)
            .Select(group =>
            {
                var category = group.First().Category;
                return new SongCategoryDescriptor(new CategoryKey(source, category.Id), category.Name, category.Order,
                    group.Select(row => new SongKey(source, row.SongId)))
                {
                    Genre = category.Genre,
                    Colour = category.Colour,
                    OutlineColour = category.OutlineColour,
                    Description = [.. category.Description ?? []],
                };
            }).ToImmutableArray();
        return ValueTask.FromResult(new SongCatalogContribution(Id, source, songs, categories, []));
    }

    private SongDescriptor song(StoredSong row)
    {
        var key = new SongKey(source, row.SongId);
        var charts = row.Charts.Select(chart => new SongChartDescriptor(new ChartKey(key, chart.Id), chart.Name,
            asset(chart.Chart, chart), chart.Course, chart.Level,
            chart.Duets.Length == 0 ? null : chart.Duets.Zip(chart.DuetDrawings).Select(duet => new CatalogAssetKey(Id,
                $"{Prefix}chart:{duet.First}:{duet.Second}:{chart.DurationMs}:{chart.ShinuchiBase}:{(chart.NoUpload ? 1 : 0)}")))
        {
            HasBranches = chart.HasBranches,
            OnlineId = chart.OnlineId,
        });
        return new SongDescriptor(key, new SongTitle(row.Title, row.TitleJa, row.TitleEn), row.Artist, charts,
            blob(row.Audio), row.PreviewMs is { } preview ? TimeSpan.FromMilliseconds(preview) : null, row.Subtitle)
        {
            EnglishSubtitle = row.EnglishSubtitle,
            Creator = row.Creator,
            Bpm = row.Bpm,
            Length = row.LengthMs is { } length ? TimeSpan.FromMilliseconds(length) : null,
            WaiwaiComposition = row.Composition is { } composition ? blob(composition) : null,
            ScenePresets = [.. row.ScenePresets ?? []],
            DemoPlay = row.DemoPlay,
            OnlineSetId = row.OnlineSetId,
        };
    }

    // blob:<hash> is a file as it is; chart:<chart>:<drawing>:<length ms>:<shin-uchi base>:<no upload> a chart with
    // what its canonical bytes leave out.
    private CatalogAssetKey blob(string hash) => new(Id, $"{Prefix}blob:{hash}");

    private CatalogAssetKey asset(string hash, StoredChart chart) =>
        new(Id, $"{Prefix}chart:{hash}:{chart.Drawing}:{chart.DurationMs}:{chart.ShinuchiBase}:{(chart.NoUpload ? 1 : 0)}");

    private static string[] parts(CatalogAssetKey asset) =>
        asset.StableId.StartsWith(Prefix, StringComparison.Ordinal) ? asset.StableId[Prefix.Length..].Split(':') : [];

    public bool UploadBlocked(CatalogAssetKey chartAsset) => parts(chartAsset) is not ["chart", _, _, _, _, "0"];

    public ValueTask<Stream> OpenReadAsync(CatalogAssetKey asset, CancellationToken cancellationToken = default) =>
        asset.Provider == Id && parts(asset) is ["blob", var hash] && store.Has(hash)
            ? ValueTask.FromResult<Stream>(File.OpenRead(store.BlobPath(hash)))
            : throw new ArgumentException($"No received file '{asset.StableId}'.", nameof(asset));

    public ValueTask<PlayableChart> LoadChartAsync(ChartKey chart, CatalogAssetKey asset, CancellationToken cancellationToken = default)
    {
        if (asset.Provider != Id || parts(asset) is not ["chart", var hash, var drawing, var duration, var shinuchi, _]
            || !store.Has(hash) || !store.Has(drawing))
            throw new ArgumentException($"No received chart '{asset.StableId}'.", nameof(asset));
        var (canonical, _) = ChartHash.Deserialize(File.ReadAllBytes(store.BlobPath(hash)), source);
        return ValueTask.FromResult(ChartDrawing.Apply(canonical, File.ReadAllBytes(store.BlobPath(drawing))) with
        {
            Key = chart,
            Duration = TimeSpan.FromMilliseconds(long.Parse(duration, System.Globalization.CultureInfo.InvariantCulture)),
            ShinuchiBase = shinuchi.Length == 0 ? null : int.Parse(shinuchi, System.Globalization.CultureInfo.InvariantCulture),
        });
    }
}

/// <summary>
/// A library with the received songs of its source joined in: its own songs first (a received song it has is left
/// out), received ones added to the same categories (by key) or to their own. It owns every asset its songs use (the
/// catalog requires it): received ones carry <see cref="StoredSongsProvider.Prefix"/> and go to the store, the rest to
/// the library, as do its modchart and upload-privacy answers.
/// </summary>
public sealed class OverlayCatalogProvider(ISongCatalogProvider local, StoredSongsProvider received)
    : ISongCatalogProvider, ICatalogAssetResolver, IPlayableChartProvider, IChartModFileProvider, IChartUploadPolicyProvider
{
    private readonly StoredSongsProvider _received = received.OwnedBy(local.Id);

    public CatalogProviderId Id => local.Id;

    public SongSourceKind Source => local.Source;

    private static bool isReceived(CatalogAssetKey asset) => asset.StableId.StartsWith(StoredSongsProvider.Prefix, StringComparison.Ordinal);

    public ValueTask<Stream> OpenReadAsync(CatalogAssetKey asset, CancellationToken cancellationToken = default) =>
        isReceived(asset) ? _received.OpenReadAsync(asset, cancellationToken) : ((ICatalogAssetResolver)local).OpenReadAsync(asset, cancellationToken);

    public ValueTask<PlayableChart> LoadChartAsync(ChartKey chart, CatalogAssetKey asset, CancellationToken cancellationToken = default) =>
        isReceived(asset) ? _received.LoadChartAsync(chart, asset, cancellationToken) : ((IPlayableChartProvider)local).LoadChartAsync(chart, asset, cancellationToken);

    public string? ModchartPath(CatalogAssetKey chartAsset) => isReceived(chartAsset) ? null : (local as IChartModFileProvider)?.ModchartPath(chartAsset);

    public string? ChartPath(CatalogAssetKey chartAsset) => isReceived(chartAsset) ? null : (local as IChartModFileProvider)?.ChartPath(chartAsset);

    public bool UploadBlocked(CatalogAssetKey chartAsset) =>
        isReceived(chartAsset) ? _received.UploadBlocked(chartAsset) : (local as IChartUploadPolicyProvider)?.UploadBlocked(chartAsset) ?? false;

    public async ValueTask<SongCatalogContribution> ScanAsync(IProgress<CatalogScanProgress>? progress, CancellationToken cancellationToken)
    {
        var mine = await local.ScanAsync(progress, cancellationToken).ConfigureAwait(false);
        var theirs = await _received.ScanAsync(null, cancellationToken).ConfigureAwait(false);        var known = mine.Songs.Select(static song => song.Key).ToHashSet();
        var added = theirs.Songs.Where(song => !known.Contains(song.Key)).ToList();
        var addedKeys = added.Select(static song => song.Key).ToHashSet();
        var categories = mine.Categories.ToList();
        foreach (var category in theirs.Categories)
        {
            var songs = category.Songs.Where(addedKeys.Contains).ToList();
            if (songs.Count == 0)
                continue;
            var index = categories.FindIndex(existing => existing.Key == category.Key);
            if (index < 0)
                categories.Add(new SongCategoryDescriptor(new CategoryKey(Source, category.Key.StableId), category.Name, category.SortOrder, songs)
                {
                    Genre = category.Genre, Colour = category.Colour, OutlineColour = category.OutlineColour, Description = category.Description,
                });
            else
                categories[index] = new SongCategoryDescriptor(categories[index].Key, categories[index].Name, categories[index].SortOrder,
                    categories[index].Songs.Concat(songs))
                {
                    Genre = categories[index].Genre, Colour = categories[index].Colour,
                    OutlineColour = categories[index].OutlineColour, Description = categories[index].Description,
                };
        }
        return new SongCatalogContribution(Id, Source, [.. mine.Songs, .. added], [.. categories], mine.Diagnostics);
    }
}
