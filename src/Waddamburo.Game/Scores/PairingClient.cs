using System.Net.Http.Json;

namespace Waddamburo.Game.Scores;

/// <summary>A player's Banapass profile: whose plays are saved, and their Don.</summary>
public sealed record ScoreProfile(long Baid, string Name)
{
    /// <summary>A home PC's guest: plays are kept locally under this baid and never uploaded.</summary>
    public const long LocalGuestBaid = 0;

    public static ScoreProfile LocalGuest { get; } = new(LocalGuestBaid, "");

    public Don.DonLook? Look { get; init; }

    /// <summary>The account's custom Don-chan picture (a transparent PNG on the server), for the picker.</summary>
    public string? Avatar { get; init; }

    /// <summary>The website account's display name (Latin letters too, unlike the Don-chan's); null for none.</summary>
    public string? AccountName { get; init; }

    /// <summary>What the home entry's name board shows: the account name, else the Don-chan's.</summary>
    public string DisplayName => AccountName is { Length: > 0 } account ? account : Name;

    /// <summary>The token its plays upload with at home (a stored account's, or a friend's short-lived one).</summary>
    public string? Token { get; init; }
}

/// <summary>What the cabinet should show after a pairing poll.</summary>
public abstract record PairingState
{
    /// <summary>Not pairing (not accepting, or the server closed the session).</summary>
    public sealed record Closed : PairingState;

    /// <summary>Show <see cref="Code"/>; the server replaces it after <see cref="ExpiresIn"/>.</summary>
    public sealed record Active(string Code, TimeSpan ExpiresIn) : PairingState;

    /// <summary>Someone entered the code on the website and chose this card.</summary>
    public sealed record Claimed(string AccessCode) : PairingState;

    /// <summary>A home PC's pairing: the friend who entered the code, with a short-lived token.</summary>
    public sealed record Visitor(ScoreProfile Profile) : PairingState;

    /// <summary>The code was used, but the card has no TaikOnline account to play with.</summary>
    public sealed record Rejected : PairingState;
}

/// <summary>
/// A home PC's six-digit pairing (POST api/wdb/pairing with the owner's token): a visiting friend
/// enters the code on the website and the game receives their profile and a 12-hour token, which it
/// keeps in memory only.
/// </summary>
public sealed class FriendPairingClient(HttpClient http)
{
    private string? _session;
    private string? _ack;

    public async Task<PairingState> PollAsync(bool accepting, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/wdb/pairing",
            new Request(accepting, _session, _ack), ScoreJson.Default.Request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var reply = (await response.Content.ReadFromJsonAsync(ScoreJson.Default.Reply, cancellationToken).ConfigureAwait(false))!;
        return Apply(reply);
    }

    /// <summary>Folds one reply into the session state (public for tests).</summary>
    public PairingState Apply(Reply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (reply.Session is { Length: > 0 } session)
            _session = session;
        switch (reply.Status)
        {
            case "active" when reply.Code is { Length: 6 } code && reply.ExpiresIn > 0:
                return new PairingState.Active(code, TimeSpan.FromSeconds(reply.ExpiresIn.Value));
            case "claimed" when reply.Friend is { } friend:
                _ack = reply.CommandId;
                return new PairingState.Visitor(friend);
            case "rejected":
                _ack = reply.CommandId;
                return new PairingState.Rejected();
            default:
                // closed / complete: the next accepting poll opens a new session.
                _session = _ack = null;
                return new PairingState.Closed();
        }
    }

    internal sealed record Request(bool Accepting, string? Session, string? Ack);

    public sealed record Reply(string Status, string? Session, string? Code, int? ExpiresIn, string? CommandId, ScoreProfile? Friend);
}

/// <summary>
/// TaikOnline's six-digit cabinet pairing (POST api/zucchini/pairing, key=value lines): while the
/// cabinet accepts, the server hands out a code; whoever enters it on the website sends their card.
/// A claimed card is acknowledged on the next poll so the server does not send it again.
/// </summary>
public sealed class PairingClient(HttpClient http, string cabinetId)
{
    private string? _session;
    private string? _ack;
    private string? _lastCommand;

    public async Task<PairingState> PollAsync(bool accepting, CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["cabinet_id"] = cabinetId,
            ["state"] = "attract",
            ["accepting"] = accepting ? "1" : "0",
        };
        if (_session is not null)
            form["session"] = _session;
        if (_ack is not null)
            form["ack"] = _ack;
        using var response = await http.PostAsync("api/zucchini/pairing", new FormUrlEncodedContent(form), cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return Apply(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Folds one response into the session state (public for tests).</summary>
    public PairingState Apply(string body)
    {
        var fields = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => line.Split('=', 2))
            .Where(static pair => pair.Length == 2)
            .ToDictionary(static pair => pair[0], static pair => pair[1]);
        string? field(string key) => fields.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

        if (field("session") is { } session)
            _session = session;
        // complete: the claimed card was acknowledged. The server keeps a polled session complete, so
        // start a new one (a rejected card goes straight back to showing a code).
        if (field("status") is "closed" or "complete")
        {
            _session = _ack = _lastCommand = null;
            return new PairingState.Closed();
        }
        if (field("command_id") is { } command && field("access_code") is { } accessCode
            && accessCode.Length == 20 && accessCode.All(char.IsAsciiDigit))
        {
            _ack = command;
            if (command == _lastCommand)
                return new PairingState.Closed();
            _lastCommand = command;
            return new PairingState.Claimed(accessCode);
        }
        if (field("status") == "active" && field("code") is { Length: 6 } code && code.All(char.IsAsciiDigit)
            && int.TryParse(field("expires_in"), out var seconds) && seconds > 0)
            return new PairingState.Active(code, TimeSpan.FromSeconds(seconds));
        return new PairingState.Closed();
    }

    internal sealed record CardRequest(string AccessCode);

    /// <summary>The profile behind a paired card (cabinet token; POST api/wdb/cards).</summary>
    public async Task<ScoreProfile?> ResolveCardAsync(string accessCode, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/wdb/cards", new CardRequest(accessCode),
            ScoreJson.Default.CardRequest, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(ScoreJson.Default.ScoreProfile, cancellationToken).ConfigureAwait(false);
    }
}
