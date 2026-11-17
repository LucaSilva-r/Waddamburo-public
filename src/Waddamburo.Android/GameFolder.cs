using Android.Content;
using Android.Media;
using Android.Views;
using Waddamburo.App.Sync;

namespace Waddamburo.Android;

/// <summary>
/// The game's folders in its own storage (Android/data/&lt;package&gt;/files: no permission needed, visible from a PC over
/// USB), set up on every start: Green (the game data), Custom Songs (TJA), and Arcade, Wii, Wii U (one subfolder per
/// release). Until Green holds the game data the setup screen says how to send it (<see cref="PhoneReceiver"/>) and the
/// game starts once the copy is complete.
/// </summary>
internal sealed class GameFolder(Activity activity)
{
    public const string Green = "Green", CustomSongs = "Custom Songs";
    private static readonly string[] Releases = ["Arcade", "Wii", "Wii U"];

    private static readonly (string Folder, string Note)[] Notes =
    [
        (Green, "Copy the contents of Taiko no Tatsujin Green's USRDIR folder here (the data folder and the rest)."),
        (CustomSongs, "One folder per category (its name is the category's), song folders with their TJA inside."),
        ("Songs", "Songs other Waddamburos sent (Settings › Receive). The game keeps these: don't edit them."),
        ("Arcade", "Other arcade releases: one folder per release (its USRDIR contents)."),
        ("Wii", "Wii releases: one folder per disc."),
        ("Wii U", "Wii U releases: one folder per release (with its DLC)."),
    ];

    /// <summary>Where the folders are (Android/data/&lt;package&gt;/files).</summary>
    public string Root { get; } = activity.GetExternalFilesDir(null)!.AbsolutePath;

    // Written once a copy is complete: later starts skip checking its ~20,000 files.
    private string VerifiedMarker => Path.Combine(activity.FilesDir!.AbsolutePath, "green-verified");

    /// <summary>
    /// Creates the folders and returns the game data folder. Until it holds all of Green's files, a screen says how to
    /// send them (the upload page over Wi-Fi or USB tethering, or a file manager) and what is still missing; once the
    /// boot's essentials are there the player may start anyway. Blocks: call it from SDL's thread, never the UI thread.
    /// </summary>
    public string Resolve()
    {
        createFolders();
        deleteDevelopmentCopy();
        var green = Path.Combine(Root, Green);
        if (File.Exists(VerifiedMarker) && Waddamburo.App.Cli.GreenData.HasEssentials(gameRoot(green)))
            return gameRoot(green);
        if (Waddamburo.App.Cli.GreenData.Missing(gameRoot(green)) is [])
            return verified(gameRoot(green));

        // Listening only while the setup waits (Settings › Receive from device listens on its own later); the
        // player is setting up, so what a PC sends is taken without asking.
        using var server = new PhoneReceiver(Root, PhoneReceiver.Targets(Root), DeviceName);
        AlertDialog? dialog = null;
        TextView? text = null;
        var startAnyway = 0;
        try
        {
            while (true)
            {
                var root = gameRoot(green);
                var missing = Waddamburo.App.Cli.GreenData.Missing(root);
                if (missing is [])
                    return verified(root);
                var essentials = Waddamburo.App.Cli.GreenData.HasEssentials(root);
                if (Volatile.Read(ref startAnyway) == 1)
                    return root;
                var message = setupMessage(server, missing.Count);
                activity.RunOnUiThread(() =>
                {
                    if (dialog is null)
                    {
                        text = new TextView(activity) { TextSize = 19 };
                        text.SetPadding(56, 32, 56, 32);
                        text.SetTextIsSelectable(true);
                        var scroll = new ScrollView(activity);
                        scroll.AddView(text);
                        dialog = new AlertDialog.Builder(activity)
                            .SetTitle("Waddamburo needs the game's files")!
                            .SetView(scroll)!
                            .SetCancelable(false)!
                            .SetNeutralButton("Start anyway", (_, _) => Volatile.Write(ref startAnyway, 1))!
                            .Create()!;
                        dialog.Show();
                    }
                    text!.Text = message;
                    if (dialog.GetButton((int)DialogButtonType.Neutral) is { } button)
                        button.Visibility = essentials ? ViewStates.Visible : ViewStates.Gone;
                });
                Thread.Sleep(3000);
            }
        }
        finally
        {
            activity.RunOnUiThread(() => dialog?.Dismiss());
        }
    }

    // One for the process (the activity may be created again): the import page stays up while the app runs.
    /// <summary>How other devices list this one.</summary>
    public static string DeviceName => global::Android.OS.Build.Model ?? "Android";

    private string verified(string root)
    {
        File.WriteAllText(VerifiedMarker, root);
        return root;
    }

    private string setupMessage(PhoneReceiver server, int missing)
    {
        var (files, bytes) = server.Received;
        // USB tethering first: about ten times Wi-Fi's speed on a budget phone (25 against 3 MB/s measured).
        return "1. Connect the phone to a PC with a USB cable and turn on USB tethering (Settings › Hotspot). "
            + "Wi-Fi works too, much slower.\n\n"
            + "2. On the PC, open Waddamburo: Settings › Send to device. Pick "
            + $"\"{DeviceName}\" and type the code\n\n        {server.Code}\n\n"
            + "3. Leave Green game data on; type a search in Songs to bring songs too (* for all). "
            + "If the copy stops, send again: it goes on where it stopped.\n\n"
            + $"Or copy USRDIR's contents into Android/data/{activity.PackageName}/files/{Green} yourself.\n\n"
            + (files > 0 ? $"Received {files:N0} files ({bytes / 1048576:N0} MB). " : "")
            + $"Missing: {missing:N0} of {Waddamburo.App.Cli.GreenData.FileCount:N0} files. The game starts by itself once they are all here.";
    }

    /// <summary>The release folders inside Arcade, Wii and Wii U (config.cfg's stock_folders tells them apart). A Wii U
    /// import holds the game's folder with its update and DLC beside it (where they are looked for): that one level
    /// deeper is listed too (an update or DLC folder alone is no disc and is passed over).</summary>
    public IReadOnlyList<string> ReleaseFolders() =>
        [.. Releases.Select(release => Path.Combine(Root, release)).Where(Directory.Exists).SelectMany(Directory.EnumerateDirectories),
            .. Directory.Exists(Path.Combine(Root, "Wii U"))
                ? Directory.EnumerateDirectories(Path.Combine(Root, "Wii U")).SelectMany(Directory.EnumerateDirectories) : []];

    // Green itself, or a USRDIR copied in whole.
    private static string gameRoot(string green) =>
        Directory.Exists(Path.Combine(green, "USRDIR", "data")) ? Path.Combine(green, "USRDIR") : green;



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
