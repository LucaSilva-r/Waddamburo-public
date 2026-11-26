using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Waddamburo.App.Sync;

/// <summary>A device answering discovery: its id (stable across runs), name and address.</summary>
public sealed record SyncDevice(string Id, string Name, IPAddress Address);

/// <summary>A paired device. <see cref="Serves"/>: it holds the libraries (this device pulls from it); else it pulls from us.</summary>
public sealed record PairedDevice(string Id, string Name, string Token, bool Serves);

/// <summary>
/// Song sync's wire (see song-sync-plan.md). Two services, each a UDP discovery port and a TCP port with the same
/// number: pairing (<see cref="PairPort"/>, the device being paired, only while its pairing page is open) and sync
/// (<see cref="SyncPort"/>, a device holding libraries, while the game runs and it has paired devices).
/// A request is one UTF-8 line, fields split by tabs: key (the code or the pair's token), command, arguments. The
/// answer: an 8-byte little-endian length, then that many bytes; a negative length is an error (no bytes follow).
/// A connection carries any number of requests.
/// </summary>
public static class SyncWire
{
    public const int PairPort = 8380, SyncPort = 8381;
    public const string PairProbe = "WADDAMBURO-PAIR?", SyncProbe = "WADDAMBURO-SYNC?";
    public const long Refused = -1, Missing = -2, Failed = -3;
    private const int MaxLine = 64 * 1024;

    // ---- This device and its pairs (<sync folder>/device-id, paired.txt: id, name, token, serves)

    /// <summary>Where pairing and toggles are kept; set at boot (the game's home, a phone's app storage).</summary>
    public static string Folder { get; set; } = Path.Combine(Path.GetTempPath(), "waddamburo-sync");

    private static readonly object Gate = new();

    /// <summary>This device's id, made once.</summary>
    public static string DeviceId
    {
        get
        {
            lock (Gate)
            {
                var path = Path.Combine(Folder, "device-id");
                if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: 32 } id)
                    return id;
                Directory.CreateDirectory(Folder);
                id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                File.WriteAllText(path, id);
                return id;
            }
        }
    }

    // Read once (menus ask every frame); only savePaired changes the file.
    private static PairedDevice[]? paired;

    public static List<PairedDevice> Paired()
    {
        lock (Gate)
        {
            if (paired is null)
            {
                var path = Path.Combine(Folder, "paired.txt");
                paired = !File.Exists(path) ? [] : [.. File.ReadAllLines(path).Select(static line => line.Split('\t'))
                    .Where(static parts => parts.Length == 4)
                    .Select(static parts => new PairedDevice(parts[0], parts[1], parts[2], parts[3] == "serves"))];
            }
            return [.. paired];
        }
    }

    /// <summary>Adds or replaces the pair with <paramref name="device"/>'s id.</summary>
    public static void Pair(PairedDevice device) =>
        savePaired([.. Paired().Where(old => old.Id != device.Id), device]);

    public static void Unpair(string id)
    {
        savePaired([.. Paired().Where(old => old.Id != id)]);
        try { File.Delete(Path.Combine(Folder, id + ".libraries")); } catch (IOException) { }
        try { File.Delete(Path.Combine(Folder, id + ".address")); } catch (IOException) { }
    }

    private static void savePaired(IEnumerable<PairedDevice> devices)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Folder);
            paired = null;
            File.WriteAllLines(Path.Combine(Folder, "paired.txt"),
                devices.Select(static device => $"{clean(device.Id)}\t{clean(device.Name)}\t{clean(device.Token)}\t{(device.Serves ? "serves" : "pulls")}"));
        }
    }

    /// <summary>Where a paired device answered last (tried before discovery), or null.</summary>
    public static IPAddress? LastAddress(string deviceId)
    {
        var path = Path.Combine(Folder, deviceId + ".address");
        lock (Gate)
            return File.Exists(path) && IPAddress.TryParse(File.ReadAllText(path).Trim(), out var address) ? address : null;
    }

    public static void RememberAddress(string deviceId, IPAddress address)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, deviceId + ".address"), address.ToString());
        }
    }

    /// <summary>The library ids switched on for a serving device.</summary>
    public static HashSet<string> Wanted(string deviceId)
    {
        var path = Path.Combine(Folder, deviceId + ".libraries");
        lock (Gate)
            return File.Exists(path) ? [.. File.ReadAllLines(path).Where(static line => line.Length > 0)] : [];
    }

    public static void SetWanted(string deviceId, IEnumerable<string> libraries)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllLines(Path.Combine(Folder, deviceId + ".libraries"), libraries);
        }
    }

    /// <summary>Names travel in tab-separated lines: no tabs or line breaks in them.</summary>
    public static string clean(string text) => text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    // ---- Discovery

    /// <summary>Devices answering <paramref name="probe"/> on <paramref name="port"/> within <paramref name="wait"/>, on every network.</summary>
    public static async Task<List<SyncDevice>> DiscoverAsync(string probe, int port, TimeSpan wait, CancellationToken cancellationToken = default)
    {
        using var socket = new UdpClient(0) { EnableBroadcast = true };
        // Windows reports an ICMP "port unreachable" (loopback, any neighbour without the game) as ConnectionReset on
        // the next receive, which ended discovery before any answer was read. SIO_UDP_CONNRESET off.
        if (OperatingSystem.IsWindows())
            socket.Client.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        var bytes = Encoding.UTF8.GetBytes(probe);
        // The limited broadcast leaves by one interface only: each network's own broadcast address too (and this
        // machine, for a second instance run here to test).
        foreach (var address in broadcastAddresses().Append(IPAddress.Broadcast).Append(IPAddress.Loopback))
            try { await socket.SendAsync(bytes, new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false); }
            catch (SocketException) { }
        var found = new List<SyncDevice>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait);
        // Some phones' Wi-Fi drops broadcasts (MIUI): every address of small networks is asked too, meanwhile (a send to
        // an absent neighbour can wait on ARP: answers to the broadcast are read in the meantime).
        var token = timeout.Token;
        var sweep = Task.Run(async () =>
        {
            foreach (var address in neighbours())
                try { await socket.SendAsync(bytes, new IPEndPoint(address, port), token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    if (token.IsCancellationRequested)
                        return;
                }
        }, CancellationToken.None);
        var answer = probe.TrimEnd('?') + " ";
        try
        {
            while (true)
            {
                var reply = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(reply.Buffer);
                // A device on several networks answers on each: every address is kept, sorted below.
                if (text.StartsWith(answer, StringComparison.Ordinal) && text[answer.Length..].Split(' ', 2) is [var id, var name]
                    && found.All(device => device.Id != id || !device.Address.Equals(reply.RemoteEndPoint.Address)))
                    found.Add(new SyncDevice(id, name, reply.RemoteEndPoint.Address));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            await sweep.ConfigureAwait(false); // before the socket goes
        }
        // USB tethering first: ~10x a phone's Wi-Fi (25 against 3 MB/s on a Redmi 8). A device on both answers twice.
        var usb = usbNetworks();
        return [.. found.OrderBy(device => usb.Any(network => network.Contains(device.Address)) ? 0 : 1)
            .DistinctBy(static device => device.Id)];
    }

    // The networks of this device's USB links (Android names them rndis0, usb0, ncm0).
    private static List<IPNetwork> usbNetworks() =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(static network => network.OperationalStatus == OperationalStatus.Up
                && (network.Name.StartsWith("rndis", StringComparison.Ordinal) || network.Name.StartsWith("usb", StringComparison.Ordinal)
                    || network.Name.StartsWith("ncm", StringComparison.Ordinal)))
            .SelectMany(static network => network.GetIPProperties().UnicastAddresses)
            .Where(static address => address.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(static address => new IPNetwork(maskOf(address.Address, address.PrefixLength), address.PrefixLength))];

    private static IPAddress maskOf(IPAddress address, int prefix)
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes()) & (prefix == 0 ? 0 : uint.MaxValue << (32 - prefix));
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }

    /// <summary>Answers <paramref name="probe"/> on <paramref name="socket"/> with this device's id and name until it is disposed.</summary>
    public static async Task AnswerAsync(UdpClient socket, string probe, string name)
    {
        if (OperatingSystem.IsWindows())
            socket.Client.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null); // see DiscoverAsync
        var reply = Encoding.UTF8.GetBytes($"{probe.TrimEnd('?')} {DeviceId} {clean(name)}");
        while (true)
        {
            try
            {
                var request = await socket.ReceiveAsync().ConfigureAwait(false);
                if (Encoding.UTF8.GetString(request.Buffer) == probe)
                    await socket.SendAsync(reply, request.RemoteEndPoint).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { }
        }
    }

    private static IEnumerable<IPAddress> broadcastAddresses()
    {
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up)
                continue;
            foreach (var unicast in network.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address))
                    continue;
                var address = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask?.GetAddressBytes();
                if (mask is not { Length: 4 })
                    continue;
                for (var i = 0; i < 4; i++)
                    address[i] |= (byte)~mask[i];
                yield return new IPAddress(address);
            }
        }
    }

    // Every host address of this device's networks of /22 or smaller (a home Wi-Fi, USB tethering), this device's own
    // excepted. ponytail: bigger networks (offices) rely on broadcast alone.
    private static IEnumerable<IPAddress> neighbours()
    {
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up)
                continue;
            foreach (var unicast in network.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address)
                    || unicast.PrefixLength is < 22 or > 30)
                    continue;
                var own = BinaryPrimitives.ReadUInt32BigEndian(unicast.Address.GetAddressBytes());
                var mask = uint.MaxValue << (32 - unicast.PrefixLength);
                var first = own & mask;
                for (var host = first + 1; host < (first | ~mask); host++)
                    if (host != own)
                    {
                        var bytes = new byte[4];
                        BinaryPrimitives.WriteUInt32BigEndian(bytes, host);
                        yield return new IPAddress(bytes);
                    }
            }
        }
    }

    /// <summary>This device's IPv4 addresses but loopback (none: no network).</summary>
    public static IReadOnlyList<string> Addresses() =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(static network => network.OperationalStatus == OperationalStatus.Up)
            .SelectMany(static network => network.GetIPProperties().UnicastAddresses)
            .Where(static address => address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
            .Select(static address => address.Address.ToString())];

    // ---- Requests and answers

    public static async Task WriteRequestAsync(Stream stream, CancellationToken cancellationToken, params string[] fields)
    {
        var line = Encoding.UTF8.GetBytes(string.Join('\t', fields.Select(clean)) + "\n");
        await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The next request's fields, null at a clean end of the connection.</summary>
    public static async Task<string[]?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = new MemoryStream();
        var one = new byte[1];
        // ponytail: a byte at a time; requests are a few dozen bytes, a buffered reader if they ever grow.
        while (true)
        {
            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
                return line.Length == 0 ? null : throw new EndOfStreamException();
            if (one[0] == '\n')
                return Encoding.UTF8.GetString(line.ToArray()).Split('\t');
            if (line.Length >= MaxLine)
                throw new InvalidDataException("Request too long.");
            line.WriteByte(one[0]);
        }
    }

    public static async Task WriteAnswerAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        await WriteLengthAsync(stream, payload.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteLengthAsync(Stream stream, long length, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(header, length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (length < 0)
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<long> ReadLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt64LittleEndian(header);
    }

    /// <summary>One request's whole answer as text; throws <see cref="SyncRefusedException"/> on an error answer.</summary>
    public static async Task<string> AskAsync(Stream stream, CancellationToken cancellationToken, params string[] fields)
    {
        await WriteRequestAsync(stream, cancellationToken, fields).ConfigureAwait(false);
        var length = await ReadLengthAsync(stream, cancellationToken).ConfigureAwait(false);
        if (length < 0)
            throw new SyncRefusedException(length);
        if (length > 256L << 20)
            throw new InvalidDataException("Answer too long.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>A connection with read/write timeouts: a dead link fails a request instead of hanging it.</summary>
    public static async Task<TcpClient> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true, ReceiveTimeout = 30_000, SendTimeout = 30_000 };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return client;
    }
}

/// <summary>The other side answered a request with an error (<see cref="SyncWire.Refused"/>: wrong code or token).</summary>
public sealed class SyncRefusedException(long code) : IOException(code switch
{
    SyncWire.Refused => "refused (wrong code, or no longer paired)",
    SyncWire.Missing => "not there",
    _ => "the other device failed",
})
{
    public long Code { get; } = code;
}
