using System.IO.Compression;

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
    private static readonly Lazy<(string[] Release, string[] Updates)> s_lists = new(load);

    /// <summary>Files the boot reads first: with these the game can start even if others are missing.</summary>
    private static readonly string[] Essentials =
    [
        "data/lumendata/packed", "data/don3d", "data/sound/se", "data/sound/bgm", "data/sound/config/nuSound2BankStr.bin",
    ];

    public static int FileCount => s_lists.Value.Release.Length;

    /// <summary>A song's chart or music: a Green may lack it (one shrunk to its system files, played with custom songs);
    /// the catalog leaves out songs whose files are missing.</summary>
    public static bool IsSongFile(string path) =>
        path.StartsWith("data/fumen/", StringComparison.Ordinal)
        || path.StartsWith("data/sound/bgm/nub/SONG_", StringComparison.Ordinal)
        || path.StartsWith("data/sound/bgm/nsh/SONG_", StringComparison.Ordinal);

    /// <summary>The listed files missing from <paramref name="root"/>, songs' files aside: none, and the game is whole.</summary>
    /// <summary>Whether <paramref name="root"/> (Green's folder, holding data) has AI battle: it came with an update,
    /// so a release-only copy (the web archive's) has none. ponytail: one movie stands for the update's ghost files.</summary>
    public static bool HasAiBattle(string root) =>
        File.Exists(Path.Combine(root, "data", "lumendata", "packed", "ghost", "song_select", "packeddata.ddp"));

    public static IReadOnlyList<string> MissingRequired(string root) => [.. Missing(root).Where(static path => !IsSongFile(path))];

    /// <summary>The listed files missing from <paramref name="root"/> (Green's USRDIR).</summary>
    public static IReadOnlyList<string> Missing(string root) =>
        [.. s_lists.Value.Release.Where(path => !File.Exists(Path.Combine(root, path)))];

    public static GreenSetupProgress SetupProgress(string root)
    {
        var staging = SongSyncStaging.StagedFolder(root);
        var hasStaging = Directory.Exists(staging);
        var received = 0;
        var installed = 0;
        foreach (var path in s_lists.Value.Release)
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

    /// <summary>Every file an import takes: the release's, then those its updates (S111_01 to _13) add, which a Green
    /// without them plays without.</summary>
    public static IEnumerable<string> ImportFiles => s_lists.Value.Release.Concat(s_lists.Value.Updates);

    private static (string[], string[]) load()
    {
        using var stream = typeof(GreenData).Assembly.GetManifestResourceStream("Waddamburo.GreenData.txt.gz")
            ?? throw new InvalidOperationException("The Green data list is missing from the build.");
        using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress));
        List<string> files = [], updates = [];
        while (reader.ReadLine() is { Length: > 0 } line)
            if (line[0] == '+')
                updates.Add(line[1..]);
            else
                files.Add(line);
        return ([.. files], [.. updates]);
    }
}
