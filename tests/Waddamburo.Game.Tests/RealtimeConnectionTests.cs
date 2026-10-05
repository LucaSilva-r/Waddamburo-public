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
        await using var connection = server.Connect(static options => options with { Authorize = static (_, _, _) => Task.FromResult<string?>(null) });
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
            public Func<string, string, CancellationToken, Task<string?>>? Authorize { get; init; } =
                static (socketId, channel, _) => Task.FromResult<string?>($"key:{socketId}:{channel}");

            public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromMilliseconds(50);

            public TimeSpan PongTimeout { get; init; } = TimeSpan.FromSeconds(30);
        }
    }
}
