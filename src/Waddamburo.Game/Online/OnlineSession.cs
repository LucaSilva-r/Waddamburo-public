using System.Text.Json;

namespace Waddamburo.Game.Online;

/// <summary>What one player tells the other through the room channel to set up the link.</summary>
public sealed record PeerOffer(byte[] Token, IReadOnlyList<PeerCandidate> Candidates)
{
    private const int TokenLength = 8;

    /// <summary>Reads an offer from the other player; null when it doesn't add up. Unknown or broken candidates are skipped.</summary>
    public static PeerOffer? Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String
            || !data.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            return null;
        var bytes = new byte[TokenLength + 1];
        if (!Convert.TryFromBase64String(token.GetString()!, bytes, out var length) || length != TokenLength)
            return null;
        List<PeerCandidate> parsed = [];
        foreach (var candidate in candidates.EnumerateArray().Take(32))
            if (candidate.ValueKind == JsonValueKind.String && PeerCandidate.Parse(candidate.GetString()!) is { } valid)
                parsed.Add(valid);
        return new PeerOffer(bytes[..TokenLength], parsed);
    }

    public void Write(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString("token", Convert.ToBase64String(Token));
        writer.WriteStartArray("candidates");
        foreach (var candidate in Candidates)
            writer.WriteStringValue(candidate.ToString());
        writer.WriteEndArray();
    }
}

/// <summary>
/// Opening a room or taking a seat in one, then connecting to the other player: gathers this side's addresses, joins
/// the room's channel, and swaps offers there. Whoever finds the other already in the room speaks first, and the other
/// answers, so it doesn't matter who arrives when. After that the link needs neither the channel nor the server.
/// </summary>
public sealed class OnlineSession(HttpClient http, bool insecure = false) : IAsyncDisposable
{
    private const string OfferEvent = "client-peer-offer";

    private readonly RoomClient _rooms = new(http);
    private readonly NoticeClient _notices = new(http);
    private RealtimeConnection? _realtime;
    private PeerOffer? _offer;
    private int _offered;
    private int _connected;

    public OnlineRoom? Room { get; private set; }

    public PeerLink Link { get; } = new();

    /// <summary>Where this side can be reached, once started.</summary>
    public IReadOnlyList<PeerCandidate> Candidates => _offer?.Candidates ?? [];

    /// <summary>Hosts a new room, or with a <paramref name="code"/> joins one; false when that room can't be joined.</summary>
    public async Task<bool> StartAsync(string? code = null, CancellationToken cancellationToken = default)
    {
        Room = code is null ? await _rooms.CreateAsync(cancellationToken).ConfigureAwait(false)
            : await _rooms.JoinAsync(code, cancellationToken).ConfigureAwait(false);
        if (Room is not { } room)
            return false;
        var servers = await _rooms.IceServersAsync(room.Code, cancellationToken).ConfigureAwait(false);
        var candidates = await Link.GatherAsync(servers, cancellationToken).ConfigureAwait(false);
        _offer = new PeerOffer(Link.LocalToken.ToArray(), candidates);
        if (await _notices.RealtimeAsync(cancellationToken).ConfigureAwait(false) is not { } url)
            throw new InvalidOperationException("The server has no realtime connection.");
        var realtime = new RealtimeConnection(url, insecure)
        {
            Authorize = (socketId, channel, token) => _notices.AuthorizeChannelAsync(socketId, channel, token),
        };
        realtime.PresenceSynced += (_, members) =>
        {
            if (members.Count > 1)
                sendOffer();
        };
        realtime.MemberAdded += (_, _) => sendOffer();
        realtime.Whispered += (_, name, _, data) =>
        {
            if (name != OfferEvent || PeerOffer.Parse(data) is not { } offer)
                return;
            // The answer to a player who arrived first, and whose offer we hadn't answered.
            sendOffer();
            if (Interlocked.Exchange(ref _connected, 1) == 0)
                Link.Connect(offer.Token, offer.Candidates);
        };
        realtime.Subscribe(room.Channel);
        realtime.Start();
        _realtime = realtime;
        return true;
    }

    /// <summary>Leaves the room: a host's closes.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_realtime is { } realtime)
            await realtime.DisposeAsync().ConfigureAwait(false);
        if (Room is { } room)
        {
            try
            {
                await _rooms.LeaveAsync(room.Code).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                Console.Error.WriteLine($"Warning ONLINE: room not left ({exception.Message}); it expires by itself.");
            }
        }
        await Link.DisposeAsync().ConfigureAwait(false);
    }

    private void sendOffer()
    {
        if (Interlocked.Exchange(ref _offered, 1) == 0 && _realtime is { } realtime && Room is { } room && _offer is { } offer)
            _ = realtime.WhisperAsync(room.Channel, OfferEvent, offer.Write);
    }
}
