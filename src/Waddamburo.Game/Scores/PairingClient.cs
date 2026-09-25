using System.Net.Http.Json;

namespace Waddamburo.Game.Scores;

/// <summary>A player's Banapass profile: whose plays are saved.</summary>
public sealed record ScoreProfile(long Baid, string Name);

/// <summary>What the cabinet should show after a pairing poll.</summary>
public abstract record PairingState
{
    /// <summary>Not pairing (not accepting, or the server closed the session).</summary>
    public sealed record Closed : PairingState;

    /// <summary>Show <see cref="Code"/>; the server replaces it after <see cref="ExpiresIn"/>.</summary>
    public sealed record Active(string Code, TimeSpan ExpiresIn) : PairingState;

    /// <summary>Someone entered the code on the website and chose this card.</summary>
    public sealed record Claimed(string AccessCode) : PairingState;
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
        if (field("status") == "closed")
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

    /// <summary>The profile behind a paired card (cabinet token; POST api/wdb/cards).</summary>
    public async Task<ScoreProfile?> ResolveCardAsync(string accessCode, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/wdb/cards", new { access_code = accessCode },
            ScoreClient.Json, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ScoreProfile>(ScoreClient.Json, cancellationToken).ConfigureAwait(false);
    }
}
