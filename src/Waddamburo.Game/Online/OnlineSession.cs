using System.Text.Json;
using Waddamburo.Game.Gameplay;

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
/// Being in a room, and connecting to the other player: gathers this side's addresses, joins the room's channel, and swaps
/// offers there. Whoever finds the other already in the room speaks first, and the other answers, so it doesn't matter who
/// arrives when. After that the link needs neither the channel nor the server, though the room is kept alive until left.
/// </summary>
/// <remarks>ponytail: one link per session, so once the other player leaves the room is over; a new guest would need a new link.</remarks>
public sealed class OnlineSession(HttpClient http, bool insecure = false) : IAsyncDisposable
{
    private const string OfferEvent = "client-peer-offer";
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    private readonly RoomClient _rooms = new(http);
    private readonly NoticeClient _notices = new(http);
    private readonly CancellationTokenSource _stop = new();
    private RealtimeConnection? _realtime;
    private Task? _keepingAlive;
    private IReadOnlyList<PeerCandidate> _candidates = [];
    private bool _strangers;
    private int _offered;
    private int _connected;
    private string? _selfId;
    private string? _partnerId;

    public OnlineRoom? Room { get; private set; }

    public PeerLink Link { get; } = new();

    /// <summary>The players' messages to each other (a song picked, ready, go).</summary>
    public RoomMessages Messages { get; } = new();

    /// <summary>
    /// A song being played together: this player's hits go out through the relay, the other player's come in to the
    /// feed. Set for a song, cleared after it (null meanwhile: their hits aren't expected).
    /// </summary>
    public (HitRelay Relay, RemoteLaneFeed Feed)? Song { get; set; }

    /// <summary>Sends what's due over the link: the messages not yet confirmed, and the song's hits. Each game tick.</summary>
    public void Pump()
    {
        foreach (var packet in Messages.Due())
            Link.Send(packet);
        if (Song is { } song)
            Link.Send(song.Relay.Build());
    }

    // What the other player sends: a message, or (in a song) their hits.
    private void received(byte[] packet)
    {
        if (Messages.Read(packet, out var reply))
        {
            if (reply is not null)
                Link.Send(reply);
            return;
        }
        if (Song is { } song)
            song.Relay.Read(packet, song.Feed);
    }

    /// <summary>This player opened the room (or is waiting in quick play) rather than joining it.</summary>
    public bool IsHost { get; private set; }

    /// <summary>Joining was refused for the password.</summary>
    public bool WrongPassword { get; private set; }

    /// <summary>The room no longer exists: the host left, or it ran out.</summary>
    public bool RoomGone { get; private set; }

    /// <summary>Offer this player's own network's addresses even to strangers (testing two games on one machine).</summary>
    public bool ShareLanWithStrangers { get; init; }

    /// <summary>The other player in the room, as their name board and Don-chan show them (null: nobody yet, or they left).</summary>
    public Scores.ScoreProfile? Partner { get; private set; }

    /// <summary>Where this side can be reached, once started.</summary>
    public IReadOnlyList<PeerCandidate> Candidates => _candidates;

    /// <summary>Opens a room hosted by this player.</summary>
    public async Task<bool> HostAsync(string? name = null, bool isPublic = true, string? password = null, CancellationToken cancellationToken = default)
    {
        Room = await _rooms.CreateAsync(name, isPublic, password, cancellationToken).ConfigureAwait(false);
        IsHost = true;
        await connectAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Joins a room; false when that can't be done. <paramref name="listed"/>: found in the public list, so its host is a stranger.</summary>
    public async Task<bool> JoinAsync(string code, string? password = null, bool listed = false, CancellationToken cancellationToken = default)
    {
        var result = await _rooms.JoinAsync(code, password, cancellationToken).ConfigureAwait(false);
        WrongPassword = result.WrongPassword;
        Room = result.Room;
        _strangers = listed;
        if (Room is null)
            return false;
        await connectAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Takes the waiting player's room, or waits for the next one in a new room.</summary>
    public async Task<bool> QuickPlayAsync(CancellationToken cancellationToken = default)
    {
        Room = await _rooms.QuickPlayAsync(cancellationToken).ConfigureAwait(false);
        // Whoever was waiting hosts: the server says which.
        IsHost = Room.IsHost;
        _strangers = true;
        await connectAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The host changes the room: what's null stays as it is; an empty password removes it.</summary>
    public async Task UpdateAsync(string? name = null, bool? isPublic = null, string? password = null, CancellationToken cancellationToken = default)
    {
        if (Room is { } room)
            Room = await _rooms.UpdateAsync(room.Code, name, isPublic, password, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Leaves the room: a host's closes.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_keepingAlive is { } keepingAlive)
            await keepingAlive.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
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
                Console.Error.WriteLine($"Warning ONLINE: room not left ({exception.Message}); it runs out by itself.");
            }
        }
        await Link.DisposeAsync().ConfigureAwait(false);
    }

    private async Task connectAsync(CancellationToken cancellationToken)
    {
        var room = Room!;
        Link.Received += received;
        _keepingAlive = Task.Run(() => keepAliveAsync(room.Code, _stop.Token), CancellationToken.None);
        var servers = await _rooms.IceServersAsync(room.Code, cancellationToken).ConfigureAwait(false);
        _candidates = await Link.GatherAsync(servers, cancellationToken).ConfigureAwait(false);
        if (await _notices.RealtimeAsync(cancellationToken).ConfigureAwait(false) is not { } url)
            throw new InvalidOperationException("The server has no realtime connection.");
        var realtime = new RealtimeConnection(url, insecure)
        {
            Authorize = async (socketId, channel, token) =>
            {
                // Who this player is on the channel, so the member who isn't is the partner.
                var authorization = await _notices.AuthorizeChannelAsync(socketId, channel, token).ConfigureAwait(false);
                if (authorization?.ChannelData is { } data)
                    _selfId = PresenceMember.UserIdOf(data);
                return authorization;
            },
        };
        realtime.PresenceSynced += (_, members) =>
        {
            foreach (var member in members)
                meet(member);
            if (members.Count > 1)
                sendOffer();
        };
        realtime.MemberAdded += (_, member) =>
        {
            meet(member);
            sendOffer();
        };
        realtime.MemberRemoved += (_, id) =>
        {
            if (id == _partnerId)
                (Partner, _partnerId) = (null, null);
        };
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
    }

    // The member who isn't this player; what they show comes signed by the server, but a token never travels this way.
    private void meet(PresenceMember member)
    {
        if (member.Id == _selfId || member.Info.ValueKind != System.Text.Json.JsonValueKind.Object)
            return;
        try
        {
            if (System.Text.Json.JsonSerializer.Deserialize(member.Info, Scores.ScoreJson.Default.ScoreProfile) is { } profile)
                (Partner, _partnerId) = (profile with { Token = null }, member.Id);
        }
        catch (System.Text.Json.JsonException exception)
        {
            Console.Error.WriteLine($"Warning ONLINE: the other player's profile wasn't read ({exception.Message}).");
        }
    }

    private async Task keepAliveAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(KeepAliveInterval, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!await _rooms.KeepAliveAsync(code, cancellationToken).ConfigureAwait(false))
                    {
                        RoomGone = true;
                        return;
                    }
                }
                catch (HttpRequestException exception)
                {
                    // The server being away doesn't end a game already connected.
                    Console.Error.WriteLine($"Warning ONLINE: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Left.
        }
    }

    // Someone met through the list or quick play is a stranger: they get no addresses from this player's own network.
    // Two players on one LAN then connect through their router. Decided now: a host may have changed the room.
    private void sendOffer()
    {
        if (Interlocked.Exchange(ref _offered, 1) != 0 || _realtime is not { } realtime || Room is not { } room)
            return;
        var strangers = (_strangers || IsHost && room.IsPublic) && !ShareLanWithStrangers;
        var offer = new PeerOffer(Link.LocalToken.ToArray(), strangers ? [.. _candidates.Where(static candidate => candidate.Kind != PeerCandidateKind.Host)] : _candidates);
        _ = realtime.WhisperAsync(room.Channel, OfferEvent, offer.Write);
    }
}
