using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Waddamburo.Game.Scores;

/// <summary>A home player stored on this PC: the server token and the Banapass profile it belongs to.</summary>
public sealed record ScoreAccount(string Token, long Baid, string Name)
{
    /// <summary>The Don's look (refreshed from the server at startup).</summary>
    public Don.DonLook? Look { get; init; }

    /// <summary>The account's custom Don-chan picture URL.</summary>
    public string? Avatar { get; init; }

    /// <summary>The website account's display name.</summary>
    public string? AccountName { get; init; }

    /// <summary>The name board's title (empty: none) and its plate (player_name TITLE_LABEL: 0 normal, 1 rainbow, 2 gold, 3 platinum).</summary>
    public string? Title { get; init; }

    public int TitlePlate { get; init; }

    public ScoreProfile Profile => new(Baid, Name)
        { Look = Look, Avatar = Avatar, Token = Token, AccountName = AccountName, Title = Title, TitlePlate = TitlePlate };
}

/// <summary>A device login in progress: the code to show and where to enter it.</summary>
/// <summary>One line of a chart's server ranking: a player's best.</summary>
public sealed record RankingEntry(long Baid, string Name, int Score);

public sealed record DeviceLoginStart(string DeviceCode, string UserCode, int ExpiresIn, int Interval, string VerificationUrl);

public sealed class TwoFactorRequiredException() : Exception("The account needs a two-factor code.");

/// <summary>TaikOnline's Waddamburo API (api/wdb): login, and the play/chart upload outbox.</summary>
public sealed class ScoreClient(HttpClient http)
{
    private const int BatchSize = 50;

    /// <summary>The API's JSON options (snake_case), backed by the generated <see cref="ScoreJson"/>.</summary>
    public static JsonSerializerOptions Json => ScoreJson.Default.Options;

    private int _syncing;

    /// <summary>The client's HTTP connection (its token), for other endpoints such as home pairing.</summary>
    public HttpClient Http => http;

    /// <summary>
    /// A client for <paramref name="server"/>. <paramref name="insecure"/> skips certificate checks
    /// (a local server's self-signed certificate); anyone on the network can then read the token.
    /// </summary>
    public static HttpClient CreateHttp(Uri server, bool insecure, string? token = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        var handler = new HttpClientHandler();
        if (insecure)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        var http = new HttpClient(handler) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    public async Task<ScoreAccount> LoginAsync(string login, string password, string? code, string device,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/wdb/login",
            new LoginRequest(login, password, code, device), ScoreJson.Default.LoginRequest, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var error = await response.Content.ReadFromJsonAsync(ScoreJson.Default.JsonElement, cancellationToken).ConfigureAwait(false);
            if (error.TryGetProperty("two_factor", out _))
                throw new TwoFactorRequiredException();
            throw new InvalidOperationException(error.TryGetProperty("message", out var message)
                ? message.GetString() : "Login failed.");
        }
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(ScoreJson.Default.ScoreAccount, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>In-game login without a password, step 1: a code for the player to enter on the website.</summary>
    public async Task<DeviceLoginStart> StartDeviceLoginAsync(string device, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/wdb/device", new DeviceRequest(device), ScoreJson.Default.DeviceRequest, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(ScoreJson.Default.DeviceLoginStart, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Step 2 (polled every Interval seconds): the account once approved; null while pending.</summary>
    /// <exception cref="InvalidOperationException">Denied or expired.</exception>
    public async Task<ScoreAccount?> PollDeviceLoginAsync(string deviceCode, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/wdb/device/token", new DeviceTokenRequest(deviceCode), ScoreJson.Default.DeviceTokenRequest,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync(ScoreJson.Default.JsonElement, cancellationToken).ConfigureAwait(false);
        return reply.GetProperty("status").GetString() switch
        {
            "pending" => null,
            "approved" => reply.Deserialize(ScoreJson.Default.ScoreAccount),
            var status => throw new InvalidOperationException(status == "denied" ? "The login was refused." : "The code expired."),
        };
    }

    /// <summary>The token's current profile (name, look, avatar); null when the token was revoked.</summary>
    public async Task<ScoreProfile?> MeAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/wdb/me", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(ScoreJson.Default.ScoreProfile, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The top three players' bests per chart hash (song select's score windows).</summary>
    public async Task<Dictionary<string, List<RankingEntry>>> RankingsAsync(IReadOnlyCollection<string> charts,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/wdb/rankings", new RankingsRequest(charts), ScoreJson.Default.RankingsRequest, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(ScoreJson.Default.RankingsResult, cancellationToken).ConfigureAwait(false))!
            .Rankings;
    }

    /// <summary>A player's bests per chart hash (home: the token's player; cabinet: <paramref name="baid"/>).</summary>
    public async Task<List<RemoteBest>> BestsAsync(long? baid = null, CancellationToken cancellationToken = default)
    {
        var path = baid is { } cabinetBaid ? $"api/wdb/bests?baid={cabinetBaid}" : "api/wdb/bests";
        using var response = await http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(ScoreJson.Default.BestsResult, cancellationToken).ConfigureAwait(false))!.Bests;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync("api/wdb/login", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Uploads the player's pending plays, then any charts the server asks for. Plays the server
    /// answered for are marked uploaded (rejected ones too: retrying would not change the answer).
    /// Returns the number of plays sent; throws on network/server errors, leaving the rest pending.
    /// </summary>
    public async Task<int> SyncAsync(ScoreStore store, long? baid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var sent = 0;
        while (store.PendingPlays(baid, BatchSize) is { Count: > 0 } batch)
        {
            using var response = await http.PostAsJsonAsync("api/wdb/plays", new PlaysRequest(batch), ScoreJson.Default.PlaysRequest, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var result = (await response.Content.ReadFromJsonAsync(ScoreJson.Default.PlaysResult, cancellationToken).ConfigureAwait(false))!;
            foreach (var sha in result.MissingCharts)
            {
                if (store.Chart(sha) is not { } chart)
                    continue;
                using var upload = await http.PutAsJsonAsync($"api/wdb/charts/{sha}", chart, ScoreJson.Default.ChartUpload, cancellationToken)
                    .ConfigureAwait(false);
                upload.EnsureSuccessStatusCode();
            }
            store.MarkUploaded(batch.Select(static play => play.Id));
            sent += batch.Count;
            if (result.Accepted.Count < batch.Count)
                Console.Error.WriteLine($"Warning SCORE_REJECTED: the server refused {batch.Count - result.Accepted.Count} play(s).");
        }
        return sent;
    }

    /// <summary>
    /// Fire-and-forget <see cref="SyncAsync"/>; a sync already running covers the new plays.
    /// <paramref name="job"/> reports it on the notice sidebar.
    /// </summary>
    public void SyncInBackground(ScoreStore store, long? baid, Flow.JobBoard.Job? job = null)
    {
        if (Interlocked.Exchange(ref _syncing, 1) == 1)
        {
            job?.Complete();
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var sent = await SyncAsync(store, baid).ConfigureAwait(false);
                job?.Complete();
                if (sent > 0)
                    Console.WriteLine($"Uploaded {sent} play(s).");
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Offline or server trouble: the plays stay pending for the next sync.
                job?.Fail(exception.Message);
                Console.Error.WriteLine($"Warning SCORE_SYNC: {exception.Message}");
            }
            finally
            {
                Volatile.Write(ref _syncing, 0);
            }
        });
    }

    internal sealed record LoginRequest(string Login, string Password, string? Code, string Device);

    internal sealed record DeviceRequest(string Device);

    internal sealed record DeviceTokenRequest(string DeviceCode);

    internal sealed record RankingsRequest(IReadOnlyCollection<string> Charts);

    internal sealed record PlaysRequest(List<UploadPlay> Plays);

    internal sealed record BestsResult(List<RemoteBest> Bests);

    internal sealed record RankingsResult(Dictionary<string, List<RankingEntry>> Rankings);

    internal sealed record PlaysResult(List<Guid> Accepted, List<string> MissingCharts);
}
