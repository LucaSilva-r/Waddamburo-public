using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Waddamburo.App.Sync;

/// <summary>A library as the serving device lists it.</summary>
public sealed record RemoteLibrary(string Id, string Kind, string Name, long Bytes, int Files);

/// <summary>What a mirror would change: files to fetch (and their bytes), files to remove.</summary>
public sealed record MirrorPlan(List<(string Path, long Size, long Time)> Fetch, long FetchBytes, List<string> Remove, int Have);

/// <summary>
/// The pulling side of song sync: talks to a paired device that serves (<see cref="SyncServer"/>) and mirrors its
/// libraries here. A mirror copies first and removes last, so an interrupted sync never leaves fewer songs; a list
/// that comes back empty removes nothing (an unplugged drive is not an empty library).
/// </summary>
public sealed class SyncClient(SyncDevice device, PairedDevice pair)
{
    private const int Workers = 4;
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(60);

    // Where the device is reached: a quicker link found during a mirror (USB tethering plugged in) replaces it.
    private volatile IPAddress _address = device.Address;

    public SyncDevice Device => device;

    /// <summary>The paired devices that serve and answer now.</summary>
    public static async Task<List<SyncClient>> FindAsync(TimeSpan wait, CancellationToken cancellationToken = default)
    {
        var paired = SyncWire.Paired().Where(static device => device.Serves).ToList();
        if (paired.Count == 0)
            return [];
        var found = await SyncWire.DiscoverAsync(SyncWire.SyncProbe, SyncWire.SyncPort, wait, cancellationToken).ConfigureAwait(false);
        return [.. found.Select(device => (device, pair: paired.FirstOrDefault(pair => pair.Id == device.Id)))
            .Where(static found => found.pair is not null).Select(static found => new SyncClient(found.device, found.pair!))];
    }

    public async Task<List<RemoteLibrary>> LibrariesAsync(CancellationToken cancellationToken = default) =>
        [.. (await askAsync(cancellationToken, "libraries").ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Split('\t')).Where(static parts => parts.Length == 5)
            .Select(static parts => new RemoteLibrary(parts[0], parts[1], parts[2], long.Parse(parts[3], CultureInfo.InvariantCulture),
                int.Parse(parts[4], CultureInfo.InvariantCulture)))];

    /// <summary>
    /// What mirroring <paramref name="library"/> into <paramref name="folder"/> would do. Green only adds (its folder is
    /// the device's own game data).
    /// </summary>
    public async Task<MirrorPlan> PlanAsync(RemoteLibrary library, string folder, CancellationToken cancellationToken = default)
    {
        var remote = (await askAsync(cancellationToken, "list", library.Id).ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Split('\t')).Where(static parts => parts.Length == 3)
            .Select(static parts => (Path: parts[0], Size: long.Parse(parts[1], CultureInfo.InvariantCulture), Time: long.Parse(parts[2], CultureInfo.InvariantCulture)))
            .ToList();
        var local = Local(folder);
        // FAT/exFAT keep times to 2 s: within that the file is the same.
        var fetch = remote.Where(file => !local.TryGetValue(file.Path, out var here) || here.Size != file.Size || Math.Abs(here.Time - file.Time) > 2).ToList();
        var names = remote.Select(static file => file.Path).ToHashSet(StringComparer.Ordinal);
        var remove = library.Kind == "green" || remote.Count == 0 ? [] : local.Keys.Where(path => !names.Contains(path)).ToList();
        return new MirrorPlan(fetch, fetch.Sum(static file => file.Size), remove, local.Count);
    }

    /// <summary>The files in a received library's folder: path ('/'), size, time. Leftover partial files are left out.</summary>
    public static Dictionary<string, (long Size, long Time)> Local(string folder) =>
        !Directory.Exists(folder) ? new(StringComparer.Ordinal) : new DirectoryInfo(folder)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
            .Where(static file => !file.Name.EndsWith(".part", StringComparison.Ordinal) && file.Name != ReceivedLibraries.Marker)
            .ToDictionary(file => Path.GetRelativePath(folder, file.FullName).Replace('\\', '/'),
                static file => (file.Length, new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds()), StringComparer.Ordinal);

    /// <summary>
    /// Carries out <paramref name="plan"/>: fetches (four at a time, each file under a temporary name until complete,
    /// with the sender's time), then removes. <paramref name="fetched"/> gets bytes as they land. A failing file is
    /// tried again for about three minutes before the sync stops (the next one goes on from there).
    /// </summary>
    public async Task MirrorAsync(RemoteLibrary library, string folder, MirrorPlan plan, Action<long>? fetched = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        if (library.Kind != "green")
            File.WriteAllText(Path.Combine(folder, ReceivedLibraries.Marker), $"{library.Kind}\t{SyncWire.clean(library.Name)}\n");
        var queue = new Queue<(string Path, long Size, long Time)>(plan.Fetch.OrderBy(static file => file.Path, StringComparer.Ordinal));
        async Task worker()
        {
            TcpClient? connection = null;
            IPAddress? connectedTo = null;
            try
            {
                while (true)
                {
                    (string Path, long Size, long Time) file;
                    lock (queue)
                        if (!queue.TryDequeue(out file))
                            return;
                    if (safePath(folder, file.Path) is not { } destination)
                        throw new InvalidDataException($"Bad path from the other device: {file.Path}");
                    for (var attempt = 0; ; attempt++)
                    {
                        try
                        {
                            if (connection is not null && !_address.Equals(connectedTo))
                            {
                                connection.Dispose(); // a quicker link: the next file goes over it
                                connection = null;
                            }
                            connectedTo = _address;
                            connection ??= await SyncWire.ConnectAsync(connectedTo, SyncWire.SyncPort, cancellationToken).ConfigureAwait(false);
                            await fetchAsync(connection.GetStream(), library, file.Path, file.Time, destination, fetched, cancellationToken).ConfigureAwait(false);
                            break;
                        }
                        catch (SyncRefusedException refused) when (refused.Code == SyncWire.Missing)
                        {
                            break; // gone from the other device since the list: the next sync removes it here
                        }
                        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && attempt < 20
                            && exception is IOException or SocketException or OperationCanceledException && exception is not SyncRefusedException { Code: SyncWire.Refused })
                        {
                            // Dropped, timed out, or the file changed on the other side: a new connection, a fresh try.
                            connection?.Dispose();
                            connection = null;
                            await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * (attempt + 1), 10)), cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            finally
            {
                connection?.Dispose();
            }
        }
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watch = Task.Run(() => watchLinks(watching.Token), CancellationToken.None);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, Workers).Select(_ => worker())).ConfigureAwait(false);
        }
        finally
        {
            watching.Cancel();
            await watch.ConfigureAwait(false);
        }
        // Removals last, and only inside the library's folder.
        foreach (var path in plan.Remove)
            if (safePath(folder, path) is { } doomed)
                File.Delete(doomed);
        foreach (var partial in Directory.EnumerateFiles(folder, "*.part", SearchOption.AllDirectories))
            File.Delete(partial);
        deleteEmptyFolders(folder);
    }

    private async Task fetchAsync(NetworkStream stream, RemoteLibrary library, string path, long time, string destination,
        Action<long>? fetched, CancellationToken cancellationToken)
    {
        // Async socket reads ignore the socket's timeouts: a stall of a minute cancels instead.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(Stall);
        await SyncWire.WriteRequestAsync(stream, stall.Token, pair.Token, "file", library.Id, path).ConfigureAwait(false);
        var length = await SyncWire.ReadLengthAsync(stream, stall.Token).ConfigureAwait(false);
        if (length < 0)
            throw new SyncRefusedException(length);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".part";
        var buffer = new byte[1 << 20];
        var counted = 0L;
        try
        {
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var left = length;
                while (left > 0)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), stall.Token).ConfigureAwait(false);
                    if (count == 0)
                        throw new EndOfStreamException();
                    stall.CancelAfter(Stall);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    left -= count;
                    counted += count;
                    fetched?.Invoke(count);
                }
            }
            File.SetLastWriteTimeUtc(partial, DateTimeOffset.FromUnixTimeSeconds(time).UtcDateTime);
            File.Move(partial, destination, overwrite: true);
        }
        catch
        {
            fetched?.Invoke(-counted); // tried again from the start
            throw;
        }
    }

    // Every ten seconds during a mirror: the device's best address now (discovery sorts USB tethering first).
    private async Task watchLinks(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                var found = await SyncWire.DiscoverAsync(SyncWire.SyncProbe, SyncWire.SyncPort, TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
                if (found.FirstOrDefault(other => other.Id == device.Id) is { } best && !best.Address.Equals(_address))
                {
                    Console.WriteLine($"Song sync: {device.Name} now over {best.Address}.");
                    _address = best.Address;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception exception) when (exception is IOException or SocketException) { }
        }
    }

    // A connection per question: libraries and lists are asked once per sync.
    private async Task<string> askAsync(CancellationToken cancellationToken, params string[] fields)
    {
        using var client = await SyncWire.ConnectAsync(_address, SyncWire.SyncPort, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2)); // the other side walks the library's folders first
        return await SyncWire.AskAsync(client.GetStream(), timeout.Token, [pair.Token, .. fields]).ConfigureAwait(false);
    }

    // A path inside root, or null: relative, no "..", no empty parts (the other device is trusted with nothing).
    private static string? safePath(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.Contains('\0') || relative.Contains(':'))
            return null;
        if (relative.Split('/').Any(static part => part is "" or "." or ".."))
            return null;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return full.StartsWith(rootFull, StringComparison.Ordinal) ? full : null;
    }

    private static void deleteEmptyFolders(string folder)
    {
        foreach (var child in Directory.EnumerateDirectories(folder))
        {
            deleteEmptyFolders(child);
            if (!Directory.EnumerateFileSystemEntries(child).Any())
                Directory.Delete(child);
        }
    }
}

/// <summary>
/// Libraries mirrored from other devices: <c>&lt;received&gt;/&lt;device id&gt;/&lt;library id&gt;/</c>, each with a
/// <see cref="Marker"/> naming its kind. They join this device's libraries of the same kind at boot (SongLibraries).
/// </summary>
public static class ReceivedLibraries
{
    public const string Marker = ".waddamburo-library";

    /// <summary>Where they live; set at boot (null: this device receives nothing).</summary>
    public static string? Root { get; set; }

    /// <summary>Where Green's files go on this device (its own Green folder), null when it can't take them (a desktop).</summary>
    public static string? GreenFolder { get; set; }

    /// <summary>The folder a library from <paramref name="deviceId"/> mirrors into; null: this device can't take it.</summary>
    public static string? FolderOf(string deviceId, RemoteLibrary library) =>
        library.Kind == "green" ? GreenFolder : Root is null ? null : Path.Combine(Root, deviceId, library.Id);

    /// <summary>Every received library: kind and folder.</summary>
    public static List<(string Kind, string Folder)> All() =>
        Root is null || !Directory.Exists(Root) ? [] : [.. Directory.EnumerateDirectories(Root).SelectMany(Directory.EnumerateDirectories)
            .Where(static folder => File.Exists(Path.Combine(folder, Marker)))
            .Select(static folder => (File.ReadAllText(Path.Combine(folder, Marker)).Split('\t')[0].Trim(), folder))
            .OrderBy(static library => library.folder, StringComparer.Ordinal)];

    /// <summary>The received libraries of <paramref name="kind"/>, each as the folder its provider reads.</summary>
    public static IEnumerable<string> Folders(string kind) => All().Where(library => library.Kind == kind).Select(static library => library.Folder);

    /// <summary>Removes a library switched off (its whole folder).</summary>
    public static void Remove(string folder)
    {
        if (Root is not null && Path.GetFullPath(folder).StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }
}
