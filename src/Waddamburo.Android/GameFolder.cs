using Android.Media;

namespace Waddamburo.Android;

/// <summary>
/// The game's folders in its own storage (Android/data/&lt;package&gt;/files: no permission needed, visible from a PC over
/// USB), set up on every start: Green (the game data), Custom Songs (TJA), and Arcade, Wii, Wii U (one subfolder per
/// release). Until Green holds the game data a dialog says where to copy it, and the game starts once it is there.
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

    /// <summary>Where the folders are (Android/data/&lt;package&gt;/files).</summary>
    public string Root { get; } = activity.GetExternalFilesDir(null)!.AbsolutePath;

    /// <summary>
    /// Creates the folders and returns the game data folder, waiting (a dialog explaining where to copy it) until it
    /// holds Green's data. Blocks: call it from SDL's thread, never the UI thread.
    /// </summary>
    public string Resolve()
    {
        createFolders();
        // Development: data copied into the app's private files (adb, run-as) wins, unless
        // WADDAMBURO_SETUP_TEST=1 (the intent's env extra) shows the setup a new player sees.
        var copied = Path.Combine(activity.FilesDir!.AbsolutePath, "game");
        if (isGameData(copied) && System.Environment.GetEnvironmentVariable("WADDAMBURO_SETUP_TEST") != "1")
            return copied;
        AlertDialog? dialog = null;
        try
        {
            string? found;
            while ((found = findGameData()) is null)
            {
                if (dialog is null)
                    activity.RunOnUiThread(() =>
                    {
                        dialog = new AlertDialog.Builder(activity)
                            .SetTitle("Copy the game data")!
                            .SetMessage("Connect the phone to a PC and copy the contents of Taiko no Tatsujin Green's USRDIR "
                                + $"folder into:\n\nAndroid/data/{activity.PackageName}/files/{Green}\n\n"
                                + "The game starts as soon as the files are there.")!
                            .SetCancelable(false)!
                            .Create();
                        dialog!.Show();
                    });
                Thread.Sleep(2000);
            }
            return found;
        }
        finally
        {
            if (dialog is not null)
                activity.RunOnUiThread(() => dialog.Dismiss());
        }
    }

    /// <summary>The release folders inside Arcade, Wii and Wii U (config.cfg's stock_folders tells them apart).</summary>
    public IReadOnlyList<string> ReleaseFolders() =>
        [.. Releases.Select(release => Path.Combine(Root, release)).Where(Directory.Exists).SelectMany(Directory.EnumerateDirectories)];

    // Green itself, or a USRDIR copied in whole.
    private string? findGameData()
    {
        var green = Path.Combine(Root, Green);
        return isGameData(green) ? green : isGameData(Path.Combine(green, "USRDIR")) ? Path.Combine(green, "USRDIR") : null;
    }

    private static bool isGameData(string path) => Directory.Exists(Path.Combine(path, "data", "lumendata", "packed"));

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
