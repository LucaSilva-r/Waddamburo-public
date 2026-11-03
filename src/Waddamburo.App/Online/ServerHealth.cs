using System.Diagnostics;
using System.Net;

namespace Waddamburo.App.Online;

/// <summary>What the last check found (the service menu's NETWORK page shows it).</summary>
internal enum LinkState
{
    /// <summary>No answer yet.</summary>
    Checking,
    Online,
    /// <summary>The server's name could not be looked up: most likely no internet (or no DNS).</summary>
    NoInternet,
    /// <summary>The server could not be reached (refused, unreachable, TLS failed).</summary>
    Unreachable,
    /// <summary>No answer in time.</summary>
    Timeout,
    /// <summary>It answered, but its health route failed (5xx, maintenance).</summary>
    ServerError,
    /// <summary>A cabinet without a cabinet token.</summary>
    NoToken,
    /// <summary>A cabinet whose token the server refuses (revoked or unknown).</summary>
    Unauthorized,
}

/// <summary>
/// Whether TaikOnline answers (its /up health route, every 15 s), for the network indicator:
/// network_icon's LABEL table is 0 allnet_good, 1 allnet_warning, 2 allnet_disconnect. A cabinet (its token on the client)
/// is only good once the server takes its token: missing or refused, it warns.
/// </summary>
internal sealed class ServerHealth : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private readonly HttpClient _http;
    private readonly bool _cabinet;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);

    public ServerHealth(HttpClient http, bool cabinet = false)
    {
        _http = http;
        _cabinet = cabinet;
        _ = Task.Run(loop);
    }

    /// <summary>The network_icon type: warning until the first answer, then good, warning (a cabinet's token) or disconnected.</summary>
    public int IconType => State switch
    {
        LinkState.Online => 0,
        LinkState.Checking or LinkState.NoToken or LinkState.Unauthorized => 1,
        _ => 2,
    };

    public LinkState State { get; private set; }

    /// <summary>The HTTP status of a failed answer (ServerError), else 0.</summary>
    public int StatusCode { get; private set; }

    /// <summary>When the last check finished, and how long its health request took.</summary>
    public DateTime? LastCheck { get; private set; }

    public TimeSpan? RoundTrip { get; private set; }

    /// <summary>Checks again now (the service menu's TEST CONNECTION) instead of at the next interval.</summary>
    public void CheckNow()
    {
        State = LinkState.Checking;
        _wake.Release();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _http.Dispose();
    }

    // A cabinet's token, as its routes check it: /me answers 401 to a missing or refused token (404 to an accepted
    // one: a cabinet has no profile).
    private async Task<LinkState> cabinetState()
    {
        if (_http.DefaultRequestHeaders.Authorization is null)
            return LinkState.NoToken;
        using var response = await _http.GetAsync("api/wdb/me", _stop.Token).ConfigureAwait(false);
        return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? LinkState.Unauthorized : LinkState.Online;
    }

    private async Task loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var response = await _http.GetAsync("up", _stop.Token).ConfigureAwait(false);
                RoundTrip = Stopwatch.GetElapsedTime(started);
                StatusCode = response.IsSuccessStatusCode ? 0 : (int)response.StatusCode;
                State = !response.IsSuccessStatusCode ? LinkState.ServerError
                    : _cabinet ? await cabinetState().ConfigureAwait(false) : LinkState.Online;
            }
            catch (HttpRequestException exception)
            {
                (RoundTrip, StatusCode) = (null, 0);
                State = exception.HttpRequestError == HttpRequestError.NameResolutionError ? LinkState.NoInternet : LinkState.Unreachable;
            }
            catch (TaskCanceledException)
            {
                if (_stop.IsCancellationRequested)
                    return;
                (RoundTrip, StatusCode, State) = (null, 0, LinkState.Timeout); // the client's 15 s timeout
            }
            LastCheck = DateTime.Now;
            try
            {
                await _wake.WaitAsync(Interval, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
