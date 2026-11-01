using System.Net;
using System.Net.Http.Json;

namespace Waddamburo.Game.Online;

/// <summary>An online room: its code to share, and the presence channel its two players set up their connection on.</summary>
public sealed record OnlineRoom(string Code, string Channel);

/// <summary>A STUN or TURN server, with credentials for TURN.</summary>
public sealed record IceServer(IReadOnlyList<string> Urls, string? Username = null, string? Credential = null);

/// <summary>TaikOnline's room endpoints (api/wdb/rooms). <paramref name="http"/> carries the player's token.</summary>
public sealed class RoomClient(HttpClient http)
{
    /// <summary>Opens a room hosted by this player.</summary>
    public async Task<OnlineRoom> CreateAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync("api/wdb/rooms", null, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(OnlineJson.Default.OnlineRoom, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Takes the room's guest seat; null when there's no such room or the seat is taken.</summary>
    public async Task<OnlineRoom?> JoinAsync(string code, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"api/wdb/rooms/{Uri.EscapeDataString(code)}/join", null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(OnlineJson.Default.OnlineRoom, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Leaves the room: a host's closes, a guest's seat frees.</summary>
    public async Task LeaveAsync(string code, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/wdb/rooms/{Uri.EscapeDataString(code)}", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>The servers for connecting to the other player, with short-lived TURN credentials when the server has them.</summary>
    public async Task<IReadOnlyList<IceServer>> IceServersAsync(string code, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/wdb/rooms/{Uri.EscapeDataString(code)}/ice-servers", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(OnlineJson.Default.IceServersResult, cancellationToken).ConfigureAwait(false))!.IceServers;
    }

    internal sealed record IceServersResult(List<IceServer> IceServers);
}
