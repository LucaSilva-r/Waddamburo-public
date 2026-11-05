using System.Net;
using System.Net.Sockets;
using System.Text;
using Waddamburo.Game.Online;
using Waddamburo.Game.Scores;

namespace Waddamburo.Game.Tests;

public sealed class ServerConnectorTests
{
    [Fact]
    public void AddressesAlternateFamiliesStartingWithIPv6()
    {
        var ordered = ServerConnector.Order([IPAddress.Parse("::1"), IPAddress.Parse("::2"), IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.2")]);

        Assert.Equal(["::1", "10.0.0.1", "::2", "10.0.0.2"], ordered.Select(static address => address.ToString()));
    }

    [Fact]
    public async Task ARequestReachesAServerOnTheOtherFamily()
    {
        // Listening on IPv4 only: "localhost" also resolves to ::1, which refuses, and the next address answers.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
        });
        using var http = ScoreClient.CreateHttp(new Uri($"http://localhost:{port}/"), insecure: false);

        Assert.Equal("ok", await http.GetStringAsync("up"));
        await serving;
    }
}
