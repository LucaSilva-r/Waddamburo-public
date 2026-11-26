using System.Globalization;

namespace Waddamburo.Game.Flow;

/// <summary>Which hits get the early/late glow: none, Goods and Bads, or every hit (Greats too).</summary>
public enum TimingIndicator { Off, GoodBad, Always }

/// <summary>
/// Which hits rumble a controller: none; big notes, rolls, balloons and kusudama; also every small note hit; or every
/// drum hit, a stray one too.
/// </summary>
public enum ControllerVibration { Off, BigNotes, AllNotes, EveryHit }

/// <summary>The on-screen drum on touch screens (config.cfg touch_drum): Auto hides it while a controller is connected
/// or keys are played.</summary>
public enum TouchDrumMode { Auto, On, Off }

/// <summary>A chart's background video (config.cfg background_video): over the backdrops of every layout, under the lane
/// only (one player; the backdrops above it stay), or not shown.</summary>
public enum BackgroundVideoMode { On, UnderLane, Off }

/// <summary>The touch drum's shape (config.cfg touch_drum_layout): one half drum at the bottom, or two don circles the
/// player places (anywhere else is ka, by the screen's side).</summary>
public enum TouchDrumLayout { Whole, Split }

/// <summary>
/// Cabinet settings (the original's service-menu options plus Waddamburo's own), read from
/// config.cfg: <c>key = value</c> lines, <c>#</c> comments. The file carries
/// <c>config_version</c>; an older file gets each later version's settings appended (with their
/// comments and defaults) the next time it is loaded, keeping the user's edits.
/// </summary>
/// <summary>How sound reaches the device (config.cfg audio_output).</summary>
public enum AudioOutputMode
{
    /// <summary>SDL's output, shared with other programs (audio_buffer_frames applies).</summary>
    Shared,
    /// <summary>The device for the game alone (audio_exclusive_period applies): WASAPI exclusive on Windows, a direct ALSA
    /// PCM on Linux (audio_device = its ALSA name, such as plughw:CARD=X,DEV=1).</summary>
    Exclusive,
    /// <summary>Windows: an ASIO driver (asio_driver, asio_buffer_frames).</summary>
    Asio,
}

/// <summary>Picture quality (config.cfg upscale; the Settings menu's Upscaling).</summary>
public enum UpscaleLevel
{
    /// <summary>The basic version of everything: the game drawn at 720p and scaled to the screen.</summary>
    Off,
    /// <summary>Drawn at the screen's resolution: Don-chan and text sharp, the game's textures as they are.</summary>
    Text,
    /// <summary>That, and the game's textures upscaled 3x.</summary>
    Full,
}

public sealed partial record ArcadeSettings
{
    public const string FileName = "config.cfg";

    /// <summary>The config_version this build writes. Bump it with each new entry in <see cref="Additions"/>.</summary>
    public const int CurrentVersion = 28;

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
        # Play Waddamburo's own movies in place of the game's wherever one exists (work in progress).
        free_movies = false

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
        # audio_buffer_frames: the shared output's period, applied at start. Lower = less delay; too low crackles.
        audio_buffer_frames = 256

        """,
        """
        # stereo_panning: two players' drums and cues on their own side (left/right), each cutting off
        # only its own sounds; false = everything centred, one of each sound at a time.
        stereo_panning = true
        # mute_in_background: fade the sound out while the window is not focused.
        mute_in_background = true
        # drum_sound_in_songs: the don/ka sound of your hits during a song (the menus keep theirs). false = silent
        # drum, as the console games' 音なし.
        drum_sound_in_songs = true
        # controller_vibration: off, big (big notes, rolls, balloons and kusudama), notes (every note hit too) or all (every
        # drum hit), on the controllers bound to the player's drum (Settings > Drum Controls).
        controller_vibration = big

        """,
        """
        # renderer: auto lets the GPU backend be picked for the hardware; vulkan, gles, opengl,
        # d3d11 or metal forces one (e.g. gles on a Sandy Bridge laptop whose Vulkan driver is slow).
        # A backend this build or machine lacks falls back to auto.
        renderer = auto

        """,
        """
        # audio_output: shared, exclusive: take the sound device for the game alone (WASAPI exclusive mode on
        # Windows, a direct ALSA PCM on Linux: audio_device = its ALSA name, for cabinets without PipeWire) for
        # the lowest delay; other programs are silent while the game runs; or asio
        # (Windows): an ASIO driver. Recalibrate the offsets after changing it. Falls back to shared
        # output when the device or driver refuses.
        audio_output = shared
        # audio_device: the playback device by name, empty = the system's default.
        audio_device =
        # audio_exclusive_period: exclusive mode's period in frames; 0 = the device's minimum (its default
        # when the minimum proves too short at start), -1 = its default. Raise it when the sound stutters.
        audio_exclusive_period = 0
        # asio_driver: the ASIO driver by name, empty = the first installed. asio_buffer_frames: its buffer,
        # 0 = the driver's preferred size (set in its control panel), else the nearest size it allows.
        asio_driver =
        asio_buffer_frames = 0
        # asio_output_pair: the outputs played on, 0 = 1-2, 1 = 3-4, and so on.
        asio_output_pair = 0

        """,
        """
        # Song folders (home mode; also set from the Settings menu). tja_folder: custom TJA songs, empty =
        # custom_songs beside the game data. nijiiro_folder: a Nijiiro installation, empty = none.
        tja_folder =
        nijiiro_folder =
        # nijiiro_datatable_key / nijiiro_fumen_key: the AES keys Nijiiro's encrypted data tables and charts open with,
        # 64 hex digits each. Waddamburo doesn't carry them: find them yourself. Empty = only unencrypted files open.
        nijiiro_datatable_key =
        nijiiro_fumen_key =

        """,
        """
        # Picture quality (also in the Settings menu). upscale: off = the basic version of everything, the game
        # drawn at 720p and scaled to the screen (lightest, for weak PCs); text = drawn at the screen's
        # resolution (Don-chan and text sharp); full = that and the game's textures upscaled 3x (made on this
        # PC, kept in waddamburo/cache/upscaled). upscale_threads: CPU threads upscaling textures not yet in the
        # cache while the game runs (paused during songs); 0 = off.
        upscale = text
        upscale_threads = 0

        """,
        """
        # squash_titles: song-select titles too long for their column keep their width and are squashed
        # vertically, as in the arcade (true); false shrinks the whole text instead.
        squash_titles = false

        """,
        """
        # Display (also in the Settings menu). vsync: wait for the screen's refresh (no tearing); false
        # renders as fast as it can. frame_latency: frames queued with vsync, 1-3; 2 keeps the notes smooth
        # when the desktop is slow to show frames (screen casting on Linux) at one refresh more of delay. fps_cap: frames per second at most in a window or borderless
        # fullscreen, 0 = no limit. fullscreen_mode (Windows): borderless (a window covering the desktop)
        # or exclusive (the game takes the screen: less load on older PCs, and the resolution and
        # refresh rate below apply; a 4:3 resolution shows the game letterboxed).
        # fullscreen_resolution: WIDTHxHEIGHT or native (the desktop's); refresh_rate in Hz, 0 = the
        # highest at that resolution. The resolution also sizes the upscaled textures loaded.
        vsync = true
        frame_latency = 1
        fps_cap = 0
        fullscreen_mode = borderless
        fullscreen_resolution = native
        refresh_rate = 0
        # Letterboxing, as in osu!: letterbox_size shrinks the game to that percent of the screen (100 =
        # off); letterbox_x and letterbox_y place it in the free space (0 = left/top, 50 = centred).
        letterbox_size = 100
        letterbox_x = 50
        letterbox_y = 50
        # performance_overlay: the frame rate and timings in the top-right corner (F5 shows or hides them
        # until the game closes).
        performance_overlay = false

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
        # pad_menus: gamepad = in the menus a controller's D-pad moves, its bottom button picks, Start opens
        # the menu and its right button goes back, and the pads above count in songs only; drum = a drum that
        # shows up as a controller: the pads above everywhere.
        pad_menus = gamepad
        # fast_song_scroll: in song select the list keeps up with quick rim hits and the mouse wheel, and
        # Page Up/Down or three quick rim hits one way skip ten songs; false = the original, one board
        # at a time.
        fast_song_scroll = true

        """,
        """
        # drum_debounce_ms: for a drum that sends double hits. Every pad ignores a hit this many
        # milliseconds after its last one, which caps rolls: each pad hits at most 1000 / this times a
        # second, a two-hand roll twice that (the Settings menu shows it). 0 = off.
        drum_debounce_ms = 0
        # retry_hold_ms: how long the retry key (Q) is held before the song restarts, the screen fading to
        # black meanwhile. 0 = at once. Home mode only.
        retry_hold_ms = 500
        # record_performance: write a performance report (frame times, CPU and RAM) to the logs folder
        # while the game runs, for bug reports. Settings > Record Performance; off when the game starts unless set here.
        record_performance = false

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
        timing_indicator = off
        # lyrics (also in the Settings menu): a song's lyrics under the lane while it plays (TJA #LYRIC, or the
        # .lrc file its LYRICS: header names).
        lyrics = false
        # Its look (Settings > Game Options > Lyrics): lyrics_x / lyrics_y move it from the bottom centre (stage
        # pixels, 1280x720), lyrics_scale in percent, lyrics_outline the outline's RGB (hex), lyrics_background the
        # band behind it in percent opacity (0 = none).
        lyrics_x = 0
        lyrics_y = 0
        lyrics_scale = 100
        lyrics_outline = 25248E
        lyrics_background = 0

        """,
        """
        # menu_volume (also in the Settings menu, percent): the entry and song select, on top of the master
        # volume; songs and their results play at the master volume.
        menu_volume = 100

        """,
        """
        # The buttons beside the drum (also in Settings > Drum Controls > Buttons): keys by their place and
        # padN:button, separated by ", ". Each does several things, by where you are: back is the menu (Escape
        # too), search also the shin-uchi board and practice's note jump, scores also practice's after-a-pass,
        # practise also favourite, notices also the loop's end, restart (hold) also the leaderboard switch and
        # the loop's start, confirm also play / pause.
        button_back = backspace
        button_search = tab, pad1:back
        button_scores = l, pad1:north
        button_practise = p, pad1:west
        button_notices = e, pad1:right_shoulder
        button_restart = q, pad1:left_shoulder
        button_confirm = return, pad1:south

        """,
        """
        # gameplay_skin (also in the Settings menu): auto = each song's own theme (its SCENEPRESET, the stock
        # song of the same title, miku for Vocaloid, else the regular gameplay); original = the regular gameplay
        # for every song; a skin's name (e.g. miku, imas, toho: docs/development/gameplay-skins.md) = that skin
        # for every song.
        gameplay_skin = auto

        """,
        """
        # mouse_controls (also in the Settings menu): in the menus the mouse wheel moves as the drum's rims
        # do (song select, player entry, difficulties, settings; replays seek) and a left click is a don;
        # false = the mouse does nothing there.
        mouse_controls = true

        """,
        """
        # online_warning_accepted: the player agreed, the first time they opened the online lobby, that playing
        # online shows their IP address to the other player. false = the lobby asks again.
        online_warning_accepted = false

        """,
        """
        # share_warning_accepted: the player confirmed, the first time a song of theirs was sent to another player, that
        # they're authorized to share its chart and audio. false = asked again next time.
        share_warning_accepted = false

        """,
        """
        # attract_volume (also in the Settings menu, percent): the attract loop and its demo plays, on top of the master
        # volume. The menu volume covers the entry and song select.
        attract_volume = 100

        """,
        """
        # stock_folders (also in Settings > Song libraries): other Taiko no Tatsujin releases' game data (an RPCS3 game
        # folder, its USRDIR or USRDIR/data), separated by |. Their songs join the game's own; a song several have comes
        # from the newest release. nijiiro_folder takes several installations the same way.
        stock_folders =

        """,
        """
        # The cabinet's coin switches (also in the arcade service menu, DRUM CONTROLS), bound like the buttons:
        # button_coin inserts a coin; button_service gives a service credit in place of the coins inserted (it isn't
        # counted as a coin in the bookkeeping).
        button_coin = f2
        button_service = f3
        # A card without a card reader: button_card_1p / button_card_2p (unbound until set in DRUM CONTROLS) insert
        # the Banapass access code card_1p / card_2p (20 digits, the card's back or TaikOnline) as if read, starting
        # the entry like a paired card. Anyone who can read this file can play on these cards.
        button_card_1p =
        button_card_2p =
        card_1p =
        card_2p =

        """,
        """
        # button_screenshot (also in Settings > Drum Controls): saves the screen as a PNG in the screenshots folder
        # beside this file.
        button_screenshot = f12

        """,
        """
        # discord_presence (also in the Settings menu): show what you're playing on your Discord profile
        # (the song, its course, who you play against online) while Discord runs on this PC. Home mode only.
        discord_presence = true

        """,
        """
        # random_song_folder (also in the Settings menu): song select gets a spine that flips through song
        # titles and gives a random song each time it's decided (the movie's own roll folder, unused by Green).
        random_song_folder = false

        """,
        """
        # autoplay (also in the Settings menu, home only): every song plays itself, as a replay you can pause, seek and
        # speed up; nothing is saved.
        autoplay = false

        """,
        """
        # arcade_hud (also in the Settings menu, home only): song select's timer, the song number (N曲目) and the
        # free-play line, which mean nothing at home. false = hidden.
        arcade_hud = false

        """,
        """
        # tutorial_seen (home): the how-to-play folder was watched once and is no longer offered. false = it comes back.
        tutorial_seen = false

        """,
        """
        # timer_freeze (also in the arcade service menu, GAME OPTIONS): the menus' timers show but never run out, as
        # the cabinet setting. Home never counts down.
        timer_freeze = false

        """,
        """
        # dev_autoplay (development builds only, Settings > Developer): the player's lane plays itself live, as if the keys
        # were pressed (AI battle's AI-don still plays against it); nothing is saved.
        dev_autoplay = false

        """,
        """
        # dev_no_focus_pause (development builds only, Settings > Developer): leaving the window doesn't pause a song.
        dev_no_focus_pause = false

        """,
        """
        # upload_custom_songs (also in Settings): upload scores, replays and charts for TJA and osu! songs.
        # false = plays made while off stay on this PC permanently. Turning it on applies to new plays.
        # Local replays remain available either way; official songs are unaffected.
        upload_custom_songs = true
        # upload_banner (also in Settings): the "Score upload disabled" line at the top (custom songs, Auto Play).
        upload_banner = true
        # The first custom-library upload question was answered; false = ask on the next entry.
        custom_upload_notice_seen = false
        # The osu! installs found were offered at startup (lazer, stable: none, one or both); false = ask on the next
        # start that finds one not set yet.
        osu_libraries_asked = false

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
        Switch ? "j, pad1:east, pad1:north, pad1:right_stick, midi:36" : "j, pad1:south, pad1:west, pad1:right_stick, midi:36",
        Switch ? "k, pad1:south, pad1:right_shoulder, pad1:right_trigger, midi:37" : "k, pad1:east, pad1:right_shoulder, pad1:right_trigger, midi:37",
        "z, pad2:dpad_left, pad2:left_shoulder, pad2:left_trigger",
        "x, pad2:dpad_down, pad2:dpad_right, pad2:left_stick",
        Switch ? "c, pad2:east, pad2:north, pad2:right_stick" : "c, pad2:south, pad2:west, pad2:right_stick",
        Switch ? "v, pad2:south, pad2:right_shoulder, pad2:right_trigger" : "v, pad2:east, pad2:right_shoulder, pad2:right_trigger",
    ]);

    // A Switch's face buttons arrive by label (its A as south, B east, X west, Y north: menus confirm with A), so its
    // drum and button defaults name them for the same places as an Xbox pad's: don on the bottom and left, ka on the right.
    private static readonly bool Switch = AppContext.TryGetSwitch("Waddamburo.Switch", out var value) && value;

    /// <summary>The buttons beside the drum, in the order of <see cref="Buttons"/> (SdlInputBindings.Buttons).</summary>
    public static readonly string[] ButtonKeys =
        ["button_back", "button_search", "button_scores", "button_practise", "button_notices", "button_restart", "button_confirm",
            "button_coin", "button_service", "button_screenshot", "button_card_1p", "button_card_2p"];

    /// <summary>What presses each button (<see cref="ButtonKeys"/> order), as <see cref="Controls"/> lists a pad's.</summary>
    public EquatableList Buttons { get; init; } = new(
    [
        "backspace",
        "tab, pad1:back",
        Switch ? "l, pad1:west" : "l, pad1:north",
        Switch ? "p, pad1:north" : "p, pad1:west",
        "e, pad1:right_shoulder",
        "q, pad1:left_shoulder",
        "return, pad1:south",
        "f2",
        "f3",
        "f12",
        "",
        "",
    ]);

    /// <summary>The Banapass access codes the card buttons insert (20 digits; null: none).</summary>
    public string? Card1P { get; init; }
    public string? Card2P { get; init; }

    /// <summary>A controller's bound pads also drive the menus (a drum); false: it is a gamepad there.</summary>
    public bool PadMenusAsDrum { get; init; }

    /// <summary>Milliseconds a drum pad ignores hits for after one (a drum that sends double hits); 0 = off.</summary>
    public int DrumDebounceMs { get; init; }

    /// <summary>Milliseconds the retry key is held before a restart (0 to 1000; #26).</summary>
    public int RetryHoldMs { get; init; } = 500;

    /// <summary>A performance report is being written (logs/performance-*.txt).</summary>
    public bool RecordPerformance { get; init; }

    /// <summary>Song select's list keeps up with quick rim hits and the wheel, and skips ten songs at a time.</summary>
    public bool FastSongScroll { get; init; } = true;

    /// <summary>Song select shows Oni at once instead of after repeated right-rim hits (the original).</summary>
    public bool ShowOni { get; init; } = true;

    /// <summary>Song select has the random-song spine (the movie's roll folder; not in the original).</summary>
    public bool RandomSongFolder { get; init; }

    /// <summary>Home: songs play themselves (a reviewed play, never saved).</summary>
    public bool Autoplay { get; init; }

    /// <summary>Home: the arcade's timer, song number and free-play line show.</summary>
    public bool ArcadeHud { get; init; }

    /// <summary>Home: the tutorial was watched once; song select no longer offers it.</summary>
    public bool TutorialSeen { get; init; }

    /// <summary>Arcade: the menus' timers show frozen (the cabinet's timer freeze setting).</summary>
    public bool TimerFreeze { get; init; }

    /// <summary>Development builds: the player's lane is drummed by the bot, played live (not a replay), never saved.</summary>
    public bool DevAutoplay { get; init; }

    /// <summary>Development builds: leaving the window doesn't pause a song.</summary>
    public bool DevNoFocusPause { get; init; }

    /// <summary>
    /// The gameplay skin for every song: "auto" (each song's own theme), "original" (the regular gameplay) or
    /// a skin's name (an enso_ archive, as SCENEPRESET names them).
    /// </summary>
    public string GameplaySkin { get; init; } = "auto";

    /// <summary>In the menus the mouse wheel is the rims (replays: it seeks) and a left click a don.</summary>
    public bool MouseControls { get; init; } = true;

    /// <summary>The on-screen drum and the menus' touch buttons on touch screens.</summary>
    public TouchDrumMode TouchDrum { get; init; } = TouchDrumMode.Auto;

    /// <summary>The on-screen drum face's size, percent of the default (config.cfg touch_drum_size).</summary>
    public int TouchDrumSize { get; init; } = 100;

    public TouchDrumLayout TouchDrumLayout { get; init; } = TouchDrumLayout.Whole;

    /// <summary>The split layout's left and right don circles: centre on the stage (0-1), radius in stage heights.</summary>
    public (float X, float Y, float R) TouchDonLeft { get; init; } = (.3f, .72f, .2f);

    public (float X, float Y, float R) TouchDonRight { get; init; } = (.7f, .72f, .2f);

    /// <summary>Discord shows what the player is doing (home mode, while Discord runs).</summary>
    public bool DiscordPresence { get; init; } = true;

    /// <summary>Upload new custom (TJA and osu!) plays; plays made while disabled stay local permanently.</summary>
    public bool UploadCustomSongs { get; init; } = true;

    /// <summary>Show the "Score upload disabled" banner (custom songs, Auto Play).</summary>
    public bool UploadBanner { get; init; } = true;

    /// <summary>The custom-library upload question has been answered on this installation.</summary>
    public bool CustomUploadNoticeSeen { get; init; }

    /// <summary>The osu! installs found at startup were offered (they are only read once chosen).</summary>
    public bool OsuLibrariesAsked { get; init; }

    /// <summary>Which hits get the early/late glow under the judgement circle.</summary>
    /// <summary>A song's lyrics (TJA #LYRIC or its .lrc) shown while it plays (#45).</summary>
    public bool Lyrics { get; init; }

    /// <summary>A chart's background video (TJA BGMOVIE, osu! Video): where it shows, or off.</summary>
    // On everywhere; a weak phone's player turns it off (software decoding; MediaCodec through FFmpeg was slower still:
    // every frame copied back through Java; a SurfaceTexture straight into bgfx would be the hardware path).
    public BackgroundVideoMode BackgroundVideo { get; init; } = BackgroundVideoMode.On;

    /// <summary>How much the background video is darkened (percent), so the notes stay readable.</summary>
    public int BackgroundVideoDim { get; init; } = 20;

    /// <summary>Don-chan's 3D rendering resolution, percent of the window's (lower on phones, whose GPUs it costs most).</summary>
    public int DonResolution { get; init; } = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() ? 60 : 100;

    /// <summary>The most frames a second a background video shows (30 or 60): 30 halves the decoding on weak CPUs.</summary>
    public int BackgroundVideoFps { get; init; } = 30;

    /// <summary>The lyrics' place from the bottom centre (stage pixels), size (percent), outline (0xRRGGBB) and backing band (percent opacity).</summary>
    public int LyricsX { get; init; }
    public int LyricsY { get; init; }
    public int LyricsScale { get; init; } = 100;
    public int LyricsOutline { get; init; } = 0x25248E;
    public int LyricsBackground { get; init; }

    public TimingIndicator TimingIndicator { get; init; } = TimingIndicator.Off; // new players took the glow for a bug (#43)

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

    /// <summary>Frames queued with vsync (1-3): more absorb a desktop's late presents, each a refresh of delay.</summary>
    public int FrameLatency { get; init; } = 1;

    /// <summary>Frames per second at most, outside exclusive fullscreen; 0 = no limit.</summary>
    public int FpsCap { get; init; }

    /// <summary>The performance overlay (frame rate, timings) from the start; F5 still toggles it for the session.</summary>
    public bool PerformanceOverlay { get; init; }

    /// <summary>The dan dojo series a cabinet offers (home mode picks one in the game).</summary>
    public string DanSeries { get; init; } = "green";

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

    /// <summary>
    /// free_movies: play Waddamburo's own movies in place of the game's wherever one exists
    /// (docs/development/free-movies.md). Off: the game's movies, ours only where the game data has none.
    /// </summary>
    public bool FreeMovies { get; init; }

    /// <summary>
    /// frontend = waddamburo: Waddamburo's own frontend instead of Taiko Green's (docs/development/frontends.md): our
    /// movies always, home rules, no attract loop (boot, then the entry), and only the features it offers (no AI battle,
    /// Waiwai or reward shop).
    /// </summary>
    public bool WaddamburoFrontend { get; init; }

    /// <summary>Our movies in place of the game's: <see cref="FreeMovies"/>, or always on Waddamburo's frontend.</summary>
    public bool OwnMovies => FreeMovies || WaddamburoFrontend;

    /// <summary>Home mode (a PC): free play, endless songs, no countdowns. False: arcade (the cabinet rules).</summary>
    /// <remarks>ponytail: Waddamburo's frontend is home only; a cabinet runs Green's.</remarks>
    public bool Home { get => field || WaddamburoFrontend; init; }

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

    /// <summary>The attract loop and its demo plays, in percent, on top of the master volume.</summary>
    public int AttractVolume { get; init; } = 100;

    /// <summary>Milliseconds the heard music lags the chart clock: notes and judgement move this much later.</summary>
    public int AudioOffsetMs { get; init; }

    /// <summary>Milliseconds the player's hits arrive late: they are judged this much earlier.</summary>
    public int InputOffsetMs { get; init; }

    /// <summary>The shared output's device period requested at start (SDL may choose another).</summary>
    public int AudioBufferFrames { get; init; } = 256;

    /// <summary>Shared (SDL) or, on Windows, WASAPI exclusive output.</summary>
    public AudioOutputMode AudioOutput { get; init; }

    /// <summary>The playback device's name; null = the system's default.</summary>
    public string? AudioDevice { get; init; }

    /// <summary>Exclusive output's period in frames; 0 = the device's minimum (checked at start), -1 = its default.</summary>
    public int AudioExclusivePeriod { get; init; }

    /// <summary>The ASIO driver's name; null = the first installed.</summary>
    public string? AsioDriver { get; init; }

    /// <summary>The ASIO buffer in frames; 0 = the driver's preferred size.</summary>
    public int AsioBufferFrames { get; init; }

    /// <summary>The ASIO outputs played on: 0 = 1-2, 1 = 3-4, and so on.</summary>
    public int AsioOutputPair { get; init; }

    /// <summary>The settings the audio output opens with: a change reopens it (back to the attract, no restart).</summary>
    public (AudioOutputMode, string?, int, int, string?, int, int) AudioOutputSettings =>
        (AudioOutput, AudioDevice, AudioBufferFrames, AudioExclusivePeriod, AsioDriver, AsioBufferFrames, AsioOutputPair);

    /// <summary>Two players' sounds panned to their own side; false: centred, shared.</summary>
    public bool StereoPanning { get; init; } = true;

    /// <summary>Picture quality: the game drawn at 720p, at the screen's resolution, or that with upscaled textures.</summary>
    public UpscaleLevel Upscale { get; init; } = UpscaleLevel.Text;

    /// <summary>Textures shown upscaled from the cache (<see cref="UpscaleLevel.Full"/>).</summary>
    public bool UpscaleTextures => Upscale == UpscaleLevel.Full;

    /// <summary>The stage's drawn resolution in lines (0: the screen's own): 720 at <see cref="UpscaleLevel.Off"/>.</summary>
    public int RenderHeight => Upscale == UpscaleLevel.Off ? 720 : 0;

    /// <summary>CPU threads upscaling missing textures in the background; 0 = off.</summary>
    public int UpscaleThreads { get; init; }

    /// <summary>Long song-select titles squashed vertically at full width (the arcade's way) instead of shrunk.</summary>
    public bool SquashTitles { get; init; }

    /// <summary>Fade the sound out while the window is in the background.</summary>
    public bool MuteInBackground { get; init; } = true;

    /// <summary>The drum's don/ka sound on hits during a song; the menus keep theirs (the console games' 音なし when off).</summary>
    public bool DrumSoundInSongs { get; init; } = true;

    /// <summary>Controllers bound to a player's drum rumble on big notes, rolls, balloons and kusudama.</summary>
    public ControllerVibration ControllerVibration { get; init; } = ControllerVibration.BigNotes;

    /// <summary>The player accepted that playing online shows their IP address to the other player.</summary>
    public bool OnlineWarningAccepted { get; init; }

    /// <summary>The player confirmed they're authorized to share their custom songs' charts and audio with other players.</summary>
    public bool ShareWarningAccepted { get; init; }

    /// <summary>The forced GPU backend (vulkan, gles, opengl, d3d11, metal), or null for auto.</summary>
    public string? Renderer { get; init; }

    /// <summary>The custom TJA library (null: custom_songs beside the game data).</summary>
    public string? TjaFolder { get; init; }

    /// <summary>The player's active gameplay layout (modcharts/layouts/&lt;name&gt;.wddmod); null: the stock screen.</summary>
    public string? Layout { get; init; }

    /// <summary>The active layout for two players (modcharts/layouts-2p); null: the stock screen.</summary>
    public string? Layout2P { get; init; }

    /// <summary>Nijiiro installations, separated by | (null: none); see <see cref="Folders"/>.</summary>
    public string? NijiiroFolder { get; init; }

    /// <summary>The player's keys for Nijiiro's encrypted data tables and charts (64 hex digits each; empty = none).</summary>
    public string NijiiroDatatableKey { get; init; } = "";

    public string NijiiroFumenKey { get; init; } = "";

    /// <summary>Other releases' game data folders, separated by | (null: none); see <see cref="Folders"/>.</summary>
    public string? StockFolders { get; init; }

    /// <summary>The folders a | separated setting names (none for null or empty).</summary>
    public static IReadOnlyList<string> Folders(string? setting) =>
        setting?.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    /// <summary>The setting naming these folders (null for none).</summary>
    public static string? JoinFolders(IEnumerable<string> folders) =>
        string.Join('|', folders.Distinct()) is { Length: > 0 } joined ? joined : null;

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

    /// <summary>An osu!lazer data folder (null: none, the library is off).</summary>
    public string? OsuFolder { get; init; }

    /// <summary>An osu!stable install, its own library beside lazer's (null: none, the library is off).</summary>
    public string? OsuStableFolder { get; init; }

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
        # dan_series: the dan dojo courses a cabinet offers: green, blue, yellow, red, white, murasaki,
        # kimidori, momoiro, sorairo or katsudon. Home mode picks one in the game.
        dan_series = green
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
        // A setting the file has already (the menu saved it before the file was upgraded) is not added again:
        // the later line would win with its default.
        var present = kept.Select(static line => line.Split('#')[0].Split('=')[0].Trim()).Where(static key => key.Length > 0).ToHashSet();
        var added = string.Concat(Additions[version..]).Split('\n')
            .Where(line => line.TrimStart().StartsWith('#') || !present.Contains(line.Split('=')[0].Trim()));
        return string.Join('\n', kept).TrimEnd() + "\n\n"
            + placeholders(string.Join('\n', added), mode)
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
                _ when KeysByName.TryGetValue(key, out var known) => known.Read(settings, value, index),
                _ when Array.IndexOf(ButtonKeys, key) is var button and >= 0 => settings with
                {
                    Buttons = settings.Buttons.With(button, string.Join(", ",
                        value.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))),
                },
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
    /// <remarks><paramref name="cabinet"/>: the arcade service menu's settings too (free play, coins, stages).</remarks>
    /// <remarks><paramref name="mode"/>: the mode is written too (a switch of mode; other saves leave it, as a save during
    /// the switch's restart would put the running mode back).</remarks>
    public static void SaveMenuSettings(string path, ArcadeSettings settings, bool cabinet = false, bool mode = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);
        var values = new Dictionary<string, object>();
        foreach (var key in Keys)
            if (key.Write is { } write && (key.Saved == Saved.Menu || key.Saved == Saved.Cabinet && cabinet || key.Saved == Saved.Mode && mode))
                values[key.Name] = write(settings);
        for (var pad = 0; pad < ControlKeys.Length; pad++)
            values[ControlKeys[pad]] = settings.Controls[pad];
        for (var button = 0; button < ButtonKeys.Length; button++)
            values[ButtonKeys[button]] = settings.Buttons[button];
        var lines = (File.Exists(path) ? File.ReadAllText(path) : DefaultFileText).Split('\n');
        for (var index = 0; index < lines.Length; index++)
            foreach (var (key, value) in values)
                // upscale_textures (on/off) became upscale, audio_exclusive audio_output: their lines are rewritten in place.
                if (isKey(lines[index], key) || KeysByName.TryGetValue(key, out var known) && known.Replaces is { } older && isKey(lines[index], older))
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
