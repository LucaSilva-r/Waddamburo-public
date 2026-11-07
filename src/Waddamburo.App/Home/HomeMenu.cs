using System.Diagnostics;
using System.Globalization;
using Waddamburo.Game.Flow;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Providers.Stock;
using static Waddamburo.App.Strings;

namespace Waddamburo.App.Home;

internal enum HomeMenuAction
{
    None,
    Resume,
    Restart,
    /// <summary>Review the song played so far (the play is void).</summary>
    InstantReplay,
    /// <summary>Report the watched replay's player to the moderators (the replay carries on).</summary>
    Report,
    SongSelect,
    Title,
    /// <summary>Close the game (home menus outside a song, the attract's demo play included).</summary>
    Quit,
    /// <summary>Leave the online room, back to the usual Song Select.</summary>
    LeaveRoom,
    /// <summary>Start upscaling every texture the game uses (the bake page shows it).</summary>
    BakeTextures,
    /// <summary>Close the game and start it again (settings that apply at start, memory after a bake).</summary>
    RestartGame,
    /// <summary>Start the audio calibration (the calibration page shows it).</summary>
    Calibrate,
    /// <summary>The calibration page closed (saved or not): its sound stops.</summary>
    EndCalibration,
    /// <summary>Open the settings overlay (the service menu's home pages) over the pause choices.</summary>
    Settings,
    /// <summary>Start the game again into the benchmark (it starts again as usual after).</summary>
    RunBenchmark,
}

/// <summary>
/// Home mode's Escape menu, behind the overlay that draws it (<see cref="OverlayMenu"/>): which choices there are (a
/// song's pause, the entry's and Song Select's, the attract's, online) and what each does (<see cref="Pick"/>,
/// <see cref="Back"/>), the restart and report prompts shown in their place, the texture bake's and the calibration's
/// pages; and the settings' definitions (<see cref="Sections"/>) that the settings overlay shows.
/// </summary>
internal sealed class HomeMenu(Func<ArcadeSettings> get, Action<ArcadeSettings> apply, Action save, Action<bool> drum)
{
    private sealed record Setting(string Label, Func<ArcadeSettings, int> Get, Func<ArcadeSettings, int, ArcadeSettings> Set,
        Func<int, int, int> Step, int Minimum, Func<int, string> Format, int Maximum, AudioBus? Bus = null, string Hint = "")
    {
        /// <summary>The shown value when it depends on other settings (replaces <see cref="Format"/>).</summary>
        public Func<ArcadeSettings, string>? FormatFor { get; init; }
    }

    // One settings row: a section header or a setting; Shown: whether it applies to the current settings (null: always).
    private sealed record Row(string? Header = null, Setting? Setting = null, Func<ArcadeSettings, bool>? Shown = null);

    // Background upscaling may take all threads but one (the game needs its own).
    private static readonly int UpscaleThreadsMaximum = Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>The display's exclusive fullscreen modes, largest first (set once by the host).</summary>
    // ponytail: read once at start; a different monitor plugged in later needs a restart.
    public static IReadOnlyList<(int Width, int Height, int RefreshRate)> DisplayModes { get; set; } = [];

    // Exclusive fullscreen exists on Windows only (Linux compositors only emulate it).
    private static readonly bool ExclusiveAvailable = OperatingSystem.IsWindows();

    private static bool exclusive(ArcadeSettings s) => ExclusiveAvailable && s.Fullscreen && s.ExclusiveFullscreen;

    // The Resolution row's choices (0 = native) and the Refresh Rate row's for the chosen one (0 = highest).
    private static (int Width, int Height)[] resolutions => [.. DisplayModes.Select(static m => (m.Width, m.Height)).Distinct()];

    private static int[] rates(ArcadeSettings s) =>
        [.. DisplayModes.Where(m => (m.Width, m.Height) == (s.FullscreenWidth, s.FullscreenHeight))
            .Select(static m => m.RefreshRate).Distinct().OrderDescending()];

    private static readonly int[] FpsCaps = [0, 60, 120, 144, 165, 240, 360, 480, 1000];

    /// <summary>The gameplay skins in the player's data, by name (set once by the host).</summary>
    public static IReadOnlyList<string> Skins { get; set; } = [];

    // The Gameplay Skin row's value: 0 automatic, 1 original, then each skin (a name the data lacks shows as automatic).
    private static int skinIndex(ArcadeSettings s) => s.GameplaySkin.ToLowerInvariant() switch
    {
        "auto" => 0,
        "original" => 1,
        var name => Skins.Select(static skin => skin.ToLowerInvariant()).ToList().IndexOf(name) is var index and >= 0 ? index + 2 : 0,
    };

    private static readonly Row[] Rows_ =
    [
        new(T("section.language")),
        // Each language by its own name, so a player who cannot read the current one still finds theirs.
        new(Setting: new(T("settings.language"),
            static s => Math.Max(0, Array.IndexOf(Available, s.Language == "auto" ? Language : s.Language)),
            static (s, v) => s with { Language = Available[v] }, static (value, step) => value + Math.Sign(step), 0,
            static value => Name(Available[value]), Available.Length - 1, Hint: T("settings.language.hint"))),
        new(Setting: new(T("settings.title_language"), static s => s.EnglishTitles ? 0 : 1, static (s, v) => s with { EnglishTitles = v == 0 },
            static (value, _) => 1 - value, 0, static value => Name(value == 0 ? "en" : "ja"), 1, Hint: T("settings.title_language.hint"))),
        new(T("section.songs")),
        new(Setting: toggle("settings.fast_song_scroll", static s => s.FastSongScroll, static (s, v) => s with { FastSongScroll = v })),
        new(Setting: toggle("settings.show_oni", static s => s.ShowOni, static (s, v) => s with { ShowOni = v })),
        new(Setting: toggle("settings.random_song_folder", static s => s.RandomSongFolder, static (s, v) => s with { RandomSongFolder = v })),
        new(Setting: toggle("settings.mouse_controls", static s => s.MouseControls, static (s, v) => s with { MouseControls = v })),
        new(Setting: toggle("settings.discord_presence", static s => s.DiscordPresence, static (s, v) => s with { DiscordPresence = v })),
        new(Setting: new(T("settings.gameplay_skin"), skinIndex,
            static (s, v) => s with { GameplaySkin = (v = Math.Min(v, Skins.Count + 1)) switch { 0 => "auto", 1 => "original", _ => Skins[v - 2] } },
            static (value, step) => Math.Min(value + Math.Sign(step), Skins.Count + 1), 0,
            static value => value switch
            {
                0 => T("settings.gameplay_skin.auto"),
                1 => T("settings.gameplay_skin.original"),
                _ => value - 2 < Skins.Count ? Skins[value - 2] : T("settings.gameplay_skin.auto"),
            },
            int.MaxValue, Hint: T("settings.gameplay_skin.hint"))),
        // Cache folder: this PC's user cache (the default; follows the user wherever the game is), the game
        // folder, or a path written in config.cfg (shown as custom, kept when chosen again).
        new(Setting: new(T("settings.cache_folder"),
            static s => ArcadeSettings.IsUserCache(s.CacheFolder) ? 0 : ArcadeSettings.IsGameCache(s.CacheFolder) ? 1 : 2,
            static (s, v) => v switch
            {
                0 => s with { CacheFolder = ArcadeSettings.UserCacheValue },
                1 => s with { CacheFolder = ArcadeSettings.GameCacheValue },
                _ => ArcadeSettings.IsUserCache(s.CacheFolder) || ArcadeSettings.IsGameCache(s.CacheFolder)
                    ? s with { CacheFolder = ArcadeSettings.GameCacheValue } : s,
            },
            static (value, step) => value + Math.Sign(step), 0,
            static value => T(value switch { 0 => "settings.cache_folder.user", 1 => "settings.cache_folder.game", _ => "settings.cache_folder.custom" }),
            2, Hint: T("settings.cache_folder.hint"))),
        new(T("section.volume")),
        new(Setting: volume("settings.master_volume", static s => s.MasterVolume, static (s, v) => s with { MasterVolume = v }, AudioBus.Bgm)),
        new(Setting: volume("settings.music_volume", static s => s.MusicVolume, static (s, v) => s with { MusicVolume = v }, AudioBus.Bgm)),
        new(Setting: volume("settings.drum_volume", static s => s.DrumVolume, static (s, v) => s with { DrumVolume = v }, null)),
        new(Setting: volume("settings.effects_volume", static s => s.EffectsVolume, static (s, v) => s with { EffectsVolume = v },
            AudioBus.MenuSound)),
        new(Setting: volume("settings.voice_volume", static s => s.VoiceVolume, static (s, v) => s with { VoiceVolume = v }, AudioBus.Voice)),
        new(Setting: volume("settings.menu_volume", static s => s.MenuVolume, static (s, v) => s with { MenuVolume = v }, AudioBus.Bgm)),
        new(Setting: volume("settings.attract_volume", static s => s.AttractVolume, static (s, v) => s with { AttractVolume = v }, AudioBus.Bgm)),
        new(T("section.timing")),
        new(Setting: offset("settings.audio_offset", static s => s.AudioOffsetMs, static (s, v) => s with { AudioOffsetMs = v })),
        new(Setting: offset("settings.input_offset", static s => s.InputOffsetMs, static (s, v) => s with { InputOffsetMs = v })),
        new(Setting: new(T("settings.timing_indicator"), static s => (int)s.TimingIndicator,
            static (s, v) => s with { TimingIndicator = (TimingIndicator)v }, static (value, step) => value + Math.Sign(step), 0,
            static value => T(value switch { 0 => "value.off", 1 => "settings.timing_indicator.good_bad", _ => "settings.timing_indicator.always" }), 2,
            Hint: T("settings.timing_indicator.hint"))),
        new(Setting: new(T("settings.drum_debounce"), static s => s.DrumDebounceMs, static (s, v) => s with { DrumDebounceMs = v },
            static (value, step) => value + step, 0,
            static value => value == 0 ? T("value.off") : T("settings.drum_debounce.value", value, 2000 / value), 100,
            Hint: T("settings.drum_debounce.hint"))),
        new(Setting: new(T("settings.retry_hold"), static s => s.RetryHoldMs, static (s, v) => s with { RetryHoldMs = v },
            static (value, step) => value + 50 * Math.Sign(step), 0,
            static value => value == 0 ? T("settings.retry_hold.instant") : T("settings.retry_hold.value", value), 1000,
            Hint: T("settings.retry_hold.hint"))),
        new(Setting: toggle("settings.record_performance", static s => s.RecordPerformance, static (s, v) => s with { RecordPerformance = v })),
        new(T("section.controls")),
        new(T("section.sound")),
        new(Setting: toggle("settings.stereo_panning", static s => s.StereoPanning, static (s, v) => s with { StereoPanning = v })),
        new(Setting: toggle("settings.mute_in_background", static s => s.MuteInBackground, static (s, v) => s with { MuteInBackground = v })),
        new(Setting: new(T("settings.audio_buffer"), static s => s.AudioBufferFrames, static (s, v) => s with { AudioBufferFrames = v },
            static (value, step) => step > 0 ? value * 2 : value / 2, 64,
            static value => T("settings.audio_buffer.value", value), 2048, Hint: T("settings.audio_buffer.hint"))),
        new(Setting: toggle("settings.exclusive_audio", static s => s.AudioExclusive, static (s, v) => s with { AudioExclusive = v })),
        new(T("section.display")),
        new(Setting: new(T("settings.fullscreen"), static s => !s.Fullscreen ? 0 : exclusive(s) ? 2 : 1,
            static (s, v) => s with { Fullscreen = v != 0, ExclusiveFullscreen = v == 2 ? true : v == 1 ? false : s.ExclusiveFullscreen },
            static (value, step) => value + Math.Sign(step), 0,
            static value => T(value switch { 0 => "value.off", 1 => "settings.fullscreen.borderless", _ => "settings.fullscreen.exclusive" }),
            ExclusiveAvailable ? 2 : 1,
            Hint: T(ExclusiveAvailable ? "settings.fullscreen.hint_exclusive" : "settings.fullscreen.hint"))),
        new(Shown: exclusive, Setting: new(T("settings.resolution"),
            static s => Array.IndexOf(resolutions, (s.FullscreenWidth, s.FullscreenHeight)) + 1,
            static (s, v) => (v = Math.Min(v, resolutions.Length)) == 0
                ? s with { FullscreenWidth = 0, FullscreenHeight = 0, RefreshRate = 0 }
                : s with { FullscreenWidth = resolutions[v - 1].Width, FullscreenHeight = resolutions[v - 1].Height, RefreshRate = 0 },
            static (value, step) => value + Math.Sign(step), 0,
            static value => value == 0 || value > resolutions.Length ? T("settings.resolution.native") : $"{resolutions[value - 1].Width}x{resolutions[value - 1].Height}",
            int.MaxValue, Hint: T("settings.resolution.hint"))),
        new(Shown: static s => exclusive(s) && s.FullscreenWidth > 0, Setting: new(T("settings.refresh_rate"),
            static s => Array.IndexOf(rates(s), s.RefreshRate) + 1,
            static (s, v) => s with { RefreshRate = Math.Min(v, rates(s).Length) is var index and > 0 ? rates(s)[index - 1] : 0 },
            static (value, step) => value + Math.Sign(step), 0, static _ => "", int.MaxValue,
            Hint: T("settings.refresh_rate.hint"))
        {
            FormatFor = static s => s.RefreshRate == 0 ? T("settings.refresh_rate.highest") : T("settings.refresh_rate.value", s.RefreshRate),
        }),
        new(Setting: toggle("settings.vsync", static s => s.Vsync, static (s, v) => s with { Vsync = v })),
        new(Shown: static s => !exclusive(s), Setting: new(T("settings.frame_limit"),
            static s => Math.Max(0, Array.IndexOf(FpsCaps, s.FpsCap)),
            static (s, v) => s with { FpsCap = FpsCaps[Math.Min(v, FpsCaps.Length - 1)] },
            static (value, step) => value + Math.Sign(step), 0,
            static value => value == 0 ? T("value.off") : T("settings.frame_limit.value", FpsCaps[value]), FpsCaps.Length - 1,
            Hint: T("settings.frame_limit.hint"))),
        new(Setting: new(T("settings.letterbox_size"), static s => s.LetterboxSize, static (s, v) => s with { LetterboxSize = v },
            static (value, step) => value + step, 20, static value => value == 100 ? T("value.off") : T("value.percent", value), 100,
            Hint: T("settings.letterbox_size.hint"))),
        new(Shown: static s => s.LetterboxSize < 100, Setting: new(T("settings.letterbox_x"), static s => s.LetterboxX,
            static (s, v) => s with { LetterboxX = v }, static (value, step) => value + step, 0, static value => T("value.percent", value), 100,
            Hint: T("settings.letterbox_x.hint"))),
        new(Shown: static s => s.LetterboxSize < 100, Setting: new(T("settings.letterbox_y"), static s => s.LetterboxY,
            static (s, v) => s with { LetterboxY = v }, static (value, step) => value + step, 0, static value => T("value.percent", value), 100,
            Hint: T("settings.letterbox_y.hint"))),
        new(T("section.graphics")),
        new(Setting: new(T("settings.upscale"), static s => (int)s.Upscale, static (s, v) => s with { Upscale = (UpscaleLevel)v },
            static (value, step) => value + Math.Sign(step), 0,
            static value => T(value switch { 0 => "value.off", 1 => "settings.upscale.text", _ => "settings.upscale.full" }), 2,
            Hint: T("settings.upscale.hint"))),
        new(Setting: new(T("settings.background_upscaling"), static s => s.UpscaleThreads, static (s, v) => s with { UpscaleThreads = v },
            static (value, step) => value + Math.Sign(step), 0,
            static value => value == 0 ? T("value.off") : value == 1 ? T("settings.background_upscaling.one")
                : T("settings.background_upscaling.many", value), UpscaleThreadsMaximum,
            Hint: T("settings.background_upscaling.hint"))),
        new(Setting: new(T("settings.long_titles"), static s => s.SquashTitles ? 1 : 0, static (s, v) => s with { SquashTitles = v != 0 },
            static (value, _) => 1 - value, 0, static value => T(value != 0 ? "settings.long_titles.squash" : "settings.long_titles.shrink"), 1,
            Hint: T("settings.long_titles.hint"))),
        new(),
    ];

    /// <summary>One of the settings page's settings, for the arcade service menu (which draws and edits it its own way).</summary>
    public sealed record SharedSetting(string Label, Func<ArcadeSettings, int> Get, Func<ArcadeSettings, int, ArcadeSettings> Set,
        Func<int, int, int> Step, int Minimum, int Maximum, Func<ArcadeSettings, string> Show, Func<ArcadeSettings, bool>? Shown, string Hint);

    /// <summary>The settings page's sections (by their title) and their settings; its commands (libraries, controls,
    /// calibration, the bake) are left out.</summary>
    public static IReadOnlyList<(string Title, SharedSetting[] Settings)> Sections { get; } = sections();

    private static List<(string Title, SharedSetting[] Settings)> sections()
    {
        var result = new List<(string Title, SharedSetting[] Settings)>();
        string? title = null;
        var settings = new List<SharedSetting>();
        foreach (var row in Rows_.Append(new Row(Header: "")))
        {
            if (row.Header is not null)
            {
                if (title is not null && settings.Count != 0)
                    result.Add((title, [.. settings]));
                (title, settings) = (row.Header, []);
            }
            else if (row.Setting is { } setting)
                settings.Add(new(setting.Label, setting.Get, setting.Set, setting.Step, setting.Minimum, setting.Maximum,
                    setting.FormatFor ?? (s => setting.Format(Math.Clamp(setting.Get(s), setting.Minimum, setting.Maximum))),
                    row.Shown, setting.Hint));
        }
        return result;
    }

    /// <summary>The buttons beside the drum's label and hint keys, in ArcadeSettings.Buttons order (the coin switches after).</summary>
    public static IReadOnlyList<(string Label, string Hint)> ButtonLabels => ButtonNames;

    // Label and hint keys, in ArcadeSettings.Buttons order.
    private static readonly (string Label, string Hint)[] ButtonNames =
    [
        ("buttons.back", "buttons.back.hint"), ("buttons.search", "buttons.search.hint"),
        ("buttons.scores", "buttons.scores.hint"), ("buttons.practise", "buttons.practise.hint"),
        ("buttons.notices", "buttons.notices.hint"), ("buttons.restart", "buttons.restart.hint"),
        ("buttons.confirm", "buttons.confirm.hint"),
    ];

    /// <summary>Why a Nijiiro installation can't be read, in the player's language; null when it can.</summary>
    public static string? NijiiroProblemText(NijiiroCatalogProvider installation) =>
        !installation.Exists ? T("library.no_nijiiro")
        : installation.Problem is not { } problem ? null
        : installation.KeysMissing ? T("library.no_keys") : problem;

    private string[] _choices = [];

    public bool IsOpen { get; private set; }

    public int Selection { get; private set; }

    public string Title => T(Bake is not null ? "menu.title.bake" : Calibration is not null ? "menu.title.calibration"
        : _restartPrompt ? "menu.title.restart" : _reportPrompt ? "menu.title.report"
        : _quitPrompt ? "menu.title.quit" : "menu.title.paused");

    /// <summary>The texture bake shown instead of the choices (running or finished).</summary>
    public Presentation.TextureBake? Bake
    {
        get => _bake;
        set
        {
            _bake = value;
            _baked |= value is not null;
        }
    }

    private Presentation.TextureBake? _bake;
    private bool _baked;

    /// <summary>A finished audio calibration, shown instead of the pause choices until saved or discarded.</summary>
    public LatencyCalibration? Calibration
    {
        get;
        set
        {
            field = value;
            _calibrationDoneAt = value is null ? 0 : Stopwatch.GetTimestamp();
        }
    }

    // When the calibration's result showed: a drum hit right after its last one does not save.
    private long _calibrationDoneAt;

    /// <summary>A line above the choices (the restart prompt's reason), or null.</summary>
    public string? Message => _restartPrompt ? T("menu.restart_message") : null;

    /// <summary>What the player picked to report the replay for (with <see cref="HomeMenuAction.Report"/>): a server reason.</summary>
    public string ReportReason { get; private set; } = "cheating";

    // The report page: the reasons a replay can be reported for (each choice is "report.{server reason}").
    private bool _reportPrompt;
    private static readonly string[] ReportChoices = ["report.autoplay", "report.macro", "report.cheating", "menu.back"];

    // Quit Game asks first: Yes closes the game, No (the one selected) goes back.
    private bool _quitPrompt;

    // Leaving the settings asks for a restart when a setting that applies at start changed, or after a
    // bake (the memory it used goes back with the restart); "Later" is not asked again for the same state.
    private readonly ArcadeSettings _atStart = get();
    private bool _restartPrompt;
    private string? _declined;
    private string[] _pauseChoices = [];

    private string restartState()
    {
        var now = get();
        var changed = now.AudioBufferFrames != _atStart.AudioBufferFrames || now.AudioExclusive != _atStart.AudioExclusive
            || now.TjaFolder != _atStart.TjaFolder || now.NijiiroFolder != _atStart.NijiiroFolder || now.OsuFolder != _atStart.OsuFolder
            || now.StockFolders != _atStart.StockFolders
            || now.CacheFolder != _atStart.CacheFolder
            || now.Language != _atStart.Language || now.EnglishTitles != _atStart.EnglishTitles;
        return changed || _baked
            ? $"{now.AudioBufferFrames}|{now.AudioExclusive}|{now.TjaFolder}|{now.NijiiroFolder}|{now.StockFolders}|{now.OsuFolder}|{now.CacheFolder}|{now.Language}|{now.EnglishTitles}|{_baked}" : "";
    }

    /// <summary>The choices shown (the pause choices, or a prompt's).</summary>
    public string[] Rows => [.. _choices.Select(static choice => T(choice))];

    // The calibration runs on the gameplay lane and ends in Song Select: offered from there only.
    private bool _calibrationOffered;

    /// <remarks>
    /// <paramref name="replayable"/>: a song's pause menu offers the instant replay of what was played.
    /// <paramref name="reviewing"/>: a replay or practice is on: restarting plays the song for real.
    /// <paramref name="reportable"/>: another player's downloaded replay: its player can be reported.
    /// <paramref name="noResume"/>: a dan course: its pause can't be resumed, only restarted or left.
    /// <paramref name="quit"/>: home, outside a song (the demo play counts as the attract): the game can be closed.
    /// </remarks>
    public void Open(bool gameplay, bool attract = false, bool songSelect = false, bool replayable = false, bool reviewing = false,
        bool reportable = false, bool noResume = false, bool inRoom = false, bool quit = false)
    {
        _noResume = noResume;
        _calibrationOffered = songSelect;
        var restart = reviewing ? "menu.play_song" : "menu.restart_song";
        _choices = gameplay ? replayable
                ? ["menu.resume", restart, "menu.instant_replay", "menu.settings", "menu.song_select"]
                : ["menu.resume", restart, "menu.settings", "menu.song_select"]
            : attract ? ["menu.resume", "menu.settings"] : ["menu.resume", "menu.settings", "menu.return_to_title"];
        if (reportable)
            _choices = [.. _choices[..^1], "menu.report_player", _choices[^1]];
        if (noResume)
            _choices = [restart, "menu.settings", "menu.song_select"];
        // In an online room: a way out of it, back to the usual Song Select. A song with the other player can't be
        // restarted or replayed alone, so that's all its menu offers besides the settings.
        if (inRoom && !gameplay)
            _choices = [.. _choices[..^1], "online.leave", _choices[^1]];
        else if (inRoom)
            _choices = ["menu.resume", "menu.settings", "online.leave"];
        if (quit && !gameplay)
            _choices = [.. _choices, "menu.quit_game"];
        IsOpen = true;
        Selection = 0;
        _restartPrompt = _reportPrompt = _quitPrompt = false;
        VgmstreamCli.Recheck(); // it may have been installed while the game ran
    }

    private bool _noResume;

    /// <summary>The calibration can start from here (Song Select's menu: it runs on its own lane and ends there).</summary>
    public bool CalibrationOffered => _calibrationOffered;

    /// <summary>The texture bake's page shows (started from the settings overlay).</summary>
    public void ShowBake(Presentation.TextureBake bake) => Bake = bake;

    /// <summary>
    /// The settings overlay closed: its settings are saved and, when one that applies at start changed, the restart
    /// prompt shows as after the settings page.
    /// </summary>
    public void SettingsOverlayClosed()
    {
        Selection = Array.IndexOf(_choices, "menu.settings");
        save();
        if (restartState() is { Length: > 0 } state && state != _declined)
        {
            _restartPrompt = true;
            _pauseChoices = _choices;
            _choices = ["menu.restart_now", "menu.later"];
            Selection = 0;
        }
    }

    /// <summary>
    /// The choice at <paramref name="index"/> of the pause choices (or of the restart or report prompt shown in their
    /// place) is picked: what the shell should do, or None when the menu itself goes on (a prompt opening or closing).
    /// </summary>
    public HomeMenuAction Pick(int index)
    {
        Selection = Math.Clamp(index, 0, _choices.Length - 1);
        if (_restartPrompt)
        {
            if (Selection == 0)
                return close(HomeMenuAction.RestartGame);
            return Back();
        }
        if (_reportPrompt)
        {
            if (_choices[Selection] is var reason && reason != "menu.back")
            {
                ReportReason = reason["report.".Length..];
                return close(HomeMenuAction.Report);
            }
            return Back();
        }
        if (_quitPrompt)
            return Selection == 0 ? close(HomeMenuAction.Quit) : Back();
        switch (_choices[Selection])
        {
            case "menu.settings":
                return HomeMenuAction.Settings;
            case "menu.restart_song" or "menu.play_song": return close(HomeMenuAction.Restart);
            case "menu.instant_replay": return close(HomeMenuAction.InstantReplay);
            case "menu.report_player":
                _reportPrompt = true;
                _pauseChoices = _choices;
                _choices = ReportChoices;
                Selection = 0;
                return HomeMenuAction.None;
            case "menu.song_select": return close(HomeMenuAction.SongSelect);
            case "menu.return_to_title": return close(HomeMenuAction.Title);
            case "menu.quit_game":
                _quitPrompt = true;
                _pauseChoices = _choices;
                _choices = ["menu.yes", "menu.no"];
                Selection = 1;
                return HomeMenuAction.None;
            case "online.leave": return close(HomeMenuAction.LeaveRoom);
            default: return close(HomeMenuAction.Resume);
        }
    }

    /// <summary>Escape (or the back button) on the pause choices: a prompt goes back to them, else the menu resumes.</summary>
    public HomeMenuAction Back()
    {
        if (_restartPrompt)
        {
            _declined = restartState();
            _restartPrompt = false;
            _choices = _pauseChoices;
            Selection = Array.IndexOf(_choices, "menu.settings");
            return HomeMenuAction.None;
        }
        if (_reportPrompt)
        {
            _reportPrompt = false;
            _choices = _pauseChoices;
            Selection = Array.IndexOf(_choices, "menu.report_player");
            return HomeMenuAction.None;
        }
        if (_quitPrompt)
        {
            _quitPrompt = false;
            _choices = _pauseChoices;
            Selection = Array.IndexOf(_choices, "menu.quit_game");
            return HomeMenuAction.None;
        }
        return _noResume ? HomeMenuAction.None : close(HomeMenuAction.Resume);
    }

    /// <summary>
    /// The texture bake's page: its one choice stops a running bake, or leaves a finished one (back to the settings
    /// overlay when it was started there).
    /// </summary>
    public HomeMenuAction BakeChoice()
    {
        if (Bake is not { } bake)
            return HomeMenuAction.None;
        drum(true);
        if (bake.Running)
        {
            bake.Stop();
            return HomeMenuAction.None;
        }
        Bake = null;
        return HomeMenuAction.Settings; // back to the settings it was started from
    }

    /// <summary>
    /// The calibration's result: saved (<paramref name="save"/>) or not, and the menu closes. A pick in the first half
    /// second after it ends is ignored (a last drum hit of the calibration).
    /// </summary>
    public HomeMenuAction FinishCalibration(bool save)
    {
        if (Calibration is not { } calibration)
            return HomeMenuAction.None;
        if (save && Stopwatch.GetElapsedTime(_calibrationDoneAt) < TimeSpan.FromSeconds(0.5))
            return HomeMenuAction.None;
        if (save)
            apply(get() with { AudioOffsetMs = calibration.AudioOffsetMs });
        Calibration = null;
        return close(HomeMenuAction.EndCalibration);
    }

    /// <summary>The pause choices (or a prompt in their place) are showing, not one of the menu's own pages.</summary>
    public bool ShowsChoices => IsOpen && Bake is null && Calibration is null;

    /// <summary>Closed from outside (the service menu resets the game).</summary>
    public void Close() => IsOpen = false;

    private HomeMenuAction close(HomeMenuAction action)
    {
        IsOpen = false;
        return action;
    }

    // The label is key's text and the hint key.hint's.
    private static Setting volume(string key, Func<ArcadeSettings, int> read, Func<ArcadeSettings, int, ArcadeSettings> write,
        AudioBus? bus) =>
        new(T(key), read, write, static (value, step) => value + step, 0, static value => T("value.percent", value), 100, bus,
            T(key + ".hint"));

    private static Setting toggle(string key, Func<ArcadeSettings, bool> read, Func<ArcadeSettings, bool, ArcadeSettings> write) =>
        new(T(key), s => read(s) ? 1 : 0, (s, v) => write(s, v != 0), static (value, _) => 1 - value, 0,
            static value => T(value != 0 ? "value.on" : "value.off"), 1, Hint: T(key + ".hint"));

    private static Setting offset(string key, Func<ArcadeSettings, int> read, Func<ArcadeSettings, int, ArcadeSettings> write) =>
        new(T(key), read, write, static (value, step) => value + step, -500,
            static value => T("value.ms", value.ToString("+0;-0;0", CultureInfo.InvariantCulture)), 500, Hint: T(key + ".hint"));
}
