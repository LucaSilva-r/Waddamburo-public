using Android.Content;
using Android.Media;
using Waddamburo.App.Cli;
using Waddamburo.App.Sync;

namespace Waddamburo.Android;

/// <summary>
/// The game's folders in its own storage (Android/data/&lt;package&gt;/files: no permission needed, visible from a PC over
/// USB), set up on every start: Green (the game data), Custom Songs (TJA), and Arcade, Wii, Wii U (one subfolder per
/// release), Received (libraries mirrored from a PC). Until Green holds the game data the setup screen pairs with a PC and
/// takes Green from it (<see cref="GreenSetup"/>), and the
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
        ("Received", "Libraries mirrored from a PC (Settings › Sync songs). Kept identical to the PC's: don't edit them."),
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
    /// boot's essentials are there the player may start anyway. Null: the player closed the setup screen. Blocks: call it
    /// from SDL's thread, never the UI thread.
    /// </summary>
    public string? Resolve()
    {
        createFolders();
        deleteDevelopmentCopy();
        var green = Path.Combine(Root, Green);
        SyncWire.Folder = Path.Combine(activity.FilesDir!.AbsolutePath, "waddamburo", "sync"); // Boot.AppHome's
        ReceivedLibraries.Root = Path.Combine(Root, "Received");
        ReceivedLibraries.GreenFolder = gameRoot(green);
        ReceivedLibraries.RecoverGreen();
        if (File.Exists(VerifiedMarker) && Waddamburo.App.Cli.GreenData.HasEssentials(gameRoot(green)))
            return gameRoot(green);
        if (Waddamburo.App.Cli.GreenData.Missing(gameRoot(green)) is [])
            return verified(gameRoot(green));

        // Pairing listens only while the setup waits (later, Settings › Sync songs pairs and syncs).
        using var setup = new GreenSetup(DeviceName);
        var status = new GreenSetupProgress(0, 0, GreenData.FileCount, false);
        using var stop = new CancellationTokenSource();
        // Counted every three seconds off the screen's thread (~20,000 files).
        var poll = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                Volatile.Write(ref status, GreenData.SetupProgress(gameRoot(green)));
                try { await Task.Delay(3000, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        });
        // Imported into the folder the count looks at (a USRDIR copied in whole is completed where it is).
        var import = new DeviceImport(gameRoot(green), Pick);
        var started = SetupScreen.Run(setup, () => Volatile.Read(ref status),
            $"Android/data/{activity.PackageName}/files/{Green}", import: import);
        stop.Cancel();
        poll.Wait();
        if (!started)
            return null;
        // A Green without (some) songs is whole too: remembered, the setup is not shown again.
        return GreenData.MissingRequired(gameRoot(green)) is [] ? verified(gameRoot(green)) : gameRoot(green);
    }

    private const int PickRequest = 0x6EE1;

    private static readonly string[] ArchiveTypes =
    [
        "application/zip", "application/x-zip-compressed", "application/x-7z-compressed",
        "application/rar", "application/vnd.rar", "application/x-rar-compressed",
    ];
    private Action<IReadOnlyList<Func<System.IO.Stream>>>? _picked;

    /// <summary>Android's file picker for one archive (no storage permission: the player grants the file they pick).</summary>
    // ponytail: by MIME type, so a provider that calls a .7z or .rar "application/octet-stream" greys it out; add
    // octet-stream back if players report it.
    private void Pick(Action<IReadOnlyList<Func<System.IO.Stream>>> picked)
    {
        _picked = picked;
        var intent = new Intent(Intent.ActionOpenDocument).AddCategory(Intent.CategoryOpenable).SetType("*/*")
            .PutExtra(Intent.ExtraMimeTypes, ArchiveTypes);
        activity.RunOnUiThread(() => activity.StartActivityForResult(intent, PickRequest));
    }

    /// <summary>The picker's answer (the activity's OnActivityResult).</summary>
    public void Picked(int request, Result result, Intent? data)
    {
        if (request != PickRequest || Interlocked.Exchange(ref _picked, null) is not { } picked)
            return;
        List<global::Android.Net.Uri> uris = [];
        if (result == Result.Ok && data?.ClipData is { } clip)
            for (var index = 0; index < clip.ItemCount; index++)
                uris.Add(clip.GetItemAt(index)!.Uri!);
        else if (result == Result.Ok && data?.Data is { } single)
            uris.Add(single);
        // A file descriptor, not the content stream: seekable, which zip and 7z need.
        picked([.. uris.Select(uri => (Func<System.IO.Stream>)(() =>
        {
            using var descriptor = activity.ContentResolver!.OpenFileDescriptor(uri, "r")
                ?? throw new IOException($"Cannot open {uri}");
            return new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(descriptor.DetachFd(), ownsHandle: true), FileAccess.Read);
        }))]);
    }

    /// <summary>How other devices list this one.</summary>
    public static string DeviceName => global::Android.OS.Build.Model ?? "Android";

    private string verified(string root)
    {
        File.WriteAllText(VerifiedMarker, root);
        return root;
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
