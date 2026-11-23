using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Waddamburo.App.Sync;

/// <summary>A library as the serving device lists it.</summary>
public sealed record RemoteLibrary(string Id, string Kind, string Name, long Bytes, int Files);

/// <summary>What a mirror would change: files to fetch (and their bytes), files to remove.</summary>
public sealed record MirrorPlan(List<(string Path, long Size, long Time)> Fetch, long FetchBytes, List<string> Remove, int Have)
{
    internal SongSyncManifest? Manifest { get; init; }
}

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
        // Where each answered last: one that still does needs no discovery.
        var clients = (await Task.WhenAll(paired.Select(pair => atLastAddress(pair, cancellationToken))).ConfigureAwait(false))
            .OfType<SyncClient>().ToList();
        if (clients.Count == paired.Count)
            return clients;
        var found = await SyncWire.DiscoverAsync(SyncWire.SyncProbe, SyncWire.SyncPort, wait, cancellationToken).ConfigureAwait(false);
        foreach (var device in found)
            if (paired.FirstOrDefault(pair => pair.Id == device.Id) is { } pair && clients.All(client => client.Device.Id != device.Id))
            {
                SyncWire.RememberAddress(device.Id, device.Address);
                clients.Add(new SyncClient(device, pair));
            }
        return clients;
    }

    private static async Task<SyncClient?> atLastAddress(PairedDevice pair, CancellationToken cancellationToken)
    {
        if (SyncWire.LastAddress(pair.Id) is not { } address)
            return null;
        var client = new SyncClient(new SyncDevice(pair.Id, pair.Name, address), pair);
        using var quick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        quick.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            var hello = await client.askWithTimeoutAsync(TimeSpan.FromSeconds(1), ["hello"], quick.Token).ConfigureAwait(false);
            return hello.Split('\t') is [var id, var name] && id == pair.Id ? new SyncClient(new SyncDevice(id, name, address), pair) : null;
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or InvalidDataException)
        {
            return null; // elsewhere now, or off: discovery
        }
    }

    public async Task<List<RemoteLibrary>> LibrariesAsync(CancellationToken cancellationToken = default) =>
        [.. (await askAsync(cancellationToken, "libraries", "nijiiro-v1").ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Split('\t')).Where(static parts => parts.Length == 5)
            .Select(static parts => new RemoteLibrary(parts[0], parts[1], parts[2], long.Parse(parts[3], CultureInfo.InvariantCulture),
                int.Parse(parts[4], CultureInfo.InvariantCulture)))];

    /// <summary>
    /// What mirroring <paramref name="library"/> into <paramref name="folder"/> would do. Green only adds (its folder is
    /// the device's own game data).
    /// </summary>
    public async Task<MirrorPlan> PlanAsync(RemoteLibrary library, string folder, CancellationToken cancellationToken = default)
    {
        string inventory;
        try
        {
            inventory = await askWithTimeoutAsync(library.Kind == NijiiroSync.Kind ? TimeSpan.FromHours(1) : StandardQueryWait,
                ["list", library.Id], cancellationToken).ConfigureAwait(false);
        }
        catch (SyncRefusedException exception) when (library.Kind == NijiiroSync.Kind && exception.Code == SyncWire.Failed)
        {
            throw new IOException(Strings.T("sync.nijiiro.prepare_failed"), exception);
        }
        var remote = inventory.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Split('\t')).Where(static parts => parts.Length == 3)
            .Select(static parts => (Path: parts[0], Size: long.Parse(parts[1], CultureInfo.InvariantCulture), Time: long.Parse(parts[2], CultureInfo.InvariantCulture)))
            .ToList();
        SongSyncStaging.Recover(folder);
        if (remote.Any(file => file.Size < 0 || SongSyncStaging.IsInternal(file.Path) || SafePath(folder, file.Path) is null))
            throw new InvalidDataException("The other device listed an invalid library file.");
        var local = Local(folder);
        // FAT/exFAT keep times to 2 s: within that the file is the same.
        var fetch = remote.Where(file => !local.TryGetValue(file.Path, out var here) || here.Size != file.Size || Math.Abs(here.Time - file.Time) > 2).ToList();
        var names = remote.Select(static file => file.Path).ToHashSet(StringComparer.Ordinal);
        var remove = library.Kind == "green" || remote.Count == 0 ? [] : local.Keys.Where(path => !names.Contains(path)).ToList();
        SongSyncManifest? manifest = null;
        if (fetch.Count > 0)
        {
            try
            {
                var json = await askAsync(cancellationToken, "song-assets", library.Id).ConfigureAwait(false);
                manifest = JsonSerializer.Deserialize(json, SongSyncJson.Default.SongSyncManifest);
                if (manifest is null || manifest.Shared is null || manifest.Songs is null
                    || manifest.Shared.Any(path => !names.Contains(path))
                    || manifest.Songs.Any(paths => paths is null || paths.Any(path => !names.Contains(path))))
                    throw new InvalidDataException("The other device's song files do not match its library listing.");
            }
            catch (SyncRefusedException exception) when (exception.Code == SyncWire.Missing)
            {
                // This sender predates song manifests.
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The other device sent an invalid song manifest.", exception);
            }
        }
        return new MirrorPlan(fetch, fetch.Where(file => !SongSyncStaging.IsStaged(folder, file)).Sum(static file => file.Size),
            remove, local.Count) { Manifest = manifest };
    }

    /// <summary>
    /// The files in a received library's folder: path ('/'), size, time. Leftover partial files are left out. A file
    /// unchanged since it was received has the sender's time (<see cref="recordTimes"/>): the Switch cannot set file times.
    /// </summary>
    public static Dictionary<string, (long Size, long Time)> Local(string folder)
    {
        var files = onDisk(folder);
        foreach (var (path, (size, here, sent)) in readTimes(folder))
            if (files.TryGetValue(path, out var file) && file.Size == size && Math.Abs(file.Time - here) <= 2)
                files[path] = (size, sent);
        return files;
    }

    private static Dictionary<string, (long Size, long Here, long Sent)> readTimes(string folder)
    {
        var times = new Dictionary<string, (long, long, long)>(StringComparer.Ordinal);
        var path = Path.Combine(folder, SongSyncStaging.TimesName);
        if (File.Exists(path))
            foreach (var parts in File.ReadLines(path).Select(static line => line.Split('\t')))
                if (parts.Length == 4 && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var size)
                    && long.TryParse(parts[2], CultureInfo.InvariantCulture, out var here) && long.TryParse(parts[3], CultureInfo.InvariantCulture, out var sent))
                    times[parts[0]] = (size, here, sent);
        return times;
    }

    /// <summary>Records the sender's times of <paramref name="received"/> beside the files' own (see <see cref="Local"/>).</summary>
    private static void recordTimes(string folder, IEnumerable<(string Path, long Size, long Time)> received)
    {
        var files = onDisk(folder);
        var times = readTimes(folder);
        foreach (var file in received)
            if (files.TryGetValue(file.Path, out var here) && here.Size == file.Size)
                times[file.Path] = (file.Size, here.Time, file.Time);
        var path = Path.Combine(folder, SongSyncStaging.TimesName);
        File.WriteAllLines(path + ".part", times.Where(entry => files.ContainsKey(entry.Key))
            .Select(static entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Key}\t{entry.Value.Size}\t{entry.Value.Here}\t{entry.Value.Sent}")));
        File.Move(path + ".part", path, overwrite: true);
    }

    private static Dictionary<string, (long Size, long Time)> onDisk(string folder) =>
        !Directory.Exists(folder) ? new(StringComparer.Ordinal) : new DirectoryInfo(folder)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
            .Where(static file => !file.Name.EndsWith(".part", StringComparison.Ordinal))
            .Select(file => (Path: Path.GetRelativePath(folder, file.FullName).Replace('\\', '/'), File: file))
            .Where(static file => !SongSyncStaging.IsInternal(file.Path))
            .ToDictionary(static file => file.Path,
                static file => (file.File.Length, new DateTimeOffset(file.File.LastWriteTimeUtc).ToUnixTimeSeconds()), StringComparer.Ordinal);

    /// <summary>
    /// Carries out <paramref name="plan"/>: fetches (four at a time, each file under a temporary name until complete,
    /// with the sender's time), then removes. <paramref name="fetched"/> gets bytes as they land. A failing file is
    /// tried again for about three minutes before the sync stops (the next one goes on from there).
    /// </summary>
    public async Task MirrorAsync(RemoteLibrary library, string folder, MirrorPlan plan, Action<long>? fetched = null,
        Action? published = null, CancellationToken cancellationToken = default)
    {
        if (library.Kind == NijiiroSync.Kind)
        {
            using var rsa = RSA.Create(2048);
            var payload = await askAsync(cancellationToken, "nijiiro-config", library.Id, NijiiroSync.PublicKey(rsa)).ConfigureAwait(false);
            try { NijiiroSync.WriteConfig(folder, NijiiroSync.UnwrapKeys(rsa, payload)); }
            catch (Exception exception) when (exception is CryptographicException or FormatException)
            {
                throw new InvalidDataException("The sender's Nijiiro key configuration could not be read.", exception);
            }
        }
        Directory.CreateDirectory(folder);
        SongSyncStaging.Recover(folder);
        if (library.Kind != "green")
            File.WriteAllText(Path.Combine(folder, ReceivedLibraries.Marker), $"{library.Kind}\t{SyncWire.clean(library.Name)}\n");
        var staging = new SongSyncStaging(folder, plan);
        var queue = new Queue<(string Path, long Size, long Time)>(staging.OrderedFiles);
        long receivedBytes = 0;
        var finished = plan.FetchBytes > 0 ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        if (finished is not null)
            _ = reportProgressAsync(library.Id, plan.FetchBytes, () => Interlocked.Read(ref receivedBytes), finished.Task);

        void reportBytes(long bytes)
        {
            Interlocked.Add(ref receivedBytes, bytes);
            fetched?.Invoke(bytes);
        }

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
                    cancellationToken.ThrowIfCancellationRequested();
                    if (SongSyncStaging.IsStaged(folder, file))
                    {
                        staging.Ready(file.Path, published);
                        continue;
                    }
                    var destination = staging.Destination(file.Path);
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
                            await fetchAsync(connection.GetStream(), library, file.Path, file.Size, file.Time, destination, reportBytes, cancellationToken).ConfigureAwait(false);
                            break;
                        }
                        catch (SyncRefusedException refused) when (refused.Code == SyncWire.Missing)
                        {
                            break; // gone from the other device since the list: the next sync removes it here
                        }
                        catch (ChangedFileException)
                        {
                            // Changed on the other device since it listed its library (it serves the one it booted
                            // with): skipped; it comes with a sync after that device restarts. The connection still
                            // holds the file's bytes.
                            Console.Error.WriteLine($"Song sync: {file.Path} changed on {device.Name}, skipped.");
                            connection?.Dispose();
                            connection = null;
                            break;
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
                    staging.Ready(file.Path, published);
                }
            }
            finally
            {
                connection?.Dispose();
            }
        }
        var success = false;
        try
        {
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
            cancellationToken.ThrowIfCancellationRequested();
            staging.Finish(published);
            SongSyncStaging.Clean(folder);
            foreach (var partial in Directory.EnumerateFiles(folder, "*.part", SearchOption.AllDirectories))
                File.Delete(partial);
            deleteEmptyFolders(folder);
            recordTimes(folder, plan.Fetch);
            success = true;
        }
        finally
        {
            finished?.TrySetResult(success);
        }
    }

    // Status is best effort. A missed update or older sender must not delay any of the file workers.
    private async Task reportProgressAsync(string library, long total, Func<long> received, Task<bool> finished)
    {
        var transfer = Guid.NewGuid().ToString("N");
        var expected = total.ToString(CultureInfo.InvariantCulture);
        TcpClient? client = null;
        IPAddress? connectedTo = null;
        try
        {
            while (true)
            {
                var state = finished.IsCompletedSuccessfully ? finished.Result ? "done" : "stopped" : "running";
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var address = _address;
                    if (!address.Equals(connectedTo))
                    {
                        client?.Dispose();
                        client = null;
                    }
                    client ??= await SyncWire.ConnectAsync(address, SyncWire.SyncPort, timeout.Token).ConfigureAwait(false);
                    connectedTo = address;
                    var bytes = Math.Clamp(received(), 0, total).ToString(CultureInfo.InvariantCulture);
                    await SyncWire.AskAsync(client.GetStream(), timeout.Token, pair.Token, "transfer-progress",
                        library, transfer, expected, bytes, state).ConfigureAwait(false);
                }
                catch (SyncRefusedException)
                {
                    return;
                }
                catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or InvalidDataException)
                {
                    client?.Dispose();
                    client = null;
                }

                if (state != "running")
                    return;
                await Task.WhenAny(finished, Task.Delay(TimeSpan.FromMilliseconds(750))).ConfigureAwait(false);
            }
        }
        finally
        {
            client?.Dispose();
        }
    }

    private async Task fetchAsync(NetworkStream stream, RemoteLibrary library, string path, long size, long time, string destination,
        Action<long>? fetched, CancellationToken cancellationToken)
    {
        // Async socket reads ignore the socket's timeouts: a stall of a minute cancels instead.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(Stall);
        await SyncWire.WriteRequestAsync(stream, stall.Token, pair.Token, "file", library.Id, path).ConfigureAwait(false);
        var length = await SyncWire.ReadLengthAsync(stream, stall.Token).ConfigureAwait(false);
        if (length < 0)
            throw new SyncRefusedException(length);
        if (length != size)
            throw new ChangedFileException();
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

    private static readonly TimeSpan StandardQueryWait = TimeSpan.FromMinutes(2);

    private Task<string> askAsync(CancellationToken cancellationToken, params string[] fields) =>
        askWithTimeoutAsync(StandardQueryWait, fields, cancellationToken);

    // A connection per question. A Nijiiro file listing also prepares the compressed audio on the sender.
    private async Task<string> askWithTimeoutAsync(TimeSpan wait, string[] fields, CancellationToken cancellationToken)
    {
        using var client = await SyncWire.ConnectAsync(_address, SyncWire.SyncPort, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait);
        return await SyncWire.AskAsync(client.GetStream(), timeout.Token, [pair.Token, .. fields]).ConfigureAwait(false);
    }

    // A path inside root, or null: relative, no "..", no empty parts (the other device is trusted with nothing).
    internal static string? SafePath(string root, string relative)
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

    public static void RecoverGreen()
    {
        if (GreenFolder is { } folder)
            SongSyncStaging.Recover(folder);
    }

    /// <summary>The folder a library from <paramref name="deviceId"/> mirrors into; null: this device can't take it.</summary>
    public static string? FolderOf(string deviceId, RemoteLibrary library) =>
        library.Kind == "green" ? GreenFolder : Root is null ? null : Path.Combine(Root, deviceId, library.Id);

    /// <summary>Every received library: kind and folder.</summary>
    public static List<(string Kind, string Folder)> All()
    {
        if (Root is not { } root || !Directory.Exists(root))
            return [];
        var libraries = new List<(string Kind, string Folder)>();
        foreach (var folder in Directory.EnumerateDirectories(root).SelectMany(Directory.EnumerateDirectories).Order(StringComparer.Ordinal))
        {
            var marker = Path.Combine(folder, Marker);
            if (!File.Exists(marker))
                continue;
            try
            {
                SongSyncStaging.Recover(folder);
                libraries.Add((File.ReadAllText(marker).Split('\t')[0].Trim(), folder));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Console.Error.WriteLine($"Song sync: couldn't open received library {Path.GetFileName(folder)}: {exception.Message}");
            }
        }
        return libraries;
    }

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

/// <summary>A library file whose size differs from the sender's listing.</summary>
internal sealed class ChangedFileException : IOException;
