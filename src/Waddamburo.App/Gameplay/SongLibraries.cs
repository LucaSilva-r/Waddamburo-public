using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Providers.Stock;
using Waddamburo.Providers.Tja;

namespace Waddamburo.App.Gameplay;

/// <summary>
/// The song sources this run lists, scanned once at start: the game's own songs beside the Lumen data,
/// and in home mode the custom TJA and Nijiiro libraries set in the settings. Either may be absent: a
/// stock install without custom songs, or custom songs alone.
/// </summary>
internal sealed class SongLibraries : IDisposable
{
    private readonly GlobalSongCatalog _catalog;

    private SongLibraries(ISongCatalogProvider[] providers)
    {
        Providers = providers;
        _catalog = new GlobalSongCatalog(providers);
        Snapshot = _catalog.RefreshAsync().AsTask().GetAwaiter().GetResult();
    }

    public ISongCatalogProvider[] Providers { get; }

    public SongCatalogSnapshot Snapshot { get; }

    /// <summary>Scans the libraries; throws when none lists a song. <paramref name="dataRoot"/>: the game's data folder.</summary>
    public static SongLibraries Load(string dataRoot, ArcadeSettings arcade, string defaultTjaFolder)
    {
        var tja = arcade.TjaFolder ?? defaultTjaFolder;
        var libraries = new SongLibraries([
            .. StockCatalogProvider.IsStockData(dataRoot) ? [new StockCatalogProvider(dataRoot)] : Array.Empty<ISongCatalogProvider>(),
            // Custom TJA is a home-mode library of its own (arcade matches the game 1:1);
            // WADDAMBURO_CUSTOM_TJA=1 lists it in arcade too.
            .. (arcade.Home || Environment.GetEnvironmentVariable("WADDAMBURO_CUSTOM_TJA") == "1") && Directory.Exists(tja)
                ? [new TjaCatalogProvider(tja)] : Array.Empty<ISongCatalogProvider>(),
            // A Nijiiro installation picked in the settings (home mode).
            .. arcade.Home && arcade.NijiiroFolder is { } nijiiro && new NijiiroCatalogProvider(nijiiro) is { Exists: true } installed
                ? [installed] : Array.Empty<ISongCatalogProvider>(),
        ]);
        var snapshot = libraries.Snapshot;
        foreach (var status in snapshot.Providers)
            Console.WriteLine(status.Succeeded
                ? $"Catalog provider {status.Provider}: {status.SongCount} songs in {status.CategoryCount} categories."
                : $"Catalog provider {status.Provider} failed.");
        if (snapshot.Categories.IsEmpty)
        {
            libraries.Dispose();
            throw new InvalidOperationException(snapshot.Diagnostics.FirstOrDefault(static diagnostic => diagnostic.Severity == CatalogDiagnosticSeverity.Error)?.Message
                ?? "No song provider discovered any browsable categories.");
        }
        Console.WriteLine($"Catalog revision {snapshot.Revision}: {snapshot.Categories.Length} categories.");
        foreach (var diagnostic in snapshot.Diagnostics)
            Console.Error.WriteLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
        return libraries;
    }

    public void Dispose() => _catalog.Dispose();
}
