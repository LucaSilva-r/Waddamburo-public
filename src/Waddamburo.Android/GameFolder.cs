using Android.Media;

namespace Waddamburo.Android;

/// <summary>
/// The game's folders in its own storage (Android/data/&lt;package&gt;/files: no permission needed, visible from a PC over
/// USB), set up on every start: Green (the game data), Custom Songs (TJA), and Arcade, Wii, Wii U (one subfolder per
/// release). Until Green holds the game data a dialog says where to copy it (and what is still missing), and the game
/// starts once the copy is complete.
/// </summary>
internal sealed class GameFolder(Activity activity)
{
    public const string Green = "Green", CustomSongs = "Custom Songs";
    private static readonly string[] Releases = ["Arcade", "Wii", "Wii U"];

    private static readonly (string Folder, string Note)[] Notes =
    [
        (Green, "Copy the contents of Taiko no Tatsujin Green's USRDIR folder here (the data folder and the rest)."),
        (CustomSongs, "Put TJA songs here, in folders as you like."),
        ("Arcade", "Other arcade releases: one folder per release (its USRDIR contents)."),
        ("Wii", "Wii releases: one folder per disc."),
        ("Wii U", "Wii U releases: one folder per release (with its DLC)."),
    ];

    // What the boot reads first: a dump without one of these fails there (a tester's lacked the sound config).
    private static readonly string[] Essentials =
    [
        "data/lumendata/packed", "data/don3d", "data/sound/se", "data/sound/bgm", "data/sound/config/nuSound2BankStr.bin",
    ];

    /// <summary>Where the folders are (Android/data/&lt;package&gt;/files).</summary>
    public string Root { get; } = activity.GetExternalFilesDir(null)!.AbsolutePath;

    /// <summary>
    /// Creates the folders and returns the game data folder, waiting (a dialog: where to copy it, what is missing) until
    /// it holds Green's data. Blocks: call it from SDL's thread, never the UI thread.
    /// </summary>
    public string Resolve()
    {
        createFolders();
        deleteDevelopmentCopy();
        var green = Path.Combine(Root, Green);
        if (missing(gameRoot(green)) is [])
            return gameRoot(green);
        AlertDialog? dialog = null;
        try
        {
            // A copy in progress: wait for the essentials, then for the folder to stop growing (a copy still running
            // would start the game with half its files).
            var settled = 0;
            var last = (Files: -1, Bytes: -1L);
            while (true)
            {
                var root = gameRoot(green);
                var lacking = missing(root);
                var now = measure(root);
                settled = lacking is [] && now == last ? settled + 1 : 0;
                last = now;
                if (settled >= 3)
                    return root;
                var message = lacking is []
                    ? $"Copying… {now.Files} files so far. The game starts once the copy has finished."
                    : $"From a PC, copy Green's USRDIR contents into Android/data/{activity.PackageName}/files/{Green}."
                        + (now.Files > 0 ? $"\n\nStill missing: {string.Join(", ", lacking)} ({now.Files} files so far)." : "");
                activity.RunOnUiThread(() =>
                {
                    dialog ??= new AlertDialog.Builder(activity).SetTitle("Copy the game data")!.SetMessage(message)!
                        .SetCancelable(false)!.Create();
                    dialog!.SetMessage(message);
                    dialog.Show();
                });
                Thread.Sleep(2000);
            }
        }
        finally
        {
            activity.RunOnUiThread(() => dialog?.Dismiss());
        }
    }

    /// <summary>The release folders inside Arcade, Wii and Wii U (config.cfg's stock_folders tells them apart).</summary>
    public IReadOnlyList<string> ReleaseFolders() =>
        [.. Releases.Select(release => Path.Combine(Root, release)).Where(Directory.Exists).SelectMany(Directory.EnumerateDirectories)];

    // Green itself, or a USRDIR copied in whole.
    private static string gameRoot(string green) =>
        Directory.Exists(Path.Combine(green, "USRDIR", "data")) ? Path.Combine(green, "USRDIR") : green;

    private static string[] missing(string root) =>
        [.. Essentials.Where(path => !Path.Exists(Path.Combine(root, path)))];

    private static (int Files, long Bytes) measure(string root)
    {
        var data = Path.Combine(root, "data");
        if (!Directory.Exists(data))
            return (0, 0);
        var files = 0;
        long bytes = 0;
        foreach (var file in new DirectoryInfo(data).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            files++;
            bytes += file.Length;
        }
        return (files, bytes);
    }

    // ponytail: development builds had the game data adb-copied into the app's private files/game; free it. Remove once
    // no test device has one.
    private void deleteDevelopmentCopy()
    {
        var copied = Path.Combine(activity.FilesDir!.AbsolutePath, "game");
        if (Directory.Exists(copied))
            Directory.Delete(copied, recursive: true);
    }

    // The folders, each with a note of what goes in it. A PC sees only what the media scanner knows: the notes are
    // registered, so the folders show up over USB.
    private void createFolders()
    {
        List<string> created = [];
        foreach (var (folder, note) in Notes)
        {
            var path = Path.Combine(Root, folder);
            Directory.CreateDirectory(path);
            var file = Path.Combine(path, "README.txt");
            if (File.Exists(file))
                continue;
            File.WriteAllText(file, note + "\n");
            created.Add(file);
        }
        if (created.Count > 0)
            MediaScannerConnection.ScanFile(activity, [.. created], null, null);
    }
}
