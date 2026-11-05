using System.Net;
using System.Net.Sockets;

namespace Waddamburo.Game.Online;

/// <summary>
/// How the game reaches TaikOnline (its API, Reverb, the relay): each of the server's addresses in turn, IPv6 and IPv4
/// alternating, each given <see cref="AttemptTimeout"/> to connect. .NET's own connection tries the addresses one
/// after another with no limit of its own, so a network whose IPv6 is offered but goes nowhere hung every request on the
/// dead address until the 15 s request timeout (a player whose rooms, notices and boot check all timed out).
/// </summary>
public static class ServerConnector
{
    /// <summary>How long one address gets to connect before the next is tried.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A handler connecting this way. <paramref name="insecure"/> skips certificate checks (a local server's
    /// self-signed certificate); anyone on the network can then read the traffic.
    /// </summary>
    public static SocketsHttpHandler Handler(bool insecure)
    {
        var handler = new SocketsHttpHandler { ConnectCallback = ConnectAsync, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
#pragma warning disable CA5359
        if (insecure)
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
        return handler;
    }

    /// <summary>For a websocket's <c>ConnectAsync(uri, invoker, token)</c>: the same connection, certificates as above.</summary>
    public static HttpMessageInvoker Invoker(bool insecure) => new(Handler(insecure), disposeHandler: true);

    /// <summary>Opens a connection to the context's host and port, trying its addresses as described above.</summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (host, port) = (context.DnsEndPoint.Host, context.DnsEndPoint.Port);
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : Order(await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false));
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(AttemptTimeout);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), attempt.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException
                && !cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                last = exception;
            }
        }
        throw new HttpRequestException($"Could not reach {host}:{port} on any of its {addresses.Length} address(es).", last);
    }

    /// <summary>IPv6 first (as the system orders them), then alternating with IPv4, so neither family can hold the other up long.</summary>
    public static IPAddress[] Order(IPAddress[] addresses)
    {
        var six = addresses.Where(static address => address.AddressFamily == AddressFamily.InterNetworkV6).ToArray();
        var four = addresses.Where(static address => address.AddressFamily == AddressFamily.InterNetwork).ToArray();
        var ordered = new List<IPAddress>(addresses.Length);
        for (var index = 0; index < Math.Max(six.Length, four.Length); index++)
        {
            if (index < six.Length) ordered.Add(six[index]);
            if (index < four.Length) ordered.Add(four[index]);
        }
        return [.. ordered];
    }
}
