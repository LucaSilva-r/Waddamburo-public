using System.Globalization;

namespace Waddamburo.Game.Flow;

/// <summary>Which hits get the early/late glow: none, Goods and Bads, or every hit (Greats too).</summary>
public enum TimingIndicator { Off, GoodBad, Always }

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
    public const int CurrentVersion = 17;

    // Version 2's mode for a file written before it: a cabinet (token, or coins) stays arcade.
    private const string ModePlaceholder = "{mode}";

    // Version 14's languages: the system's (Japanese or English), for the menus and the song titles alike.
    private const string LanguagePlaceholder = "{language}", TitleLanguagePlaceholder = "{title_language}";

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
        // Version 3's title_language moved to version 14 (which replaces it with the system's language).
        "",
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
        """
        # audio_exclusive (Windows): take the sound device for the game alone (WASAPI exclusive mode)
        # for the lowest delay; other programs are silent while the game runs. Recalibrate the offsets
        # after changing it. Falls back to shared output when the device refuses.
        audio_exclusive = false

        """,
        """
        # Song folders (home mode; also set from the Settings menu). tja_folder: custom TJA songs, empty =
        # custom_songs beside the game data. nijiiro_folder: a Nijiiro installation, empty = none.
        tja_folder =
        nijiiro_folder =

        """,
        """
        # Texture upscaling (also in the Settings menu). upscale_textures: show the game's textures upscaled
        # 3x (made on this PC, kept in waddamburo/cache/upscaled). upscale_threads: CPU threads upscaling
        # textures not yet in the cache while the game runs (paused during songs); 0 = off.
        upscale_textures = false
        upscale_threads = 0

        """,
        """
        # squash_titles: song-select titles too long for their column keep their width and are squashed
        # vertically, as in the arcade (true); false shrinks the whole text instead.
        squash_titles = false

        """,
        """
        # Display (also in the Settings menu). vsync: wait for the screen's refresh (no tearing); false
        # renders as fast as it can. fps_cap: frames per second at most in a window or borderless
        # fullscreen, 0 = no limit. fullscreen_mode (Windows): borderless (a window covering the desktop)
        # or exclusive (the game takes the screen: less load on older PCs, and the resolution and
        # refresh rate below apply; a 4:3 resolution shows the game letterboxed).
        # fullscreen_resolution: WIDTHxHEIGHT or native (the desktop's); refresh_rate in Hz, 0 = the
        # highest at that resolution. The resolution also sizes the upscaled textures loaded.
        vsync = true
        fps_cap = 0
        fullscreen_mode = borderless
        fullscreen_resolution = native
        refresh_rate = 0
        # Letterboxing, as in osu!: letterbox_size shrinks the game to that percent of the screen (100 =
        # off); letterbox_x and letterbox_y place it in the free space (0 = left/top, 50 = centred).
        letterbox_size = 100
        letterbox_x = 50
        letterbox_y = 50

        """,
        """
        # Drum controls (also in the Settings menu): what hits each pad, separated by commas, any number
        # per pad. A key is named by its place on a US keyboard (d, semicolon, kp_1), whatever yours prints
        # there. pad1: to pad4: = a button of the first to fourth controller connected (dpad_left, south,
        # east, west, north, left_shoulder, left_trigger, left_stick, ...). midi:<note> = a MIDI drum's note.
        p1_left_ka = d, pad1:dpad_left, pad1:left_shoulder, pad1:left_trigger, midi:31
        p1_left_don = f, pad1:dpad_down, pad1:dpad_right, pad1:left_stick, midi:35
        p1_right_don = j, pad1:south, pad1:west, pad1:right_stick, midi:36
        p1_right_ka = k, pad1:east, pad1:right_shoulder, pad1:right_trigger, midi:37
        p2_left_ka = z, pad2:dpad_left, pad2:left_shoulder, pad2:left_trigger
        p2_left_don = x, pad2:dpad_down, pad2:dpad_right, pad2:left_stick
        p2_right_don = c, pad2:south, pad2:west, pad2:right_stick
        p2_right_ka = v, pad2:east, pad2:right_shoulder, pad2:right_trigger
        # pad_menus: gamepad = in the menus a controller's D-pad moves, its bottom button picks and its
        # right button or Start is Escape, and the pads above count in songs only; drum = a drum that
        # shows up as a controller: the pads above everywhere.
        pad_menus = gamepad
        # fast_song_scroll: in song select the list keeps up with quick rim hits and the mouse wheel, and
        # Page Up/Down or three quick rim hits one way skip ten songs; false = the original, one board
        # at a time.
        fast_song_scroll = false

        """,
        """
        # drum_debounce_ms: for a drum that sends double hits. Every pad ignores a hit this many
        # milliseconds after its last one, which caps rolls: each pad hits at most 1000 / this times a
        # second, a two-hand roll twice that (the Settings menu shows it). 0 = off.
        drum_debounce_ms = 0

        """,
        """
        # The game's menus and messages: en (English) or ja (Japanese). First set from the system's language.
        language = {language}
        # Song titles in english (translated, when known) or japanese (the original).
        title_language = {title_language}

        """,
        """
        # show_oni (also in the Settings menu): song select shows the Oni course at once; false = the
        # original, where Oni appears after hitting the right rim repeatedly on the course select.
        show_oni = true

        """,
        """
        # cache_folder (also in the Settings menu): where decoded songs (vgmstream) and upscaled textures
        # are kept. user (or empty) = this PC's own cache folder (%LOCALAPPDATA%\Waddamburo on Windows,
        # ~/.cache/Waddamburo on Linux), found again on any PC the game is started from, so a game on a
        # USB stick never writes its cache to the stick; game = waddamburo/cache beside the game data;
        # or any folder path. A cache left in waddamburo/cache moves to the chosen one at start.
        cache_folder = user

        """,
        """
        # timing_indicator (also in the Settings menu): a glow under the judgement circle after a hit,
        # blue when early, red when late. good_bad = after a Good or Bad only; always = after every hit
        # (Greats too); off.
        timing_indicator = good_bad

        """,
        """
        # menu_volume (also in the Settings menu, percent): the attract, the entry and song select, on top
        # of the master volume; songs and their results play at the master volume.
        menu_volume = 100

        """,
    ];

    /// <summary>The drum pads' settings, in the order of <see cref="Controls"/>.</summary>
    public static readonly string[] ControlKeys =
    [
        "p1_left_ka", "p1_left_don", "p1_right_don", "p1_right_ka",
        "p2_left_ka", "p2_left_don", "p2_right_don", "p2_right_ka",
    ];

    /// <summary>
    /// What hits each drum pad (<see cref="ControlKeys"/> order): inputs separated by ", " (a key by its
    /// place, padN:button, midi:note). The platform layer reads them.
    /// </summary>
    public EquatableList Controls { get; init; } = new(
    [
        "d, pad1:dpad_left, pad1:left_shoulder, pad1:left_trigger, midi:31",
        "f, pad1:dpad_down, pad1:dpad_right, pad1:left_stick, midi:35",
        "j, pad1:south, pad1:west, pad1:right_stick, midi:36",
        "k, pad1:east, pad1:right_shoulder, pad1:right_trigger, midi:37",
        "z, pad2:dpad_left, pad2:left_shoulder, pad2:left_trigger",
        "x, pad2:dpad_down, pad2:dpad_right, pad2:left_stick",
        "c, pad2:south, pad2:west, pad2:right_stick",
        "v, pad2:east, pad2:right_shoulder, pad2:right_trigger",
    ]);

    /// <summary>A controller's bound pads also drive the menus (a drum); false: it is a gamepad there.</summary>
    public bool PadMenusAsDrum { get; init; }

    /// <summary>Milliseconds a drum pad ignores hits for after one (a drum that sends double hits); 0 = off.</summary>
    public int DrumDebounceMs { get; init; }

    /// <summary>Song select's list keeps up with quick rim hits and the wheel, and skips ten songs at a time.</summary>
    public bool FastSongScroll { get; init; }

    /// <summary>Song select shows Oni at once instead of after repeated right-rim hits (the original).</summary>
    public bool ShowOni { get; init; } = true;

    /// <summary>Which hits get the early/late glow under the judgement circle.</summary>
    public TimingIndicator TimingIndicator { get; init; } = TimingIndicator.GoodBad;

    /// <summary>A list of strings compared by content, so the settings record still compares by value.</summary>
    public sealed class EquatableList(IEnumerable<string> items) : IReadOnlyList<string>, IEquatable<EquatableList>
    {
        private readonly string[] _items = [.. items];

        public string this[int index] => _items[index];

        public int Count => _items.Length;

        /// <summary>The list with one entry replaced.</summary>
        public EquatableList With(int index, string item) => new(_items.Select((old, at) => at == index ? item : old));

        public bool Equals(EquatableList? other) => other is not null && _items.SequenceEqual(other._items);

        public override bool Equals(object? obj) => Equals(obj as EquatableList);

        public override int GetHashCode() => _items.Aggregate(0, static (hash, item) => HashCode.Combine(hash, item));

        public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)_items).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }

    /// <summary>The file's config_version (0: written before versioning).</summary>
    public int Version { get; init; }

    public bool Fullscreen { get; init; }

    /// <summary>Wait for the display's refresh before showing a frame.</summary>
    public bool Vsync { get; init; } = true;

    /// <summary>Frames per second at most, outside exclusive fullscreen; 0 = no limit.</summary>
    public int FpsCap { get; init; }

    /// <summary>Exclusive fullscreen (the resolution and refresh rate apply) instead of borderless.</summary>
    public bool ExclusiveFullscreen { get; init; }

    /// <summary>Exclusive fullscreen's resolution; 0 x 0 = the desktop's.</summary>
    public int FullscreenWidth { get; init; }

    public int FullscreenHeight { get; init; }

    /// <summary>Exclusive fullscreen's refresh rate in Hz; 0 = the highest at its resolution.</summary>
    public int RefreshRate { get; init; }

    /// <summary>Percent of the screen the game fills (100 = no letterbox).</summary>
    public int LetterboxSize { get; init; } = 100;

    /// <summary>The letterboxed game's place in the free space, percent (50 = centred).</summary>
    public int LetterboxX { get; init; } = 50;

    public int LetterboxY { get; init; } = 50;

    public bool ShowConsole { get; init; }

    public bool AutoUpdate { get; init; } = true;

    /// <summary>Home mode (a PC): free play, endless songs, no countdowns. False: arcade (the cabinet rules).</summary>
    public bool Home { get; init; }

    public bool FreePlay { get; init; } = true;

    /// <summary>Song titles and subtitles in English when a song has them; false: the Japanese originals.</summary>
    public bool EnglishTitles { get; init; } = SystemLanguage != "ja";

    /// <summary>The menus' language code ("en", "ja"); "auto" follows the system's at each start.</summary>
    public string Language { get; init; } = SystemLanguage;

    /// <summary>"ja" on a Japanese system, else "en": the languages Waddamburo and the song titles both have.</summary>
    public static string SystemLanguage => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja" ? "ja" : "en";

    public int MasterVolume { get; init; } = 50;

    public int MusicVolume { get; init; } = 100;

    public int DrumVolume { get; init; } = 100;

    public int EffectsVolume { get; init; } = 100;

    public int VoiceVolume { get; init; } = 100;

    /// <summary>The attract, the entry and Song Select play at this much of the master volume (songs and results at all of it).</summary>
    public int MenuVolume { get; init; } = 100;

    /// <summary>Milliseconds the heard music lags the chart clock: notes and judgement move this much later.</summary>
    public int AudioOffsetMs { get; init; }

    /// <summary>Milliseconds the player's hits arrive late: they are judged this much earlier.</summary>
    public int InputOffsetMs { get; init; }

    /// <summary>The audio device period requested at start (SDL may choose another).</summary>
    public int AudioBufferFrames { get; init; } = 256;

    /// <summary>Windows: WASAPI exclusive output at the device's minimum period, instead of SDL's shared output.</summary>
    public bool AudioExclusive { get; init; }

    /// <summary>Two players' sounds panned to their own side; false: centred, shared.</summary>
    public bool StereoPanning { get; init; } = true;

    /// <summary>Show textures upscaled from the cache (opt-in).</summary>
    public bool UpscaleTextures { get; init; }

    /// <summary>CPU threads upscaling missing textures in the background; 0 = off.</summary>
    public int UpscaleThreads { get; init; }

    /// <summary>Long song-select titles squashed vertically at full width (the arcade's way) instead of shrunk.</summary>
    public bool SquashTitles { get; init; }

    /// <summary>Fade the sound out while the window is in the background.</summary>
    public bool MuteInBackground { get; init; } = true;

    /// <summary>The forced GPU backend (vulkan, gles, opengl, d3d11, metal), or null for auto.</summary>
    public string? Renderer { get; init; }

    /// <summary>The custom TJA library (null: custom_songs beside the game data).</summary>
    public string? TjaFolder { get; init; }

    /// <summary>A Nijiiro installation (null: none).</summary>
    public string? NijiiroFolder { get; init; }

    /// <summary>
    /// The cache folder for decoded songs and upscaled textures: null or <see cref="UserCacheValue"/> =
    /// the running user's cache folder on this PC (the default: a game on a USB stick keeps its cache off
    /// the stick), <see cref="GameCacheValue"/> = waddamburo/cache in the game data, else a path.
    /// </summary>
    public string? CacheFolder { get; init; } = UserCacheValue;

    public const string UserCacheValue = "user";

    public const string GameCacheValue = "game";

    public static bool IsUserCache(string? value) =>
        value is null || value.Length == 0 || string.Equals(value, UserCacheValue, StringComparison.OrdinalIgnoreCase);

    public static bool IsGameCache(string? value) => string.Equals(value, GameCacheValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>The running user's cache folder on this PC: %LOCALAPPDATA%\Waddamburo, or $XDG_CACHE_HOME (~/.cache)/Waddamburo.</summary>
    public static string UserCacheFolder => Path.Combine(OperatingSystem.IsWindows()
        ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        : Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg ? xdg
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
        "Waddamburo");

    /// <summary>The cache folder in use, given Waddamburo's home folder (USRDIR/waddamburo).</summary>
    public static string CacheRoot(string home, string? cacheFolder) =>
        IsUserCache(cacheFolder) ? UserCacheFolder
        : IsGameCache(cacheFolder) ? Path.Combine(home, "cache")
        : Path.GetFullPath(cacheFolder!);

    /// <summary>An osu!lazer data folder (null: the default install's, when there is one).</summary>
    public string? OsuFolder { get; init; }

    /// <summary>The osu!lazer library's folder grouping and song order (its Group and Sort spines change them).</summary>
    public SongSelect.SongBrowse OsuBrowse { get; init; } = new();

    public int CreditsPerCoin { get; init; } = 1;

    /// <summary>Credits one player needs to start a session.</summary>
    public int CreditsOnePlayer { get; init; } = 1;

    /// <summary>Credits two players need together (the second player pays the difference).</summary>
    public int CreditsTwoPlayers { get; init; } = 2;

    public int SongsPerSession { get; init; } = 2;

    /// <summary>The official TaikOnline server, used unless config.cfg names another or none.</summary>
    public static readonly Uri DefaultServer = new("https://taikonline.com/");

    /// <summary>The TaikOnline server scores go to (null: offline, scores stay local).</summary>
    public Uri? Server { get; init; } = DefaultServer;

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
        # TaikOnline server for scores (log in with --login). Left out = https://taikonline.com;
        # an empty value (server =) plays offline, scores stay local.
        # server = https://taikonline.com
        # server_insecure: true accepts a self-signed certificate (local servers only).
        # server_insecure = false
        # cabinet_token: a cabinet token from the server admin; players then log in with a 6-digit code.
        # cabinet_token =

        """ + placeholders(string.Concat(Additions), "home");

    private static string placeholders(string text, string mode) => text.Replace(ModePlaceholder, mode)
        .Replace(LanguagePlaceholder, SystemLanguage).Replace(TitleLanguagePlaceholder, SystemLanguage == "ja" ? "japanese" : "english");

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
        // Version 14 replaces the older title_language (and its comment) with the system's language.
        var kept = text.Split('\n').Where(line => !isKey(line, "config_version")
            && !(version < 14 && (isKey(line, "title_language") || line.Trim() == TitleLanguageComment)));
        var mode = settings.CabinetToken is not null || !settings.FreePlay ? "arcade" : "home";
        return string.Join('\n', kept).TrimEnd() + "\n\n"
            + placeholders(string.Concat(Additions[version..]), mode)
            + $"config_version = {CurrentVersion}\n";
    }

    private const string TitleLanguageComment = "# Song titles in english (translated, when known) or japanese (the original).";

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
                "server" => settings with { Server = value.Length == 0 ? null : server(value, index) },
                "server_insecure" => settings with { ServerInsecure = boolean(value, index) },
                "cabinet_token" => settings with { CabinetToken = value.Length == 0 ? null : value },
                "config_version" => settings with { Version = count(value, index, 0) },
                "fullscreen" => settings with { Fullscreen = boolean(value, index) },
                "vsync" => settings with { Vsync = boolean(value, index) },
                "fps_cap" => settings with { FpsCap = integer(value, index, 0, 10000) },
                "fullscreen_mode" => settings with { ExclusiveFullscreen = value.ToLowerInvariant() switch
                {
                    "borderless" => false,
                    "exclusive" => true,
                    _ => throw new InvalidDataException($"{FileName} line {index}: fullscreen_mode is borderless or exclusive, not '{value}'."),
                } },
                "fullscreen_resolution" => resolution(value, index) is var (width, height)
                    ? settings with { FullscreenWidth = width, FullscreenHeight = height } : settings,
                "refresh_rate" => settings with { RefreshRate = integer(value, index, 0, 1000) },
                "letterbox_size" => settings with { LetterboxSize = integer(value, index, 20, 100) },
                "letterbox_x" => settings with { LetterboxX = integer(value, index, 0, 100) },
                "letterbox_y" => settings with { LetterboxY = integer(value, index, 0, 100) },
                "console" => settings with { ShowConsole = boolean(value, index) },
                "auto_update" => settings with { AutoUpdate = boolean(value, index) },
                "title_language" => settings with { EnglishTitles = value.ToLowerInvariant() switch
                {
                    "english" => true,
                    "japanese" => false,
                    _ => throw new InvalidDataException($"{FileName} line {index}: title_language is english or japanese, not '{value}'."),
                } },
                "language" => settings with { Language = value.Length > 0 ? value.ToLowerInvariant() : SystemLanguage },
                "master_volume" => settings with { MasterVolume = integer(value, index, 0, 100) },
                "music_volume" => settings with { MusicVolume = integer(value, index, 0, 100) },
                "drum_volume" => settings with { DrumVolume = integer(value, index, 0, 100) },
                "effects_volume" => settings with { EffectsVolume = integer(value, index, 0, 100) },
                "voice_volume" => settings with { VoiceVolume = integer(value, index, 0, 100) },
                "menu_volume" => settings with { MenuVolume = integer(value, index, 0, 100) },
                "audio_offset_ms" => settings with { AudioOffsetMs = integer(value, index, -1000, 1000) },
                "input_offset_ms" => settings with { InputOffsetMs = integer(value, index, -1000, 1000) },
                "audio_buffer_frames" => settings with { AudioBufferFrames = integer(value, index, 16, 8192) },
                "audio_exclusive" => settings with { AudioExclusive = boolean(value, index) },
                "stereo_panning" => settings with { StereoPanning = boolean(value, index) },
                "upscale_textures" => settings with { UpscaleTextures = boolean(value, index) },
                "upscale_threads" => settings with { UpscaleThreads = integer(value, index, 0, 1024) },
                "squash_titles" => settings with { SquashTitles = boolean(value, index) },
                "mute_in_background" => settings with { MuteInBackground = boolean(value, index) },
                // ponytail: '#' starts a comment, so a folder path cannot contain one.
                "tja_folder" => settings with { TjaFolder = value.Length == 0 ? null : value },
                "nijiiro_folder" => settings with { NijiiroFolder = value.Length == 0 ? null : value },
                "osu_folder" => settings with { OsuFolder = value.Length == 0 ? null : value },
                "cache_folder" => settings with { CacheFolder = value.Length == 0 ? null : value },
                "osu_group" => settings with { OsuBrowse = settings.OsuBrowse with
                {
                    Group = Enum.TryParse<SongSelect.SongGroupMode>(value, true, out var group) ? group : settings.OsuBrowse.Group,
                } },
                "osu_sort" => settings with { OsuBrowse = settings.OsuBrowse with
                {
                    Sort = Enum.TryParse<SongSelect.SongSortMode>(value, true, out var sort) ? sort : settings.OsuBrowse.Sort,
                } },
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
                "drum_debounce_ms" => settings with { DrumDebounceMs = integer(value, index, 0, 100) },
                "fast_song_scroll" => settings with { FastSongScroll = boolean(value, index) },
                "show_oni" => settings with { ShowOni = boolean(value, index) },
                "timing_indicator" => settings with { TimingIndicator = value.ToLowerInvariant() switch
                {
                    "off" => TimingIndicator.Off,
                    "good_bad" => TimingIndicator.GoodBad,
                    "always" => TimingIndicator.Always,
                    _ => throw new InvalidDataException($"{FileName} line {index}: timing_indicator is off, good_bad or always, not '{value}'."),
                } },
                "pad_menus" => settings with { PadMenusAsDrum = value.ToLowerInvariant() switch
                {
                    "gamepad" => false,
                    "drum" => true,
                    _ => throw new InvalidDataException($"{FileName} line {index}: pad_menus is gamepad or drum, not '{value}'."),
                } },
                _ when Array.IndexOf(ControlKeys, key) is var pad and >= 0 => settings with
                {
                    Controls = settings.Controls.With(pad, string.Join(", ",
                        value.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))),
                },
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
            ["menu_volume"] = settings.MenuVolume,
            ["audio_offset_ms"] = settings.AudioOffsetMs,
            ["input_offset_ms"] = settings.InputOffsetMs,
            ["audio_buffer_frames"] = settings.AudioBufferFrames,
            ["audio_exclusive"] = settings.AudioExclusive ? "true" : "false",
            ["stereo_panning"] = settings.StereoPanning ? "true" : "false",
            ["upscale_textures"] = settings.UpscaleTextures ? "true" : "false",
            ["upscale_threads"] = settings.UpscaleThreads,
            ["squash_titles"] = settings.SquashTitles ? "true" : "false",
            ["mute_in_background"] = settings.MuteInBackground ? "true" : "false",
            ["tja_folder"] = settings.TjaFolder ?? "",
            ["nijiiro_folder"] = settings.NijiiroFolder ?? "",
            ["osu_folder"] = settings.OsuFolder ?? "",
            ["cache_folder"] = settings.CacheFolder ?? "",
            ["osu_group"] = settings.OsuBrowse.Group.ToString().ToLowerInvariant(),
            ["osu_sort"] = settings.OsuBrowse.Sort.ToString().ToLowerInvariant(),
            ["fullscreen"] = settings.Fullscreen ? "true" : "false",
            ["vsync"] = settings.Vsync ? "true" : "false",
            ["fps_cap"] = settings.FpsCap,
            ["fullscreen_mode"] = settings.ExclusiveFullscreen ? "exclusive" : "borderless",
            ["fullscreen_resolution"] = settings.FullscreenWidth == 0 ? "native" : $"{settings.FullscreenWidth}x{settings.FullscreenHeight}",
            ["refresh_rate"] = settings.RefreshRate,
            ["letterbox_size"] = settings.LetterboxSize,
            ["letterbox_x"] = settings.LetterboxX,
            ["letterbox_y"] = settings.LetterboxY,
            ["drum_debounce_ms"] = settings.DrumDebounceMs,
            ["fast_song_scroll"] = settings.FastSongScroll ? "true" : "false",
            ["show_oni"] = settings.ShowOni ? "true" : "false",
            ["timing_indicator"] = settings.TimingIndicator switch
            {
                TimingIndicator.Off => "off",
                TimingIndicator.Always => "always",
                _ => "good_bad",
            },
            ["pad_menus"] = settings.PadMenusAsDrum ? "drum" : "gamepad",
            ["language"] = settings.Language,
            ["title_language"] = settings.EnglishTitles ? "english" : "japanese",
        };
        for (var pad = 0; pad < ControlKeys.Length; pad++)
            values[ControlKeys[pad]] = settings.Controls[pad];
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

    private static (int Width, int Height) resolution(string value, int line) =>
        value.Equals("native", StringComparison.OrdinalIgnoreCase) ? (0, 0)
        : value.Split('x', 'X') is [var width, var height]
            && int.TryParse(width, NumberStyles.None, CultureInfo.InvariantCulture, out var w) && w is >= 320 and <= 16384
            && int.TryParse(height, NumberStyles.None, CultureInfo.InvariantCulture, out var h) && h is >= 200 and <= 16384
            ? (w, h)
            : throw new InvalidDataException($"{FileName} line {line}: fullscreen_resolution is native or WIDTHxHEIGHT, not '{value}'.");

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
