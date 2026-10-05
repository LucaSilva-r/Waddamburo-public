using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Waddamburo.Game.Online;

/// <summary>
/// TaikOnline's notice endpoints (api/wdb): where the realtime server is, what is showing, a player's
/// unread notices, and signing their private channel. <paramref name="http"/> carries the player's
/// token for the personal calls; the realtime pushes themselves arrive on <see cref="RealtimeConnection"/>.
/// </summary>
public sealed class NoticeClient(HttpClient http)
{
    /// <summary>The realtime (Reverb, Pusher protocol) WebSocket URL; null when the server has none.</summary>
    public async Task<Uri?> RealtimeAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/wdb/realtime", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null; // an older server
        response.EnsureSuccessStatusCode();
        var info = await response.Content.ReadFromJsonAsync(OnlineJson.Default.RealtimeResult, cancellationToken).ConfigureAwait(false);
        return info?.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>The system notices showing now (for every client, no login needed).</summary>
    public async Task<List<ServerNotice>> NoticesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/wdb/notices", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(OnlineJson.Default.NoticesResult, cancellationToken).ConfigureAwait(false))!.Notices;
    }

    /// <summary>The token's unread notices, and the private channel new ones are pushed on.</summary>
    public async Task<(string? Channel, List<ServerNotice> Notices)> NotificationsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/wdb/notifications", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return (null, []);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync(OnlineJson.Default.NotificationsResult, cancellationToken).ConfigureAwait(false))!;
        return (result.Channel, result.Notifications);
    }

    /// <summary>Marks the token's notices read (they were shown).</summary>
    public async Task MarkReadAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/wdb/notifications/read", new ReadRequest(ids), OnlineJson.Default.ReadRequest, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Signs a private realtime channel for a socket (Pusher auth); null when refused.</summary>
    public async Task<string?> AuthorizeChannelAsync(string socketId, string channel, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/wdb/broadcasting/auth", new ChannelAuthRequest(socketId, channel),
            OnlineJson.Default.ChannelAuthRequest, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            return null;
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(OnlineJson.Default.ChannelAuthResult, cancellationToken).ConfigureAwait(false))?.Auth;
    }

    /// <summary>A notice pushed over the realtime connection (the event's data).</summary>
    public static ServerNotice? ParseNotice(JsonElement data) => data.Deserialize(OnlineJson.Default.ServerNotice);

    internal sealed record RealtimeResult(string? Url);

    internal sealed record NoticesResult(List<ServerNotice> Notices);

    internal sealed record NotificationsResult(string? Channel, List<ServerNotice> Notifications);

    internal sealed record ReadRequest(IReadOnlyCollection<string> Ids);

    internal sealed record ChannelAuthRequest(string SocketId, string ChannelName);

    internal sealed record ChannelAuthResult(string? Auth);
}

/// <summary>The notice endpoints' JSON (snake_case), generated at compile time for NativeAOT.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ServerNotice))]
[JsonSerializable(typeof(NoticeClient.RealtimeResult))]
[JsonSerializable(typeof(NoticeClient.NoticesResult))]
[JsonSerializable(typeof(NoticeClient.NotificationsResult))]
[JsonSerializable(typeof(NoticeClient.ReadRequest))]
[JsonSerializable(typeof(NoticeClient.ChannelAuthRequest))]
[JsonSerializable(typeof(NoticeClient.ChannelAuthResult))]
internal sealed partial class OnlineJson : JsonSerializerContext;
