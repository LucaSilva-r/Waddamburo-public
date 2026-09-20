using System.Collections.Immutable;

namespace Waddamburo.Catalog;

public sealed record CatalogProviderStatus(
    CatalogProviderId Provider,
    SongSourceKind Source,
    bool Succeeded,
    int SongCount,
    int CategoryCount);

public sealed class SongCatalogSnapshot
{
    internal SongCatalogSnapshot(
        long revision,
        ImmutableDictionary<SongKey, SongDescriptor> songs,
        ImmutableArray<SongCategoryDescriptor> categories,
        ImmutableArray<CatalogProviderStatus> providers,
        ImmutableArray<CatalogDiagnostic> diagnostics)
    {
        Revision = revision;
        Songs = songs;
        Categories = categories;
        Providers = providers;
        Diagnostics = diagnostics;
    }

    public long Revision { get; }

    public ImmutableDictionary<SongKey, SongDescriptor> Songs { get; }

    public ImmutableArray<SongCategoryDescriptor> Categories { get; }

    public ImmutableArray<CatalogProviderStatus> Providers { get; }

    public ImmutableArray<CatalogDiagnostic> Diagnostics { get; }

    internal static SongCatalogSnapshot Empty { get; } = new(0, ImmutableDictionary<SongKey, SongDescriptor>.Empty, [], [], []);
}

/// <summary>Scans independent providers and atomically publishes one immutable catalog.</summary>
public sealed class GlobalSongCatalog : IDisposable
{
    private readonly ImmutableArray<ISongCatalogProvider> _providers;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private SongCatalogSnapshot _current = SongCatalogSnapshot.Empty;

    public GlobalSongCatalog(IEnumerable<ISongCatalogProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var materialized = providers.ToImmutableArray();
        if (materialized.Any(static provider => provider is null))
            throw new ArgumentException("Catalog providers cannot contain null entries.", nameof(providers));
        _providers = [.. materialized.OrderBy(static provider => provider.Id.Value, StringComparer.Ordinal)];
        if (_providers.Select(static provider => provider.Id).Distinct().Count() != _providers.Length)
            throw new ArgumentException("Catalog provider IDs must be unique.", nameof(providers));
    }

    public SongCatalogSnapshot Current => Volatile.Read(ref _current);

    public void Dispose() => _refreshLock.Dispose();

    public async ValueTask<SongCatalogSnapshot> RefreshAsync(
        IProgress<CatalogScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scans = _providers.Select(provider => scanAsync(provider, progress, cancellationToken)).ToArray();
            var results = await Task.WhenAll(scans).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var songs = ImmutableDictionary.CreateBuilder<SongKey, SongDescriptor>();
            var categories = ImmutableArray.CreateBuilder<SongCategoryDescriptor>();
            var statuses = ImmutableArray.CreateBuilder<CatalogProviderStatus>(_providers.Length);
            var diagnostics = ImmutableArray.CreateBuilder<CatalogDiagnostic>();
            foreach (var result in results.OrderBy(static result => result.Provider.Id.Value, StringComparer.Ordinal))
            {
                if (result.Contribution is not { } contribution)
                {
                    statuses.Add(new CatalogProviderStatus(result.Provider.Id, result.Provider.Source, false, 0, 0));
                    diagnostics.Add(result.Failure!);
                    continue;
                }

                if (contribution.Provider != result.Provider.Id || contribution.Source != result.Provider.Source)
                {
                    rejectContribution(result.Provider, "CATALOG_PROVIDER_IDENTITY_MISMATCH", "Provider returned a contribution with a different identity.");
                    continue;
                }
                if (contribution.Songs.Any(song => songs.ContainsKey(song.Key))
                    || contribution.Categories.Any(category => categories.Any(existing => existing.Key == category.Key)))
                {
                    rejectContribution(result.Provider, "CATALOG_KEY_COLLISION", "Provider contribution collides with an already accepted stable key.");
                    continue;
                }

                foreach (var song in contribution.Songs)
                    songs.Add(song.Key, song);
                categories.AddRange(contribution.Categories);
                diagnostics.AddRange(contribution.Diagnostics);
                statuses.Add(new CatalogProviderStatus(
                    contribution.Provider,
                    contribution.Source,
                    true,
                    contribution.Songs.Length,
                    contribution.Categories.Length));

                void rejectContribution(ISongCatalogProvider provider, string code, string message)
                {
                    statuses.Add(new CatalogProviderStatus(provider.Id, provider.Source, false, 0, 0));
                    diagnostics.Add(new CatalogDiagnostic(CatalogDiagnosticSeverity.Error, code, message, provider.Id));
                }
            }

            var snapshot = new SongCatalogSnapshot(
                checked(Current.Revision + 1),
                songs.ToImmutable(),
                [.. categories.OrderBy(static category => category.SortOrder)
                    .ThenBy(static category => category.Key.Source)
                    .ThenBy(static category => category.Key.StableId, StringComparer.Ordinal)],
                statuses.MoveToImmutable(),
                diagnostics.ToImmutable());
            Volatile.Write(ref _current, snapshot);
            return snapshot;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static async Task<ProviderScanResult> scanAsync(
        ISongCatalogProvider provider,
        IProgress<CatalogScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var contribution = await provider.ScanAsync(progress, cancellationToken).ConfigureAwait(false);
            return new ProviderScanResult(provider, contribution, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ProviderScanResult(
                provider,
                null,
                new CatalogDiagnostic(
                    CatalogDiagnosticSeverity.Error,
                    "CATALOG_PROVIDER_FAILED",
                    $"Provider scan failed: {exception.Message}",
                    provider.Id));
        }
    }

    private sealed record ProviderScanResult(
        ISongCatalogProvider Provider,
        SongCatalogContribution? Contribution,
        CatalogDiagnostic? Failure);
}
