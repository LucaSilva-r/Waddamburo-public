using System.IO.Compression;
using Waddamburo.App.Sync;

namespace Waddamburo.App.Cli;

public sealed record GreenSetupProgress(int Received, int Installed, int Total, bool Essentials)
{
    public bool Complete => Installed == Total;
}

/// <summary>
/// Whether a folder holds all of Green's game data: every file its install manifest lists (hash/S111_00.md5, the
/// release; eng/green-data-list.py), by name only (language packs change sizes, not names). A copy still in progress or a
/// broken dump shows what is missing instead of failing at boot.
/// </summary>
public static class GreenData
{
    private static readonly Lazy<string[]> s_files = new(load);

    /// <summary>Files the boot reads first: with these the game can start even if others are missing.</summary>
    private static readonly string[] Essentials =
    [
        "data/lumendata/packed", "data/don3d", "data/sound/se", "data/sound/bgm", "data/sound/config/nuSound2BankStr.bin",
    ];

    public static int FileCount => s_files.Value.Length;

    /// <summary>The listed files missing from <paramref name="root"/> (Green's USRDIR).</summary>
    public static IReadOnlyList<string> Missing(string root) =>
        [.. s_files.Value.Where(path => !File.Exists(Path.Combine(root, path)))];

    public static GreenSetupProgress SetupProgress(string root)
    {
        var staging = SongSyncStaging.StagedFolder(root);
        var hasStaging = Directory.Exists(staging);
        var received = 0;
        var installed = 0;
        foreach (var path in s_files.Value)
        {
            // Check the staging path first because a completed file only moves from staging to its final path.
            var staged = hasStaging && File.Exists(Path.Combine(staging, path));
            var published = File.Exists(Path.Combine(root, path));
            if (staged || published)
                received++;
            if (published)
                installed++;
        }
        return new GreenSetupProgress(received, installed, FileCount, HasEssentials(root));
    }

    /// <summary>Whether the boot's essentials are there (the game can start, "anyway").</summary>
    public static bool HasEssentials(string root) => Essentials.All(path => Path.Exists(Path.Combine(root, path)));

    internal static bool IsEssentialFile(string path) => Essentials.Any(essential =>
        path == essential || path.StartsWith(essential + "/", StringComparison.Ordinal));

    private static string[] load()
    {
        using var stream = typeof(GreenData).Assembly.GetManifestResourceStream("Waddamburo.GreenData.txt.gz")
            ?? throw new InvalidOperationException("The Green data list is missing from the build.");
        using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress));
        List<string> files = [];
        while (reader.ReadLine() is { Length: > 0 } line)
            files.Add(line);
        return [.. files];
    }
}
