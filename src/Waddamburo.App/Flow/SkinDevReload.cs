using Waddamburo.Game;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;

namespace Waddamburo.App.Flow;

/// <summary>
/// Skin development (<c>--skin-dev=&lt;folder&gt;</c>): the game plays the packages of a skin folder (the one the
/// skin editor saves into) and, when a file there changes, shows the screen it is on again with the new movies, so a
/// save in the editor is seen in the real game with its real data. scenes.json is read again too. Song Select
/// reloads under its quick rainbow, its music playing on; during a song the reload waits for the lane to be left.
/// ponytail: the whole folder is listed every half second; a watcher if a skin grows past a few thousand files.
/// </summary>
internal sealed class SkinDevReload(GameShell shell, string root, Action forgetKept, Action<int, LumenMovieContent>? swap = null)
{
    private readonly HashSet<string> _swaps = new(StringComparer.Ordinal); // packages saved, their movies to swap on screen
    private const int Every = 30;
    private Dictionary<string, DateTime> _stamps = list(root);
    private bool _pending;

    /// <summary>The skin folder given on the command line (--skin-dev=), or null.</summary>
    public static string? Folder => Environment.GetCommandLineArgs().FirstOrDefault(static arg => arg.StartsWith("--skin-dev=", StringComparison.Ordinal)) is { } arg
        ? Path.GetFullPath(arg["--skin-dev=".Length..]) : null;

    private static Dictionary<string, DateTime> list(string folder) => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(static file => !file.Contains($"{Path.DirectorySeparatorChar}.", StringComparison.Ordinal))
            .ToDictionary(static file => file, File.GetLastWriteTimeUtc, StringComparer.Ordinal)
        : [];

    public void Tick()
    {
        if (shell.Tick % Every == 0)
            look();
        if (_swaps.Count > 0 && !shell.Scenes.OnLane && !shell.Reload.Busy && !shell.Scenes.FadePending
            && shell.Coordinator.Flow.State == GameFlowState.Active)
            swapSaved();
        if (!_pending || shell.Scenes.OnLane || shell.Reload.Busy || shell.Scenes.FadePending
            || shell.Coordinator.Flow.State != GameFlowState.Active)
            return;
        _pending = false;
        var id = shell.Active.Id;
        Console.WriteLine($"Skin dev: showing {id} again with the saved skin.");
        if (id == FlowScenes.SongSelect)
        {
            shell.Reload.Start(static () => { });
            return;
        }
        // A screen the skin composes: its new composition into the catalog first (scenes.json may have changed).
        if (shell.Screens.Initial([]).FirstOrDefault(scene => scene.Id == id) is { } definition)
            shell.Catalog.Replace(definition);
        shell.Scenes.Show(id);
    }

    /// <summary>Shows the screen again with the folder's movies now (scenes: scenes.json read again too).</summary>
    public void ReloadNow(bool scenes)
    {
        if (scenes)
            Frontends.WaddamburoScreens.Instance.Skin = SkinScenes.Find([root, Path.Combine(AppContext.BaseDirectory, "movies")],
                static problem => Console.Error.WriteLine($"Skin scenes ignored: {problem}"));
        forgetKept();
        _pending = true;
    }

    private void look()
    {
        var now = list(root);
        var changed = now.Where(file => !_stamps.TryGetValue(file.Key, out var was) || was != file.Value).Select(static file => file.Key)
            .Concat(_stamps.Keys.Where(file => !now.ContainsKey(file))).ToArray();
        _stamps = now;
        if (changed.Length == 0)
            return;
        Console.WriteLine($"Skin dev: {changed.Length} file(s) changed ({Path.GetRelativePath(root, changed[0])}{(changed.Length > 1 ? ", ..." : "")}).");
        if (changed.Any(static file => Path.GetFileName(file) == SkinScenes.FileName))
            Frontends.WaddamburoScreens.Instance.Skin = SkinScenes.Find([root, Path.Combine(AppContext.BaseDirectory, "movies")],
                static problem => Console.Error.WriteLine($"Skin scenes ignored: {problem}"));
        // Text styles and fonts: every text drawn again in them, the screen going on.
        if (changed.Any(static file => Path.GetFileName(file) == SkinText.FileName || Path.GetExtension(file) is ".ttf" or ".otf"))
            SkinText.Current = SkinText.Find([root, Path.Combine(AppContext.BaseDirectory, "movies")],
                static problem => Console.Error.WriteLine($"Skin text styles ignored: {problem}"));
        forgetKept();
        // Packages only (no scenes.json): their movies on the screen are swapped in place, the screen going on. A song's
        // lane holds its movies itself: then the whole screen again, once the song is over.
        var packages = changed.Select(packageOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (swap is null || changed.Any(static file => Path.GetFileName(file) == SkinScenes.FileName))
            _pending = true;
        else if (shell.Scenes.OnLane)
            _pending = packages.Count > 0;
        else
            _swaps.UnionWith(packages);
    }

    // The package a file belongs to (its folder with a movie.json, up from the file), from the root; null for none.
    private string? packageOf(string file)
    {
        for (var folder = Path.GetDirectoryName(file); folder is not null && folder.Length > root.Length; folder = Path.GetDirectoryName(folder))
            if (File.Exists(Path.Combine(folder, Formats.Package.MoviePackage.FileName)))
                return Path.GetRelativePath(root, folder).Replace('\\', '/');
        return null;
    }

    private void swapSaved()
    {
        var scene = shell.Active;
        for (var index = 0; index < scene.Layers.Length; index++)
        {
            var definition = scene.Layers[index].Definition;
            if (DirectoryLumenMovieContentSource.PackageFolder(root, definition.ArchiveId, definition.MovieId) is not { } folder
                || !_swaps.Contains(Path.GetRelativePath(root, folder).Replace('\\', '/')))
                continue;
            try
            {
                var content = shell.MovieContent.LoadAsync(definition.ArchiveId, definition.MovieId, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                swap!(index, content);
                Console.WriteLine($"Skin dev: {definition.MovieId} swapped in place.");
            }
            catch (Exception error) when (error is InvalidDataException or FormatException or IOException or InvalidOperationException)
            {
                Console.Error.WriteLine($"Skin dev: {definition.MovieId} not swapped: {error.Message}");
            }
        }
        _swaps.Clear();
    }
}
