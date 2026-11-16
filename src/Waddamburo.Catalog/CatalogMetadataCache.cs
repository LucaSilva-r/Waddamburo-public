using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;

namespace Waddamburo.Catalog;

/// <summary>Disposable, local metadata acceleration. Source files remain authoritative.</summary>
public sealed class CatalogMetadataCache(string databasePath)
{
    /// <summary>A source tree is a game installation: links are followed and unreadable folders passed over, as
    /// plain file probes would. Otherwise links are left out and an unreadable folder fails the scan (user libraries).
    /// <paramref name="within"/> keeps the inventory to those folders and files below the root (the rest of an
    /// installation is never walked): everything the scan asks about must lie in them.</summary>
    public CatalogMetadataScope Open(string root, string parserVersion, bool sourceTree = false,
        IReadOnlyList<string>? within = null, CancellationToken cancellationToken = default) =>
        new(databasePath, root, parserVersion, sourceTree, within, cancellationToken, check => { lock (_checks) _checks.Add(check); });

    private readonly List<Task> _checks = [];

    /// <summary>Completes when the background checks of the installations started from their last inventory are done.</summary>
    public Task Revalidations { get { lock (_checks) return Task.WhenAll(_checks); } }
}

/// <summary>One inventory and one batched SQLite transaction per library scan.</summary>
public sealed class CatalogMetadataScope : IDisposable
{
    private sealed record Stamp(long Size, long Modified);
    private sealed record Entry(long Size, long Modified, string Json);
    private readonly Dictionary<string, Stamp> _files;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);
    private readonly System.Diagnostics.Stopwatch _elapsed = System.Diagnostics.Stopwatch.StartNew();
    private readonly string _root;
    private readonly string _space;
    private readonly CancellationToken _cancellation;
    private SqliteConnection? _connection;
    private bool _disposed;
    private bool _changed;
    private readonly string _database;
    private readonly bool _sourceTree;
    private readonly IReadOnlyList<string>? _within;
    private readonly string _inventory;
    private bool _fromSnapshot;
    // One background walk at a time: several releases walked at once thrash a spinning disk.
    private static readonly SemaphoreSlim Walking = new(1, 1);
    private readonly Action<Task> _checking;
    public int Hits { get; private set; }
    public int Misses { get; private set; }

    internal CatalogMetadataScope(string database, string root, string version, bool sourceTree, IReadOnlyList<string>? within, CancellationToken cancellation, Action<Task> checking)
    {
        _cancellation = cancellation;
        _checking = checking;
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _space = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_root + "|" + version)));
        _files = newInventory();
        _database = database;
        _sourceTree = sourceTree;
        _within = within;
        _inventory = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_space + "|" + string.Join('|', within ?? []))));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(database))!);
                _connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = database, DefaultTimeout = 2, Pooling = false,
                }.ToString());
                _connection.Open();
                using var command = _connection.CreateCommand();
                command.CommandText = "CREATE TABLE IF NOT EXISTS file_metadata_v1 (scope TEXT NOT NULL, path TEXT NOT NULL, size INTEGER NOT NULL, modified INTEGER NOT NULL, json TEXT NOT NULL, PRIMARY KEY(scope,path));"
                    + "CREATE TABLE IF NOT EXISTS file_inventory_v1 (scope TEXT NOT NULL, path TEXT NOT NULL, size INTEGER NOT NULL, modified INTEGER NOT NULL, PRIMARY KEY(scope,path));";
                command.ExecuteNonQuery();
                // An installation's last inventory stands in for the walk (a spinning disk takes most of the boot
                // walking a dozen releases); Dispose walks it again in the background. User libraries are walked
                // now: their files are edited in place, and the edit must show on the next start.
                // ponytail: a changed installation shows its changes one start late; swap the catalog live if that bites.
                if (sourceTree)
                {
                    command.CommandText = "SELECT path,size,modified FROM file_inventory_v1 WHERE scope=$scope";
                    command.Parameters.AddWithValue("$scope", _inventory);
                    using (var stored = command.ExecuteReader())
                        while (stored.Read())
                            _files[Path.Join(_root, stored.GetString(0))] = new Stamp(stored.GetInt64(1), stored.GetInt64(2));
                    _fromSnapshot = _files.Count > 0;
                    command.Parameters.Clear();
                }
                command.CommandText = "SELECT path,size,modified,json FROM file_metadata_v1 WHERE scope=$scope";
                command.Parameters.AddWithValue("$scope", _space);
                using var reader = command.ExecuteReader();
                while (reader.Read()) _entries[reader.GetString(0)] = new Entry(reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3));
                break;
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
            {
                _connection?.Dispose();
                _connection = null;
                _entries.Clear();
                _files.Clear();
                _fromSnapshot = false;
                if (attempt == 0 && exception is SqliteException { SqliteErrorCode: 11 or 26 })
                {
                    // This database is disposable. Rebuild corrupt storage from the source files.
                    try { File.Delete(database); continue; }
                    catch (Exception removal) when (removal is IOException or UnauthorizedAccessException) { }
                }
                Console.Error.WriteLine($"Catalog cache unavailable; scanning source metadata: {exception.Message}");
                break;
            }
        }
        if (!_fromSnapshot)
            foreach (var (path, stamp) in walk(_root, sourceTree, within, cancellation))
                _files[path] = stamp;
    }

    private static Dictionary<string, Stamp> newInventory() =>
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static Dictionary<string, Stamp> walk(string root, bool sourceTree, IReadOnlyList<string>? within, CancellationToken cancellation)
    {
        var found = newInventory();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = sourceTree,
            // ponytail: a link looping back into the tree recurses until the path is too long; track visited folders if seen.
            AttributesToSkip = sourceTree ? 0 : FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };
        var files = within is null ? new DirectoryInfo(root).EnumerateFiles("*", options)
            : within.Select(part => Path.Combine(root, part)).SelectMany(part => Directory.Exists(part)
                ? new DirectoryInfo(part).EnumerateFiles("*", options) : [new FileInfo(part)]);
        foreach (var file in files)
        {
            cancellation.ThrowIfCancellationRequested();
            // A linked file is stamped by what it points at (its own size and time never change); a dead link is no file.
            if (target(file) is { } real)
                found[file.FullName] = new Stamp(real.Length, real.LastWriteTimeUtc.Ticks);
        }
        return found;
    }

    private void saveInventory(SqliteConnection connection, Dictionary<string, Stamp> files)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM file_inventory_v1 WHERE scope=$scope";
        command.Parameters.AddWithValue("$scope", _inventory); command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO file_inventory_v1(scope,path,size,modified) VALUES($scope,$path,$size,$modified)";
        var path = command.Parameters.Add("$path", SqliteType.Text);
        var size = command.Parameters.Add("$size", SqliteType.Integer);
        var modified = command.Parameters.Add("$modified", SqliteType.Integer);
        foreach (var (file, stamp) in files)
        {
            path.Value = Path.GetRelativePath(_root, file); size.Value = stamp.Size; modified.Value = stamp.Modified;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    // Walks the installation again after a start from its stored inventory; a difference is stored for the next start.
    private async Task revalidate()
    {
        await Walking.WaitAsync().ConfigureAwait(false);
        try
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var now = walk(_root, _sourceTree, _within, CancellationToken.None);
            if (now.Count == _files.Count && now.All(file => _files.GetValueOrDefault(file.Key) == file.Value)) return;
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _database, DefaultTimeout = 2, Pooling = false,
            }.ToString());
            connection.Open();
            saveInventory(connection, now);
            Console.WriteLine($"Library {_root} changed ({elapsed.Elapsed.TotalSeconds:F1}s check): the changes show on the next start.");
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine($"Library check of {_root} failed: {exception.Message}"); }
        finally { Walking.Release(); }
    }

    public IEnumerable<string> Paths => _files.Keys;

    public long? Size(string path) => _files.GetValueOrDefault(Path.GetFullPath(path))?.Size;

    public bool Exists(string path) => _files.ContainsKey(Path.GetFullPath(path));

    public T Read<T>(string path, string kind, Func<T> read, JsonTypeInfo<T> jsonType, List<CatalogDiagnostic>? diagnostics = null)
    {
        _cancellation.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        if (!_files.TryGetValue(path, out var stamp)) return read();
        var key = Path.GetRelativePath(_root, path).Replace('\\', '/') + "|" + kind;
        _used.Add(key);
        if (_entries.TryGetValue(key, out var entry) && entry.Size == stamp.Size && entry.Modified == stamp.Modified)
        {
            try
            {
                var cachedValue = JsonSerializer.Deserialize(entry.Json, jsonType);
                Hits++;
                return cachedValue!;
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException) { }
        }
        Misses++;
        var before = diagnostics?.Count ?? 0;
        var value = read();
        var additions = diagnostics?.Skip(before).ToArray() ?? [];
        // Do not persist errors or a file which changed while it was being parsed.
        if (additions.Length == 0 && target(new FileInfo(path)) is { } after && after.Length == stamp.Size && after.LastWriteTimeUtc.Ticks == stamp.Modified)
        {
            _entries[key] = new Entry(stamp.Size, stamp.Modified, JsonSerializer.Serialize(value, jsonType));
            _changed = true;
        }
        return value;
    }

    private static FileInfo? target(FileInfo file)
    {
        if (!file.Exists) return null;
        if (!file.Attributes.HasFlag(FileAttributes.ReparsePoint)) return file;
        try { return file.ResolveLinkTarget(true) is FileInfo { Exists: true } real ? real : null; }
        catch (IOException) { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_connection is null) return;
        try
        {
            if (_cancellation.IsCancellationRequested) return;
            if (_fromSnapshot) _checking(Task.Run(revalidate));
            else if (_sourceTree) saveInventory(_connection, _files);
            if (!_changed && _used.SetEquals(_entries.Keys)) return;
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM file_metadata_v1 WHERE scope=$scope";
            command.Parameters.AddWithValue("$scope", _space); command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO file_metadata_v1(scope,path,size,modified,json) VALUES($scope,$path,$size,$modified,$json)";
            var path = command.Parameters.Add("$path", SqliteType.Text);
            var size = command.Parameters.Add("$size", SqliteType.Integer);
            var modified = command.Parameters.Add("$modified", SqliteType.Integer);
            var json = command.Parameters.Add("$json", SqliteType.Text);
            foreach (var key in _used)
                if (_entries.TryGetValue(key, out var entry))
                {
                    path.Value = key; size.Value = entry.Size; modified.Value = entry.Modified; json.Value = entry.Json;
                    command.ExecuteNonQuery();
                }
            transaction.Commit();
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine($"Catalog cache not saved: {exception.Message}"); }
        finally
        {
            _connection.Dispose();
            Console.WriteLine($"Catalog metadata cache: {Hits} hits, {Misses} misses, {_files.Count} files {(_fromSnapshot ? "from the last inventory" : "inventoried")}, {_elapsed.Elapsed.TotalSeconds:F2}s.");
        }
    }
}
