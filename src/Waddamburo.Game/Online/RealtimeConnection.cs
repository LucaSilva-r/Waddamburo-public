using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text.Json;

namespace Waddamburo.Game.Online;

/// <summary>
/// A Pusher-protocol (v7) WebSocket client, as TaikOnline's Reverb speaks it: subscribes channels
/// (private ones signed through <see cref="Authorize"/>), answers the server's pings, pings back when
/// the line goes quiet and reconnects with jittered backoff, subscribing everything again.
/// </summary>
/// <remarks>
/// Close codes follow the Pusher protocol: 4000-4099 the server will refuse a reconnect as well (the
/// client stops), 4100-4199 reconnect after backing off, 4200-4299 reconnect at once; anything else
/// (a dropped line) backs off. pusher:error messages never decide this: Reverb sends them for a refused
/// private channel (4009) on a connection that stays open. ponytail: receive-only (no client events)
/// and no presence channels; multiplayer will need both.
/// </remarks>
public sealed class RealtimeConnection(Uri url, bool insecure = false) : IAsyncDisposable
{
    private const int MaxMessageBytes = 1 << 20;

    private readonly HashSet<string> _channels = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private ClientWebSocket? _socket;
    private string? _socketId;
    private Task? _run;

    /// <summary>Signs a private channel for this connection's socket id: the "auth" string, or null to skip it.</summary>
    public Func<string, string, CancellationToken, Task<string?>>? Authorize { get; init; }

    /// <summary>The first reconnect delay; doubled per failed attempt up to <see cref="MaxReconnectDelay"/>.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a ping may go unanswered before the line counts as dead.</summary>
    public TimeSpan PongTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>An event on a subscribed channel: channel, event name, data (the decoded payload).</summary>
    public event Action<string, string, JsonElement>? Received;

    /// <summary>Connected (true) or lost (false); a lost connection keeps retrying unless <see cref="Stopped"/>.</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>A channel the server or <see cref="Authorize"/> refused; it is tried again on the next connection.</summary>
    public event Action<string>? SubscriptionFailed;

    public bool Connected => _socketId is not null;

    /// <summary>The server closed with a code that a reconnect would get again (4000-4099): no more attempts.</summary>
    public bool Stopped { get; private set; }

    public void Start() => _run ??= Task.Run(() => runAsync(_stop.Token));

    /// <summary>Subscribes now when connected, and again after every reconnect.</summary>
    public void Subscribe(string channel)
    {
        lock (_channels)
            if (!_channels.Add(channel))
                return;
        if (_socketId is { } socketId)
            _ = subscribeAsync(channel, socketId, _stop.Token);
    }

    public void Unsubscribe(string channel)
    {
        lock (_channels)
            if (!_channels.Remove(channel))
                return;
        if (_socketId is not null)
            _ = sendAsync("pusher:unsubscribe", writer => writer.WriteString("channel", channel), _stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_run is { } run)
            await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
        _send.Dispose();
    }

    private async Task runAsync(CancellationToken cancellationToken)
    {
        var backoff = ReconnectDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            int? closeCode = null;
            try
            {
                using var socket = new ClientWebSocket();
                // server_insecure: a local server's self-signed certificate, as ScoreClient.CreateHttp accepts it.
#pragma warning disable CA5359
                if (insecure)
                    socket.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
                var query = $"protocol=7&client=waddamburo&version={typeof(RealtimeConnection).Assembly.GetName().Version}";
                await socket.ConnectAsync(new UriBuilder(url) { Query = query }.Uri, cancellationToken).ConfigureAwait(false);
                _socket = socket;
                await readAsync(socket, () => backoff = ReconnectDelay, cancellationToken).ConfigureAwait(false);
                closeCode = (int?)socket.CloseStatus;
            }
            catch (Exception exception) when (exception is WebSocketException or IOException or JsonException
                or TimeoutException or AuthenticationException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine($"Warning REALTIME: {exception.Message}");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                // Anything else is a bug here, but the notices must keep coming: log it and reconnect.
                Console.Error.WriteLine($"Warning REALTIME: unexpected {exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                _socket = null;
                if (Interlocked.Exchange(ref _socketId, null) is not null)
                    ConnectionChanged?.Invoke(false);
            }

            TimeSpan delay;
            switch (closeCode)
            {
                case >= 4000 and < 4100:
                    Stopped = true;
                    Console.Error.WriteLine($"Warning REALTIME: the server refused the connection (close {closeCode}); not reconnecting.");
                    return;
                case >= 4200 and < 4300:
                    delay = TimeSpan.Zero;
                    break;
                default: // 4100-4199, a normal close, a dropped line
                    // Jitter: a restarted server is not hit by every client in the same instant.
                    delay = backoff * (0.5 + Random.Shared.NextDouble() / 2);
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxReconnectDelay.Ticks));
                    break;
            }
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task readAsync(ClientWebSocket socket, Action connected, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        var activity = TimeSpan.FromSeconds(30);
        var pinged = false;
        Task<ValueWebSocketReceiveResult>? pending = null;
        while (socket.State == WebSocketState.Open)
        {
            // Quiet for the activity timeout: ping; no answer within PongTimeout: the line is dead. The
            // receive stays pending meanwhile: cancelling a ClientWebSocket receive aborts the socket.
            pending ??= socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).AsTask();
            if (await Task.WhenAny(pending, Task.Delay(pinged ? PongTimeout : activity, cancellationToken)).ConfigureAwait(false) != pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pinged)
                    throw new TimeoutException("The realtime server stopped answering.");
                pinged = true;
                await sendAsync("pusher:ping", null, cancellationToken).ConfigureAwait(false);
                continue;
            }
            var result = await pending.ConfigureAwait(false);
            pending = null;
            if (result.MessageType == WebSocketMessageType.Close)
                return;
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
                throw new IOException("A realtime message was too large.");
            if (!result.EndOfMessage)
                continue;
            pinged = false;
            var text = message.ToArray();
            message.SetLength(0);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var name = root.TryGetProperty("event", out var nameElement) ? nameElement.GetString() ?? "" : "";
            var channelName = root.TryGetProperty("channel", out var channelElement) ? channelElement.GetString() : null;
            // Pusher sends data as a JSON string holding JSON (mostly); decode it once more when it does.
            using var data = root.TryGetProperty("data", out var dataElement) ? decode(dataElement) : null;
            switch (name)
            {
                case "pusher:connection_established":
                    var socketId = data!.RootElement.GetProperty("socket_id").GetString()!;
                    if (data.RootElement.TryGetProperty("activity_timeout", out var timeout) && timeout.TryGetInt32(out var seconds))
                        activity = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 120));
                    _socketId = socketId;
                    connected();
                    ConnectionChanged?.Invoke(true);
                    string[] channels;
                    lock (_channels)
                        channels = [.. _channels];
                    foreach (var channel in channels)
                        await subscribeAsync(channel, socketId, cancellationToken).ConfigureAwait(false);
                    break;
                case "pusher:ping":
                    await sendAsync("pusher:pong", null, cancellationToken).ConfigureAwait(false);
                    break;
                case "pusher:error":
                    // Reverb: 4009 for a refused private channel (the connection stays open), 4301 rate limit, ...
                    Console.Error.WriteLine($"Warning REALTIME: {data?.RootElement.ToString()}");
                    break;
                case "pusher:subscription_error":
                    if (channelName is not null)
                        SubscriptionFailed?.Invoke(channelName);
                    break;
                case "pusher:pong":
                    break;
                default:
                    if (channelName is not null && !name.StartsWith("pusher_internal:", StringComparison.Ordinal) && data is not null)
                        Received?.Invoke(channelName, name, data.RootElement);
                    break;
            }
        }
    }

    private async Task subscribeAsync(string channel, string socketId, CancellationToken cancellationToken)
    {
        string? auth = null;
        if (channel.StartsWith("private-", StringComparison.Ordinal))
        {
            try
            {
                auth = Authorize is null ? null : await Authorize(socketId, channel, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                Console.Error.WriteLine($"Warning REALTIME: {channel} not authorized ({exception.Message}).");
            }
            if (auth is null)
            {
                SubscriptionFailed?.Invoke(channel);
                return;
            }
        }
        await sendAsync("pusher:subscribe", writer =>
        {
            writer.WriteString("channel", channel);
            if (auth is not null)
                writer.WriteString("auth", auth);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task sendAsync(string name, Action<Utf8JsonWriter>? data, CancellationToken cancellationToken)
    {
        if (_socket is not { State: WebSocketState.Open } socket)
            return;
        var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("event", name);
            writer.WriteStartObject("data");
            data?.Invoke(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(output.ToArray(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException)
        {
            // The read loop sees the broken socket and reconnects.
        }
        finally
        {
            _send.Release();
        }
    }

    private static JsonDocument decode(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.String && data.GetString() is { } text)
        {
            try
            {
                return JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                // Plain text: keep it as a JSON string.
                var output = new MemoryStream();
                using (var writer = new Utf8JsonWriter(output))
                    writer.WriteStringValue(text);
                return JsonDocument.Parse(output.ToArray());
            }
        }
        return JsonDocument.Parse(data.GetRawText());
    }
}
