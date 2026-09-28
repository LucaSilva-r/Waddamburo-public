using System.Globalization;

namespace Waddamburo.Game.Flow;

/// <summary>
/// Cabinet settings (the original's service-menu options plus Waddamburo's own), read from
/// config.cfg: <c>key = value</c> lines, <c>#</c> comments. The file carries
/// <c>config_version</c>; an older file gets each later version's settings appended (with their
/// comments and defaults) the next time it is loaded, keeping the user's edits.
/// </summary>
public sealed record ArcadeSettings
{
    public const string FileName = "config.cfg";

    /// <summary>The config_version this build writes. Bump it with each new entry in <see cref="Additions"/>.</summary>
    public const int CurrentVersion = 6;

    // Version 2's mode for a file written before it: a cabinet (token, or coins) stays arcade.
    private const string ModePlaceholder = "{mode}";

    /// <summary>Settings added in each version (index = version - 1), as they are appended to older files.</summary>
    private static readonly string[] Additions =
    [
        """
        # Start in fullscreen (F11 switches between fullscreen and a window).
        fullscreen = false
        # Windows release builds run without a console; true opens one showing the log.
        console = false
        # Check for a new Waddamburo release at start, install it and restart into it.
        auto_update = true

        """,
        """
        # mode: home = a PC (free play, endless songs, no countdowns, Escape back to the title);
        # arcade = a cabinet (coins or free play, songs per session, revival, countdowns).
        mode = {mode}

        """,
        """
        # Song titles in english (translated, when known) or japanese (the original).
        title_language = english

        """,
        """
        # Volumes in percent (0-100). These and the timing settings below can also be changed in
        # home mode's Escape menu > Settings, which saves them here.
        master_volume = 50
        music_volume = 100
        drum_volume = 100
        effects_volume = 100
        # voice_volume: Don-chan's (and Katsu-chan's) voice lines.
        voice_volume = 100
        # audio_offset_ms: raise it when the music sounds late against the notes (moves notes and judgement later).
        audio_offset_ms = 0
        # input_offset_ms: raise it when your hits are judged late (the drum or keyboard lags).
        input_offset_ms = 0
        # audio_buffer_frames: the audio device's period, applied at start. Lower = less delay; too low crackles.
        audio_buffer_frames = 256

        """,
        """
        # stereo_panning: two players' drums and cues on their own side (left/right), each cutting off
        # only its own sounds; false = everything centred, one of each sound at a time.
        stereo_panning = true
        # mute_in_background: fade the sound out while the window is not focused.
        mute_in_background = true

        """,
        """
        # renderer: auto lets the GPU backend be picked for the hardware; vulkan, gles, opengl,
        # d3d11 or metal forces one (e.g. gles on a Sandy Bridge laptop whose Vulkan driver is slow).
        # A backend this build or machine lacks falls back to auto.
        renderer = auto

        """,
    ];

    /// <summary>The file's config_version (0: written before versioning).</summary>
    public int Version { get; init; }

    public bool Fullscreen { get; init; }

    public bool ShowConsole { get; init; }

    public bool AutoUpdate { get; init; } = true;

    /// <summary>Home mode (a PC): free play, endless songs, no countdowns. False: arcade (the cabinet rules).</summary>
    public bool Home { get; init; }

    public bool FreePlay { get; init; } = true;

    /// <summary>Song titles and subtitles in English when a song has them; false: the Japanese originals.</summary>
    public bool EnglishTitles { get; init; } = true;

    public int MasterVolume { get; init; } = 50;

    public int MusicVolume { get; init; } = 100;

    public int DrumVolume { get; init; } = 100;

    public int EffectsVolume { get; init; } = 100;

    public int VoiceVolume { get; init; } = 100;

    /// <summary>Milliseconds the heard music lags the chart clock: notes and judgement move this much later.</summary>
    public int AudioOffsetMs { get; init; }

    /// <summary>Milliseconds the player's hits arrive late: they are judged this much earlier.</summary>
    public int InputOffsetMs { get; init; }

    /// <summary>The audio device period requested at start (SDL may choose another).</summary>
    public int AudioBufferFrames { get; init; } = 256;

    /// <summary>Two players' sounds panned to their own side; false: centred, shared.</summary>
    public bool StereoPanning { get; init; } = true;

    /// <summary>Fade the sound out while the window is in the background.</summary>
    public bool MuteInBackground { get; init; } = true;

    /// <summary>The forced GPU backend (vulkan, gles, opengl, d3d11, metal), or null for auto.</summary>
    public string? Renderer { get; init; }

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

    /// <summary>The cabinet's TaikOnline token: enables 6-digit pairing login and uploads for every player.</summary>
    public string? CabinetToken { get; init; }

    public static string DefaultFileText => $"""
        # Waddamburo cabinet settings. Restart the game after editing.
        config_version = {CurrentVersion}
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
        # cabinet_token: a cabinet token from the server admin; players then log in with a 6-digit code.
        # cabinet_token =

        """ + string.Concat(Additions).Replace(ModePlaceholder, "home");

    /// <summary>Reads the file, writing the defaults first when it does not exist and upgrading an older one.</summary>
    public static ArcadeSettings LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            File.WriteAllText(path, DefaultFileText);
        var text = File.ReadAllText(path);
        var upgraded = Upgrade(text);
        if (!ReferenceEquals(upgraded, text))
            File.WriteAllText(path, upgraded);
        return Parse(upgraded);
    }

    /// <summary>
    /// Appends the settings of every version after the file's own and sets config_version, or
    /// returns <paramref name="text"/> itself when it is already current (or newer).
    /// </summary>
    public static string Upgrade(string text)
    {
        var settings = Parse(text);
        var version = settings.Version;
        if (version >= CurrentVersion)
            return text;
        var kept = text.Split('\n').Where(static line => !isKey(line, "config_version"));
        var mode = settings.CabinetToken is not null || !settings.FreePlay ? "arcade" : "home";
        return string.Join('\n', kept).TrimEnd() + "\n\n"
            + string.Concat(Additions[version..]).Replace(ModePlaceholder, mode)
            + $"config_version = {CurrentVersion}\n";
    }

    private static bool isKey(string line, string key)
    {
        var content = line.Split('#')[0];
        var separator = content.IndexOf('=');
        return separator > 0 && content[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase);
    }

    public static ArcadeSettings Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var settings = new ArcadeSettings();
        string? unknown = null;
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
                "cabinet_token" => settings with { CabinetToken = value.Length == 0 ? null : value },
                "config_version" => settings with { Version = count(value, index, 0) },
                "fullscreen" => settings with { Fullscreen = boolean(value, index) },
                "console" => settings with { ShowConsole = boolean(value, index) },
                "auto_update" => settings with { AutoUpdate = boolean(value, index) },
                "title_language" => settings with { EnglishTitles = value.ToLowerInvariant() switch
                {
                    "english" => true,
                    "japanese" => false,
                    _ => throw new InvalidDataException($"{FileName} line {index}: title_language is english or japanese, not '{value}'."),
                } },
                "master_volume" => settings with { MasterVolume = integer(value, index, 0, 100) },
                "music_volume" => settings with { MusicVolume = integer(value, index, 0, 100) },
                "drum_volume" => settings with { DrumVolume = integer(value, index, 0, 100) },
                "effects_volume" => settings with { EffectsVolume = integer(value, index, 0, 100) },
                "voice_volume" => settings with { VoiceVolume = integer(value, index, 0, 100) },
                "audio_offset_ms" => settings with { AudioOffsetMs = integer(value, index, -1000, 1000) },
                "input_offset_ms" => settings with { InputOffsetMs = integer(value, index, -1000, 1000) },
                "audio_buffer_frames" => settings with { AudioBufferFrames = integer(value, index, 16, 8192) },
                "stereo_panning" => settings with { StereoPanning = boolean(value, index) },
                "mute_in_background" => settings with { MuteInBackground = boolean(value, index) },
                "renderer" => settings with { Renderer = value.ToLowerInvariant() switch
                {
                    "auto" => null,
                    "vulkan" or "gles" or "opengl" or "d3d11" or "metal" => value.ToLowerInvariant(),
                    _ => throw new InvalidDataException($"{FileName} line {index}: renderer is auto, vulkan, gles, opengl, d3d11 or metal, not '{value}'."),
                } },
                "mode" => settings with { Home = value.ToLowerInvariant() switch
                {
                    "home" => true,
                    "arcade" => false,
                    _ => throw new InvalidDataException($"{FileName} line {index}: mode is home or arcade, not '{value}'."),
                } },
                _ => unknownSetting($"{FileName} line {index}: unknown setting '{key}'."),
            };
        }
        // A file from a newer build (after a rollback) may hold settings this one does not know.
        if (unknown is not null && settings.Version <= CurrentVersion)
            throw new InvalidDataException(unknown);
        if (settings.CreditsTwoPlayers < settings.CreditsOnePlayer)
            throw new InvalidDataException($"{FileName}: credits_2p must be at least credits_1p.");
        return settings;

        ArcadeSettings unknownSetting(string message)
        {
            unknown ??= message;
            return settings;
        }
    }

    /// <summary>
    /// Writes the settings the in-game menu changes into the file, replacing their lines and keeping
    /// every other line (and the user's comments) as it is.
    /// </summary>
    public static void SaveMenuSettings(string path, ArcadeSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);
        var values = new Dictionary<string, object>
        {
            ["master_volume"] = settings.MasterVolume,
            ["music_volume"] = settings.MusicVolume,
            ["drum_volume"] = settings.DrumVolume,
            ["effects_volume"] = settings.EffectsVolume,
            ["voice_volume"] = settings.VoiceVolume,
            ["audio_offset_ms"] = settings.AudioOffsetMs,
            ["input_offset_ms"] = settings.InputOffsetMs,
            ["audio_buffer_frames"] = settings.AudioBufferFrames,
            ["stereo_panning"] = settings.StereoPanning ? "true" : "false",
            ["mute_in_background"] = settings.MuteInBackground ? "true" : "false",
        };
        var lines = (File.Exists(path) ? File.ReadAllText(path) : DefaultFileText).Split('\n');
        for (var index = 0; index < lines.Length; index++)
            foreach (var (key, value) in values)
                if (isKey(lines[index], key))
                {
                    lines[index] = string.Create(CultureInfo.InvariantCulture, $"{key} = {value}");
                    values.Remove(key);
                    break;
                }
        var text = string.Join('\n', lines);
        if (values.Count != 0)
            text = text.TrimEnd() + "\n" + string.Concat(values.Select(static pair =>
                string.Create(CultureInfo.InvariantCulture, $"{pair.Key} = {pair.Value}\n")));
        // Written beside the file and moved over it: a crash mid-write keeps the old file.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
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

    private static int integer(string value, int line, int minimum, int maximum) =>
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            && number >= minimum && number <= maximum
            ? number
            : throw new InvalidDataException($"{FileName} line {line}: '{value}' must be a whole number from {minimum} to {maximum}.");

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
