using Waddamburo.App.Cli;
using Waddamburo.App.Sync;

namespace Waddamburo.iOS;

/// <summary>
/// The game's folders in the app's Documents (the Files app shows them): Green (the game data), Custom Songs (TJA), and
/// Arcade, Wii, Wii U (one subfolder per release), Received (libraries mirrored from a PC). Until Green holds the game data
/// the setup screen pairs with a PC and takes Green from it (<see cref="GreenSetup"/>).
/// </summary>
/// <remarks>ponytail: Android's GameFolder less its archive picker and media scanner; share the rest once both settle.
/// An archive import needs UIDocumentPickerViewController.</remarks>
internal sealed class GameFolder(string root)
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

    public string Root { get; } = root;

    /// <summary>How other devices list this one.</summary>
    public static string DeviceName => "iPhone";

    // Written once a copy is complete: later starts skip checking its ~20,000 files.
    private static string VerifiedMarker => Path.Combine(App.Boot.AppHome!, "green-verified");

    /// <summary>
    /// Creates the folders and returns the game data folder; until it holds Green, the setup screen says how to send it
    /// (from a PC over Wi-Fi, or the Files app). Null: the player closed the setup screen.
    /// </summary>
    public string? Resolve()
    {
        foreach (var (folder, note) in Notes)
        {
            Directory.CreateDirectory(Path.Combine(Root, folder));
            var file = Path.Combine(Root, folder, "README.txt");
            if (!File.Exists(file))
                File.WriteAllText(file, note + "\n");
        }
        Directory.CreateDirectory(App.Boot.AppHome!);
        var green = gameRoot(Path.Combine(Root, Green));
        SyncWire.Folder = Path.Combine(App.Boot.AppHome!, "sync");
        ReceivedLibraries.Root = Path.Combine(Root, "Received");
        ReceivedLibraries.GreenFolder = green;
        ReceivedLibraries.RecoverGreen();
        if (File.Exists(VerifiedMarker) && GreenData.HasEssentials(green))
            return green;
        if (GreenData.Missing(green) is [])
            return verified(green);

        using var setup = new GreenSetup(DeviceName);
        var status = new GreenSetupProgress(0, 0, GreenData.FileCount, false);
        using var stop = new CancellationTokenSource();
        var poll = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                Volatile.Write(ref status, GreenData.SetupProgress(gameRoot(Path.Combine(Root, Green))));
                try { await Task.Delay(3000, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        });
        var started = SetupScreen.Run(setup, () => Volatile.Read(ref status), $"Files › On My iPhone › Waddamburo › {Green}");
        stop.Cancel();
        poll.Wait();
        if (!started)
            return null;
        green = gameRoot(Path.Combine(Root, Green));
        return GreenData.MissingRequired(green) is [] ? verified(green) : green;
    }

    /// <summary>The release folders inside Arcade, Wii and Wii U (as on Android).</summary>
    public IReadOnlyList<string> ReleaseFolders() =>
        [.. Releases.Select(release => Path.Combine(Root, release)).Where(Directory.Exists).SelectMany(Directory.EnumerateDirectories),
            .. Directory.Exists(Path.Combine(Root, "Wii U"))
                ? Directory.EnumerateDirectories(Path.Combine(Root, "Wii U")).SelectMany(Directory.EnumerateDirectories) : []];

    private static string verified(string root)
    {
        File.WriteAllText(VerifiedMarker, root);
        return root;
    }

    // Green itself, or a USRDIR copied in whole.
    private static string gameRoot(string green) =>
        Directory.Exists(Path.Combine(green, "USRDIR", "data")) ? Path.Combine(green, "USRDIR") : green;
}
