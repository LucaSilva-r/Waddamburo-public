using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Waddamburo.Game.Online;

namespace Waddamburo.Game.Tests;

public sealed class PeerLinkTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static PeerCandidate loopback(PeerLink link) => new(PeerCandidateKind.Host, new IPEndPoint(IPAddress.Loopback, link.LocalPort));

    private static async Task until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task TwoLinksConnectAndCarryPayloadsBothWays()
    {
        await using var left = new PeerLink();
        await using var right = new PeerLink();
        var toLeft = Channel.CreateUnbounded<byte[]>();
        var toRight = Channel.CreateUnbounded<byte[]>();
        left.Received += payload => toLeft.Writer.TryWrite(payload);
        right.Received += payload => toRight.Writer.TryWrite(payload);

        left.Connect(right.LocalToken, [loopback(right)]);
        right.Connect(left.LocalToken, [loopback(left)]);
        await until(() => left.State == PeerLinkState.Direct && right.State == PeerLinkState.Direct);

        left.Send([1, 2, 3]);
        right.Send([4, 5]);
        Assert.Equal([1, 2, 3], await toRight.Reader.ReadAsync().AsTask().WaitAsync(Wait));
        Assert.Equal([4, 5], await toLeft.Reader.ReadAsync().AsTask().WaitAsync(Wait));
        Assert.NotNull(left.RoundTrip);
    }

    [Fact]
    public async Task TwoLinksConnectOverIPv6()
    {
        if (!Socket.OSSupportsIPv6)
            return;
        await using var left = new PeerLink();
        await using var right = new PeerLink();
        var toRight = Channel.CreateUnbounded<byte[]>();
        right.Received += payload => toRight.Writer.TryWrite(payload);

        left.Connect(right.LocalToken, [new PeerCandidate(PeerCandidateKind.Host, new IPEndPoint(IPAddress.IPv6Loopback, right.LocalPort))]);
        right.Connect(left.LocalToken, [new PeerCandidate(PeerCandidateKind.Host, new IPEndPoint(IPAddress.IPv6Loopback, left.LocalPort))]);
        await until(() => left.State == PeerLinkState.Direct && right.State == PeerLinkState.Direct);

        left.Send([7]);
        Assert.Equal([7], await toRight.Reader.ReadAsync().AsTask().WaitAsync(Wait));
        Assert.Equal(IPAddress.IPv6Loopback, left.RemoteEndPoint?.Address);
    }

    [Fact]
    public void AnIPv6StunAddressSurvivesTheWire()
    {
        var address = new IPEndPoint(IPAddress.Parse("2001:db8::1234:5678"), 40000);
        var wire = new StunMessage(StunMessage.BindingSuccess).WithAddress(StunMessage.XorMappedAddress, address).Encode();

        Assert.Equal(address, StunMessage.Parse(wire)?.Address(StunMessage.XorMappedAddress));
    }

    [Fact]
    public void PeerOffersFromTheOtherPlayerAreCheckedAndSurviveTheChannel()
    {
        PeerOffer offer = new(RandomNumberGenerator.GetBytes(8), [new PeerCandidate(PeerCandidateKind.Reflexive, IPEndPoint.Parse("203.0.113.9:40000"))]);
        using var output = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            offer.Write(writer);
            writer.WriteEndObject();
        }
        using var good = System.Text.Json.JsonDocument.Parse(output.ToArray());
        var parsed = PeerOffer.Parse(good.RootElement);
        Assert.Equal(offer.Token, parsed?.Token);
        Assert.Equal(offer.Candidates, parsed?.Candidates);

        foreach (var bad in new[] { "[]", "{}", """{"token":"AAAA","candidates":[]}""", """{"token":7,"candidates":[]}""", """{"token":"AAAAAAAAAAA=","candidates":"x"}""" })
        {
            using var document = System.Text.Json.JsonDocument.Parse(bad);
            Assert.Null(PeerOffer.Parse(document.RootElement));
        }
        using var mixed = System.Text.Json.JsonDocument.Parse("""{"token":"AAAAAAAAAAA=","candidates":[7,"nonsense","host 127.0.0.1:1","host 192.168.1.2:1234"]}""");
        Assert.Equal("host 192.168.1.2:1234", Assert.Single(PeerOffer.Parse(mixed.RootElement)!.Candidates).ToString());
    }

    [Fact]
    public async Task AWrongTokenNeverConnects()
    {
        await using var left = new PeerLink { ConnectTimeout = TimeSpan.FromMilliseconds(500) };
        await using var right = new PeerLink { ConnectTimeout = TimeSpan.FromMilliseconds(500) };

        left.Connect(RandomNumberGenerator.GetBytes(8), [loopback(right)]);
        right.Connect(left.LocalToken, [loopback(left)]);

        await until(() => left.State == PeerLinkState.Failed);
        await until(() => right.State == PeerLinkState.Failed);
    }

    [Fact]
    public async Task APeerThatGoesSilentCountsAsDisconnected()
    {
        await using var left = new PeerLink { PeerTimeout = TimeSpan.FromMilliseconds(500) };
        var right = new PeerLink();
        left.Connect(right.LocalToken, [loopback(right)]);
        right.Connect(left.LocalToken, [loopback(left)]);
        await until(() => left.State == PeerLinkState.Direct);

        await right.DisposeAsync();

        await until(() => left.State == PeerLinkState.Disconnected);
        left.Send([1]); // dropped, not thrown
    }

    [Fact]
    public async Task GarbageFromStrangersIsIgnored()
    {
        await using var left = new PeerLink();
        await using var right = new PeerLink();
        var received = 0;
        left.Received += _ => Interlocked.Increment(ref received);
        using var stranger = new UdpClient(AddressFamily.InterNetwork);
        var target = new IPEndPoint(IPAddress.Loopback, left.LocalPort);
        left.Connect(right.LocalToken, [loopback(right)]);
        right.Connect(left.LocalToken, [loopback(left)]);

        for (var size = 0; size < 64; size++)
        {
            var garbage = RandomNumberGenerator.GetBytes(size);
            await stranger.SendAsync(garbage, target);
            if (size >= 9)
            {
                garbage[0] = 0xD3; // a payload without the token
                await stranger.SendAsync(garbage, target);
            }
            if (size >= 20)
            {
                // STUN's magic, with attributes that run past the end, and a relay's data from someone not our relay.
                new byte[] { 0x00, 0x17, 0x00, (byte)(size - 20), 0x21, 0x12, 0xA4, 0x42 }.CopyTo(garbage, 0);
                await stranger.SendAsync(garbage, target);
            }
        }

        await until(() => left.State == PeerLinkState.Direct);
        Assert.Equal(0, Volatile.Read(ref received));
    }

    [Fact]
    public void StunMessagesSurviveTheWireAndBrokenOnesAreRefused()
    {
        var address = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 40000);
        var message = new StunMessage(StunMessage.SendIndication).WithAddress(StunMessage.XorPeerAddress, address).With(StunMessage.Data, [1, 2, 3]);
        var wire = message.Encode();

        var parsed = StunMessage.Parse(wire);
        Assert.NotNull(parsed);
        Assert.Equal(StunMessage.SendIndication, parsed.Type);
        Assert.Equal(address, parsed.Address(StunMessage.XorPeerAddress));
        Assert.Equal([1, 2, 3], parsed.Find(StunMessage.Data));
        Assert.Equal(0, wire.Length % 4); // attributes are padded

        Assert.Null(StunMessage.Parse(wire.AsSpan(0, wire.Length - 1)));
        wire[4] ^= 0xFF;
        Assert.Null(StunMessage.Parse(wire));
    }

    [Theory]
    [InlineData("relay 203.0.113.9:40000", false)] // relays are a WebSocket now, never an address
    [InlineData("host 192.168.1.2:1234", true)]
    [InlineData("nonsense", false)]
    [InlineData("relay 203.0.113.9", false)]
    [InlineData("7 203.0.113.9:40000", false)]
    [InlineData("host [2001:db8::1]:1234", true)]
    [InlineData("host [::1]:1234", false)]
    [InlineData("host 127.0.0.1:1234", false)]
    [InlineData("host 0.0.0.0:1234", false)]
    [InlineData("host 239.1.1.1:1234", false)]
    [InlineData("host [ff02::1]:1234", false)]
    [InlineData("host 192.168.1.2:0", false)]
    public void CandidatesFromTheOtherPlayerAreParsedCarefully(string text, bool valid)
    {
        var candidate = PeerCandidate.Parse(text);
        Assert.Equal(valid, candidate is not null);
        if (candidate is { } parsed)
            Assert.Equal(text, parsed.ToString());
    }

    [Fact]
    public async Task TwoLinksWithNoDirectRouteConnectThroughTheRelay()
    {
        // A stand-in for relay/server.js: pairs the first two connections and passes binary frames between them.
        var port = freeTcpPort();
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var sockets = new System.Net.WebSockets.WebSocket[2];
        var paired = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                var context = await listener.GetContextAsync();
                Assert.Equal("t", context.Request.QueryString["ticket"]);
                sockets[index] = (await context.AcceptWebSocketAsync(null)).WebSocket;
            }
            paired.SetResult();
            await Task.WhenAll(forward(sockets[0], sockets[1]), forward(sockets[1], sockets[0]));
        });

        await using var left = new PeerLink { Relay = new Uri($"ws://127.0.0.1:{port}/?ticket=t") };
        await using var right = new PeerLink { Relay = new Uri($"ws://127.0.0.1:{port}/?ticket=t") };
        var toRight = Channel.CreateUnbounded<byte[]>();
        right.Received += payload => toRight.Writer.TryWrite(payload);

        // No addresses at all, as for two players whose networks block everything else.
        left.Connect(right.LocalToken, []);
        right.Connect(left.LocalToken, []);
        await paired.Task.WaitAsync(Wait);
        await until(() => left.State == PeerLinkState.Relayed && right.State == PeerLinkState.Relayed);

        left.Send([9, 9]);
        Assert.Equal([9, 9], await toRight.Reader.ReadAsync().AsTask().WaitAsync(Wait));
        Assert.NotNull(left.RoundTrip);

        static async Task forward(System.Net.WebSockets.WebSocket from, System.Net.WebSockets.WebSocket to)
        {
            var buffer = new byte[4096];
            while (from.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var result = await from.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    return;
                await to.SendAsync(buffer.AsMemory(0, result.Count), System.Net.WebSockets.WebSocketMessageType.Binary, result.EndOfMessage, CancellationToken.None);
            }
        }

        static int freeTcpPort()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
    }
}
