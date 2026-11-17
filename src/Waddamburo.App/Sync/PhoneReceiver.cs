using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Waddamburo.App.Sync;

/// <summary>
/// The phone's side of Send to phone (<see cref="PhoneSync"/>): Waddamburo on a PC finds it over Wi-Fi or USB tethering
/// (UDP <see cref="Port"/>: "WADDAMBURO?" is answered "WADDAMBURO &lt;instance&gt; &lt;name&gt;"), asks which files are already here and
/// sends the rest, so an interrupted copy goes on where it stopped. Every request carries a code shown on the phone
/// (nobody else on the network writes into the game). Files land under a temporary name and take theirs once complete.
/// Runs for as long as the app does. The desktop can run one too (--phone-receiver=DIR), to test sending.
/// </summary>
public sealed class PhoneReceiver : IDisposable
{
    public const int Port = 8380;
    public const string Probe = "WADDAMBURO?", Answer = "WADDAMBURO ";
    private readonly HttpListener _listener = new();
    private readonly UdpClient _discovery;
    private readonly string _name, _root;
    private readonly Dictionary<string, string> _targets;
    private long _files, _bytes;
    private readonly SongStore? _store;
    private SongOffer? _offer;
    private HashSet<string> _offered = [];
    private const int MaxOfferBytes = 256 << 20, MaxWrongCodes = 10;
    private int _wrongCodes, _commits;

    /// <param name="root">The folder the targets are in (its free space is reported).</param>
    /// <param name="targets">Each target's folder: green (game data files), store (received songs, <see cref="SongStore"/>).</param>
    /// <param name="name">How a PC lists this device.</param>
    /// <param name="code">The code (random when null).</param>
    public PhoneReceiver(string root, IReadOnlyDictionary<string, string> targets, string name, string? code = null)
    {
        (_root, _name) = (root, name);
        _targets = new(targets, StringComparer.Ordinal);
        _store = targets.TryGetValue("store", out var store) ? new SongStore(store) : null;
        Code = code ?? Random.Shared.Next(1000, 10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _listener.Prefixes.Add($"http://*:{Port}/");
        _listener.Start();
        _discovery = new UdpClient(new IPEndPoint(IPAddress.Any, Port)) { EnableBroadcast = true };
        _ = Task.Run(serve);
        _ = Task.Run(answer);
    }

    /// <summary>
    /// Asked before songs are written: how many and how many bytes are missing here; false refuses them. Null accepts
    /// (the console receiver). Called on the request's thread: block on the player's answer.
    /// </summary>
    public Func<int, long, bool>? Accept { get; set; }

    /// <summary>Songs taken by the last commit.</summary>
    public int Committed { get; private set; }

    /// <summary>Commits so far (the screen says "done" when it grows).</summary>
    public int Commits => Volatile.Read(ref _commits);

    /// <summary>Bytes the last accepted offer lacked here (what is coming), and who offered it.</summary>
    public (long Bytes, string From) Expected { get; private set; }

    /// <summary>It stopped listening after too many wrong codes.</summary>
    public bool LockedOut => Volatile.Read(ref _wrongCodes) >= MaxWrongCodes;

    /// <summary>The device's name, as a PC lists it.</summary>
    public string Name => _name;

    /// <summary>The code a PC must give (http://phone:port/code/), shown on the phone.</summary>
    public string Code { get; }

    /// <summary>Files and bytes received so far.</summary>
    public (long Files, long Bytes) Received => (Interlocked.Read(ref _files), Interlocked.Read(ref _bytes));

    /// <summary>This device's addresses: Wi-Fi and USB tethering (any IPv4 address but loopback).</summary>
    public IReadOnlyList<string> Addresses() =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(static network => network.OperationalStatus == OperationalStatus.Up)
            .SelectMany(static network => network.GetIPProperties().UnicastAddresses)
            .Where(static address => address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
            .Select(static address => address.Address.ToString())];

    /// <summary>The targets' folders in a device's game folder (the phone's layout).</summary>
    public static Dictionary<string, string> Targets(string root) => new(StringComparer.Ordinal)
    {
        ["green"] = Path.Combine(root, "Green"),
        ["store"] = Path.Combine(root, "Songs"),
    };

    public void Dispose()
    {
        _listener.Close();
        _discovery.Dispose();
    }

    // Discovery: a PC's broadcast probe is answered with this device's name, to the PC alone.
    private async Task answer()
    {
        // The instance tells a PC that answers from several networks (Wi-Fi and USB tethering) are one device.
        var reply = Encoding.UTF8.GetBytes($"{Answer}{Guid.NewGuid():N} {_name}");
        while (true)
        {
            try
            {
                var probe = await _discovery.ReceiveAsync().ConfigureAwait(false);
                if (Encoding.UTF8.GetString(probe.Buffer) == Probe)
                    await _discovery.SendAsync(reply, probe.RemoteEndPoint).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
            }
        }
    }

    private async Task serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => handle(context));
        }
    }

    private async Task handle(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var path = context.Request.Url!.AbsolutePath.Trim('/').Split('/', 2);
            if (path[0] != Code)
            {
                // Ten wrong codes and it stops listening: a 4-digit code is guessable, not with ten tries.
                if (Interlocked.Increment(ref _wrongCodes) >= MaxWrongCodes)
                    _listener.Stop();
                response.StatusCode = 404;
                return;
            }
            var action = path.Length > 1 ? path[1] : "";
            var target = context.Request.QueryString["target"] is { } name && _targets.TryGetValue(name, out var root) ? root : null;
            switch (action, context.Request.HttpMethod)
            {
                case ("space", "GET"):
                    // Free bytes where the files go, so the PC can say before sending when they won't fit.
                    await write(response, "text/plain", new DriveInfo(_root).AvailableFreeSpace.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    break;
                case ("have", "GET") when target is not null:
                    // What is here already (path and size), so the page sends only the rest.
                    var have = new StringBuilder();
                    if (Directory.Exists(target))
                        foreach (var file in new DirectoryInfo(target).EnumerateFiles("*", SearchOption.AllDirectories))
                            if (!file.Name.EndsWith(".part", StringComparison.Ordinal))
                                have.Append(Path.GetRelativePath(target, file.FullName).Replace('\\', '/')).Append('\t').Append(file.Length).Append('\n');
                    await write(response, "text/plain; charset=utf-8", have.ToString()).ConfigureAwait(false);
                    break;
                case ("offer", "PUT") when _store is not null && context.Request.ContentLength64 is > 0 and <= MaxOfferBytes:
                {
                    // The songs a sender offers: the blobs missing here are answered (after the player accepts).
                    var offer = await System.Text.Json.JsonSerializer.DeserializeAsync(context.Request.InputStream, SyncJson.Default.SongOffer).ConfigureAwait(false)
                        ?? throw new InvalidDataException("Empty offer.");
                    if (offer.Format != SongOffer.Current)
                    {
                        response.StatusCode = 409;
                        break;
                    }
                    if (offer.Sizes.Keys.Any(static hash => !SongStore.IsHash(hash)) || offer.Songs.Any(row => row.Blobs().Any(hash => !offer.Sizes.ContainsKey(hash))))
                        throw new InvalidDataException("Malformed offer.");
                    var missing = offer.Sizes.Where(pair => !_store.Has(pair.Key)).ToList();
                    var bytes = missing.Sum(static pair => pair.Value);
                    // 507: they don't fit (the sender says so); 403: the player said no.
                    if (bytes > new DriveInfo(_root).AvailableFreeSpace)
                    {
                        response.StatusCode = 507;
                        break;
                    }
                    if (Accept is { } accept && !accept(offer.Songs.Count, bytes))
                    {
                        response.StatusCode = 403;
                        break;
                    }
                    (_offer, _offered, Expected) = (offer, [.. offer.Sizes.Keys], (bytes, offer.From ?? "?"));
                    Interlocked.Exchange(ref _bytes, 0);
                    await write(response, "text/plain", string.Join('\n', missing.Select(static pair => pair.Key))).ConfigureAwait(false);
                    break;
                }
                case ("commit", "PUT") when _store is not null && _offer is { } accepted:
                    Committed = _store.Commit(accepted.Songs);
                    Interlocked.Increment(ref _commits);
                    _offer = null;
                    await write(response, "text/plain", Committed.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    break;
                case ("batch", "PUT") when context.Request.QueryString["target"] == "blobs" && _store is not null && _offer is not null:
                {
                    // Blobs of the accepted offer, framed as files are (the path is the hash), each checked against it.
                    var blobs = context.Request.InputStream;
                    var frame = new byte[8];
                    while (await readAll(blobs, frame.AsMemory(0, 4)).ConfigureAwait(false))
                    {
                        var nameLength = BitConverter.ToInt32(frame, 0);
                        if (nameLength != 64)
                            throw new InvalidDataException("Bad blob name.");
                        var nameBytes = new byte[nameLength];
                        if (!await readAll(blobs, nameBytes).ConfigureAwait(false) || !await readAll(blobs, frame).ConfigureAwait(false))
                            throw new EndOfStreamException();
                        var hash = Encoding.ASCII.GetString(nameBytes);
                        var size = BitConverter.ToInt64(frame, 0);
                        if (!_offered.Contains(hash) || size != _offer.Sizes[hash])
                            throw new InvalidDataException("A blob that wasn't offered.");
                        await _store.KeepAsync(hash, blobs, size).ConfigureAwait(false);
                        Interlocked.Increment(ref _files);
                        Interlocked.Add(ref _bytes, size);
                    }
                    response.StatusCode = 204;
                    break;
                }
                case ("batch", "PUT") when target is not null:
                    // Files one after another: [path length (4), path (UTF-8), size (8), contents], little-endian.
                    var input = context.Request.InputStream;
                    var header = new byte[8];
                    while (await readAll(input, header.AsMemory(0, 4)).ConfigureAwait(false))
                    {
                        var nameLength = BitConverter.ToInt32(header, 0);
                        if (nameLength is <= 0 or > 1024)
                            throw new InvalidDataException("Bad file name length.");
                        var nameBytes = new byte[nameLength];
                        if (!await readAll(input, nameBytes).ConfigureAwait(false) || !await readAll(input, header).ConfigureAwait(false))
                            throw new EndOfStreamException();
                        var size = BitConverter.ToInt64(header, 0);
                        if (size < 0 || safePath(target, Encoding.UTF8.GetString(nameBytes)) is not { } destination)
                            throw new InvalidDataException("Bad file entry.");
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        var partial = destination + ".part";
                        await using (var file = File.Create(partial))
                            await copy(input, file, size).ConfigureAwait(false);
                        File.Move(partial, destination, overwrite: true);
                        Interlocked.Increment(ref _files);
                        Interlocked.Add(ref _bytes, size);
                    }
                    response.StatusCode = 204;
                    break;
                default:
                    response.StatusCode = 400;
                    break;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Upload: {exception.Message}");
            try { response.StatusCode = 500; } catch (InvalidOperationException) { }
        }
        finally
        {
            try { response.Close(); } catch (Exception) { }
        }
    }

    // A path inside root, or null: relative, no "..", no empty parts (the page is trusted with nothing).
    private static string? safePath(string root, string? relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.Contains('\0'))
            return null;
        var parts = relative.Split('/');
        if (parts.Any(static part => part is "" or "." or ".."))
            return null;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var rootFull = Path.GetFullPath(root).TrimEnd('/') + "/";
        return full.StartsWith(rootFull, StringComparison.Ordinal) ? full : null;
    }

    // Fills the buffer, or false at a clean end (no byte of it read).
    private static async Task<bool> readAll(Stream input, Memory<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await input.ReadAsync(buffer[read..]).ConfigureAwait(false);
            if (count == 0)
                return read == 0 ? false : throw new EndOfStreamException();
            read += count;
        }
        return true;
    }

    private static async Task copy(Stream input, Stream output, long size)
    {
        var buffer = new byte[1 << 20];
        while (size > 0)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size))).ConfigureAwait(false);
            if (count == 0)
                throw new EndOfStreamException();
            await output.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
            size -= count;
        }
    }

    private static async Task write(HttpListenerResponse response, string type, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentType = type;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }
}
