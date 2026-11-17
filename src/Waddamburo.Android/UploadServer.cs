using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Waddamburo.Android;

/// <summary>
/// Receives the game's files from a PC's browser, over Wi-Fi or USB tethering, while the setup waits: the page asks
/// which files are already here and sends the rest, so an interrupted copy goes on where it stopped. Every address
/// carries a code shown on the phone (nobody else on the network writes into the game). Files land under a temporary
/// name and take theirs once complete.
/// </summary>
internal sealed class UploadServer : IDisposable
{
    public const int Port = 8380;
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, string> _targets;
    private long _files, _bytes;

    public UploadServer(IReadOnlyDictionary<string, string> targets)
    {
        _targets = new(targets, StringComparer.Ordinal);
        Code = Random.Shared.Next(1000, 10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _listener.Prefixes.Add($"http://*:{Port}/");
        _listener.Start();
        _ = Task.Run(serve);
    }

    /// <summary>The address's code (http://phone:port/code/).</summary>
    public string Code { get; }

    /// <summary>Files and bytes received so far.</summary>
    public (long Files, long Bytes) Received => (Interlocked.Read(ref _files), Interlocked.Read(ref _bytes));

    /// <summary>The page's addresses: Wi-Fi and USB tethering (any IPv4 address but loopback).</summary>
    public IReadOnlyList<string> Addresses() =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(static network => network.OperationalStatus == OperationalStatus.Up)
            .SelectMany(static network => network.GetIPProperties().UnicastAddresses)
            .Where(static address => address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
            .Select(address => $"http://{address.Address}:{Port}/{Code}/")];

    public void Dispose() => _listener.Close();

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
                response.StatusCode = 404;
                return;
            }
            // ".../7941" without its slash: the page's relative links would lose the code.
            if (path.Length == 1 && !context.Request.Url.AbsolutePath.EndsWith('/'))
            {
                response.Redirect($"/{Code}/");
                return;
            }
            var action = path.Length > 1 ? path[1] : "";
            var target = context.Request.QueryString["target"] is { } name && _targets.TryGetValue(name, out var root) ? root : null;
            switch (action, context.Request.HttpMethod)
            {
                case ("", "GET"):
                    await write(response, "text/html; charset=utf-8", UploadPage.Html).ConfigureAwait(false);
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
