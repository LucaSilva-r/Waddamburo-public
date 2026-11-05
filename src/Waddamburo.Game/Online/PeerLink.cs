using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace Waddamburo.Game.Online;

/// <summary>Where a peer might be reached: its own network (Host), or its public address (Reflexive).</summary>
public enum PeerCandidateKind
{
    Host,
    Reflexive,
}

public readonly record struct PeerCandidate(PeerCandidateKind Kind, IPEndPoint EndPoint)
{
    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()} {EndPoint}";

    /// <summary>A candidate as <see cref="ToString"/> writes it; null when it isn't one (they come from the other player).</summary>
    public static PeerCandidate? Parse(string text)
    {
        var parts = text.Split(' ');
        // Probes go wherever this says, so nothing that points back at this machine or everyone on a network.
        return parts.Length == 2 && !char.IsDigit(parts[0][0]) && Enum.TryParse<PeerCandidateKind>(parts[0], true, out var kind) && Enum.IsDefined(kind)
            && IPEndPoint.TryParse(parts[1], out var endPoint) && endPoint.Port != 0 && !IPAddress.IsLoopback(endPoint.Address) && !endPoint.Address.IsIPv6Multicast
            && !endPoint.Address.Equals(IPAddress.Any) && !endPoint.Address.Equals(IPAddress.IPv6Any)
            && !(endPoint.AddressFamily == AddressFamily.InterNetwork && endPoint.Address.GetAddressBytes()[0] is >= 224)
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
/// A link to one other Waddamburo. Both ends are ours, so it needs no full ICE: each side gathers where it can be
/// reached (<see cref="GatherAsync"/>), they swap that and a token through the room channel, then each probes every
/// address (<see cref="Connect"/>) over UDP, and through the relay when there is one (<see cref="Relay"/>: a WebSocket
/// server behind the Cloudflare tunnel that pairs the room's two players and passes their packets on). The first answer
/// opens the link; a direct route replaces the relay. Packets carry the receiver's token, so strangers' datagrams are
/// dropped. Delivery isn't guaranteed: callers repeat what must arrive. Events are raised on a background thread.
/// </summary>
/// <remarks>One dual-stack socket speaks both IPv4 and IPv6; STUN is reached over IPv4.</remarks>
public sealed class PeerLink : IAsyncDisposable
{
    private const byte Probe = 0xD1, ProbeAnswer = 0xD2, Payload = 0xD3;
    private const int TokenLength = 8;
    private const int HeaderLength = 1 + TokenLength;
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(1);
    private static readonly IPEndPoint RelayEndPoint = new(IPAddress.None, 0);

    private readonly Socket _socket;
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
    // When the route in use last answered: silent longer than RouteStale, every address and the relay are probed
    // again and the first to answer takes over (a dropped IPv6 path, a NAT mapping gone), before PeerTimeout gives up.
    private long _routeHeard;
    private static readonly TimeSpan RouteStale = TimeSpan.FromSeconds(2.5);

    private ClientWebSocket? _relay;
    private bool _relayOpen;
    private Task? _relaying;
    // What goes through the relay, in order; a stalled relay drops the oldest rather than holding up the game.
    private readonly Channel<byte[]> _relayOut = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest });

    public PeerLink()
    {
        if (Socket.OSSupportsIPv6)
        {
            try
            {
                // Non-blocking: a full send buffer (a song going out) drops the packet, to be sent again, rather than
                // holding up the game's frame.
                _socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true, Blocking = false };
                _socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
                return;
            }
            catch (SocketException)
            {
                // IPv6 turned off on this machine.
                _socket?.Dispose();
            }
        }
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { Blocking = false };
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
    }

    /// <summary>Give up connecting after this long.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>A connected peer counts as gone after this long without a packet.</summary>
    public TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public PeerLinkState State { get; private set; }

    /// <summary>The latest measured round trip, once connected.</summary>
    public TimeSpan? RoundTrip { get; private set; }

    /// <summary>Where the peer is being reached, once connected.</summary>
    public IPEndPoint? RemoteEndPoint => _route?.Target;

    /// <summary>The token the other player must be told (it proves their packets came from them).</summary>
    public ReadOnlyMemory<byte> LocalToken => _localToken;

    public int LocalPort => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    public event Action<PeerLinkState>? StateChanged;

    /// <summary>A payload from the peer. Untrusted: they could send anything.</summary>
    public event Action<byte[]>? Received;

    /// <summary>The relay to try alongside the direct addresses (its address carries this player's ticket); set before <see cref="Connect"/>.</summary>
    public Uri? Relay { get; set; }

    /// <summary>Everywhere this link might be reached, asking <paramref name="servers"/> (STUN) for the public address.</summary>
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
        if (Relay is { } relay)
            _relaying ??= Task.Run(() => relayAsync(relay), CancellationToken.None);
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
        _relayOut.Writer.TryComplete();
        _relay?.Abort();
        foreach (var task in new[] { _receiving, _ticking, _relaying }.OfType<Task>())
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
            .Where(static address => !IPAddress.IsLoopback(address) && (address.AddressFamily == AddressFamily.InterNetwork
                || address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast && !address.IsIPv4MappedToIPv6));
    }

    // "stun:host:port": only UDP is spoken here.
    private static async Task<(string Scheme, IPEndPoint EndPoint)?> resolveAsync(string url, CancellationToken cancellationToken)
    {
        var scheme = url.Split(':', 2)[0];
        if (scheme != "stun")
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
        EndPoint any = new IPEndPoint(_socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
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
            // A dual-stack socket reports IPv4 senders as ::ffff:a.b.c.d.
            var from = (IPEndPoint)result.RemoteEndPoint;
            if (from.Address.IsIPv4MappedToIPv6)
                from = new IPEndPoint(from.Address.MapToIPv4(), from.Port);
            var data = buffer.AsSpan(0, result.ReceivedBytes);
            if (data.Length >= HeaderLength && data[0] is Probe or ProbeAnswer or Payload)
                handlePacket(data, from, viaRelay: false);
            else if (StunMessage.Parse(data) is { } message)
                handleStun(message, from);
        }
    }

    private void handleStun(StunMessage message, IPEndPoint from)
    {
        if (_pending.TryRemove(Convert.ToHexString(message.TransactionId), out var answer))
            answer.TrySetResult(message);
    }

    private void handlePacket(ReadOnlySpan<byte> data, IPEndPoint from, bool viaRelay)
    {
        if (!data.Slice(1, TokenLength).SequenceEqual(_localToken) || _remoteToken is not { } token)
            return;
        Volatile.Write(ref _lastHeard, Stopwatch.GetTimestamp());
        if (_route is { } current && current.ViaRelay == viaRelay && (viaRelay || current.Target.Equals(from)))
            Volatile.Write(ref _routeHeard, Stopwatch.GetTimestamp());
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

    // An answered probe proves the route. One that needs no relay beats one that does; otherwise the first stays,
    // unless it has gone silent: then whichever answers first takes over.
    private void choose(IPEndPoint target, bool viaRelay)
    {
        bool relayed;
        lock (_lock)
        {
            relayed = viaRelay;
            if (!routeStale() && (_route is { Relayed: false } || _route is not null && relayed))
                return;
            if (_route is { } old && old.ViaRelay == viaRelay && (viaRelay || old.Target.Equals(target)))
                return;
            _route = (target, viaRelay, relayed);
            Volatile.Write(ref _routeHeard, Stopwatch.GetTimestamp());
        }
        // The player's own log; no addresses (logs get sent around).
        Console.WriteLine($"Online: link {(relayed ? "relayed" : target.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6 direct" : "IPv4 direct")}.");
        setState(relayed ? PeerLinkState.Relayed : PeerLinkState.Direct);
    }

    private bool routeStale() => _route is not null && Stopwatch.GetElapsedTime(Volatile.Read(ref _routeHeard)) > RouteStale;

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
                        probeAll();
                        break;
                    case PeerLinkState.Direct or PeerLinkState.Relayed:
                        if (Stopwatch.GetElapsedTime(Volatile.Read(ref _lastHeard)) > PeerTimeout)
                        {
                            _route = null;
                            setState(PeerLinkState.Disconnected);
                            return;
                        }
                        // The route in use went quiet: every address and the relay again, as while connecting.
                        if (routeStale())
                        {
                            probeAll();
                            break;
                        }
                        if (Stopwatch.GetElapsedTime(lastSlowProbe) >= KeepAliveInterval)
                        {
                            lastSlowProbe = Stopwatch.GetTimestamp();
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
            sendProbe(candidate.EndPoint, viaRelay: false);
        if (Volatile.Read(ref _relayOpen))
            sendProbe(RelayEndPoint, viaRelay: true);
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
            if (Volatile.Read(ref _relayOpen))
                _relayOut.Writer.TryWrite(packet.ToArray());
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            // An unreachable address is normal while probing; a dead link shows up as silence.
        }
    }

    // The relay: connects with this player's ticket, then sends what's queued and hands on what arrives, until the link
    // goes. A relay that can't be reached just leaves the direct addresses.
    private async Task relayAsync(Uri address)
    {
        using var relay = new ClientWebSocket();
        // Kept for the socket's life: it carries the connection (each relay address tried in turn, see ServerConnector).
        using var invoker = ServerConnector.Invoker(insecure: false);
        relay.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _relay = relay;
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                timeout.CancelAfter(ConnectTimeout);
                await relay.ConnectAsync(address, invoker, timeout.Token).ConfigureAwait(false);
            }
            Volatile.Write(ref _relayOpen, true);
            var sending = Task.Run(async () =>
            {
                await foreach (var packet in _relayOut.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                    await relay.SendAsync(packet, WebSocketMessageType.Binary, true, _stop.Token).ConfigureAwait(false);
            });
            var buffer = new byte[4096];
            while (!_stop.IsCancellationRequested)
            {
                var length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await relay.ReceiveAsync(buffer.AsMemory(length), _stop.Token).ConfigureAwait(false);
                    length += result.Count;
                }
                while (!result.EndOfMessage && length < buffer.Length);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (result.MessageType == WebSocketMessageType.Binary && result.EndOfMessage && length >= HeaderLength
                    && buffer[0] is Probe or ProbeAnswer or Payload)
                    handlePacket(buffer.AsSpan(0, length), RelayEndPoint, viaRelay: true);
            }
            await sending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            if (!_stop.IsCancellationRequested)
                Console.Error.WriteLine($"Warning PEERLINK: the relay at {address.Host} isn't usable ({exception.Message}).");
        }
        finally
        {
            Volatile.Write(ref _relayOpen, false);
        }
    }
}
