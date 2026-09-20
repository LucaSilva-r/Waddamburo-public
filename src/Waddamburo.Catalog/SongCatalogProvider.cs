using System.Collections.Immutable;

namespace Waddamburo.Catalog;

public interface ISongCatalogProvider
{
    CatalogProviderId Id { get; }

    SongSourceKind Source { get; }

    ValueTask<SongCatalogContribution> ScanAsync(
        IProgress<CatalogScanProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Resolves opaque assets owned by one catalog provider.</summary>
public interface ICatalogAssetResolver
{
    CatalogProviderId Id { get; }

    ValueTask<Stream> OpenReadAsync(CatalogAssetKey asset, CancellationToken cancellationToken = default);
}

public sealed record CatalogScanProgress(
    CatalogProviderId Provider,
    string Stage,
    int Completed,
    int? Total);

public sealed class SongCatalogContribution
{
    public SongCatalogContribution(
        CatalogProviderId provider,
        SongSourceKind source,
        IEnumerable<SongDescriptor> songs,
        IEnumerable<SongCategoryDescriptor> categories,
        IEnumerable<CatalogDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(songs);
        ArgumentNullException.ThrowIfNull(categories);
        var materializedSongs = songs.ToImmutableArray();
        var materializedCategories = categories.ToImmutableArray();
        var materializedDiagnostics = diagnostics?.ToImmutableArray() ?? [];
        if (materializedSongs.Any(song => song is null || song.Key.Source != source))
            throw new ArgumentException("Every song must use the provider's source kind.", nameof(songs));
        if (materializedSongs.Select(static song => song.Key).Distinct().Count() != materializedSongs.Length)
            throw new ArgumentException("A provider contribution cannot contain duplicate song keys.", nameof(songs));
        if (materializedSongs.Any(song => song.AudioAsset is { } audio && audio.Provider != provider)
            || materializedSongs.SelectMany(static song => song.Charts).Any(chart => chart.ChartAsset.Provider != provider))
            throw new ArgumentException("Every asset must be owned by the contributing provider.", nameof(songs));
        if (materializedCategories.Any(category => category is null || category.Key.Source != source))
            throw new ArgumentException("Every category must use the provider's source kind.", nameof(categories));
        if (materializedCategories.Select(static category => category.Key).Distinct().Count() != materializedCategories.Length)
            throw new ArgumentException("A provider contribution cannot contain duplicate category keys.", nameof(categories));
        var songKeys = materializedSongs.Select(static song => song.Key).ToHashSet();
        if (materializedCategories.SelectMany(static category => category.Songs).Any(key => !songKeys.Contains(key)))
            throw new ArgumentException("Categories may reference only songs in the same contribution.", nameof(categories));
        if (materializedDiagnostics.Any(diagnostic => diagnostic is null || diagnostic.Provider != provider))
            throw new ArgumentException("Every diagnostic must identify its provider.", nameof(diagnostics));

        Provider = provider;
        Source = source;
        Songs = materializedSongs;
        Categories = materializedCategories;
        Diagnostics = materializedDiagnostics;
    }

    public CatalogProviderId Provider { get; }

    public SongSourceKind Source { get; }

    public ImmutableArray<SongDescriptor> Songs { get; }

    public ImmutableArray<SongCategoryDescriptor> Categories { get; }

    public ImmutableArray<CatalogDiagnostic> Diagnostics { get; }
}
