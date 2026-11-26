using Waddamburo.Game.SongSelect;
using Waddamburo.Services.Scores;

namespace Waddamburo.Features.Events;

/// <summary>
/// Events: TaikOnline's running event, for Song Select's event folder (each frontend shows it its way): asked at start,
/// again once it ends, and every half hour otherwise (a failed ask keeps the last answer). Offline, or before the first
/// answer, there is no event.
/// </summary>
public sealed class EventsModule
{
    private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(30);
    private volatile SongSelectEvent? _event;

    public EventsModule(Uri? server, bool insecure)
    {
        if (server is not null)
            _ = Task.Run(() => refreshAsync(server, insecure));
    }

    /// <summary>The event running now, or null.</summary>
    public SongSelectEvent? Current => _event is { } running && running.EndsAt > DateTimeOffset.Now ? running : null;

    private async Task refreshAsync(Uri server, bool insecure)
    {
        using var http = ScoreClient.CreateHttp(server, insecure);
        var client = new ScoreClient(http);
        while (true)
        {
            try
            {
                _event = await client.EventAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                // An older server has no events, or the network is down: keep what we had.
            }
            var wait = _event is { } known && known.EndsAt - DateTimeOffset.Now is { } left && left > TimeSpan.Zero && left < Refresh
                ? left + TimeSpan.FromSeconds(5) : Refresh;
            await Task.Delay(wait).ConfigureAwait(false);
        }
    }
}
