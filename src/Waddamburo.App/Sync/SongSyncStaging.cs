namespace Waddamburo.App.Sync;

/// <summary>Stages changed files out of sight, then publishes whole dependency groups. A journal lets the next
/// catalog scan finish an interrupted publication before it reads visible files.</summary>
internal sealed class SongSyncStaging
{
    private const string FolderName = ".waddamburo-staging";
    private const string JournalName = ".waddamburo-commit";
    internal const string TimesName = ".waddamburo-times"; // SyncClient: the sender's times of received files
    private readonly string _folder;
    private readonly List<string> _remove;
    private static readonly object PublishLock = new();
    private readonly Dictionary<string, Group> _groups = new(StringComparer.Ordinal);

    private sealed class Group(string[] paths)
    {
        public string[] Paths { get; } = paths;
        public int Pending = paths.Length;
    }

    public SongSyncStaging(string folder, MirrorPlan plan)
    {
        _folder = folder;
        _remove = plan.Remove;
        var files = plan.Fetch.ToDictionary(static file => file.Path, StringComparer.Ordinal);
        var groups = groupFiles(files.Keys, plan.Manifest, plan.Have > 0).ToArray();
        foreach (var paths in groups)
        {
            var group = new Group(paths);
            foreach (var path in paths)
                _groups.Add(path, group);
        }
        OrderedFiles = groups.SelectMany(paths => paths.Select(path => files[path]));
    }

    public IEnumerable<(string Path, long Size, long Time)> OrderedFiles { get; }

    public static bool IsInternal(string path) => path.Equals(ReceivedLibraries.Marker, StringComparison.OrdinalIgnoreCase)
        || path.Equals(JournalName, StringComparison.OrdinalIgnoreCase)
        || path.Equals(JournalName + ".part", StringComparison.OrdinalIgnoreCase)
        || path.Equals(TimesName, StringComparison.OrdinalIgnoreCase)
        || path.Equals(TimesName + ".part", StringComparison.OrdinalIgnoreCase)
        || path.Equals(FolderName, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(FolderName + "/", StringComparison.OrdinalIgnoreCase);

    internal static string StagedFolder(string folder) => Path.Combine(folder, FolderName);

    public string Destination(string path) => pathIn(StagedFolder(_folder), path);

    public static bool IsStaged(string folder, (string Path, long Size, long Time) file)
    {
        var path = SyncClient.SafePath(StagedFolder(folder), file.Path);
        return path is not null && File.Exists(path) && new FileInfo(path) is var info && info.Length == file.Size
            && Math.Abs(new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds() - file.Time) <= 2;
    }

    public void Ready(string path, Action? published)
    {
        var group = _groups[path];
        if (Interlocked.Decrement(ref group.Pending) == 0 && _remove.Count == 0)
            publish(group.Paths, [], published);
    }

    public void Finish(Action? published)
    {
        // Without an inventory of earlier song versions, a removed chart or audio file may belong to any changed song.
        // Keep the old versions until all replacements are ready and include the removals in that publication.
        if (_remove.Count > 0)
            publish(_groups.Keys, _remove, published);
    }

    private void publish(IEnumerable<string> copies, IEnumerable<string> removals, Action? published)
    {
        lock (PublishLock)
        {
            recover(_folder);
            var journal = Path.Combine(_folder, JournalName);
            File.WriteAllLines(journal + ".part", copies.Select(static path => "+" + path).Concat(removals.Select(static path => "-" + path)));
            File.Move(journal + ".part", journal, overwrite: true);
            recover(_folder);
        }
        published?.Invoke();
    }

    public static void Recover(string folder)
    {
        lock (PublishLock)
            recover(folder);
    }

    private static void recover(string folder)
    {
        var journal = Path.Combine(folder, JournalName);
        if (!File.Exists(journal))
            return;
        foreach (var entry in File.ReadLines(journal))
        {
            if (entry.Length < 2 || entry[0] is not ('+' or '-'))
                throw new InvalidDataException("Invalid song publication journal.");
            var path = entry[1..];
            var destination = pathIn(folder, path);
            if (entry[0] == '-')
            {
                File.Delete(destination);
                continue;
            }
            var source = pathIn(StagedFolder(folder), path);
            if (!File.Exists(source))
                continue; // published already, or skipped (gone or changed on the sender)
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination, overwrite: true);
        }
        File.Delete(journal);
    }

    public static void Clean(string folder)
    {
        var staging = StagedFolder(folder);
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
    }

    private static string pathIn(string folder, string path) =>
        !IsInternal(path) && SyncClient.SafePath(folder, path) is { } full
            ? full : throw new InvalidDataException("Invalid song sync path.");

    private static IEnumerable<string[]> groupFiles(IEnumerable<string> files, SongSyncManifest? manifest, bool hasLibrary)
    {
        var pending = files.ToHashSet(StringComparer.Ordinal);
        if (manifest is null)
        {
            // Older senders can mirror a library, but cannot say which files make one song playable.
            if (pending.Count > 0)
                yield return [.. pending.Order(StringComparer.Ordinal)];
            yield break;
        }

        var shared = manifest.Shared.Where(pending.Remove).Distinct(StringComparer.Ordinal).ToArray();
        if (shared.Length > 0 && hasLibrary)
        {
            // Changing metadata already used by the library can change several songs at once.
            var dependent = manifest.Songs.SelectMany(static song => song).Where(pending.Remove);
            yield return [.. shared, .. dependent];
        }
        else if (shared.Length > 0)
            yield return shared;

        // Several songs can share one physical file. Publish all of their changed files together.
        var byPath = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var song in manifest.Songs)
        {
            var changed = song.Where(pending.Contains).Distinct(StringComparer.Ordinal).ToArray();
            if (changed.Length == 0)
                continue;
            var connected = changed.Select(path => byPath.GetValueOrDefault(path)).OfType<HashSet<string>>().Distinct().ToArray();
            var group = connected.FirstOrDefault() ?? new HashSet<string>(StringComparer.Ordinal);
            foreach (var other in connected.Skip(1))
            {
                foreach (var path in other)
                    byPath[path] = group;
                group.UnionWith(other);
            }
            foreach (var path in changed)
            {
                group.Add(path);
                byPath[path] = group;
            }
        }

        foreach (var group in byPath.Values.Distinct())
        {
            pending.ExceptWith(group);
            yield return [.. group.Order(StringComparer.Ordinal)];
        }

        // Install everything else as well. Some of these may be alternate versions or optional song files,
        // so do not expose them during an incomplete copy of the remainder.
        if (pending.Count > 0)
            yield return [.. pending.Order(StringComparer.Ordinal)];
    }
}
