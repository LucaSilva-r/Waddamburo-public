namespace Waddamburo.App.Flow;

/// <summary>
/// Whether TaikOnline answers (its /up health route, every 15 s), for the network indicator:
/// network_icon's LABEL table is 0 allnet_good, 1 allnet_warning, 2 allnet_disconnect.
/// </summary>
internal sealed class ServerHealth : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _stop = new();

    public ServerHealth(HttpClient http)
    {
        _http = http;
        _ = Task.Run(loop);
    }

    /// <summary>The network_icon type: warning until the first answer, then good or disconnected.</summary>
    public int IconType { get; private set; } = 1;

    public void Dispose()
    {
        _stop.Cancel();
        _http.Dispose();
    }

    private async Task loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var response = await _http.GetAsync("up", _stop.Token).ConfigureAwait(false);
                IconType = response.IsSuccessStatusCode ? 0 : 2;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                if (_stop.IsCancellationRequested)
                    return;
                IconType = 2;
            }
            try
            {
                await Task.Delay(Interval, _stop.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }
}
