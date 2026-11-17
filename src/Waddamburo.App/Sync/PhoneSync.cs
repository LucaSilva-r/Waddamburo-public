using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Waddamburo.App.Sync;

/// <summary>A device answering discovery (<see cref="PhoneReceiver"/>).</summary>
public sealed record Phone(IPAddress Address, string Name);

/// <summary>One file to send: where it goes on the device (target and path in it, or a blob's hash), how to read it here.</summary>
public sealed record PhoneFile(string Target, string Path, long Size, Func<Task<Stream>> Open);

/// <summary>
/// What a sender offers: the rows, and every blob they need with its size. <see cref="Format"/> is the rows' format: a
/// receiver of another one refuses the offer (it would drop what it doesn't know, quietly).
/// </summary>
public sealed record SongOffer(int Format, List<StoredSong> Songs, Dictionary<string, long> Sizes, string? From = null)
{
    /// <summary>Bump whenever <see cref="StoredSong"/> or <see cref="StoredChart"/> changes.</summary>
    public const int Current = 1;
}

/// <summary>
/// Send, the sender's side (see song-sync-plan.md): finds devices (<see cref="DiscoverAsync"/>), sends songs as rows and
/// blobs (<see cref="SendSongsAsync"/>, from <see cref="SongExport"/>) and Green's game data for a first setup
/// (<see cref="GreenPlan"/>), only what the device lacks.
/// </summary>
public static class PhoneSync
{
    // ---- Discovery

    /// <summary>Phones answering a broadcast within <paramref name="wait"/>, on every network (Wi-Fi, USB tethering).</summary>
    public static async Task<List<Phone>> DiscoverAsync(TimeSpan wait, CancellationToken cancellationToken = default)
    {
        using var socket = new UdpClient(0) { EnableBroadcast = true };
        var probe = Encoding.UTF8.GetBytes(PhoneReceiver.Probe);
        // The limited broadcast leaves by one interface only: each network's own broadcast address too (and this PC,
        // for a receiver run here to test).
        foreach (var address in broadcastAddresses().Append(IPAddress.Broadcast).Append(IPAddress.Loopback))
            try { await socket.SendAsync(probe, new IPEndPoint(address, PhoneReceiver.Port)).ConfigureAwait(false); }
            catch (SocketException) { }
        var found = new List<Phone>();
        var instances = new HashSet<string>(StringComparer.Ordinal);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait);
        try
        {
            while (true)
            {
                var answer = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(answer.Buffer);
                // A device on several networks answers once on each: the first answer is kept (ponytail: usually the
                // quicker link, USB tethering; measure each if a slow one ever wins).
                if (text.StartsWith(PhoneReceiver.Answer, StringComparison.Ordinal)
                    && text[PhoneReceiver.Answer.Length..].Split(' ', 2) is [var instance, var name]
                    && instances.Add(instance))
                    found.Add(new Phone(answer.RemoteEndPoint.Address, name));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        return found;
    }

    private static IEnumerable<IPAddress> broadcastAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(static network => network.OperationalStatus == OperationalStatus.Up)
            .SelectMany(static network => network.GetIPProperties().UnicastAddresses)
            .Where(static unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address))
            .Select(static unicast =>
            {
                var address = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                for (var index = 0; index < 4; index++)
                    address[index] |= (byte)~mask[index];
                return new IPAddress(address);
            });

    // ---- Green (game data, raw files)

    /// <summary>Green's data folder as a device's first setup needs it (data/...): raw files, unlike songs.</summary>
    public static List<PhoneFile> GreenPlan(string greenData) =>
        [.. files(greenData).Select(static file => new PhoneFile("green", "data/" + file.Path, file.Size,
            () => Task.FromResult<Stream>(File.OpenRead(file.Source))))];

    // ---- Songs

    /// <summary>
    /// Sends songs: the rows are offered, the device (once its player accepts) answers the blobs it lacks, those go,
    /// then the rows are committed. Returns how many songs the device took; <paramref name="sent"/> gets the bytes.
    /// </summary>
    public static async Task<int> SendSongsAsync(HttpClient http, Uri phone, IReadOnlyList<StoredSong> rows,
        IReadOnlyDictionary<string, OutgoingBlob> blobs, Action<long>? sent = null, Action<long>? missingBytes = null,
        CancellationToken cancellationToken = default)
    {
        var offer = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new SongOffer(SongOffer.Current, [.. rows],
            rows.SelectMany(static row => row.Blobs()).Distinct().ToDictionary(hash => hash, hash => blobs[hash].Size), Boot.DeviceName), SyncJson.Default.SongOffer);
        using var answer = await http.PutAsync(new Uri(phone, "offer"), new ByteArrayContent(offer), cancellationToken).ConfigureAwait(false);
        if (answer.StatusCode == HttpStatusCode.Forbidden)
            throw new HttpRequestException("The device refused the songs.");
        if (answer.StatusCode == HttpStatusCode.InsufficientStorage)
            throw new HttpRequestException("They don't fit on the device: send fewer songs.");
        if (answer.StatusCode == HttpStatusCode.Conflict)
            throw new HttpRequestException("The device runs another Waddamburo version: update both to the same release.");
        answer.EnsureSuccessStatusCode();
        var missing = (await answer.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(hash => new PhoneFile("blobs", hash, blobs[hash].Size, blobs[hash].Open)).ToList();
        missingBytes?.Invoke(missing.Sum(static file => file.Size));
        await SendAsync(http, phone, missing, sent, cancellationToken).ConfigureAwait(false);
        using var commit = await http.PutAsync(new Uri(phone, "commit"), null, cancellationToken).ConfigureAwait(false);
        commit.EnsureSuccessStatusCode();
        return int.Parse(await commit.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    // Every file below a folder (links followed: game folders are often linked in), paths with '/'.
    private static IEnumerable<(string Path, string Source, long Size)> files(string root) =>
        new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(static file => !file.Name.EndsWith(".part", StringComparison.Ordinal))
            .Select(file => (Path.GetRelativePath(root, file.FullName).Replace('\\', '/'), file.FullName, file.Length));

    // ---- Sending

    /// <summary>Free bytes on the phone where the files go.</summary>
    public static async Task<long> FreeSpaceAsync(HttpClient http, Uri phone, CancellationToken cancellationToken = default) =>
        long.Parse(await http.GetStringAsync(new Uri(phone, "space"), cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The files of <paramref name="plan"/> the phone lacks (not there, or another size).</summary>
    public static async Task<List<PhoneFile>> MissingAsync(HttpClient http, Uri phone, IReadOnlyList<PhoneFile> plan, CancellationToken cancellationToken = default)
    {
        var missing = new List<PhoneFile>();
        foreach (var target in plan.Select(static file => file.Target).Distinct())
        {
            var text = await http.GetStringAsync(new Uri(phone, "have?target=" + target), cancellationToken).ConfigureAwait(false);
            var have = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static line => line.Split('\t'))
                .Where(static parts => parts.Length == 2)
                .ToDictionary(static parts => parts[0], static parts => long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
            missing.AddRange(plan.Where(file => file.Target == target && (!have.TryGetValue(file.Path, out var size) || size != file.Size)));
        }
        return missing;
    }

    /// <summary>The phone's address for a code (http://phone:port/code/).</summary>
    public static Uri Address(IPAddress phone, string code) => new($"http://{phone}:{PhoneReceiver.Port}/{code}/");

    /// <summary>
    /// Sends <paramref name="files"/>, small ones batched, four requests at a time; <paramref name="sent"/> gets the bytes
    /// as each batch lands. A batch is tried five times before the copy stops (it goes on where it stopped next time).
    /// </summary>
    public static async Task SendAsync(HttpClient http, Uri phone, IReadOnlyList<PhoneFile> files, Action<long>? sent = null,
        CancellationToken cancellationToken = default)
    {
        // A batch is one target's: sorted by target, a batch ends where its target does.
        var queue = new Queue<PhoneFile>(files.OrderBy(static file => file.Target, StringComparer.Ordinal));
        List<PhoneFile>? next()
        {
            lock (queue)
            {
                if (queue.Count == 0)
                    return null;
                var batch = new List<PhoneFile> { queue.Dequeue() };
                var bytes = batch[0].Size;
                // Many small files go in one request (Green has ~13,000 charts of ~16 KB: a request each was most of the time).
                while (queue.Count > 0 && batch.Count < 200 && bytes + queue.Peek().Size <= 16 << 20 && queue.Peek().Target == batch[0].Target)
                {
                    bytes += queue.Peek().Size;
                    batch.Add(queue.Dequeue());
                }
                return batch;
            }
        }
        async Task worker()
        {
            while (next() is { } batch)
            {
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var response = await http.PutAsync(new Uri(phone, "batch?target=" + batch[0].Target), new BatchContent(batch), cancellationToken).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        break;
                    }
                    catch (Exception exception) when (exception is HttpRequestException or IOException && attempt < 4)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                    }
                }
                sent?.Invoke(batch.Sum(static file => file.Size));
            }
        }
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => worker())).ConfigureAwait(false);
    }

    // Files one after another: [path length (4), path (UTF-8), size (8), contents], little-endian (PhoneReceiver's batch).
    private sealed class BatchContent : HttpContent
    {
        private readonly List<PhoneFile> _files;

        public BatchContent(List<PhoneFile> files)
        {
            _files = files;
            Headers.ContentLength = files.Sum(static file => 12L + Encoding.UTF8.GetByteCount(file.Path) + file.Size);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var header = new byte[12];
            foreach (var file in _files)
            {
                var name = Encoding.UTF8.GetBytes(file.Path);
                BitConverter.TryWriteBytes(header.AsSpan(0, 4), name.Length);
                await stream.WriteAsync(header.AsMemory(0, 4)).ConfigureAwait(false);
                await stream.WriteAsync(name).ConfigureAwait(false);
                BitConverter.TryWriteBytes(header.AsSpan(4, 8), file.Size);
                await stream.WriteAsync(header.AsMemory(4, 8)).ConfigureAwait(false);
                await using var source = await file.Open().ConfigureAwait(false);
                // Exactly the planned size: a file that changed since would break the stream's framing.
                await copy(source, stream, file.Size).ConfigureAwait(false);
            }
        }

        private static async Task copy(Stream input, Stream output, long size)
        {
            var buffer = new byte[1 << 20];
            while (size > 0)
            {
                var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size))).ConfigureAwait(false);
                if (count == 0)
                    throw new IOException("A file got shorter while it was sent.");
                await output.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
                size -= count;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Headers.ContentLength ?? -1;
            return length >= 0;
        }
    }
}
