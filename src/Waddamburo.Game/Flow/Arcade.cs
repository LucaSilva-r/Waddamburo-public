using System.Globalization;

namespace Waddamburo.Game.Flow;

/// <summary>
/// Cabinet coin settings (the original's service-menu options), read from config.cfg:
/// <c>key = value</c> lines, <c>#</c> comments.
/// </summary>
public sealed record ArcadeSettings
{
    public const string FileName = "config.cfg";

    public bool FreePlay { get; init; } = true;

    public int CreditsPerCoin { get; init; } = 1;

    /// <summary>Credits one player needs to start a session.</summary>
    public int CreditsOnePlayer { get; init; } = 1;

    /// <summary>Credits two players need together (the second player pays the difference).</summary>
    public int CreditsTwoPlayers { get; init; } = 2;

    public int SongsPerSession { get; init; } = 2;

    /// <summary>The TaikOnline server scores go to (null: offline, scores stay local).</summary>
    public Uri? Server { get; init; }

    /// <summary>Accept any server certificate (a local server's self-signed one). Exposes the login to the network.</summary>
    public bool ServerInsecure { get; init; }

    public static string DefaultFileText => """
        # Waddamburo cabinet settings. Restart the game after editing.
        # free_play: true = a drum hit starts; false = coins (F2) buy credits.
        free_play = true
        # Credits added by each coin.
        credits_per_coin = 1
        # Credits needed for one player, and for two players together.
        credits_1p = 1
        credits_2p = 2
        # Songs in one session.
        songs_per_session = 2
        # TaikOnline server for scores (log in with --login). Leave out to play offline.
        # server = https://taikonline.example
        # server_insecure: true accepts a self-signed certificate (local servers only).
        # server_insecure = false

        """;

    /// <summary>Reads the file, writing the defaults first when it does not exist.</summary>
    public static ArcadeSettings LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            File.WriteAllText(path, DefaultFileText);
        return Parse(File.ReadAllText(path));
    }

    public static ArcadeSettings Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var settings = new ArcadeSettings();
        foreach (var (raw, index) in text.Split('\n').Select((line, index) => (line, index + 1)))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
                continue;
            var separator = line.IndexOf('=');
            if (separator <= 0)
                throw new InvalidDataException($"{FileName} line {index}: expected 'key = value'.");
            var key = line[..separator].Trim().ToLowerInvariant();
            var value = line[(separator + 1)..].Trim();
            settings = key switch
            {
                "free_play" => settings with { FreePlay = boolean(value, index) },
                "credits_per_coin" => settings with { CreditsPerCoin = count(value, index, 1) },
                "credits_1p" => settings with { CreditsOnePlayer = count(value, index, 1) },
                "credits_2p" => settings with { CreditsTwoPlayers = count(value, index, 1) },
                "songs_per_session" => settings with { SongsPerSession = count(value, index, 1) },
                "server" => settings with { Server = server(value, index) },
                "server_insecure" => settings with { ServerInsecure = boolean(value, index) },
                _ => throw new InvalidDataException($"{FileName} line {index}: unknown setting '{key}'."),
            };
        }
        if (settings.CreditsTwoPlayers < settings.CreditsOnePlayer)
            throw new InvalidDataException($"{FileName}: credits_2p must be at least credits_1p.");
        return settings;
    }

    private static bool boolean(string value, int line) => value.ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        "false" or "0" or "no" or "off" => false,
        _ => throw new InvalidDataException($"{FileName} line {line}: '{value}' is not true or false."),
    };

    private static Uri server(string value, int line) =>
        Uri.TryCreate(value.EndsWith('/') ? value : value + "/", UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            ? uri
            : throw new InvalidDataException($"{FileName} line {line}: '{value}' is not an http(s) address.");

    private static int count(string value, int line, int minimum) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= minimum
            ? number
            : throw new InvalidDataException($"{FileName} line {line}: '{value}' must be a whole number of at least {minimum}.");
}

/// <summary>
/// The cabinet's credit counter: a coin is credited the moment it arrives (its sound is queued
/// separately by the host); credits only go when a player joins. It lives for the whole process:
/// nothing but a restart resets it.
/// </summary>
public sealed class CoinBank(ArcadeSettings settings)
{
    public ArcadeSettings Settings { get; } = settings ?? throw new ArgumentNullException(nameof(settings));

    public int Credits { get; private set; }

    public void InsertCoin() => Credits += Settings.CreditsPerCoin;

    /// <summary>What the next player to join pays: one player's price, or the rest of the two-player price.</summary>
    public int JoinCost(int joinedPlayers) => joinedPlayers == 0
        ? Settings.CreditsOnePlayer
        : Settings.CreditsTwoPlayers - Settings.CreditsOnePlayer;

    /// <summary>Credits still missing for the next join (0 when affordable).</summary>
    public int Missing(int joinedPlayers) => Math.Max(0, JoinCost(joinedPlayers) - Credits);

    public bool TryJoin(int joinedPlayers)
    {
        var cost = JoinCost(joinedPlayers);
        if (Credits < cost)
            return false;
        Credits -= cost;
        return true;
    }
}
