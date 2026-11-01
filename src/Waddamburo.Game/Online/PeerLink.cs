using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Waddamburo.Game.Online;

/// <summary>Where a peer might be reached: its own network (Host), its public address (Reflexive), or a relay (Relay).</summary>
public enum PeerCandidateKind
{
    Host,
    Reflexive,
    Relay,
}

public readonly record struct PeerCandidate(PeerCandidateKind Kind, IPEndPoint EndPoint)
{
    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()} {EndPoint}";

    /// <summary>A candidate as <see cref="ToString"/> writes it; null when it isn't one (they come from the other player).</summary>
    public static PeerCandidate? Parse(string text)
    {
        var parts = text.Split(' ');
        return parts.Length == 2 && Enum.TryParse<PeerCandidateKind>(parts[0], true, out var kind) && Enum.IsDefined(kind)
            && IPEndPoint.TryParse(parts[1], out var endPoint) && endPoint.AddressFamily == AddressFamily.InterNetwork && endPoint.Port != 0
                ? new PeerCandidate(kind, endPoint) : null;
    }
}

public enum PeerLinkState
{
    Idle,
    Connecting,
    Direct,
    Relayed,
    Failed,
    Disconnected,
}

/// <summary>
/// A UDP link to one other Waddamburo. Both ends are ours, so it needs no full ICE: each side gathers where it can be
/// reached (<see cref="GatherAsync"/>), they swap that and a token through the room channel, then each probes every
/// address (<see cref="Connect"/>). The first answer opens the link, a route without a relay replaces one with.
/// Packets carry the receiver's token, so strangers' datagrams are dropped. Delivery isn't guaranteed: callers repeat
/// what must arrive. Events are raised on a background thread.
/// </summary>
/// <remarks>ponytail: IPv4 only; an IPv6-only network needs the relay's IPv4 path or a dual-stack socket added here.</remarks>
public sealed class PeerLink : IAsyncDisposable
{
    private const byte Probe = 0xD1, ProbeAnswer = 0xD2, Payload = 0xD3;
    private const int TokenLength = 8;
    private const int HeaderLength = 1 + TokenLength;
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PermissionLifetime = TimeSpan.FromMinutes(4);

    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<StunMessage>> _pending = new();
    private readonly byte[] _localToken = RandomNumberGenerator.GetBytes(TokenLength);
    private readonly object _lock = new();
    private byte[]? _remoteToken;
    private PeerCandidate[] _remote = [];
    private Task? _receiving;
    private Task? _ticking;
    private (IPEndPoint Target, bool ViaRelay, bool Relayed)? _route;
    private long _connectStarted;
    private long _lastHeard;

    private IPEndPoint? _relayServer;
    private IPEndPoint? _relayAddress;
    private byte[]? _relayKey;
    private string? _relayUsername, _relayRealm, _relayNonce;
    private TimeSpan _relayLifetime;
    private long _relayRefreshed, _permissionsSent;

    public PeerLink() => _socket.Bind(new IPEndPoint(IPAddress.Any, 0));

    /// <summary>Give up connecting after this long.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>A connected peer counts as gone after this long without a packet.</summary>
    public TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public PeerLinkState State { get; private set; }

    /// <summary>The latest measured round trip, once connected.</summary>
    public TimeSpan? RoundTrip { get; private set; }

    /// <summary>The token the other player must be told (it proves their packets came from them).</summary>
    public ReadOnlyMemory<byte> LocalToken => _localToken;

    public int LocalPort => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    public event Action<PeerLinkState>? StateChanged;

    /// <summary>A payload from the peer. Untrusted: they could send anything.</summary>
    public event Action<byte[]>? Received;

    /// <summary>Everywhere this link might be reached, asking <paramref name="servers"/> for the public and relayed addresses.</summary>
    public async Task<IReadOnlyList<PeerCandidate>> GatherAsync(IReadOnlyList<IceServer> servers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(servers);
        _receiving ??= Task.Run(receiveAsync, CancellationToken.None);
        var candidates = hostAddresses().Select(address => new PeerCandidate(PeerCandidateKind.Host, new IPEndPoint(address, LocalPort))).ToList();
        foreach (var server in servers)
        {
            foreach (var url in server.Urls)
            {
                try
                {
                    if (await resolveAsync(url, cancellationToken).ConfigureAwait(false) is not var (scheme, endPoint))
                        continue;
                    if (scheme == "stun" && await reflexiveAsync(endPoint, cancellationToken).ConfigureAwait(false) is { } reflexive)
                        candidates.Add(new PeerCandidate(PeerCandidateKind.Reflexive, reflexive));
                    else if (scheme == "turn" && _relayAddress is null && server is { Username: { } username, Credential: { } credential }
                        && await allocateAsync(endPoint, username, credential, cancellationToken).ConfigureAwait(false) is { } relayed)
                        candidates.Add(new PeerCandidate(PeerCandidateKind.Relay, relayed));
                }
                catch (Exception exception) when (exception is SocketException or TimeoutException)
                {
                    // One server being down costs its candidate, not the link.
                    Console.Error.WriteLine($"Warning PEERLINK: {url}: {exception.Message}");
                }
            }
        }
        return [.. candidates.Distinct()];
    }

    /// <summary>Starts probing the peer. Bad data from them just gives fewer addresses to try.</summary>
    public void Connect(ReadOnlyMemory<byte> remoteToken, IEnumerable<PeerCandidate> remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (remoteToken.Length != TokenLength)
            throw new ArgumentException($"A peer token is {TokenLength} bytes.", nameof(remoteToken));
        lock (_lock)
        {
            _remoteToken = remoteToken.ToArray();
            _remote = [.. remote.Distinct().Take(32)]; // the list comes from the other player
            _connectStarted = _lastHeard = Stopwatch.GetTimestamp();
        }
        setState(PeerLinkState.Connecting);
        _receiving ??= Task.Run(receiveAsync, CancellationToken.None);
        _ticking ??= Task.Run(tickAsync, CancellationToken.None);
    }

    /// <summary>Sends to the peer when connected; otherwise it's dropped. Never blocks on the network.</summary>
    public void Send(ReadOnlySpan<byte> payload)
    {
        if (_route is not { } route || _remoteToken is not { } token)
            return;
        Span<byte> packet = new byte[HeaderLength + payload.Length];
        packet[0] = Payload;
        token.CopyTo(packet[1..]);
        payload.CopyTo(packet[HeaderLength..]);
        sendVia(packet, route.Target, route.ViaRelay);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _socket.Dispose();
        foreach (var task in new[] { _receiving, _ticking }.OfType<Task>())
            await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }

    private void setState(PeerLinkState state)
    {
        if (State == state)
            return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private static IEnumerable<IPAddress> hostAddresses()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
        return interfaces.Where(static adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(static adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(static unicast => unicast.Address)
            .Where(static address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));
    }

    // "stun:host:port" or "turn:host:port?transport=udp": only UDP is spoken here.
    private static async Task<(string Scheme, IPEndPoint EndPoint)?> resolveAsync(string url, CancellationToken cancellationToken)
    {
        var scheme = url.Split(':', 2)[0];
        if (scheme is not ("stun" or "turn") || url.Contains("transport=tcp", StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = url[(scheme.Length + 1)..].Split('?')[0];
        var separator = rest.LastIndexOf(':');
        var host = separator < 0 ? rest : rest[..separator];
        if (host.Length == 0 || !int.TryParse(separator < 0 ? "3478" : rest[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is 0 or > 65535)
            return null;
        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
        return addresses.Length == 0 ? null : (scheme, new IPEndPoint(addresses[0], port));
    }

    private async Task<IPEndPoint?> reflexiveAsync(IPEndPoint server, CancellationToken cancellationToken)
    {
        var request = new StunMessage(StunMessage.BindingRequest);
        var response = await transactAsync(request, null, server, cancellationToken).ConfigureAwait(false);
        return response.Type == StunMessage.BindingSuccess ? response.Address(StunMessage.XorMappedAddress) : null;
    }

    private async Task<IPEndPoint?> allocateAsync(IPEndPoint server, string username, string credential, CancellationToken cancellationToken)
    {
        byte[] transport = [17, 0, 0, 0]; // UDP
        // The first try is refused with the realm and nonce to sign with.
        var challenge = await transactAsync(new StunMessage(StunMessage.AllocateRequest).With(StunMessage.RequestedTransport, transport), null, server, cancellationToken).ConfigureAwait(false);
        if (challenge.Type == StunMessage.AllocateError && challenge.Error == 401 && challenge.Text(StunMessage.Realm) is { } realm && challenge.Text(StunMessage.Nonce) is { } nonce)
        {
            var key = StunMessage.LongTermKey(username, realm, credential);
            var request = new StunMessage(StunMessage.AllocateRequest).With(StunMessage.RequestedTransport, transport)
                .With(StunMessage.Username, username).With(StunMessage.Realm, realm).With(StunMessage.Nonce, nonce);
            var response = await transactAsync(request, key, server, cancellationToken).ConfigureAwait(false);
            if (response.Type == StunMessage.AllocateSuccess && response.Address(StunMessage.XorRelayedAddress) is { } relayed)
            {
                (_relayServer, _relayKey, _relayUsername, _relayRealm, _relayNonce) = (server, key, username, realm, nonce);
                _relayLifetime = TimeSpan.FromSeconds(response.Find(StunMessage.Lifetime) is { Length: 4 } lifetime ? BinaryPrimitives.ReadUInt32BigEndian(lifetime) : 600);
                _relayRefreshed = Stopwatch.GetTimestamp();
                _relayAddress = relayed;
                return relayed;
            }
            Console.Error.WriteLine($"Warning PEERLINK: the relay refused the allocation ({response.Error}).");
        }
        return null;
    }

    // A STUN request and its answer, sent again twice if nothing comes back (it's UDP).
    private async Task<StunMessage> transactAsync(StunMessage request, byte[]? key, IPEndPoint server, CancellationToken cancellationToken)
    {
        var id = Convert.ToHexString(request.TransactionId);
        var answer = new TaskCompletionSource<StunMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            var packet = request.Encode(key);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await _socket.SendToAsync(packet, SocketFlags.None, server, cancellationToken).ConfigureAwait(false);
                if (await Task.WhenAny(answer.Task, Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)).ConfigureAwait(false) == answer.Task)
                    return await answer.Task.ConfigureAwait(false);
            }
            throw new TimeoutException($"No answer from {server}.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task receiveAsync()
    {
        var buffer = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // Windows reports an unreachable probe target on the next receive.
            }
            var from = (IPEndPoint)result.RemoteEndPoint;
            var data = buffer.AsSpan(0, result.ReceivedBytes);
            if (data.Length >= HeaderLength && data[0] is Probe or ProbeAnswer or Payload)
                handlePacket(data, from, viaRelay: false);
            else if (StunMessage.Parse(data) is { } message)
                handleStun(message, from);
        }
    }

    private void handleStun(StunMessage message, IPEndPoint from)
    {
        if (message.Type == StunMessage.DataIndication)
        {
            // Only our own relay may speak for someone else.
            if (from.Equals(_relayServer) && message.Address(StunMessage.XorPeerAddress) is { } peer && message.Find(StunMessage.Data) is { Length: >= HeaderLength } data)
                handlePacket(data, peer, viaRelay: true);
            return;
        }
        if (_pending.TryRemove(Convert.ToHexString(message.TransactionId), out var answer))
            answer.TrySetResult(message);
    }

    private void handlePacket(ReadOnlySpan<byte> data, IPEndPoint from, bool viaRelay)
    {
        if (!data.Slice(1, TokenLength).SequenceEqual(_localToken) || _remoteToken is not { } token)
            return;
        Volatile.Write(ref _lastHeard, Stopwatch.GetTimestamp());
        switch (data[0])
        {
            case Probe when data.Length == HeaderLength + 8:
                Span<byte> answer = stackalloc byte[HeaderLength + 8];
                answer[0] = ProbeAnswer;
                token.CopyTo(answer[1..]);
                data[HeaderLength..].CopyTo(answer[HeaderLength..]);
                sendVia(answer, from, viaRelay);
                break;
            case ProbeAnswer when data.Length == HeaderLength + 8:
                var sent = BinaryPrimitives.ReadInt64BigEndian(data[HeaderLength..]);
                var elapsed = Stopwatch.GetElapsedTime(sent);
                if (elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromSeconds(30))
                    RoundTrip = elapsed;
                choose(from, viaRelay);
                break;
            case Payload:
                Received?.Invoke(data[HeaderLength..].ToArray());
                break;
        }
    }

    // An answered probe proves the route. One that needs no relay beats one that does; otherwise the first stays.
    private void choose(IPEndPoint target, bool viaRelay)
    {
        bool relayed;
        lock (_lock)
        {
            relayed = viaRelay || _remote.Any(candidate => candidate.Kind == PeerCandidateKind.Relay && candidate.EndPoint.Equals(target));
            if (_route is { Relayed: false } || _route is not null && relayed)
                return;
            _route = (target, viaRelay, relayed);
        }
        setState(relayed ? PeerLinkState.Relayed : PeerLinkState.Direct);
    }

    private async Task tickAsync()
    {
        using var timer = new PeriodicTimer(ProbeInterval);
        long lastSlowProbe = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                switch (State)
                {
                    case PeerLinkState.Connecting:
                        if (Stopwatch.GetElapsedTime(_connectStarted) > ConnectTimeout)
                        {
                            setState(PeerLinkState.Failed);
                            return;
                        }
                        await refreshRelayAsync().ConfigureAwait(false);
                        probeAll();
                        break;
                    case PeerLinkState.Direct or PeerLinkState.Relayed:
                        if (Stopwatch.GetElapsedTime(Volatile.Read(ref _lastHeard)) > PeerTimeout)
                        {
                            _route = null;
                            setState(PeerLinkState.Disconnected);
                            return;
                        }
                        if (Stopwatch.GetElapsedTime(lastSlowProbe) >= KeepAliveInterval)
                        {
                            lastSlowProbe = Stopwatch.GetTimestamp();
                            await refreshRelayAsync().ConfigureAwait(false);
                            // While relayed, keep looking for a route without the relay: NATs can open late.
                            if (State == PeerLinkState.Relayed)
                                probeAll();
                            else if (_route is { } route)
                                sendProbe(route.Target, route.ViaRelay);
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
    }

    private void probeAll()
    {
        PeerCandidate[] remote;
        lock (_lock)
            remote = _remote;
        foreach (var candidate in remote)
        {
            sendProbe(candidate.EndPoint, viaRelay: false);
            if (_relayServer is not null && candidate.Kind != PeerCandidateKind.Host)
                sendProbe(candidate.EndPoint, viaRelay: true);
        }
    }

    private void sendProbe(IPEndPoint target, bool viaRelay)
    {
        if (_remoteToken is not { } token)
            return;
        Span<byte> packet = stackalloc byte[HeaderLength + 8];
        packet[0] = Probe;
        token.CopyTo(packet[1..]);
        BinaryPrimitives.WriteInt64BigEndian(packet[HeaderLength..], Stopwatch.GetTimestamp());
        sendVia(packet, target, viaRelay);
    }

    private void sendVia(ReadOnlySpan<byte> packet, IPEndPoint target, bool viaRelay)
    {
        try
        {
            if (!viaRelay)
            {
                _socket.SendTo(packet, SocketFlags.None, target);
                return;
            }
            if (_relayServer is { } relay)
                _socket.SendTo(new StunMessage(StunMessage.SendIndication).WithAddress(StunMessage.XorPeerAddress, target)
                    .With(StunMessage.Data, packet.ToArray()).Encode(), SocketFlags.None, relay);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            // An unreachable address is normal while probing; a dead link shows up as silence.
        }
    }

    // The relay only passes traffic from addresses it has permission for, and forgets both those and the allocation.
    private async Task refreshRelayAsync()
    {
        if (_relayServer is not { } server)
            return;
        try
        {
            if (Stopwatch.GetElapsedTime(_permissionsSent) >= PermissionLifetime)
            {
                _permissionsSent = Stopwatch.GetTimestamp();
                PeerCandidate[] remote;
                lock (_lock)
                    remote = _remote;
                foreach (var address in remote.Where(static candidate => candidate.Kind != PeerCandidateKind.Host).Select(static candidate => candidate.EndPoint.Address).Distinct())
                    await signedAsync(new StunMessage(StunMessage.CreatePermissionRequest).WithAddress(StunMessage.XorPeerAddress, new IPEndPoint(address, 1)), server).ConfigureAwait(false);
            }
            if (Stopwatch.GetElapsedTime(_relayRefreshed) >= _relayLifetime / 2)
            {
                _relayRefreshed = Stopwatch.GetTimestamp();
                await signedAsync(new StunMessage(StunMessage.RefreshRequest).With(StunMessage.Lifetime, [0, 0, 2, 88]), server).ConfigureAwait(false);
            }
        }
        catch (TimeoutException exception)
        {
            Console.Error.WriteLine($"Warning PEERLINK: the relay didn't answer ({exception.Message}).");
        }
        catch (SocketException exception)
        {
            Console.Error.WriteLine($"Warning PEERLINK: the relay didn't answer ({exception.Message}).");
        }
    }

    // A signed relay request; a stale nonce (438) gets a fresh one and a second try.
    private async Task signedAsync(StunMessage request, IPEndPoint server)
    {
        var response = await transactAsync(sign(request), _relayKey, server, _stop.Token).ConfigureAwait(false);
        if (response.Error == 438 && response.Text(StunMessage.Nonce) is { } nonce)
        {
            _relayNonce = nonce;
            var again = new StunMessage(request.Type);
            foreach (var attribute in request.Attributes.Where(static attribute => attribute.Type is not (StunMessage.Username or StunMessage.Realm or StunMessage.Nonce)))
                again.Attributes.Add(attribute);
            response = await transactAsync(sign(again), _relayKey, server, _stop.Token).ConfigureAwait(false);
        }
        // Refused permissions are normal: the relay won't let us reach private addresses.
        if (response.Error is { } error and not 403)
            Console.Error.WriteLine($"Warning PEERLINK: relay request {request.Type:X4} refused ({error}).");
    }

    private StunMessage sign(StunMessage request) => request.With(StunMessage.Username, _relayUsername!).With(StunMessage.Realm, _relayRealm!).With(StunMessage.Nonce, _relayNonce!);
}
