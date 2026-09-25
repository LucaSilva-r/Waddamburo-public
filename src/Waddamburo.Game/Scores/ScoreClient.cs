using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Waddamburo.Game.Scores;

/// <summary>A logged-in home player: the server token and the Banapass profile it belongs to.</summary>
public sealed record ScoreAccount(string Token, long Baid, string Name)
{
    public const string FileName = "account.json";

    public ScoreProfile Profile => new(Baid, Name);

    public static ScoreAccount? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<ScoreAccount>(File.ReadAllText(path), ScoreClient.Json) : null;

    /// <summary>Written owner-only: the token is a password equivalent.</summary>
    public void Save(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        JsonSerializer.Serialize(stream, this, ScoreClient.Json);
    }
}

public sealed class TwoFactorRequiredException() : Exception("The account needs a two-factor code.");

/// <summary>TaikOnline's Waddamburo API (api/wdb): login, and the play/chart upload outbox.</summary>
public sealed class ScoreClient(HttpClient http)
{
    private const int BatchSize = 50;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private int _syncing;

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
            new { login, password, code, device }, Json, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var error = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken).ConfigureAwait(false);
            if (error.TryGetProperty("two_factor", out _))
                throw new TwoFactorRequiredException();
            throw new InvalidOperationException(error.TryGetProperty("message", out var message)
                ? message.GetString() : "Login failed.");
        }
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ScoreAccount>(Json, cancellationToken).ConfigureAwait(false))!;
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
            using var response = await http.PostAsJsonAsync("api/wdb/plays", new { plays = batch }, Json, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var result = (await response.Content.ReadFromJsonAsync<PlaysResult>(Json, cancellationToken).ConfigureAwait(false))!;
            foreach (var sha in result.MissingCharts)
            {
                if (store.Chart(sha) is not { } chart)
                    continue;
                using var upload = await http.PutAsJsonAsync($"api/wdb/charts/{sha}", chart, Json, cancellationToken)
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

    /// <summary>Fire-and-forget <see cref="SyncAsync"/>; a sync already running covers the new plays.</summary>
    public void SyncInBackground(ScoreStore store, long? baid)
    {
        if (Interlocked.Exchange(ref _syncing, 1) == 1)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                var sent = await SyncAsync(store, baid).ConfigureAwait(false);
                if (sent > 0)
                    Console.WriteLine($"Uploaded {sent} play(s).");
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Offline or server trouble: the plays stay pending for the next sync.
                Console.Error.WriteLine($"Warning SCORE_SYNC: {exception.Message}");
            }
            finally
            {
                Volatile.Write(ref _syncing, 0);
            }
        });
    }

    private sealed record PlaysResult(List<Guid> Accepted, List<string> MissingCharts);
}
