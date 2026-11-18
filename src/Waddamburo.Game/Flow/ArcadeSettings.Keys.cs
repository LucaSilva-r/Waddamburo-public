using System.Globalization;

namespace Waddamburo.Game.Flow;

public sealed partial record ArcadeSettings
{
    // Which save writes a key back: the in-game menu's, the arcade service menu's too, a mode switch's, or none (the
    // file's own: server, token, version...).
    private enum Saved { Menu, Cabinet, Mode, Never }

    /// <summary>
    /// One config.cfg key: how its value sets the settings (with the file's line number for errors), how it is written
    /// back, and the older key whose line a save rewrites in place.
    /// </summary>
    private sealed record Key(string Name, Func<ArcadeSettings, string, int, ArcadeSettings> Read,
        Func<ArcadeSettings, string>? Write, Saved Saved = Saved.Menu, string? Replaces = null);

    private static string format(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static Key flag(string name, Func<ArcadeSettings, bool> get, Func<ArcadeSettings, bool, ArcadeSettings> set,
        Saved saved = Saved.Menu) =>
        new(name, (s, value, line) => set(s, boolean(value, line)), s => get(s) ? "true" : "false", saved);

    private static Key number(string name, Func<ArcadeSettings, int> get, Func<ArcadeSettings, int, ArcadeSettings> set,
        int minimum, int maximum) =>
        new(name, (s, value, line) => set(s, integer(value, line, minimum, maximum)), s => format(get(s)));

    private static Key atLeast(string name, Func<ArcadeSettings, int> get, Func<ArcadeSettings, int, ArcadeSettings> set,
        int minimum, Saved saved) =>
        new(name, (s, value, line) => set(s, count(value, line, minimum)), s => format(get(s)), saved);

    // A folder, a device, a name: empty is none.
    private static Key optional(string name, Func<ArcadeSettings, string?> get, Func<ArcadeSettings, string?, ArcadeSettings> set) =>
        new(name, (s, value, _) => set(s, value.Length == 0 ? null : value), s => get(s) ?? "");

    private static Key text(string name, Func<ArcadeSettings, string> get, Func<ArcadeSettings, string, ArcadeSettings> set) =>
        new(name, (s, value, _) => set(s, value), get);

    // One of a few names (any case); the first name for a value is the one written. Aliases read but are not listed.
    private static Key choice<T>(string name, Func<ArcadeSettings, T> get, Func<ArcadeSettings, T, ArcadeSettings> set,
        (string Name, T Value)[] names, (string Name, T Value)[]? aliases = null, Saved saved = Saved.Menu)
    {
        var listed = names.Length == 1 ? names[0].Name
            : string.Join(", ", names[..^1].Select(static entry => entry.Name)) + " or " + names[^1].Name;
        return new(name, (s, value, line) =>
        {
            var lower = value.ToLowerInvariant();
            foreach (var entry in aliases is null ? names : [.. names, .. aliases])
                if (entry.Name == lower)
                    return set(s, entry.Value);
            throw new InvalidDataException($"{FileName} line {line}: {name} is {listed}, not '{value}'.");
        }, s => names.First(entry => EqualityComparer<T>.Default.Equals(entry.Value, get(s))).Name, saved);
    }

    // The keys in the order a save appends the missing ones (the menu's, the cabinet's, the mode); then the file-only ones.
    private static readonly Key[] Keys =
    [
        number("master_volume", static s => s.MasterVolume, static (s, v) => s with { MasterVolume = v }, 0, 100),
        number("music_volume", static s => s.MusicVolume, static (s, v) => s with { MusicVolume = v }, 0, 100),
        number("drum_volume", static s => s.DrumVolume, static (s, v) => s with { DrumVolume = v }, 0, 100),
        number("effects_volume", static s => s.EffectsVolume, static (s, v) => s with { EffectsVolume = v }, 0, 100),
        number("voice_volume", static s => s.VoiceVolume, static (s, v) => s with { VoiceVolume = v }, 0, 100),
        number("menu_volume", static s => s.MenuVolume, static (s, v) => s with { MenuVolume = v }, 0, 100),
        number("attract_volume", static s => s.AttractVolume, static (s, v) => s with { AttractVolume = v }, 0, 100),
        number("audio_offset_ms", static s => s.AudioOffsetMs, static (s, v) => s with { AudioOffsetMs = v }, -1000, 1000),
        number("input_offset_ms", static s => s.InputOffsetMs, static (s, v) => s with { InputOffsetMs = v }, -1000, 1000),
        number("audio_buffer_frames", static s => s.AudioBufferFrames, static (s, v) => s with { AudioBufferFrames = v }, 16, 8192),
        choice("audio_output", static s => s.AudioOutput, static (s, v) => s with { AudioOutput = v },
            [("shared", AudioOutputMode.Shared), ("exclusive", AudioOutputMode.Exclusive), ("asio", AudioOutputMode.Asio)])
            with { Replaces = "audio_exclusive" },
        optional("audio_device", static s => s.AudioDevice, static (s, v) => s with { AudioDevice = v }),
        number("audio_exclusive_period", static s => s.AudioExclusivePeriod, static (s, v) => s with { AudioExclusivePeriod = v }, -1, 8192),
        optional("asio_driver", static s => s.AsioDriver, static (s, v) => s with { AsioDriver = v }),
        number("asio_buffer_frames", static s => s.AsioBufferFrames, static (s, v) => s with { AsioBufferFrames = v }, 0, 8192),
        number("asio_output_pair", static s => s.AsioOutputPair, static (s, v) => s with { AsioOutputPair = v }, 0, 63),
        flag("stereo_panning", static s => s.StereoPanning, static (s, v) => s with { StereoPanning = v }),
        choice("upscale", static s => s.Upscale, static (s, v) => s with { Upscale = v },
            [("off", UpscaleLevel.Off), ("text", UpscaleLevel.Text), ("full", UpscaleLevel.Full)]) with { Replaces = "upscale_textures" },
        number("upscale_threads", static s => s.UpscaleThreads, static (s, v) => s with { UpscaleThreads = v }, 0, 1024),
        flag("squash_titles", static s => s.SquashTitles, static (s, v) => s with { SquashTitles = v }),
        flag("mute_in_background", static s => s.MuteInBackground, static (s, v) => s with { MuteInBackground = v }),
        flag("drum_sound_in_songs", static s => s.DrumSoundInSongs, static (s, v) => s with { DrumSoundInSongs = v }),
        choice("controller_vibration", static s => s.ControllerVibration, static (s, v) => s with { ControllerVibration = v },
            [("off", ControllerVibration.Off), ("big", ControllerVibration.BigNotes), ("notes", ControllerVibration.AllNotes),
                ("all", ControllerVibration.EveryHit)],
            [("false", ControllerVibration.Off), ("true", ControllerVibration.BigNotes)]),
        flag("online_warning_accepted", static s => s.OnlineWarningAccepted, static (s, v) => s with { OnlineWarningAccepted = v }),
        flag("share_warning_accepted", static s => s.ShareWarningAccepted, static (s, v) => s with { ShareWarningAccepted = v }),
        // ponytail: '#' starts a comment, so a folder path cannot contain one.
        optional("tja_folder", static s => s.TjaFolder, static (s, v) => s with { TjaFolder = v }),
        optional("layout", static s => s.Layout, static (s, v) => s with { Layout = v }),
        optional("layout_2p", static s => s.Layout2P, static (s, v) => s with { Layout2P = v }),
        optional("nijiiro_folder", static s => s.NijiiroFolder, static (s, v) => s with { NijiiroFolder = v }),
        text("nijiiro_datatable_key", static s => s.NijiiroDatatableKey, static (s, v) => s with { NijiiroDatatableKey = v }),
        text("nijiiro_fumen_key", static s => s.NijiiroFumenKey, static (s, v) => s with { NijiiroFumenKey = v }),
        optional("stock_folders", static s => s.StockFolders, static (s, v) => s with { StockFolders = v }),
        optional("osu_folder", static s => s.OsuFolder, static (s, v) => s with { OsuFolder = v }),
        optional("osu_stable_folder", static s => s.OsuStableFolder, static (s, v) => s with { OsuStableFolder = v }),
        optional("cache_folder", static s => s.CacheFolder, static (s, v) => s with { CacheFolder = v }),
        // An unknown grouping or order keeps the current one.
        new("osu_group", static (s, value, _) => s with
        {
            OsuBrowse = s.OsuBrowse with
            {
                Group = Enum.TryParse<SongSelect.SongGroupMode>(value, true, out var group) ? group : s.OsuBrowse.Group,
            },
        }, static s => s.OsuBrowse.Group.ToString().ToLowerInvariant()),
        new("osu_sort", static (s, value, _) => s with
        {
            OsuBrowse = s.OsuBrowse with
            {
                Sort = Enum.TryParse<SongSelect.SongSortMode>(value, true, out var sort) ? sort : s.OsuBrowse.Sort,
            },
        }, static s => s.OsuBrowse.Sort.ToString().ToLowerInvariant()),
        flag("fullscreen", static s => s.Fullscreen, static (s, v) => s with { Fullscreen = v }),
        flag("vsync", static s => s.Vsync, static (s, v) => s with { Vsync = v }),
        number("frame_latency", static s => s.FrameLatency, static (s, v) => s with { FrameLatency = v }, 1, 3),
        number("fps_cap", static s => s.FpsCap, static (s, v) => s with { FpsCap = v }, 0, 10000),
        choice("fullscreen_mode", static s => s.ExclusiveFullscreen, static (s, v) => s with { ExclusiveFullscreen = v },
            [("borderless", false), ("exclusive", true)]),
        new("fullscreen_resolution", static (s, value, line) => resolution(value, line) is var (width, height)
                ? s with { FullscreenWidth = width, FullscreenHeight = height } : s,
            static s => s.FullscreenWidth == 0 ? "native" : $"{format(s.FullscreenWidth)}x{format(s.FullscreenHeight)}"),
        number("refresh_rate", static s => s.RefreshRate, static (s, v) => s with { RefreshRate = v }, 0, 1000),
        number("letterbox_size", static s => s.LetterboxSize, static (s, v) => s with { LetterboxSize = v }, 20, 100),
        number("letterbox_x", static s => s.LetterboxX, static (s, v) => s with { LetterboxX = v }, 0, 100),
        number("letterbox_y", static s => s.LetterboxY, static (s, v) => s with { LetterboxY = v }, 0, 100),
        number("drum_debounce_ms", static s => s.DrumDebounceMs, static (s, v) => s with { DrumDebounceMs = v }, 0, 100),
        number("retry_hold_ms", static s => s.RetryHoldMs, static (s, v) => s with { RetryHoldMs = v }, 0, 1000),
        flag("record_performance", static s => s.RecordPerformance, static (s, v) => s with { RecordPerformance = v }),
        flag("fast_song_scroll", static s => s.FastSongScroll, static (s, v) => s with { FastSongScroll = v }),
        flag("show_oni", static s => s.ShowOni, static (s, v) => s with { ShowOni = v }),
        flag("random_song_folder", static s => s.RandomSongFolder, static (s, v) => s with { RandomSongFolder = v }),
        flag("autoplay", static s => s.Autoplay, static (s, v) => s with { Autoplay = v }),
        flag("arcade_hud", static s => s.ArcadeHud, static (s, v) => s with { ArcadeHud = v }),
        flag("tutorial_seen", static s => s.TutorialSeen, static (s, v) => s with { TutorialSeen = v }),
        flag("timer_freeze", static s => s.TimerFreeze, static (s, v) => s with { TimerFreeze = v }),
        flag("dev_autoplay", static s => s.DevAutoplay, static (s, v) => s with { DevAutoplay = v }),
        flag("dev_no_focus_pause", static s => s.DevNoFocusPause, static (s, v) => s with { DevNoFocusPause = v }),
        flag("mouse_controls", static s => s.MouseControls, static (s, v) => s with { MouseControls = v }),
        choice("touch_drum", static s => s.TouchDrum, static (s, v) => s with { TouchDrum = v },
            [("auto", TouchDrumMode.Auto), ("on", TouchDrumMode.On), ("off", TouchDrumMode.Off)],
            [("true", TouchDrumMode.On), ("false", TouchDrumMode.Off)]),
        flag("custom_upload_notice_seen", static s => s.CustomUploadNoticeSeen, static (s, v) => s with { CustomUploadNoticeSeen = v }),
        flag("upload_custom_songs", static s => s.UploadCustomSongs, static (s, v) => s with { UploadCustomSongs = v }),
        flag("upload_banner", static s => s.UploadBanner, static (s, v) => s with { UploadBanner = v }),
        flag("discord_presence", static s => s.DiscordPresence, static (s, v) => s with { DiscordPresence = v }),
        new("gameplay_skin", static (s, value, _) => s with { GameplaySkin = value.Length > 0 ? value : "auto" }, static s => s.GameplaySkin),
        choice("timing_indicator", static s => s.TimingIndicator, static (s, v) => s with { TimingIndicator = v },
            [("off", TimingIndicator.Off), ("good_bad", TimingIndicator.GoodBad), ("always", TimingIndicator.Always)]),
        choice("pad_menus", static s => s.PadMenusAsDrum, static (s, v) => s with { PadMenusAsDrum = v },
            [("gamepad", false), ("drum", true)]),
        new("language", static (s, value, _) => s with { Language = value.Length > 0 ? value.ToLowerInvariant() : SystemLanguage },
            static s => s.Language),
        choice("title_language", static s => s.EnglishTitles, static (s, v) => s with { EnglishTitles = v },
            [("english", true), ("japanese", false)]),

        // The arcade service menu's.
        flag("free_play", static s => s.FreePlay, static (s, v) => s with { FreePlay = v }, Saved.Cabinet),
        atLeast("credits_per_coin", static s => s.CreditsPerCoin, static (s, v) => s with { CreditsPerCoin = v }, 1, Saved.Cabinet),
        atLeast("credits_1p", static s => s.CreditsOnePlayer, static (s, v) => s with { CreditsOnePlayer = v }, 1, Saved.Cabinet),
        atLeast("credits_2p", static s => s.CreditsTwoPlayers, static (s, v) => s with { CreditsTwoPlayers = v }, 1, Saved.Cabinet),
        atLeast("songs_per_session", static s => s.SongsPerSession, static (s, v) => s with { SongsPerSession = v }, 1, Saved.Cabinet),

        choice("mode", static s => s.Home, static (s, v) => s with { Home = v }, [("home", true), ("arcade", false)], saved: Saved.Mode),

        // The file's own: never saved by the game.
        new("server", static (s, value, line) => s with { Server = value.Length == 0 ? null : server(value, line) }, null, Saved.Never),
        new("server_insecure", static (s, value, line) => s with { ServerInsecure = boolean(value, line) }, null, Saved.Never),
        new("cabinet_token", static (s, value, _) => s with { CabinetToken = value.Length == 0 ? null : value }, null, Saved.Never),
        new("config_version", static (s, value, line) => s with { Version = count(value, line, 0) }, null, Saved.Never),
        new("dan_series", static (s, value, _) => s with { DanSeries = value }, null, Saved.Never),
        new("console", static (s, value, line) => s with { ShowConsole = boolean(value, line) }, null, Saved.Never),
        new("auto_update", static (s, value, line) => s with { AutoUpdate = boolean(value, line) }, null, Saved.Never),
        new("renderer", static (s, value, line) => s with
        {
            Renderer = value.ToLowerInvariant() switch
            {
                "auto" => null,
                "vulkan" or "gles" or "opengl" or "d3d11" or "metal" => value.ToLowerInvariant(),
                _ => throw new InvalidDataException($"{FileName} line {line}: renderer is auto, vulkan, gles, opengl, d3d11 or metal, not '{value}'."),
            },
        }, null, Saved.Never),
        // Before audio_output (true = exclusive), and before upscale (on or off; off kept the game drawn at the screen's
        // resolution): a save rewrites their lines as the new keys.
        new("audio_exclusive", static (s, value, line) => boolean(value, line) ? s with { AudioOutput = AudioOutputMode.Exclusive } : s,
            null, Saved.Never),
        new("upscale_textures", static (s, value, line) => s with { Upscale = boolean(value, line) ? UpscaleLevel.Full : UpscaleLevel.Text },
            null, Saved.Never),
    ];

    private static readonly Dictionary<string, Key> KeysByName = Keys.ToDictionary(static key => key.Name);
}
