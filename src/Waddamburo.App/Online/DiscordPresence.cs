using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Waddamburo.App.Online;

/// <summary>What Discord shows on the player's profile: two lines, and since when (a song's start).</summary>
internal sealed record PresenceActivity(string Details, string? State = null, DateTimeOffset? Since = null);

/// <summary>
/// Discord Rich Presence over Discord's local IPC (a Unix socket on Linux, a named pipe on Windows): no library, no
/// network. <see cref="Set"/> is called every tick and only notes the activity; a background thread connects (again
/// every 15 s while Discord isn't running), and sends the latest activity when it changes. Nothing here can stall or
/// fail the game: Discord missing or closing only means no status.
/// </summary>
internal sealed class DiscordPresence : IDisposable
{
    // The Waddamburo application on Discord's developer portal (its "logo" art asset is the large image).
    private const string ApplicationId = "1568020082242617344";
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(15);

    private readonly object _lock = new();
    private readonly AutoResetEvent _changed = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private PresenceActivity? _wanted;
    private bool _enabled;

    public DiscordPresence()
    {
        _thread = new Thread(run) { IsBackground = true, Name = "Discord presence" };
        _thread.Start();
    }

    /// <summary>The activity to show; null clears it. <paramref name="enabled"/> false (the setting) clears it too.</summary>
    public void Set(PresenceActivity? activity, bool enabled)
    {
        lock (_lock)
        {
            if (activity == _wanted && enabled == _enabled)
                return;
            _wanted = activity;
            _enabled = enabled;
        }
        _changed.Set();
    }

    private void run()
    {
        while (!_stop.IsCancellationRequested)
        {
            bool enabled;
            lock (_lock)
                enabled = _enabled;
            if (!enabled)
            {
                WaitHandle.WaitAny([_changed, _stop.Token.WaitHandle]);
                continue;
            }
            try
            {
                using var stream = connect();
                if (stream is not null)
                    session(stream);
            }
            catch (Exception exception) when (exception is IOException or SocketException or JsonException
                or InvalidDataException or ObjectDisposedException or UnauthorizedAccessException or TimeoutException)
            {
                Console.Error.WriteLine($"Warning DISCORD: {exception.Message}");
            }
            WaitHandle.WaitAny([_stop.Token.WaitHandle], RetryAfter);
        }
    }

    // Sends the activity whenever it changes, until the setting goes off or Discord closes (a write then throws).
    private void session(Stream stream)
    {
        write(stream, 0, writer =>
        {
            writer.WriteNumber("v", 1);
            writer.WriteString("client_id", ApplicationId);
        });
        read(stream); // READY
        Console.WriteLine("Discord presence connected.");
        PresenceActivity? sent = null;
        var first = true;
        while (!_stop.IsCancellationRequested)
        {
            PresenceActivity? wanted;
            bool enabled;
            lock (_lock)
                (wanted, enabled) = (_wanted, _enabled);
            if (!enabled)
                wanted = null;
            if (first || wanted != sent)
            {
                write(stream, 1, writer => activity(writer, wanted));
                read(stream);
                sent = wanted;
                first = false;
            }
            if (!enabled)
                return;
            WaitHandle.WaitAny([_changed, _stop.Token.WaitHandle]);
        }
    }

    private static void activity(Utf8JsonWriter writer, PresenceActivity? activity)
    {
        writer.WriteString("cmd", "SET_ACTIVITY");
        writer.WriteString("nonce", Guid.NewGuid().ToString());
        writer.WriteStartObject("args");
        writer.WriteNumber("pid", Environment.ProcessId);
        if (activity is null)
            writer.WriteNull("activity");
        else
        {
            writer.WriteStartObject("activity");
            writer.WriteString("details", clip(activity.Details));
            if (activity.State is { Length: > 0 } state)
                writer.WriteString("state", clip(state));
            if (activity.Since is { } since)
            {
                writer.WriteStartObject("timestamps");
                writer.WriteNumber("start", since.ToUnixTimeSeconds());
                writer.WriteEndObject();
            }
            writer.WriteStartObject("assets");
            writer.WriteString("large_image", "logo");
            writer.WriteString("large_text", "Waddamburo");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    // Discord refuses a field over 128 characters (and the whole activity with it).
    private static string clip(string text) => text.Length <= 128 ? text : text[..127] + "…";

    private static void write(Stream stream, int opcode, Action<Utf8JsonWriter> body)
    {
        var json = new MemoryStream();
        using (var writer = new Utf8JsonWriter(json))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        var frame = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), (int)json.Length);
        json.ToArray().CopyTo(frame, 8);
        stream.Write(frame);
        stream.Flush();
    }

    // One frame back; an error or a close from Discord ends the session.
    private static void read(Stream stream)
    {
        Span<byte> header = stackalloc byte[8];
        stream.ReadExactly(header);
        var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        if (length is < 0 or > 1 << 20)
            throw new InvalidDataException("Discord sent a malformed frame.");
        var body = new byte[length];
        stream.ReadExactly(body);
        if (opcode == 2)
            throw new IOException($"Discord closed the connection: {Encoding.UTF8.GetString(body)}");
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("evt", out var evt) && evt.ValueEquals("ERROR"))
            Console.Error.WriteLine($"Warning DISCORD: {document.RootElement.GetProperty("data")}");
    }

    // Discord's ipc-0 to ipc-9; on Linux also inside a Flatpak's or Snap's runtime folder.
    private static Stream? connect()
    {
        for (var index = 0; index < 10; index++)
        {
            if (OperatingSystem.IsWindows())
            {
                var pipe = new NamedPipeClientStream(".", $"discord-ipc-{index}", PipeDirection.InOut, PipeOptions.None);
                try
                {
                    pipe.Connect(200);
                    return pipe;
                }
                catch (Exception exception) when (exception is TimeoutException or IOException)
                {
                    pipe.Dispose();
                }
                continue;
            }
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } folder ? folder
                : Environment.GetEnvironmentVariable("TMPDIR") is { Length: > 0 } temporary ? temporary : "/tmp";
            // A Flatpak client (Discord, Vesktop) has its own runtime folder: .flatpak/<app>/xdg-run, or app/<app>.
            string[] flatpaks = Directory.Exists($"{runtime}/.flatpak")
                ? [.. Directory.GetDirectories($"{runtime}/.flatpak").Select(app => $"{app}/xdg-run/discord-ipc-{index}")] : [];
            foreach (var path in (string[])[$"{runtime}/discord-ipc-{index}", .. flatpaks, $"{runtime}/app/com.discordapp.Discord/discord-ipc-{index}",
                         $"{runtime}/snap.discord/discord-ipc-{index}", $"{runtime}/app/dev.vencord.Vesktop/discord-ipc-{index}"])
            {
                // No File.Exists first: a socket isn't a file to it. A missing path fails to connect at once.
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    socket.Connect(new UnixDomainSocketEndPoint(path));
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException)
                {
                    socket.Dispose();
                }
            }
        }
        return null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _changed.Set();
        _thread.Join(TimeSpan.FromSeconds(1));
        _stop.Dispose();
        _changed.Dispose();
    }
}
