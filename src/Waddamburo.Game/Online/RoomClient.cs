using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Waddamburo.Game.Online;

/// <summary>An online room: the code that identifies it, and the presence channel its two players set up their connection on.</summary>
public sealed record OnlineRoom(string Code, string Channel)
{
    public string Name { get; init; } = "";

    public bool IsPublic { get; init; }

    public bool HasPassword { get; init; }
}

/// <summary>A room anyone can join, as the list shows it.</summary>
public sealed record ListedRoom(string Code, string Name, string Host, bool HasPassword);

/// <summary>Joining: the room, or null with <paramref name="WrongPassword"/> saying whether that was why.</summary>
public sealed record RoomJoinResult(OnlineRoom? Room, bool WrongPassword = false);

/// <summary>A STUN or TURN server, with credentials for TURN.</summary>
public sealed record IceServer(IReadOnlyList<string> Urls, string? Username = null, string? Credential = null);

/// <summary>TaikOnline's room endpoints (api/wdb/rooms). <paramref name="http"/> carries the player's token.</summary>
public sealed class RoomClient(HttpClient http)
{
    /// <summary>The rooms with a free seat that anyone can join.</summary>
    public async Task<IReadOnlyList<ListedRoom>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/wdb/rooms", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(OnlineJson.Default.ListResult, cancellationToken).ConfigureAwait(false))!.Rooms;
    }

    /// <summary>Opens a room hosted by this player; without a name it takes theirs.</summary>
    public async Task<OnlineRoom> CreateAsync(string? name = null, bool isPublic = false, string? password = null, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/wdb/rooms", new SettingsRequest(name, isPublic, password), OnlineJson.Default.SettingsRequest, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(OnlineJson.Default.OnlineRoom, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>The host changes their room: what's null stays as it is; an empty password removes it.</summary>
    public async Task<OnlineRoom> UpdateAsync(string code, string? name = null, bool? isPublic = null, string? password = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"api/wdb/rooms/{Uri.EscapeDataString(code)}")
        {
            Content = JsonContent.Create(new SettingsRequest(name, isPublic, password), OnlineJson.Default.SettingsRequest),
        };
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(OnlineJson.Default.OnlineRoom, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Takes the waiting player's room, or starts waiting in a new one.</summary>
    public async Task<OnlineRoom> QuickPlayAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync("api/wdb/rooms/quick-play", null, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(OnlineJson.Default.OnlineRoom, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Tells the server this game is still in the room; false when the room is gone.</summary>
    public async Task<bool> KeepAliveAsync(string code, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"api/wdb/rooms/{Uri.EscapeDataString(code)}/keep-alive", null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>Takes the room's guest seat; no room when there's no such room or the seat is taken.</summary>
    public async Task<RoomJoinResult> JoinAsync(string code, string? password = null, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/wdb/rooms/{Uri.EscapeDataString(code)}/join", new SettingsRequest(null, null, password), OnlineJson.Default.SettingsRequest, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new RoomJoinResult(null);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            var refusal = await response.Content.ReadFromJsonAsync(OnlineJson.Default.JoinRefusal, cancellationToken).ConfigureAwait(false);
            if (refusal?.WrongPassword == true)
                return new RoomJoinResult(null, WrongPassword: true);
        }
        response.EnsureSuccessStatusCode();
        return new RoomJoinResult(await response.Content.ReadFromJsonAsync(OnlineJson.Default.OnlineRoom, cancellationToken).ConfigureAwait(false));
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

    internal sealed record ListResult(List<ListedRoom> Rooms);

    internal sealed record JoinRefusal(bool WrongPassword);

    internal sealed record SettingsRequest(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsPublic,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Password);
}
