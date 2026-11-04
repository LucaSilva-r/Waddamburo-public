using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Providers.OsuLazer;
using Waddamburo.Providers.Stock;
using Waddamburo.Providers.Tja;

namespace Waddamburo.App.Gameplay;

/// <summary>
/// The song sources this run lists, scanned once at start: the game's own songs beside the Lumen data (in home mode
/// joined by other releases' set in the settings), and in home mode the custom TJA, Nijiiro and osu! libraries. Either
/// may be absent: a stock install without custom songs, or custom songs alone. Several official installs of a kind are
/// one library (<see cref="MergedCatalogProvider"/>): a song more of them have comes from the newest release.
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

    /// <summary>The libraries left out (a folder set that has no songs, a Nijiiro installation that can't play): for the boot's NG.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>Scans the libraries; throws when none lists a song. <paramref name="dataRoot"/>: the game's data folder.</summary>
    public static SongLibraries Load(string dataRoot, ArcadeSettings arcade, string defaultTjaFolder)
    {
        var tja = arcade.TjaFolder ?? defaultTjaFolder;
        List<string> problems = [];
        if (arcade.Home && arcade.TjaFolder is not null && !Directory.Exists(tja))
            problems.Add($"TJA folder not found: {tja}");
        if (arcade.Home && !string.IsNullOrEmpty(arcade.OsuFolder) && OsuLazerCatalogProvider.FindDataFolder(arcade.OsuFolder) is null)
            problems.Add($"osu!lazer data not found: {arcade.OsuFolder}");
        var libraries = new SongLibraries([
            .. stock(dataRoot, arcade, problems),
            // Custom TJA is a home-mode library of its own (arcade matches the game 1:1);
            // WADDAMBURO_CUSTOM_TJA=1 lists it in arcade too.
            .. (arcade.Home || Environment.GetEnvironmentVariable("WADDAMBURO_CUSTOM_TJA") == "1") && Directory.Exists(tja)
                ? [new TjaCatalogProvider(tja)] : Array.Empty<ISongCatalogProvider>(),
            // A Nijiiro installation picked in the settings (home mode), left out when it cannot play.
            .. arcade.Home ? nijiiro(arcade, problems) : [],
            // osu!lazer's native taiko beatmaps (home mode): the folder set in the settings, else the default install.
            .. arcade.Home && OsuLazerCatalogProvider.FindDataFolder(arcade.OsuFolder) is { } osu
                ? [new OsuLazerCatalogProvider(osu)] : Array.Empty<ISongCatalogProvider>(),
        ]);
        libraries.Problems.AddRange(problems);
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

    // The game's own songs, then (home mode) other releases' that it lacks, newest release first.
    private static ISongCatalogProvider[] stock(string dataRoot, ArcadeSettings arcade, List<string> problems)
    {
        var others = arcade.Home ? ArcadeSettings.Folders(arcade.StockFolders)
            .Select(folder => StockCatalogProvider.FindDataRoot(folder) is { } root ? root : warnMissing(folder, problems))
            .OfType<string>()
            .Where(root => !string.Equals(root, Path.GetFullPath(dataRoot), StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static root => Cli.GameDataLayout.ReleaseOf(root).Order)
            .ToList() : [];
        // The running game's data is Green, the newest release: it comes first whatever the others are.
        List<string> roots = [.. StockCatalogProvider.IsStockData(dataRoot) ? [dataRoot] : Array.Empty<string>(), .. others];
        return roots.Count switch
        {
            0 => [],
            1 => [new StockCatalogProvider(roots[0])],
            _ => [new MergedCatalogProvider([.. roots.Select(static root => new StockCatalogProvider(root))])],
        };
    }

    // Nijiiro installations that can play, the newest (highest song number) first.
    private static ISongCatalogProvider[] nijiiro(ArcadeSettings arcade, List<string> problems)
    {
        var installs = ArcadeSettings.Folders(arcade.NijiiroFolder)
            .Select(static folder => new NijiiroCatalogProvider(folder))
            .Where(installation => usable(installation, problems))
            .OrderByDescending(static installation => installation.NewestSong)
            .ToList();
        foreach (var installation in installs)
            Console.WriteLine($"Nijiiro installation {installation.Root}: newest song #{installation.NewestSong}.");
        return installs.Count switch
        {
            0 => [],
            1 => [installs[0]],
            _ => [new MergedCatalogProvider(installs)],
        };
    }

    private static string? warnMissing(string folder, List<string> problems)
    {
        Console.Error.WriteLine($"Game data left out: no songs in {folder}.");
        problems.Add($"No game songs in {folder}");
        return null;
    }

    /// <summary>
    /// Why a Nijiiro installation cannot be played, null when it can: its own <see cref="NijiiroCatalogProvider.Problem"/>,
    /// or no vgmstream-cli for its G.719 music (a song would start without its sound).
    /// </summary>
    public static string? NijiiroProblem(NijiiroCatalogProvider installation) => installation.Problem ?? VgmstreamProblem;

    /// <summary>Null when vgmstream-cli is found, else what to do about it.</summary>
    public static string? VgmstreamProblem => VgmstreamCli.IsInstalled ? null
        : Strings.T("library.no_vgmstream");

    private static bool usable(NijiiroCatalogProvider installation, List<string> problems)
    {
        if (NijiiroProblem(installation) is not { } problem)
            return true;
        Console.Error.WriteLine($"Nijiiro library left out: {problem}");
        problems.Add($"Nijiiro {installation.Root}: {problem}");
        return false;
    }

    public void Dispose() => _catalog.Dispose();
}
