using System.Net.Sockets;

namespace Waddamburo.Services.Online;

/// <summary>
/// HTTPS and secure websockets on the Nintendo Switch: .NET's TLS needs OpenSSL, which the Switch build does not have,
/// so TLS goes through the console's own service (platforms/switch/shim/tls.c: its trusted CAs, host name check). The
/// request is sent as plain HTTP on the same port (<see cref="Handler"/>), and its connection wraps the connected socket
/// in the system's TLS (<see cref="Wrap"/>, set by the app on the Switch: Waddamburo.Platform.Sdl's SwitchSystemTls).
/// Elsewhere none of this runs.
/// </summary>
public static class SwitchTls
{
    /// <summary>The console's TLS around a connected socket (host, insecure = no certificate checks); null: .NET's own.</summary>
    public static Func<Socket, string, bool, Stream>? Wrap { get; set; }

    /// <summary>
    /// The Switch build (switch.targets' switch): its handlers rewrite https whenever they were made (some are made before
    /// the app sets <see cref="Wrap"/>, which only the first connection needs).
    /// </summary>
    public static bool Enabled { get; } = AppContext.TryGetSwitch("Waddamburo.Switch", out var on) && on;

    private static readonly HttpRequestOptionsKey<bool> TlsWanted = new("waddamburo.tls");

    /// <summary>Whether the connection being opened is for a request that was https (or wss).</summary>
    internal static bool Wanted(SocketsHttpConnectionContext context) =>
        context.InitialRequestMessage.Options.TryGetValue(TlsWanted, out var wanted) && wanted;

    /// <summary>
    /// https becomes http (wss: ws) on the same port, marked for <see cref="Wrap"/>.
    /// Redirects are followed here, outside the inner handler: one to https inside it would reach for OpenSSL.
    /// </summary>
    internal sealed class Handler(SocketsHttpHandler inner) : DelegatingHandler(disable(inner))
    {
        private static SocketsHttpHandler disable(SocketsHttpHandler handler)
        {
            handler.AllowAutoRedirect = false;
            return handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            for (var redirects = 0; ; redirects++)
            {
                mark(request);
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (redirects == 5 || (int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)
                    || response.Headers.Location is not { } location || request.Content is not null)
                    return response;
                response.Dispose();
                request.RequestUri = location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location);
                request.Headers.Host = null;
            }
        }

        private static void mark(HttpRequestMessage request)
        {
            // A websocket's request comes as wss (SocketsHttpHandler takes ws and wss itself).
            if (request.RequestUri is not { Scheme: "https" or "wss" } uri)
                return;
            request.RequestUri = new UriBuilder(uri) { Scheme = uri.Scheme == "wss" ? "ws" : Uri.UriSchemeHttp, Port = uri.Port }.Uri;
            // As https would send it: no ":443" after the host.
            request.Headers.Host = uri.IsDefaultPort ? uri.Host : uri.Authority;
            request.Options.Set(TlsWanted, true);
        }
    }

}
