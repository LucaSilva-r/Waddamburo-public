using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Waddamburo.Game.Online;

namespace Waddamburo.Game.Tests;

public sealed class RealtimeConnectionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SubscribesAnswersPingsAndDeliversEvents()
    {
        await using var server = new PusherTestServer();
        var received = new TaskCompletionSource<(string Channel, string Event, string Message)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = server.Connect(static options => options);
        connection.Received += (channel, name, data) =>
            received.TrySetResult((channel, name, data.GetProperty("message").GetString()!));
        connection.Subscribe("wdb.notices");
        connection.Subscribe("private-App.Models.User.7");
        connection.Start();

        var socket = await server.AcceptAsync();
        Assert.Contains("protocol=7", server.LastQuery, StringComparison.Ordinal);
        await PusherTestServer.EstablishAsync(socket);
        string[] subscribed = [await PusherTestServer.ReceiveAsync(socket), await PusherTestServer.ReceiveAsync(socket)];
        Assert.Contains(subscribed, message => message.Contains("\"channel\":\"wdb.notices\"", StringComparison.Ordinal)
            && !message.Contains("auth", StringComparison.Ordinal));
        // A private channel is signed for this socket.
        Assert.Contains(subscribed, message => message.Contains("\"auth\":\"key:1.2:private-App.Models.User.7\"", StringComparison.Ordinal));
        await PusherTestServer.SendAsync(socket, """{"event":"pusher:ping","data":{}}""");
        Assert.Contains("pusher:pong", await PusherTestServer.ReceiveAsync(socket), StringComparison.Ordinal);
        await PusherTestServer.SendAsync(socket, """{"event":"notice","channel":"wdb.notices","data":"{\"id\":\"3\",\"message\":\"Maintenance\",\"severity\":\"warning\"}"}""");

        Assert.Equal(("wdb.notices", "notice", "Maintenance"), await received.Task.WaitAsync(Wait));
    }

    [Fact]
    public async Task ReconnectsAfterADroppedLineAndSubscribesAgain()
    {
        await using var server = new PusherTestServer();
        var states = new List<bool>();
        await using var connection = server.Connect(static options => options);
        connection.ConnectionChanged += state => { lock (states) states.Add(state); };
        connection.Subscribe("wdb.notices");
        connection.Start();

        var first = await server.AcceptAsync();
        await PusherTestServer.EstablishAsync(first);
        await PusherTestServer.ReceiveAsync(first);
        server.Drop(first); // the line drops: no close handshake

        var second = await server.AcceptAsync();
        await PusherTestServer.EstablishAsync(second);
        Assert.Contains("\"channel\":\"wdb.notices\"", await PusherTestServer.ReceiveAsync(second), StringComparison.Ordinal);
        lock (states)
            Assert.Equal([true, false, true], states);
    }

    [Fact]
    public async Task KeepsAQuietLineWhosePingsAreAnswered()
    {
        await using var server = new PusherTestServer();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = server.Connect(static options => options with { PongTimeout = TimeSpan.FromMilliseconds(500) });
        connection.Received += (_, name, _) => received.TrySetResult(name);
        connection.Subscribe("wdb.notices");
        connection.Start();

        var socket = await server.AcceptAsync();
        await PusherTestServer.EstablishAsync(socket, activityTimeoutSeconds: 1);
        await PusherTestServer.ReceiveAsync(socket); // the subscription
        for (var i = 0; i < 2; i++) // two quiet spells, each pinged and answered
        {
            Assert.Contains("pusher:ping", await PusherTestServer.ReceiveAsync(socket), StringComparison.Ordinal);
            await PusherTestServer.SendAsync(socket, """{"event":"pusher:pong","data":{}}""");
        }
        // Still the same socket: an event sent on it arrives, and no second connection was made.
        await PusherTestServer.SendAsync(socket, """{"event":"notice","channel":"wdb.notices","data":"{}"}""");
        Assert.Equal("notice", await received.Task.WaitAsync(Wait));
        await Assert.ThrowsAsync<TimeoutException>(() => server.AcceptAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task PingsWhenQuietAndReconnectsWhenThePingGoesUnanswered()
    {
        await using var server = new PusherTestServer();
        await using var connection = server.Connect(static options => options with { PongTimeout = TimeSpan.FromMilliseconds(300) });
        connection.Start();

        var first = await server.AcceptAsync();
        await PusherTestServer.EstablishAsync(first, activityTimeoutSeconds: 1);
        Assert.Contains("pusher:ping", await PusherTestServer.ReceiveAsync(first), StringComparison.Ordinal);
        // No pong: the client gives the line up and dials again.
        await server.AcceptAsync();
    }

    [Theory]
    [InlineData(4001, false)] // the server would refuse again: stop
    [InlineData(4100, true)] // back off, then reconnect
    [InlineData(4200, true)] // reconnect at once (no backoff: the delay below would time the test out)
    public async Task CloseCodesDecideWhetherToReconnect(int code, bool reconnects)
    {
        await using var server = new PusherTestServer();
        var delay = code >= 4200 ? TimeSpan.FromMinutes(5) : TimeSpan.FromMilliseconds(50);
        await using var connection = server.Connect(options => options with { ReconnectDelay = delay });
        connection.Start();

        var socket = await server.AcceptAsync();
        await PusherTestServer.EstablishAsync(socket);
        await socket.CloseOutputAsync((WebSocketCloseStatus)code, "closing", CancellationToken.None);

        if (reconnects)
        {
            await server.AcceptAsync();
            Assert.False(connection.Stopped);
        }
        else
        {
            await Assert.ThrowsAsync<TimeoutException>(() => server.AcceptAsync(TimeSpan.FromMilliseconds(500)));
            Assert.True(connection.Stopped);
        }
    }

    [Fact]
    public async Task RefusedChannelsAreReportedWithoutDroppingTheConnection()
    {
        await using var server = new PusherTestServer();
        var failed = Channel.CreateUnbounded<string>();
        await using var connection = server.Connect(static options => options with { Authorize = static (_, _, _) => Task.FromResult<ChannelAuthorization?>(null) });
        connection.SubscriptionFailed += channel => failed.Writer.TryWrite(channel);
        connection.Subscribe("private-App.Models.User.7");
        connection.Subscribe("wdb.notices");
        connection.Start();

        var socket = await server.AcceptAsync();
        await PusherTestServer.EstablishAsync(socket);
        // The refused private channel is never sent; the public one is.
        Assert.Contains("\"channel\":\"wdb.notices\"", await PusherTestServer.ReceiveAsync(socket), StringComparison.Ordinal);
        Assert.Equal("private-App.Models.User.7", await failed.Reader.ReadAsync().AsTask().WaitAsync(Wait));
        // Reverb's answer to a bad signature: an error on a connection that stays open.
        await PusherTestServer.SendAsync(socket, """{"event":"pusher:error","data":"{\"code\":4009,\"message\":\"Connection is unauthorized\"}"}""");
        await PusherTestServer.SendAsync(socket, """{"event":"pusher:subscription_error","channel":"private-x","data":{"status":403}}""");
        Assert.Equal("private-x", await failed.Reader.ReadAsync().AsTask().WaitAsync(Wait));
        Assert.True(connection.Connected);
        await Assert.ThrowsAsync<TimeoutException>(() => server.AcceptAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task JoinsPresenceChannelsWithSignedChannelDataAndTracksMembers()
    {
        await using var server = new PusherTestServer();
        const string channel = "presence-wdb.online";
        const string channelData = """{"user_id":7,"user_info":{"name":"Don"}}""";
        var events = Channel.CreateUnbounded<string>();
        await using var connection = server.Connect(static options => options with
        {
            Authorize = static (socketId, name, _) => Task.FromResult<ChannelAuthorization?>(new($"key:{socketId}:{name}", channelData)),
        });
        connection.PresenceSynced += (name, members) => events.Writer.TryWrite(
            $"{name} synced {string.Join(',', members.Select(static member => $"{member.Id}={member.Info.GetProperty("name").GetString()}"))}");
        connection.MemberAdded += (name, member) => events.Writer.TryWrite($"{name} added {member.Id}={member.Info.GetProperty("name").GetString()}");
        connection.MemberRemoved += (name, id) => events.Writer.TryWrite($"{name} removed {id}");
        connection.Subscribe(channel);
        connection.Start();

        var socket = await server.AcceptAsync();
        await PusherTestServer.EstablishAsync(socket);
        using (var subscribe = JsonDocument.Parse(await PusherTestServer.ReceiveAsync(socket)))
        {
            var data = subscribe.RootElement.GetProperty("data");
            Assert.Equal($"key:1.2:{channel}", data.GetProperty("auth").GetString());
            // Byte for byte: the signature covers this string.
            Assert.Equal(channelData, data.GetProperty("channel_data").GetString());
        }

        await PusherTestServer.SendAsync(socket, """{"event":"pusher_internal:subscription_succeeded","channel":"presence-wdb.online","data":"{\"presence\":{\"count\":2,\"ids\":[7,8],\"hash\":{\"7\":{\"name\":\"Don\"},\"8\":{\"name\":\"Katsu\"}}}}"}""");
        Assert.Equal($"{channel} synced 7=Don,8=Katsu", await events.Reader.ReadAsync().AsTask().WaitAsync(Wait));
        await PusherTestServer.SendAsync(socket, """{"event":"pusher_internal:member_added","channel":"presence-wdb.online","data":"{\"user_id\":9,\"user_info\":{\"name\":\"Wada\"}}"}""");
        Assert.Equal($"{channel} added 9=Wada", await events.Reader.ReadAsync().AsTask().WaitAsync(Wait));
        await PusherTestServer.SendAsync(socket, """{"event":"pusher_internal:member_removed","channel":"presence-wdb.online","data":"{\"user_id\":8}"}""");
        Assert.Equal($"{channel} removed 8", await events.Reader.ReadAsync().AsTask().WaitAsync(Wait));
    }

    [Fact]
    public async Task StatusIsSentWhenItChangesAndAtEachHeartbeatOneRequestAtATime()
    {
        var sent = new List<PlayerStatus>();
        var now = TimeSpan.Zero;
        var hold = new TaskCompletionSource();
        var reporter = new StatusReporter(status =>
        {
            sent.Add(status);
            return hold.Task;
        }, () => now);

        reporter.Report(PlayerStatus.Online);
        reporter.Report(PlayerStatus.Afk); // the first is still running: wait
        Assert.Equal([PlayerStatus.Online], sent);
        hold.SetResult();
        await Task.Yield();

        reporter.Report(PlayerStatus.Online); // unchanged: nothing
        Assert.Single(sent);
        reporter.Report(PlayerStatus.Playing("abc")); // changed: now
        Assert.Equal(PlayerStatus.Playing("abc"), sent[^1]);
        now = StatusReporter.Heartbeat;
        reporter.Report(PlayerStatus.Playing("abc")); // the server would forget it otherwise
        Assert.Equal(3, sent.Count);
    }

    [Fact]
    public async Task StatusReachesTheServerWithItsChart()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://taikonline.test/") };

        await new NoticeClient(http).SetStatusAsync(PlayerStatus.Playing("abc"));

        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal("/api/wdb/status", handler.Path);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("playing", body.RootElement.GetProperty("state").GetString());
        Assert.Equal("abc", body.RootElement.GetProperty("chart_sha256").GetString());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public string? Path { get; private set; }

        public string? Body { get; private set; }

        public Func<HttpRequestMessage, (HttpStatusCode Status, string Json)>? Respond { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (Respond is null)
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            var (status, json) = Respond(request);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task AFailedStatusIsTriedAgainAtTheNextHeartbeat()
    {
        var attempts = 0;
        var now = TimeSpan.Zero;
        var reporter = new StatusReporter(_ =>
        {
            attempts++;
            return Task.FromException(new HttpRequestException("down"));
        }, () => now);

        reporter.Report(PlayerStatus.Online);
        await Task.Yield();
        reporter.Report(PlayerStatus.Online);
        Assert.Equal(1, attempts);
        now = StatusReporter.Heartbeat;
        reporter.Report(PlayerStatus.Online);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task WhispersToAChannelAndHearsOtherMembersOnly()
    {
        await using var server = new PusherTestServer();
        var whispers = Channel.CreateUnbounded<string>();
        var received = Channel.CreateUnbounded<string>();
        await using var connection = server.Connect(static options => options);
        connection.Whispered += (channel, name, sender, data) => whispers.Writer.TryWrite($"{channel} {name} {sender} {data.GetProperty("sdp").GetString()}");
        connection.Received += (_, name, _) => received.Writer.TryWrite(name);
        connection.Subscribe("presence-wdb.room.ABC234");
        connection.Start();

        var socket = await server.AcceptAsync();
        await PusherTestServer.EstablishAsync(socket);
        await PusherTestServer.ReceiveAsync(socket); // the subscription

        await connection.WhisperAsync("presence-wdb.room.ABC234", "client-candidate", static writer => writer.WriteString("sdp", "a=candidate"));
        using (var sent = JsonDocument.Parse(await PusherTestServer.ReceiveAsync(socket)))
        {
            Assert.Equal("client-candidate", sent.RootElement.GetProperty("event").GetString());
            Assert.Equal("presence-wdb.room.ABC234", sent.RootElement.GetProperty("channel").GetString());
            Assert.Equal("a=candidate", sent.RootElement.GetProperty("data").GetProperty("sdp").GetString());
        }
        await Assert.ThrowsAsync<ArgumentException>(() => connection.WhisperAsync("presence-wdb.room.ABC234", "notice", static _ => { }));

        await PusherTestServer.SendAsync(socket, """{"event":"client-candidate","channel":"presence-wdb.room.ABC234","user_id":"8","data":{"sdp":"theirs"}}""");
        Assert.Equal("presence-wdb.room.ABC234 client-candidate 8 theirs", await whispers.Reader.ReadAsync().AsTask().WaitAsync(Wait));
        await PusherTestServer.SendAsync(socket, """{"event":"notice","channel":"presence-wdb.room.ABC234","data":"{}"}""");
        Assert.Equal("notice", await received.Reader.ReadAsync().AsTask().WaitAsync(Wait));
    }

    [Fact]
    public async Task RoomsAreOpenedJoinedAndGiveTheirIceServers()
    {
        var handler = new CapturingHandler
        {
            Respond = request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
            {
                ("POST", "/api/wdb/rooms") => (HttpStatusCode.Created, """{"code":"ABC234","channel":"presence-wdb.room.ABC234"}"""),
                ("POST", "/api/wdb/rooms/quick-play") => (HttpStatusCode.OK, """{"code":"QUICK2","channel":"presence-wdb.room.QUICK2"}"""),
                ("POST", "/api/wdb/rooms/QUICK2/keep-alive") => (HttpStatusCode.NoContent, "{}"),
                ("POST", "/api/wdb/rooms/GONE22/keep-alive") => (HttpStatusCode.NotFound, "{}"),
                ("POST", "/api/wdb/rooms/LOCKED/join") => (HttpStatusCode.Forbidden, """{"message":"Wrong password.","wrong_password":true}"""),
                ("GET", "/api/wdb/rooms") => (HttpStatusCode.OK, """{"rooms":[{"code":"ABC234","name":"Oni only","host":"Don","has_password":true}]}"""),
                ("PATCH", "/api/wdb/rooms/ABC234") => (HttpStatusCode.OK, """{"code":"ABC234","channel":"c","name":"New","is_public":false,"has_password":true}"""),
                ("POST", "/api/wdb/rooms/ABC234/join") => (HttpStatusCode.OK, """{"code":"ABC234","channel":"presence-wdb.room.ABC234"}"""),
                ("POST", "/api/wdb/rooms/GONE22/join") => (HttpStatusCode.NotFound, "{}"),
                ("GET", "/api/wdb/rooms/ABC234/ice-servers") => (HttpStatusCode.OK, """{"ice_servers":[{"urls":["stun:stun.cloudflare.com:3478"]}],"relay":{"url":"wss://relay.taikonline.test","ticket":"a b"}}"""),
                _ => (HttpStatusCode.InternalServerError, "{}"),
            },
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://taikonline.test/") };
        var rooms = new RoomClient(http);

        Assert.Equal("presence-wdb.room.ABC234", (await rooms.CreateAsync()).Channel);
        Assert.Equal("presence-wdb.room.ABC234", (await rooms.JoinAsync("ABC234")).Room?.Channel);
        Assert.Null((await rooms.JoinAsync("GONE22")).Room);
        Assert.Equal("QUICK2", (await rooms.QuickPlayAsync()).Code);
        Assert.True(await rooms.KeepAliveAsync("QUICK2"));
        Assert.False(await rooms.KeepAliveAsync("GONE22"));
        Assert.True((await rooms.JoinAsync("LOCKED", "wrong")).WrongPassword);
        Assert.Equal(new ListedRoom("ABC234", "Oni only", "Don", true), Assert.Single(await rooms.ListAsync()));
        var updated = await rooms.UpdateAsync("ABC234", password: "");
        Assert.Equal(("New", false, true), (updated.Name, updated.IsPublic, updated.HasPassword));
        // What wasn't changed isn't sent: null would clear it.
        Assert.Equal("""{"password":""}""", handler.Body);
        var (servers, relay) = await rooms.IceServersAsync("ABC234");
        Assert.Equal("stun:stun.cloudflare.com:3478", Assert.Single(Assert.Single(servers).Urls));
        Assert.Equal("wss://relay.taikonline.test/?ticket=a%20b", relay?.Address?.AbsoluteUri);
        Assert.Null(new RelayTicket("https://elsewhere.test", "t").Address);
    }

    [Fact]
    public void NoticesAreKeptOnceAndExpire()
    {
        var board = new NoticeBoard();
        var added = 0;
        board.Added += _ => added++;
        var notice = Notice.From(new ServerNotice("3", "Maintenance", "warning"), NoticeSource.System);
        Assert.True(board.Add(notice));
        Assert.False(board.Add(notice)); // pushed, then in a backlog
        board.Add(Notice.From(new ServerNotice("4", "Over", "info") { EndsAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, NoticeSource.System));
        Assert.Equal(2, added);
        Assert.Equal(NoticeSeverity.Warning, Assert.Single(board.Current).Severity);
    }

    /// <summary>A scripted Pusher server: each test drives the accepted sockets by hand.</summary>
    private sealed class PusherTestServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Channel<WebSocket> _accepted = Channel.CreateUnbounded<WebSocket>();
        private readonly Dictionary<WebSocket, HttpListenerContext> _sockets = [];
        private readonly Task _accept;
        private readonly int _port;

        public PusherTestServer()
        {
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
            _accept = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        LastQuery = context.Request.Url!.Query;
                        var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                        lock (_sockets)
                            _sockets[socket] = context;
                        _accepted.Writer.TryWrite(socket);
                    }
                    catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }
                }
            });
        }

        public string LastQuery { get; private set; } = "";

        public RealtimeConnection Connect(Func<Options, Options> configure)
        {
            var options = configure(new Options());
            return new RealtimeConnection(new Uri($"ws://127.0.0.1:{_port}/app/key"))
            {
                Authorize = options.Authorize,
                ReconnectDelay = options.ReconnectDelay,
                PongTimeout = options.PongTimeout,
            };
        }

        /// <summary>Cuts the TCP connection under a socket (WebSocket.Abort alone leaves it open here).</summary>
        public void Drop(WebSocket socket)
        {
            lock (_sockets)
                _sockets[socket].Response.Abort();
        }

        public async Task<WebSocket> AcceptAsync(TimeSpan? timeout = null) =>
            await _accepted.Reader.ReadAsync().AsTask().WaitAsync(timeout ?? Wait);

        public static Task EstablishAsync(WebSocket socket, int activityTimeoutSeconds = 30) => SendAsync(socket,
            $$"""{"event":"pusher:connection_established","data":"{\"socket_id\":\"1.2\",\"activity_timeout\":{{activityTimeoutSeconds}}}"}""");

        public static Task SendAsync(WebSocket socket, string text) =>
            socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

        public static async Task<string> ReceiveAsync(WebSocket socket)
        {
            var buffer = new byte[4096];
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None).WaitAsync(Wait);
            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            using var _ = JsonDocument.Parse(text); // every client message is JSON
            return text;
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            lock (_sockets)
                foreach (var context in _sockets.Values)
                    context.Response.Abort();
            await _accept.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        public sealed record Options
        {
            public Func<string, string, CancellationToken, Task<ChannelAuthorization?>>? Authorize { get; init; } =
                static (socketId, channel, _) => Task.FromResult<ChannelAuthorization?>(new($"key:{socketId}:{channel}"));

            public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromMilliseconds(50);

            public TimeSpan PongTimeout { get; init; } = TimeSpan.FromSeconds(30);
        }
    }
}
