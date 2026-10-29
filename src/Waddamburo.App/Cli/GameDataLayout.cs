

namespace Waddamburo.App.Cli;

/// <summary>
/// Resolves the stable paths used by a normal game boot from one USRDIR root. Waddamburo's own files
/// (settings, accounts, scores, avatars, caches) live together in <see cref="Home"/>
/// (USRDIR/waddamburo); custom_songs stays at the root, as the user's own song folder.
/// </summary>
internal sealed record GameDataLayout(
    string Root,
    string Home,
    string LumenRoot,
    string DonRoot,
    string TjaRoot,
    string SoundRoot,
    string FontPath,
    IReadOnlyList<string> FallbackFonts)
{
    public static GameDataLayout Resolve(string root, string? fontOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.GetFullPath(root);
        requireDirectory(fullRoot, "game data root");

        var lumen = Path.Combine(fullRoot, "data", "lumendata", "packed");
        var don = Path.Combine(fullRoot, "data", "don3d");
        var tja = Path.Combine(fullRoot, "custom_songs");
        var sound = Path.Combine(fullRoot, "data", "sound");
        requireDirectory(lumen, "Lumen asset root");
        requireDirectory(don, "Don asset root");
        requireDirectory(sound, "sound root");

        var home = Path.Combine(fullRoot, "waddamburo");
        moveIntoHome(fullRoot, home);
        // The user's font when they supplied one, else the bundled one; the bundled fonts draw what it lacks.
        var bundled = BundledFonts.Extract(Path.Combine(home, "fonts"));
        var font = fontOverride is null ? findTitleFont(fullRoot) ?? bundled.Title : Path.GetFullPath(fontOverride);
        if (!File.Exists(font))
            throw new FileNotFoundException($"The title font does not exist: {font}", font);
        string[] fallbacks = font == bundled.Title ? [bundled.Fallback] : [bundled.Title, bundled.Fallback];
        return new GameDataLayout(fullRoot, home, lumen, don, tja, sound, font, fallbacks);
    }

    /// <summary>
    /// The cache folder: <paramref name="chosen"/> (config cache_folder, e.g. an internal disk when the
    /// game runs from a USB stick) or waddamburo/cache. Upscaled textures and decoded audio go inside it.
    /// </summary>
    public string Cache(string? chosen) => Waddamburo.Game.Flow.ArcadeSettings.CacheRoot(Home, chosen);

    /// <summary>
    /// Moves a cache left in waddamburo/cache (where every cache lived before cache_folder) into
    /// <paramref name="root"/>, file by file (across drives too), keeping files already there. Runs at
    /// start, before anything opens the caches; a file that cannot move stays and is reported.
    /// </summary>
    public void MoveGameCacheTo(string root)
    {
        var from = Path.Combine(Home, "cache");
        if (!Directory.Exists(from) || Path.GetFullPath(from).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return;
        var files = Directory.GetFiles(from, "*", SearchOption.AllDirectories);
        if (files.Length > 0)
            Console.WriteLine($"Moving the cache from {from} to {root} ({files.Length} files)...");
        var failed = 0;
        foreach (var file in files)
        {
            var target = Path.Combine(root, Path.GetRelativePath(from, file));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                    File.Delete(file);
                else
                    File.Move(file, target);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failed++;
                Console.Error.WriteLine($"Cache file {file} stays: {exception.Message}");
            }
        }
        if (failed == 0)
        {
            try
            {
                Directory.Delete(from, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Old cache folder {from} stays: {exception.Message}");
            }
        }
    }

    public string UpscaleCache(string? chosen) => Path.Combine(Cache(chosen), "upscaled");

    public string AudioCache(string? chosen) => Path.Combine(Cache(chosen), "vgmstream");

    // Files from before the waddamburo folder move in once (never over newer ones).
    private static readonly string[] HomeFiles =
        ["config.cfg", "accounts.json", "account.json", "scores.db", "scores.db-wal", "scores.db-shm", "scores.db-journal"];

    private static void moveIntoHome(string root, string home)
    {
        Directory.CreateDirectory(home);
        foreach (var name in HomeFiles)
            if (File.Exists(Path.Combine(root, name)) && !File.Exists(Path.Combine(home, name)))
                File.Move(Path.Combine(root, name), Path.Combine(home, name));
        foreach (var (from, to) in new[] { ("avatars", "avatars"), ("upscaled", Path.Combine("cache", "upscaled")) })
        {
            var source = Path.Combine(root, from);
            var target = Path.Combine(home, to);
            if (!Directory.Exists(source) || Directory.Exists(target))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(source, target);
        }
    }

    /// <summary>
    /// The USRDIR a release was dropped into: the executable's directory (the AppImage's for
    /// Linux releases) when it holds the game, otherwise the working directory.
    /// </summary>
    public static string DefaultRoot()
    {
        var executable = Environment.GetEnvironmentVariable("APPIMAGE") ?? Environment.ProcessPath;
        var directory = Path.GetDirectoryName(executable);
        return directory is not null && Directory.Exists(Path.Combine(directory, "data", "lumendata", "packed"))
            ? directory
            : Directory.GetCurrentDirectory();
    }

    private static string? findTitleFont(string root)
    {
        var fontDirectory = Path.Combine(root, "data", "font");
        if (Directory.Exists(fontDirectory))
        {
            var preferredNames = new[] { "font.ttf", "font.otf", "font.ttc" };
            foreach (var name in preferredNames)
            {
                var candidate = Path.Combine(fontDirectory, name);
                if (File.Exists(candidate))
                    return candidate;
            }

            var suppliedFont = Directory.EnumerateFiles(fontDirectory)
                .Where(path => isSupportedFontExtension(Path.GetExtension(path)))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (suppliedFont is not null)
                return suppliedFont;
        }

        return null;
    }

    private static bool isSupportedFontExtension(string extension)
        => extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".otf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase);

    private static void requireDirectory(string path, string description)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"The {description} does not exist: {path}");
    }
}
