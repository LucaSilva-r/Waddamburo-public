using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Waddamburo.Catalog;
using Waddamburo.Game.Flow;

namespace Waddamburo.App.Sync;

/// <summary>
/// A library another device can mirror: its folders, each laid out under <c>&lt;library&gt;/&lt;part&gt;/</c> on the
/// receiver, where the same provider kind reads it (see <see cref="ReceivedLibraries"/>). Kinds: green (Green's data,
/// into the device's own Green folder, never deleted from), stock (another arcade release's data), wii, wiiu (game,
/// update and DLC title folders side by side), tja, osu-lazer, osu-stable.
/// </summary>
public sealed record SyncLibrary(string Id, string Kind, string Name, IReadOnlyList<(string Part, string Folder)> Parts)
{
    private static readonly string[] StableDatabases = ["osu!.db", "collection.db"];
    internal Func<ISongCatalogProvider>? OpenCatalog { get; init; }
    internal NijiiroSync? Nijiiro { get; init; }

    /// <summary>A library of <paramref name="kind"/> made of <paramref name="parts"/>; its id follows the folders.</summary>
    public static SyncLibrary Of(string kind, string name, params (string Part, string Folder)[] parts) =>
        new(kind == "green" ? "green" : kind + "-" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join('|', parts.Select(static part => Path.GetFullPath(part.Folder))))))[..12], kind, name, parts);

    internal string? RelativePath(string file)
    {
        foreach (var (part, folder) in Parts)
        {
            var relative = Path.GetRelativePath(folder, file);
            if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !Path.IsPathRooted(relative))
                return part + "/" + relative.Replace('\\', '/');
        }
        return null;
    }

    /// <summary>Every file: path on the receiver (part/relative, '/'), size, modified time (Unix seconds), full path here.</summary>
    public IEnumerable<(string Path, long Size, long Time, string Full)> Files() =>
        rawFiles().Where(file => Nijiiro is null || NijiiroSync.Includes(file.Path));

    private IEnumerable<(string Path, long Size, long Time, string Full)> rawFiles() =>
        Parts.Where(static part => Directory.Exists(part.Folder)).SelectMany(part =>
            part.Part == "osu" && Kind == "osu-stable"
                // The sender's per-user config can point outside its installation. The receiver always uses osu/Songs.
                ? StableDatabases.Select(name => new FileInfo(Path.Combine(part.Folder, name)))
                    .Where(static file => file.Exists).Select(file => describe(part, file))
                : new DirectoryInfo(part.Folder).EnumerateFiles("*", new EnumerationOptions
                    { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
                    .Select(file => describe(part, file))
                    .Where(file => !file.Path.EndsWith(".part", StringComparison.Ordinal)
                        && !SongSyncStaging.IsInternal(file.Path[(part.Part.Length + 1)..])));

    private static (string Path, long Size, long Time, string Full) describe((string Part, string Folder) part, FileInfo file) =>
        (part.Part + "/" + Path.GetRelativePath(part.Folder, file.FullName).Replace('\\', '/'), file.Length,
            new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds(), file.FullName);
}

/// <summary>
/// The serving side of song sync: paired devices list this device's libraries, each library's files, and fetch them
/// (see <see cref="SyncWire"/>). Read-only: nothing a request says is written here. Runs while the game does once a
/// device is paired to pull from it.
/// </summary>
public sealed class SyncServer : IDisposable
{
    private static readonly int NijiiroConversionWorkers = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    private static readonly SemaphoreSlim NijiiroConversionSlots = new(NijiiroConversionWorkers);

    private readonly TcpListener _listener = new(IPAddress.Any, SyncWire.SyncPort);
    private readonly UdpClient _discovery;
    private readonly CancellationTokenSource _stop = new();
    private readonly IReadOnlyList<SyncLibrary> _libraries;
    private readonly OutgoingSyncProgress? _progress;
    private readonly JobBoard? _jobs;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _prepareLocks = new();
    // Each library's file list, kept a minute (a sync asks for it, then for its files).
    private readonly Dictionary<string, (DateTime At, Dictionary<string, (long Size, long Time, string Full)> Files)> _lists = [];

    public SyncServer(IReadOnlyList<SyncLibrary> libraries, string name, JobBoard? jobs = null)
    {
        _libraries = libraries;
        _jobs = jobs;
        _listener.Start();
        _discovery = new UdpClient(new IPEndPoint(IPAddress.Any, SyncWire.SyncPort)) { EnableBroadcast = true };
        _progress = jobs is null ? null : new OutgoingSyncProgress(jobs);
        _ = Task.Run(() => SyncWire.AnswerAsync(_discovery, SyncWire.SyncProbe, name));
        _ = Task.Run(accept);
    }

    /// <summary>A server when a paired device pulls from this one; null when none does or the port is taken.</summary>
    public static SyncServer? StartIfPaired(IReadOnlyList<SyncLibrary> libraries, string name, JobBoard jobs)
    {
        if (SyncWire.Paired().All(static device => device.Serves))
            return null;
        try
        {
            return new SyncServer(libraries, name, jobs);
        }
        catch (SocketException exception)
        {
            Console.Error.WriteLine($"Song sync: can't listen on port {SyncWire.SyncPort}: {exception.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _discovery.Dispose();
        _progress?.Dispose();
    }

    private const string FirewallRule = "Waddamburo song sync";

    /// <summary>
    /// Windows blocks inbound connections to a program without a rule: its own prompt shows behind a fullscreen game,
    /// allows only private networks by default (home Wi-Fi is often "public"), and dismissed it adds Block rules, which
    /// win over any Allow. So: once per program path, with one UAC prompt, this program's inbound rules are replaced by
    /// one Allow rule on every profile. Waits for the answer, so the rule is there before the port opens.
    /// </summary>
    public static void AllowThroughWindowsFirewall()
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } program)
            return;
        try
        {
            using (var show = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("netsh",
                $"advfirewall firewall show rule name=\"{FirewallRule}\" verbose") { RedirectStandardOutput = true, CreateNoWindow = true }))
            {
                if (show is null || show.StandardOutput.ReadToEnd().Contains(program, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            var netsh = "netsh advfirewall firewall";
            using var add = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/c {netsh} delete rule name=\"{FirewallRule}\" & {netsh} delete rule name=all dir=in program=\"{program}\""
                + $" & {netsh} add rule name=\"{FirewallRule}\" dir=in action=allow program=\"{program}\" enable=yes profile=any")
                { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
            add?.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception exception) // UAC declined, or no netsh
        {
            Console.Error.WriteLine($"Song sync: firewall rule not added: {exception.Message}");
        }
    }

    private async Task accept()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => serve(client));
        }
    }

    private async Task serve(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        try
        {
            while (await SyncWire.ReadRequestAsync(stream, _stop.Token).ConfigureAwait(false) is { Length: >= 2 } request)
            {
                // The token is the pair's secret: compared in constant time, re-read each request (unpairing takes effect at once).
                var receiver = SyncWire.Paired().FirstOrDefault(device => !device.Serves && CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(device.Token), Encoding.UTF8.GetBytes(request[0])));
                if (receiver is null)
                {
                    await SyncWire.WriteLengthAsync(stream, SyncWire.Refused, _stop.Token).ConfigureAwait(false);
                    return;
                }
                switch (request[1], request.Length)
                {
                    case ("hello", _):
                        await answer(stream, $"{SyncWire.DeviceId}\t{SyncWire.clean(Boot.DeviceName)}").ConfigureAwait(false);
                        break;
                    case ("libraries", _):
                        // id, kind, name, bytes, files
                        await answer(stream, string.Join('\n', _libraries.Where(library => library.Nijiiro is null || request is [_, _, "nijiiro-v1"]).Select(library =>
                        {
                            var files = library.Nijiiro is null
                                ? list(library).Values.Select(static file => file.Size).ToArray()
                                : library.Files().Select(static file => file.Size).ToArray(); // estimate without converting
                            return $"{library.Id}\t{library.Kind}\t{SyncWire.clean(library.Name)}\t{files.Sum()}\t{files.Length}";
                        }))).ConfigureAwait(false);
                        break;
                    case ("list", 3) when find(request[2]) is { } library:
                        try
                        {
                            var files = list(library, fresh: true, client.Client);
                            await answer(stream, string.Join('\n', files.Select(static pair => $"{pair.Key}\t{pair.Value.Size}\t{pair.Value.Time}"))).ConfigureAwait(false);
                        }
                        catch (IOException) when (library.Nijiiro is not null)
                        {
                            await SyncWire.WriteLengthAsync(stream, SyncWire.Failed, _stop.Token).ConfigureAwait(false);
                        }
                        break;
                    case ("nijiiro-config", 4) when find(request[2])?.Nijiiro is { } nijiiro:
                        try
                        {
                            await answer(stream, NijiiroSync.WrapKeys(nijiiro.Keys, request[3])).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is CryptographicException or FormatException)
                        {
                            await SyncWire.WriteLengthAsync(stream, SyncWire.Failed, _stop.Token).ConfigureAwait(false);
                        }
                        break;
                    case ("song-assets", 3) when find(request[2]) is { OpenCatalog: not null } songs:
                        var paths = new HashSet<string>(list(songs).Keys, StringComparer.Ordinal);
                        try
                        {
                            var manifest = await SongSyncManifest.CreateAsync(songs, paths, _stop.Token).ConfigureAwait(false);
                            await answer(stream, JsonSerializer.Serialize(manifest, SongSyncJson.Default.SongSyncManifest)).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                            or System.Xml.XmlException or ArgumentException)
                        {
                            Console.Error.WriteLine($"Song sync: could not describe {songs.Name}: {exception.Message}");
                            await SyncWire.WriteLengthAsync(stream, SyncWire.Failed, _stop.Token).ConfigureAwait(false);
                        }
                        break;
                    case ("transfer-progress", 7) when _progress is { } progress && find(request[2]) is { } transferLibrary:
                        if (progress.Report(receiver, transferLibrary, request))
                            await answer(stream, "").ConfigureAwait(false);
                        else
                            await SyncWire.WriteLengthAsync(stream, SyncWire.Failed, _stop.Token).ConfigureAwait(false);
                        break;
                    case ("file", 4) when find(request[2]) is { } library && list(library).TryGetValue(request[3], out var file):
                        await send(stream, file.Full).ConfigureAwait(false);
                        break;
                    default:
                        await SyncWire.WriteLengthAsync(stream, SyncWire.Missing, _stop.Token).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException
            or ObjectDisposedException or InvalidDataException or UnauthorizedAccessException)
        {
            // The other side went away, or a file did: the device asks again.
        }
    }

    private SyncLibrary? find(string id) => _libraries.FirstOrDefault(library => library.Id == id);

    private Dictionary<string, (long Size, long Time, string Full)> list(SyncLibrary library, bool fresh = false, Socket? requestingSocket = null)
    {
        lock (_lists)
            if (!fresh && _lists.TryGetValue(library.Id, out var kept)
                && (library.Nijiiro is not null || DateTime.UtcNow - kept.At < TimeSpan.FromMinutes(1)))
                return kept.Files;
        if (library.Nijiiro is not null)
        {
            lock (_prepareLocks.GetOrAdd(library.Id, static _ => new object()))
                return storeList(library, prepareNijiiro(library, requestingSocket));
        }
        return storeList(library, library.Files());
    }

    private Dictionary<string, (long Size, long Time, string Full)> storeList(SyncLibrary library,
        IEnumerable<(string Path, long Size, long Time, string Full)> inventory)
    {
        var files = inventory.ToDictionary(static file => file.Path, static file => (file.Size, file.Time, file.Full), StringComparer.Ordinal);
        lock (_lists)
            _lists[library.Id] = (DateTime.UtcNow, files);
        return files;
    }

    private List<(string Path, long Size, long Time, string Full)> prepareNijiiro(SyncLibrary library, Socket? requestingSocket)
    {
        var input = library.Files().ToList();
        var banks = input.Where(file => NijiiroSync.IsBank(file.Path)).ToArray();
        var job = _jobs?.Start(Strings.T("job.sync_nijiiro", library.Name), banks.Length);
        var files = input.Where(file => !NijiiroSync.IsBank(file.Path)).ToList();
        var converted = new (string Path, long Size, long Time, string Full)[banks.Length][];
        var completed = 0;
        try
        {
            var options = new ParallelOptions
            {
                CancellationToken = _stop.Token,
                MaxDegreeOfParallelism = NijiiroConversionWorkers,
            };
            Parallel.ForAsync(0, banks.Length, options, async (i, token) =>
            {
                await NijiiroConversionSlots.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (requestingSocket is not null && requestingSocket.Poll(0, SelectMode.SelectRead) && requestingSocket.Available == 0)
                        throw new OperationCanceledException("Audio preparation stopped when the receiver disconnected.");
                    converted[i] = [.. NijiiroSync.PrepareAudio(banks[i])];
                    if (job is not null)
                        lock (files)
                            job.Report(++completed);
                }
                finally { NijiiroConversionSlots.Release(); }
            }).GetAwaiter().GetResult();
            files.AddRange(converted.SelectMany(static bank => bank));
            job?.Complete();
            return files;
        }
        catch (Exception exception) when (exception is IOException or SocketException or UnauthorizedAccessException or OperationCanceledException)
        {
            job?.Fail(exception.Message);
            throw;
        }
    }

    private Task answer(Stream stream, string text) => SyncWire.WriteAnswerAsync(stream, Encoding.UTF8.GetBytes(text), _stop.Token);

    private async Task send(Stream stream, string path)
    {
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await SyncWire.WriteLengthAsync(stream, SyncWire.Missing, _stop.Token).ConfigureAwait(false);
            return;
        }
        await using var _ = file;
        var length = file.Length;
        await SyncWire.WriteLengthAsync(stream, length, _stop.Token).ConfigureAwait(false);
        var buffer = new byte[1 << 20];
        var left = length;
        while (left > 0)
        {
            var count = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), _stop.Token).ConfigureAwait(false);
            if (count == 0)
                throw new IOException("The file got shorter while it was sent."); // the connection is dropped; the device asks again
            await stream.WriteAsync(buffer.AsMemory(0, count), _stop.Token).ConfigureAwait(false);
            left -= count;
        }
        await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
    }
}
