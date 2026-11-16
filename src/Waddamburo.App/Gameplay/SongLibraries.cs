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

    private SongLibraries(ISongCatalogProvider[] providers, Action<int>? percent)
    {
        Providers = providers;
        _catalog = new GlobalSongCatalog(providers);
        Snapshot = _catalog.RefreshAsync(percent is null ? null : new ScanPercent(providers.Length, percent)).AsTask().GetAwaiter().GetResult();
        percent?.Invoke(100);
    }

    // Average each provider's share, reserving 100% until the final snapshot has been published.
    // ponytail: equal weight per provider, not per song (totals arrive late); weight by Total if it reads jumpy.
    private sealed class ScanPercent(int providers, Action<int> percent) : IProgress<CatalogScanProgress>
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<CatalogProviderId, double> _done = new();
        private int _shown = -1;

        public void Report(CatalogScanProgress value)
        {
            _done[value.Provider] = value.Total is > 0 and var total ? Math.Clamp((double)value.Completed / total, 0, 1) : 0;
            var now = Math.Min(99, (int)(_done.Values.Sum() * 100 / Math.Max(providers, _done.Count)));
            lock (_done) // the scans report from several threads
                if (now > _shown)
                    percent(_shown = now);
        }
    }

    public ISongCatalogProvider[] Providers { get; }

    /// <summary>The metadata cache the scan used: its background checks of the installations report through it.</summary>
    public CatalogMetadataCache? MetadataCache { get; private set; }

    public SongCatalogSnapshot Snapshot { get; }

    /// <summary>
    /// The libraries left out (a folder set that has no songs, a Nijiiro installation that can't play) and why: the
    /// boot's NG, and the settings' SONG LIBRARIES show them.
    /// </summary>
    public List<(string Folder, string Why)> Problems { get; } = [];

    /// <summary>Scans the libraries; throws when none lists a song. <paramref name="dataRoot"/>: the game's data folder.</summary>
    /// <paramref name="percent"/>: how far the scan is, 0-100, from the scanning threads.
    /// <paramref name="building"/>: runs once when this start parses many song files again (the cache is being built).
    public static SongLibraries Load(string dataRoot, ArcadeSettings arcade, string defaultTjaFolder, Action<int>? percent = null,
        Action? building = null)
    {
        var tja = arcade.TjaFolder ?? defaultTjaFolder;
        var cacheFolder = Path.GetDirectoryName(VgmstreamCli.CacheDirectory)!;
        // v1's rowid tables were read with a seek per row; v2 starts clean (the cache is disposable).
        try { File.Delete(Path.Combine(cacheFolder, "catalog-metadata.db")); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        var metadataCache = new CatalogMetadataCache(Path.Combine(cacheFolder, "catalog-metadata-v2.db"))
        {
            // The installations' folders are walked again once the start is over, not against its own reads.
            // ponytail: a fixed minute after the scan; signal the end of the boot instead if a slow disk still collides.
            RevalidateAfter = Task.Delay(TimeSpan.FromMinutes(1)),
            Building = building,
        };
        List<(string, string)> problems = [];
        if (arcade.Home && arcade.TjaFolder is not null && !Directory.Exists(tja))
            problems.Add((tja, "Folder not found"));
        if (arcade.Home && !string.IsNullOrEmpty(arcade.OsuFolder) && OsuLazerCatalogProvider.FindDataFolder(arcade.OsuFolder) is null)
            problems.Add((arcade.OsuFolder, "No osu!lazer data in this folder"));
        if (arcade.Home && !string.IsNullOrEmpty(arcade.OsuStableFolder) && OsuLazerCatalogProvider.FindStableFolder(arcade.OsuStableFolder) is null)
            problems.Add((arcade.OsuStableFolder, "No osu!stable install (osu!.db) in this folder"));
        var libraries = new SongLibraries([
            .. stock(dataRoot, arcade, problems, metadataCache),
            // Custom TJA is a home-mode library of its own (arcade matches the game 1:1);
            // WADDAMBURO_CUSTOM_TJA=1 lists it in arcade too.
            .. (arcade.Home || Environment.GetEnvironmentVariable("WADDAMBURO_CUSTOM_TJA") == "1") && Directory.Exists(tja)
                ? [new TjaCatalogProvider(tja) { MetadataCache = metadataCache }] : Array.Empty<ISongCatalogProvider>(),
            // A Nijiiro installation picked in the settings (home mode), left out when it cannot play.
            .. arcade.Home ? nijiiro(arcade, problems, metadataCache) : [],
            // osu!lazer's native taiko beatmaps (home mode): the folder set in the settings, else the default install.
            .. arcade.Home && OsuLazerCatalogProvider.FindDataFolder(arcade.OsuFolder) is { } osu
                ? [new OsuLazerCatalogProvider(osu)] : Array.Empty<ISongCatalogProvider>(),
            // osu!stable's (home mode), a library of its own: the folder set in the settings, else the default install.
            .. arcade.Home && OsuLazerCatalogProvider.FindStableFolder(arcade.OsuStableFolder) is { } stable
                ? [new OsuLazerCatalogProvider(stable)] : Array.Empty<ISongCatalogProvider>(),
        ], percent);
        metadataCache.ForgetUnopenedInventories();
        libraries.MetadataCache = metadataCache;
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
    private static ISongCatalogProvider[] stock(string dataRoot, ArcadeSettings arcade, List<(string, string)> problems, CatalogMetadataCache metadataCache)
    {
        var others = arcade.Home ? ArcadeSettings.Folders(arcade.StockFolders)
            .Where(static folder => WiiCatalogProvider.FindRoot(folder) is null && WiiUCatalogProvider.FindRoot(folder) is null)
            .Select(folder => StockCatalogProvider.FindDataRoot(folder) is { } root ? root : warnMissing(folder, problems))
            .OfType<string>()
            .Where(root => !string.Equals(root, Path.GetFullPath(dataRoot), StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static root => Cli.GameDataLayout.ReleaseOf(root).Order)
            .ToList() : [];
        // The running game's data is Green, the newest release: it comes first whatever the others are.
        List<string> roots = [.. StockCatalogProvider.IsStockData(dataRoot) ? [dataRoot] : Array.Empty<string>(), .. others];
        // Wii and Wii U discs set among them: the WII/WII U library.
        var folders = arcade.Home ? ArcadeSettings.Folders(arcade.StockFolders) : [];
        List<WiiUCatalogProvider> wiiU = [.. folders.Select(WiiUCatalogProvider.FindRoot).OfType<string>()
            .Distinct(StringComparer.Ordinal).Select(static root => new WiiUCatalogProvider(root))];
        List<WiiCatalogProvider> wii = [.. folders.Select(WiiCatalogProvider.FindRoot).OfType<string>()
            .Distinct(StringComparer.Ordinal).Select(static root => new WiiCatalogProvider(root))];
        ISongCatalogProvider[] wiiLibrary = wiiU.Count + wii.Count > 0 ? [new WiiLibraryProvider(wiiU, wii)] : [];
        return [.. roots.Count switch
        {
            0 => [],
            1 => [new StockCatalogProvider(roots[0]) { MetadataCache = metadataCache }],
            _ => new ISongCatalogProvider[] { new MergedCatalogProvider([.. roots.Select(root => new StockCatalogProvider(root) { MetadataCache = metadataCache })]) },
        }, .. wiiLibrary];
    }

    // Nijiiro installations that can play, the newest (highest song number) first.
    private static ISongCatalogProvider[] nijiiro(ArcadeSettings arcade, List<(string, string)> problems, CatalogMetadataCache metadataCache)
    {
        var installs = ArcadeSettings.Folders(arcade.NijiiroFolder)
            .Select(folder => (Folder: folder, Installation: new NijiiroCatalogProvider(folder,
                keys: NijiiroKeys.Parse(arcade.NijiiroDatatableKey, arcade.NijiiroFumenKey)) { MetadataCache = metadataCache }))
            .Where(install => usable(install.Folder, install.Installation, problems))
            .Select(static install => install.Installation)
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

    private static string? warnMissing(string folder, List<(string, string)> problems)
    {
        // A missing folder (removed, or a drive not plugged in) says so: its songs are left out until it is back.
        var why = Directory.Exists(folder) ? "No game songs in this folder" : "Folder not found";
        Console.Error.WriteLine($"Game data left out: {why}: {folder}.");
        problems.Add((folder, why));
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

    private static bool usable(string folder, NijiiroCatalogProvider installation, List<(string, string)> problems)
    {
        if ((Directory.Exists(folder) ? NijiiroProblem(installation) : "Folder not found") is not { } problem)
            return true;
        Console.Error.WriteLine($"Nijiiro library left out: {problem}");
        problems.Add((folder, problem));
        return false;
    }

    public void Dispose() => _catalog.Dispose();
}
