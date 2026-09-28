using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scores;

namespace Waddamburo.App.Online;

/// <summary>
/// The local score database and the TaikOnline side: uploads, server bests, rankings and the
/// cabinet's card pairing. Home keeps every play on this PC (guests' too, baid 0, never uploaded);
/// a cabinet (server and cabinet token set) keeps and uploads everyone's.
/// </summary>
internal sealed class ScoreSync : IDisposable
{
    private readonly ArcadeSettings _arcade;
    private readonly AccountBook? _accounts;
    private readonly Uri? _server;
    private readonly Dictionary<string, ScoreClient> _clients = [];

    // accounts: the ones stored on this PC (home mode; null in arcade).
    public ScoreSync(ArcadeSettings arcade, string? scoresPath, AccountBook? accounts)
    {
        _arcade = arcade;
        _accounts = accounts;
        var cabinet = !arcade.Home && arcade is { Server: not null, CabinetToken: not null };
        Health = arcade.Server is { } healthServer ? new ServerHealth(ScoreClient.CreateHttp(healthServer, arcade.ServerInsecure)) : null;
        Scores = (arcade.Home || cabinet) && scoresPath is not null ? new ScoreStore(scoresPath) : null;
        if (Scores is null || arcade.Server is not { } server)
            return;
        _server = server;
        if (cabinet)
        {
            var http = ScoreClient.CreateHttp(server, arcade.ServerInsecure, arcade.CabinetToken);
            CabinetServer = new ScoreClient(http);
            Pairing = new CabinetPairing(http);
        }
        // Plays left over from offline runs: a cabinet's (everyone's), or each stored account's.
        if (CabinetServer is not null)
            Upload(ScoreProfile.LocalGuest);
        foreach (var account in accounts?.Accounts ?? [])
        {
            Upload(account.Profile);
            RefreshBests(account.Profile);
        }
        // Names, looks and avatars as the website has them now (revoked logins drop out).
        if (accounts is not null)
            _ = Task.Run(async () =>
            {
                try
                {
                    await accounts.RefreshAsync(account => ClientFor(account.Token)!).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    Console.Error.WriteLine($"Warning ACCOUNT: profiles not refreshed ({exception.Message}).");
                }
            });
    }

    /// <summary>The local score database, open when a profile plays (null: guests, nothing saved).</summary>
    public ScoreStore? Scores { get; }

    /// <summary>A cabinet's own server client (its token uploads for every player); null at home.</summary>
    public ScoreClient? CabinetServer { get; }

    /// <summary>A cabinet's 6-digit card pairing (null at home).</summary>
    public CabinetPairing? Pairing { get; }

    /// <summary>The server's reachability, for the network icon (null offline).</summary>
    public ServerHealth? Health { get; }

    /// <summary>Scores go to a server (charts need their hashes to match its bests).</summary>
    public bool Online => _server is not null;

    /// <summary>
    /// Uploads a player's pending plays in the background: a cabinet uploads everyone's with its own
    /// token; a home PC uploads each account's with that account's token (a friend's short-lived one
    /// included). Guests (no token) stay local.
    /// </summary>
    public void Upload(ScoreProfile profile)
    {
        if (Scores is null)
            return;
        if (CabinetServer is { } cabinet)
        {
            cabinet.SyncInBackground(Scores, null);
            return;
        }
        if (profile.Token is { } token && ClientFor(token) is { } uploader)
            uploader.SyncInBackground(Scores, profile.Baid);
    }

    /// <summary>
    /// Downloads a player's server bests (crowns from every machine) into the store in the background;
    /// song select reads them when it loads. Guests have none.
    /// </summary>
    public void RefreshBests(ScoreProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (Scores is not { } scores || profile.Baid == ScoreProfile.LocalGuestBaid)
            return;
        var client = CabinetServer ?? (profile.Token is { } token ? ClientFor(token) : null);
        if (client is null)
            return;
        long? baid = CabinetServer is null ? null : profile.Baid;
        _ = Task.Run(async () =>
        {
            try
            {
                scores.ReplaceRemoteBests(profile.Baid, await client.BestsAsync(baid).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"Warning BESTS: {profile.Name}'s server bests not loaded ({exception.Message}).");
            }
        });
    }

    /// <summary>A server client with a home player's token (one per token, reused); null offline.</summary>
    public ScoreClient? ClientFor(string token)
    {
        if (_server is not { } server)
            return null;
        lock (_clients)
        {
            if (!_clients.TryGetValue(token, out var client))
                _clients[token] = client = new ScoreClient(ScoreClient.CreateHttp(server, _arcade.ServerInsecure, token));
            return client;
        }
    }

    /// <summary>
    /// Rankings are read with the cabinet's token, or at home with any token at hand: a joined
    /// player's, the default account's, then any stored account's.
    /// </summary>
    public ScoreClient? RankingClient() => CabinetServer ?? TaikoGuest.Profiles.Select(static profile => profile?.Token)
        .Append(_accounts?.Default?.Token).Concat(_accounts?.Accounts.Select(static account => account.Token) ?? [])
        .OfType<string>().Select(ClientFor).FirstOrDefault(static client => client is not null);

    public void Dispose()
    {
        Pairing?.Dispose();
        Health?.Dispose();
        Scores?.Dispose();
    }
}
