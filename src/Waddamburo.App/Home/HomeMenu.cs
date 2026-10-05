using System.Diagnostics;
using System.Globalization;
using Waddamburo.App.Gameplay;
using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Providers.OsuLazer;
using Waddamburo.Providers.Stock;
using static Waddamburo.App.Strings;

namespace Waddamburo.App.Home;

internal enum HomeMenuAction
{
    None,
    Resume,
    Restart,
    SongSelect,
    Title,
    /// <summary>Open the folder picker for the custom TJA library.</summary>
    PickTjaFolder,
    /// <summary>Open the folder picker for the Nijiiro installation.</summary>
    PickNijiiroFolder,
    /// <summary>Open the folder picker for the osu!lazer data folder.</summary>
    PickOsuFolder,
    /// <summary>Start upscaling every texture the game uses (the bake page shows it).</summary>
    BakeTextures,
    /// <summary>Close the game and start it again (settings that apply at start, memory after a bake).</summary>
    RestartGame,
    /// <summary>Start the audio calibration (the calibration page shows it).</summary>
    Calibrate,
    /// <summary>The calibration page closed (saved or not): its sound stops.</summary>
    EndCalibration,
    /// <summary>Open the drum controls page (this and the next two are done by the menu itself).</summary>
    Controls,
    /// <summary>Ask for each of the player's pads in turn on the chosen device.</summary>
    BindAllPads,
    /// <summary>The player's pads on the chosen device back to their defaults.</summary>
    ResetControls,
}

/// <summary>
/// Home mode's Escape menu: the pause choices (gameplay) or the session choices (entry, Song Select),
/// and a settings page. Rims or Up/Down move, centre or Enter picks; on a setting, centre starts
/// editing and the rims (or Left/Right at any time) change it: by 1, or by 5 then 10 when pressed
/// quickly again and again. Each move plays a Ka, each pick a Don. The drum controls have their own
/// page: one player's four pads on one device (keyboard, controller, MIDI) at a time.
/// </summary>
/// <remarks><paramref name="cachedTextures"/>: Textures in the upscale cache; null when upscaling is unavailable.</remarks>
internal sealed class HomeMenu(Func<ArcadeSettings> get, Action<ArcadeSettings> apply, Action save, Action<bool> drum,
    string defaultTjaFolder, Func<int?> cachedTextures, SdlApplication input)
{
    // Presses closer together than this keep a streak going; its length picks the step.
    private static readonly TimeSpan StreakGap = TimeSpan.FromMilliseconds(200);

    private sealed record Setting(string Label, Func<ArcadeSettings, int> Get, Func<ArcadeSettings, int, ArcadeSettings> Set,
        Func<int, int, int> Step, int Minimum, Func<int, string> Format, int Maximum, AudioBus? Bus = null, string Hint = "")
    {
        /// <summary>The shown value when it depends on other settings (replaces <see cref="Format"/>).</summary>
        public Func<ArcadeSettings, string>? FormatFor { get; init; }
    }

    /// <summary>A song library the player points at a folder (picked in the system's folder dialog).</summary>
    private sealed record Library(string Label, Func<ArcadeSettings, string?> Get, HomeMenuAction Pick, SongSourceKind Kind);

    /// <summary>A row that starts something (the texture bake, the audio calibration).</summary>
    private sealed record Command(string Label, HomeMenuAction Action, string Hint);

    /// <summary>A drum pad's inputs on the controls page (Pad: 0-3 of the chosen player).</summary>
    private sealed record Binding(string Label, int Pad);

    // One settings row: a section header, a setting, a song library, a command, a drum pad, or Back (all null).
    // Shown: whether the row applies to the current settings (hidden rows are skipped); null = always.
    private sealed record Row(string? Header = null, Setting? Setting = null, Library? Library = null, Command? Command = null,
        Func<ArcadeSettings, bool>? Shown = null, Binding? Binding = null);


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
        new(Library: new(T("library.tja"), static s => s.TjaFolder, HomeMenuAction.PickTjaFolder, SongSourceKind.Tja)),
        new(Library: new(T("library.nijiiro"), static s => s.NijiiroFolder, HomeMenuAction.PickNijiiroFolder, SongSourceKind.Nijiiro)),
        new(Library: new(T("library.osu"), static s => s.OsuFolder, HomeMenuAction.PickOsuFolder, SongSourceKind.OsuLazer)),
        new(Setting: toggle("settings.fast_song_scroll", static s => s.FastSongScroll, static (s, v) => s with { FastSongScroll = v })),
        new(Setting: toggle("settings.show_oni", static s => s.ShowOni, static (s, v) => s with { ShowOni = v })),
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
        new(T("section.timing")),
        new(Command: new(T("settings.calibrate"), HomeMenuAction.Calibrate, T("settings.calibrate.hint"))),
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
        new(T("section.controls")),
        new(Command: new(T("settings.drum_controls"), HomeMenuAction.Controls, T("settings.drum_controls.hint"))),
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
        new(Setting: toggle("settings.upscaled_textures", static s => s.UpscaleTextures, static (s, v) => s with { UpscaleTextures = v })),
        new(Setting: new(T("settings.background_upscaling"), static s => s.UpscaleThreads, static (s, v) => s with { UpscaleThreads = v },
            static (value, step) => value + Math.Sign(step), 0,
            static value => value == 0 ? T("value.off") : value == 1 ? T("settings.background_upscaling.one")
                : T("settings.background_upscaling.many", value), UpscaleThreadsMaximum,
            Hint: T("settings.background_upscaling.hint"))),
        new(Setting: new(T("settings.long_titles"), static s => s.SquashTitles ? 1 : 0, static (s, v) => s with { SquashTitles = v != 0 },
            static (value, _) => 1 - value, 0, static value => T(value != 0 ? "settings.long_titles.squash" : "settings.long_titles.shrink"), 1,
            Hint: T("settings.long_titles.hint"))),
        new(Command: new(T("settings.bake"), HomeMenuAction.BakeTextures, T("settings.bake.hint"))),
        new(),
    ];

    // The controls page: one player's pads on one kind of device at a time.
    private static readonly string[] Devices = ["controls.device.keyboard", "controls.device.controller", "controls.device.midi"];
    private static readonly string[] Prompts = ["controls.prompt.keyboard", "controls.prompt.controller", "controls.prompt.midi"];
    // A pad takes two keyboard keys at most (controllers and MIDI: any number).
    private const int MaximumKeys = 2;
    private bool _controlsPage;
    private int _player, _device;
    // Pads still to ask for in Set Up All Pads, the selected one included; 0: one pad was picked.
    private int _guided;
    private Row[]? _controlRows;

    private Row[] controlRows => _controlRows ??=
    [
        new(Setting: new(T("controls.player"), _ => _player, (s, v) => { _player = v; return s; }, static (value, _) => 1 - value, 0,
            static value => value == 0 ? "1" : "2", 1, Hint: T("controls.player.hint"))),
        new(Setting: new(T("controls.device"), _ => _device, (s, v) => { _device = v; return s; },
            static (value, step) => value + Math.Sign(step), 0, static value => T(Devices[value]), Devices.Length - 1,
            Hint: T("controls.device.hint"))),
        new(Shown: _ => _device == 1, Setting: new(T("controls.in_menus"), static s => s.PadMenusAsDrum ? 1 : 0,
            static (s, v) => s with { PadMenusAsDrum = v != 0 }, static (value, _) => 1 - value, 0,
            static value => T(value != 0 ? "controls.in_menus.drum" : "controls.in_menus.gamepad"), 1,
            Hint: T("controls.in_menus.hint"))),
        new(T("controls.pads")),
        new(Binding: new(T("controls.left_ka"), 0)),
        new(Binding: new(T("controls.left_don"), 1)),
        new(Binding: new(T("controls.right_don"), 2)),
        new(Binding: new(T("controls.right_ka"), 3)),
        new(Command: new(T("controls.set_up_all"), HomeMenuAction.BindAllPads, T("controls.set_up_all.hint"))),
        new(Command: new(T("controls.reset"), HomeMenuAction.ResetControls, T("controls.reset.hint"))),
        new(),
    ];

    public enum ItemKind { Header, Setting, Library, Command, Binding, Back }

    /// <summary>A song library's state: red (not set up), orange (set up, something missing), green.</summary>
    public enum LibraryState { Missing, Warning, Ready }

    /// <summary>One settings row as drawn, with what it does.</summary>
    public readonly record struct Item(ItemKind Kind, string Label, string? Value, string Hint, LibraryState State = default);

    // The folders the running game loaded its songs from: a change needs a restart.
    private readonly ArcadeSettings _atStart = get();
    private readonly Dictionary<string, bool> _valid = [];
    private readonly Dictionary<string, string?> _nijiiroProblems = [];
    private readonly Dictionary<string, string?> _osuFolders = [];

    private string[] _choices = [];
    private bool _settingsPage;
    private bool _editing;
    // Waiting for the input to bind to the selected pad.
    private bool _listening;
    private long _lastChangeAt;
    private int _streak;

    public bool IsOpen { get; private set; }

    public int Selection { get; private set; }

    public string Title => T(Bake is not null ? "menu.title.bake" : Calibration is not null ? "menu.title.calibration"
        : _restartPrompt ? "menu.title.restart" : _controlsPage ? "menu.title.controls" : _settingsPage ? "menu.title.settings"
        : "menu.title.paused");

    /// <summary>The texture bake shown instead of the settings (running or finished).</summary>
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
    public LatencyCalibration? Calibration { get; set; }

    // When the calibration was first seen finished: a drum hit right after the last one does not save.
    private long _calibrationDoneAt;

    /// <summary>A line above the choices (the restart prompt's reason), or null.</summary>
    public string? Message => _restartPrompt ? T("menu.restart_message") : null;

    // Leaving the settings asks for a restart when a setting that applies at start changed, or after a
    // bake (the memory it used goes back with the restart); "Later" is not asked again for the same state.
    private bool _restartPrompt;
    private string? _declined;
    private string[] _pauseChoices = [];

    private string restartState()
    {
        var now = get();
        var changed = now.AudioBufferFrames != _atStart.AudioBufferFrames || now.AudioExclusive != _atStart.AudioExclusive
            || now.TjaFolder != _atStart.TjaFolder || now.NijiiroFolder != _atStart.NijiiroFolder || now.OsuFolder != _atStart.OsuFolder
            || now.CacheFolder != _atStart.CacheFolder
            || now.Language != _atStart.Language || now.EnglishTitles != _atStart.EnglishTitles;
        return changed || _baked
            ? $"{now.AudioBufferFrames}|{now.AudioExclusive}|{now.TjaFolder}|{now.NijiiroFolder}|{now.OsuFolder}|{now.CacheFolder}|{now.Language}|{now.EnglishTitles}|{_baked}" : "";
    }

    /// <summary>The pause page's choices (the settings page draws <see cref="Items"/>).</summary>
    public string[] Rows => [.. _choices.Select(static choice => T(choice))];

    public bool SettingsPage => _settingsPage;

    /// <summary>The selected setting is being changed (drawn between arrows).</summary>
    public bool Editing => _editing;

    public Item[] Items => [.. rows.Select(item)];

    // The rows shown for the current settings; Selection indexes these. Only rows below the one being
    // changed come and go, so the selection stays on it.
    private Row[] rows => [.. (_controlsPage ? controlRows : Rows_).Where(row => (row.Shown?.Invoke(get()) ?? true)
        && (row.Command?.Action != HomeMenuAction.Calibrate || _calibrationOffered))];

    // The calibration runs on the gameplay lane and ends in Song Select: offered from there only.
    private bool _calibrationOffered;

    private Item item(Row row) => row switch
    {
        { Header: { } header } => new(ItemKind.Header, header, null, ""),
        { Setting: { } setting } => new(ItemKind.Setting, setting.Label,
            setting.FormatFor?.Invoke(get()) ?? setting.Format(setting.Get(get())), setting.Hint),
        { Library: { } library } => libraryItem(library),
        { Binding: { } binding } => new(ItemKind.Binding, binding.Label,
            _listening && rows[Selection].Binding == binding ? T(Prompts[_device])
            : inputs(get().Controls[_player * 4 + binding.Pad]).Where(onDevice).ToArray() is { Length: > 0 } bound
                ? string.Join(", ", bound.Select(describe)) : T("value.none"),
            T(_device == 0 ? "controls.pad.hint_keyboard" : "controls.pad.hint")),
        { Command: { Action: not HomeMenuAction.BakeTextures } command } => new(ItemKind.Command, command.Label, null, command.Hint),
        { Command: { } command } => cachedTextures() is { } cached
            ? new(ItemKind.Command, command.Label, T("settings.bake.cached", cached), command.Hint)
            : new(ItemKind.Command, command.Label, T("settings.bake.unavailable"), T("settings.bake.unavailable_hint")),
        _ => new(ItemKind.Back, T("menu.back"), null, T("menu.back.hint")),
    };

    private static string[] inputs(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string describe(string token) =>
        SdlInput.TryParse(token, out var parsed) ? SdlApplication.Describe(parsed) : token;

    // Keyboard 0, controller 1, MIDI 2 (the Devices).
    private static int deviceOf(SdlInput input) => input.Kind switch { SdlInputKind.Key => 0, SdlInputKind.Midi => 2, _ => 1 };

    private bool onDevice(string token) => SdlInput.TryParse(token, out var parsed) && deviceOf(parsed) == _device;

    // The input joins the pad, leaving any other pad it hit (either player's). replace: the pad's others
    // on this device go; else one already on the pad leaves it instead, and the oldest keys over the limit.
    private void bind(int pad, SdlInput captured, bool replace)
    {
        var controls = get().Controls;
        for (var other = 0; other < controls.Count; other++)
        {
            var bound = inputs(controls[other]);
            var kept = bound.Where(token => token != captured.Token && !(replace && other == pad && onDevice(token)));
            if (other == pad && (replace || !bound.Contains(captured.Token)))
                kept = kept.Append(captured.Token);
            if (other == pad && _device == 0)
            {
                var surplus = kept.Where(onDevice).SkipLast(MaximumKeys).ToHashSet();
                kept = kept.Where(token => !surplus.Contains(token));
            }
            controls = controls.With(other, string.Join(", ", kept));
        }
        apply(get() with { Controls = controls });
    }

    // The player's pads get their default inputs of this device back (taken from wherever they are now).
    private void resetControls()
    {
        var defaults = new ArcadeSettings().Controls;
        var controls = get().Controls;
        var restored = Enumerable.Range(_player * 4, 4).SelectMany(pad => inputs(defaults[pad]).Where(onDevice)).ToHashSet();
        for (var pad = 0; pad < controls.Count; pad++)
        {
            var mine = pad / 4 == _player;
            var kept = inputs(controls[pad]).Where(token => !restored.Contains(token) && !(mine && onDevice(token)));
            controls = controls.With(pad, string.Join(", ", mine ? kept.Concat(inputs(defaults[pad]).Where(onDevice)) : kept));
        }
        apply(get() with { Controls = controls });
    }

    private void listen(int pads)
    {
        _guided = pads;
        _listening = true;
        input.BeginCapture();
    }

    private Item libraryItem(Library library)
    {
        var chosen = library.Get(get());
        var path = library.Kind switch
        {
            SongSourceKind.Tja => chosen ?? defaultTjaFolder,
            SongSourceKind.OsuLazer => osuFolder(chosen),
            _ => chosen,
        };
        var choose = T("library.choose", T(library.Kind switch
        {
            SongSourceKind.Nijiiro => "library.nijiiro.what",
            SongSourceKind.OsuLazer => "library.osu.what",
            _ => "library.tja.what",
        }));
        if (path is null)
            return new(ItemKind.Library, library.Label, T(chosen is null ? "library.not_set_up" : "library.not_usable"),
                chosen is null ? choose : $"{T("library.no_osu")} {choose}", LibraryState.Missing);
        path = Path.TrimEndingDirectorySeparator(path);
        // Nijiiro: the same checks as loading it (a library that cannot play is left out of Song Select).
        if (library.Kind == SongSourceKind.Nijiiro && nijiiroProblem(path) is { } problem)
            return new(ItemKind.Library, library.Label, T("library.not_usable"), $"{problem} {choose}",
                LibraryState.Missing);
        if (library.Kind == SongSourceKind.Tja && !valid(path))
            return new(ItemKind.Library, library.Label, T("library.not_set_up"), $"{T("library.no_tja")} {choose}",
                LibraryState.Missing);
        return new(ItemKind.Library, library.Label, Path.GetFileName(path),
            T(chosen != library.Get(_atStart) ? "library.restart" : "library.choose_another"),
            LibraryState.Ready);
    }

    // ponytail: remembered per path for the session (the menu redraws every frame); a folder filled
    // while the game runs shows as set up after choosing it again.
    private bool valid(string path)
    {
        if (!_valid.TryGetValue(path, out var ok))
        {
            try
            {
                ok = Directory.Exists(path) && Directory.EnumerateFiles(path, "*.tja", SearchOption.AllDirectories).Any();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ok = false;
            }
            _valid[path] = ok;
        }
        return ok;
    }

    // Looked up once per chosen path like valid() (the default install when none is chosen).
    private string? osuFolder(string? chosen)
    {
        var key = chosen ?? "";
        if (!_osuFolders.TryGetValue(key, out var found))
            _osuFolders[key] = found = OsuLazerCatalogProvider.FindDataFolder(chosen);
        return found;
    }

    // The installation's own problem is remembered per path like valid() (it decrypts the song table);
    // vgmstream-cli is looked for again each time the menu opens.
    private string? nijiiroProblem(string path)
    {
        if (!_nijiiroProblems.TryGetValue(path, out var problem))
            _nijiiroProblems[path] = problem = new NijiiroCatalogProvider(path) is var installation && !installation.Exists
                ? T("library.no_nijiiro") : installation.Problem;
        return problem ?? SongLibraries.VgmstreamProblem;
    }

    /// <summary>
    /// The bus to keep a sample playing on while its volume is edited (music for the master; none for
    /// the drum, whose Ka on each change is already its sample).
    /// </summary>
    public AudioBus? PreviewBus => IsOpen && _settingsPage && _editing ? rows[Selection].Setting?.Bus : null;

    public void Open(bool gameplay, bool attract = false, bool songSelect = false)
    {
        _calibrationOffered = songSelect;
        _choices = gameplay ? ["menu.resume", "menu.restart_song", "menu.settings", "menu.song_select"]
            : attract ? ["menu.resume", "menu.settings"] : ["menu.resume", "menu.settings", "menu.return_to_title"];
        IsOpen = true;
        Selection = 0;
        _settingsPage = _editing = _restartPrompt = _listening = _controlsPage = false;
        input.CancelCapture();
        VgmstreamCli.Recheck(); // it may have been installed while the game ran
    }

    /// <summary>One tick of menu input (key pulses); returns what the shell should do.</summary>
    public HomeMenuAction Input(SdlKeyboardSnapshot keys, bool escape, SdlKeyboardSnapshot held)
    {
        if (Calibration is { } calibration)
            return calibrationInput(calibration, keys, escape);
        // The platform holds the next input back for the pad (Escape there cancels, ending a guided setup).
        if (_listening)
        {
            if (input.Capturing)
                return HomeMenuAction.None;
            if (input.TakeCaptured() is not { } captured)
                _listening = false;
            // Another device's input is not for this page: keep waiting.
            else if (deviceOf(captured) != _device)
                input.BeginCapture();
            else
            {
                drum(true);
                bind(_player * 4 + rows[Selection].Binding!.Pad, captured, replace: _guided > 0);
                if (_guided > 1)
                {
                    Selection++;
                    listen(_guided - 1);
                }
                else
                    _listening = false;
            }
            return HomeMenuAction.None;
        }
        var up = keys.IsDown(SdlKeyboardKey.Up) || keys.IsDown(SdlKeyboardKey.D) || keys.IsDown(SdlKeyboardKey.Z)
            || keys.IsDown(SdlKeyboardKey.WheelUp);
        var down = keys.IsDown(SdlKeyboardKey.Down) || keys.IsDown(SdlKeyboardKey.K) || keys.IsDown(SdlKeyboardKey.V)
            || keys.IsDown(SdlKeyboardKey.WheelDown);
        var decide = keys.IsDown(SdlKeyboardKey.Enter) || keys.IsDown(SdlKeyboardKey.Space)
            || keys.IsDown(SdlKeyboardKey.F) || keys.IsDown(SdlKeyboardKey.J)
            || keys.IsDown(SdlKeyboardKey.X) || keys.IsDown(SdlKeyboardKey.C);
        var arrows = keys.IsDown(SdlKeyboardKey.Right) ? 1 : keys.IsDown(SdlKeyboardKey.Left) ? -1 : 0;
        if (decide)
            drum(true);
        else if (up || down || arrows != 0)
            drum(false);
        if (_restartPrompt)
        {
            if (up || down)
                Selection = 1 - Selection;
            else if (escape || decide)
            {
                if (decide && Selection == 0)
                    return close(HomeMenuAction.RestartGame);
                _declined = restartState();
                _restartPrompt = false;
                _choices = _pauseChoices;
                Selection = Array.IndexOf(_choices, "menu.settings");
            }
            return HomeMenuAction.None;
        }
        if (!_settingsPage)
        {
            if (escape)
                return close(HomeMenuAction.Resume);
            if (up || down)
                Selection = (Selection + (up ? _choices.Length - 1 : 1)) % _choices.Length;
            else if (decide)
                switch (_choices[Selection])
                {
                    case "menu.settings":
                        _settingsPage = true;
                        Selection = move(0, 1);
                        break;
                    case "menu.restart_song": return close(HomeMenuAction.Restart);
                    case "menu.song_select": return close(HomeMenuAction.SongSelect);
                    case "menu.return_to_title": return close(HomeMenuAction.Title);
                    default: return close(HomeMenuAction.Resume);
                }
            return HomeMenuAction.None;
        }
        // The bake page: Escape or a pick stops a running bake, and leaves a finished one.
        if (Bake is { } bake)
        {
            if (escape || decide)
            {
                if (bake.Running)
                    bake.Stop();
                else
                    Bake = null;
            }
            return HomeMenuAction.None;
        }
        var row = rows[Selection];
        if (row.Setting is not null && !(up || down || arrows != 0 || decide || escape) && repeat(held) is { } step)
        {
            drum(false);
            change(step);
            return HomeMenuAction.None;
        }
        if (_editing)
        {
            if (escape || decide)
                _editing = false;
            // Up raises and Down lowers the value; the left rim lowers, the right rim raises.
            else if (up || down || arrows != 0)
                change(arrows != 0 ? arrows : keys.IsDown(SdlKeyboardKey.Up) ? 1 : keys.IsDown(SdlKeyboardKey.Down) ? -1
                    : up ? -1 : 1);
        }
        else if (_controlsPage && (escape || decide && row == Rows_[^1]))
        {
            _controlsPage = false;
            Selection = Array.FindIndex(rows, static shown => shown.Command?.Action == HomeMenuAction.Controls);
            save();
        }
        else if (escape || decide && row == Rows_[^1])
        {
            _settingsPage = false;
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
        else if (up || down)
            Selection = move(Selection + (up ? -1 : 1), up ? -1 : 1);
        else if (decide && row.Library is { } library)
            return library.Pick;
        else if (decide && row.Command is { Action: HomeMenuAction.Calibrate })
        {
            // It leaves Song Select: the settings are saved and the menu closes, as with Back then Resume.
            _settingsPage = false;
            save();
            return close(HomeMenuAction.Calibrate);
        }
        else if (decide && row.Command is { Action: HomeMenuAction.Controls })
        {
            _controlsPage = true;
            Selection = 0;
        }
        else if (decide && row.Command is { Action: HomeMenuAction.BindAllPads })
        {
            Selection = Array.FindIndex(rows, static shown => shown.Binding is not null);
            listen(pads: 4);
        }
        else if (decide && row.Command is { Action: HomeMenuAction.ResetControls })
            resetControls();
        else if (decide && row.Binding is not null)
            listen(pads: 0);
        else if (keys.IsDown(SdlKeyboardKey.Delete) && row.Binding is { } binding)
        {
            drum(false);
            var pad = _player * 4 + binding.Pad;
            apply(get() with { Controls = get().Controls.With(pad, string.Join(", ", inputs(get().Controls[pad]).Where(token => !onDevice(token)))) });
        }
        else if (decide && row.Command is { } command && cachedTextures() is not null)
            return command.Action;
        else if (decide && row.Setting is not null)
            _editing = true;
        else if (arrows != 0 && row.Setting is not null)
            change(arrows);
        return HomeMenuAction.None;
    }

    // The calibration's result: Enter or centre saves the Audio Offset (from half a second on, so the
    // confirming big don does not), Escape discards it.
    private HomeMenuAction calibrationInput(LatencyCalibration calibration, SdlKeyboardSnapshot keys, bool escape)
    {
        var decide = keys.IsDown(SdlKeyboardKey.Enter) || keys.IsDown(SdlKeyboardKey.F) || keys.IsDown(SdlKeyboardKey.J);
        if (_calibrationDoneAt == 0)
            _calibrationDoneAt = Stopwatch.GetTimestamp();
        var save = decide && Stopwatch.GetElapsedTime(_calibrationDoneAt) >= TimeSpan.FromSeconds(0.5);
        if (!escape && !save)
            return HomeMenuAction.None;
        drum(save);
        if (save)
            apply(get() with { AudioOffsetMs = calibration.AudioOffsetMs });
        Calibration = null;
        _calibrationDoneAt = 0;
        return close(HomeMenuAction.EndCalibration);
    }

    // A held key repeats its change after RepeatDelay, every RepeatInterval: Left/Right on a setting, and
    // the rims too while it is being edited.
    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400), RepeatInterval = TimeSpan.FromMilliseconds(60);
    private long _heldSince, _repeatedAt;

    private int? repeat(SdlKeyboardSnapshot held)
    {
        var less = held.IsDown(SdlKeyboardKey.Left)
            || _editing && (held.IsDown(SdlKeyboardKey.Down) || held.IsDown(SdlKeyboardKey.D) || held.IsDown(SdlKeyboardKey.Z));
        var more = held.IsDown(SdlKeyboardKey.Right)
            || _editing && (held.IsDown(SdlKeyboardKey.Up) || held.IsDown(SdlKeyboardKey.K) || held.IsDown(SdlKeyboardKey.V));
        var now = Stopwatch.GetTimestamp();
        if (less == more)
        {
            _heldSince = 0;
            return null;
        }
        if (_heldSince == 0)
            _heldSince = _repeatedAt = now; // the press itself changed it already
        if (Stopwatch.GetElapsedTime(_heldSince, now) < RepeatDelay || Stopwatch.GetElapsedTime(_repeatedAt, now) < RepeatInterval)
            return null;
        _repeatedAt = now;
        return more ? 1 : -1;
    }

    // The next selectable row from index on, going in direction (headers are skipped; the list wraps).
    private int move(int index, int direction)
    {
        var shown = rows;
        index = (index % shown.Length + shown.Length) % shown.Length;
        while (shown[index].Header is not null)
            index = ((index + direction) % shown.Length + shown.Length) % shown.Length;
        return index;
    }

    private void change(int direction)
    {
        var now = Stopwatch.GetTimestamp();
        _streak = _lastChangeAt != 0 && Stopwatch.GetElapsedTime(_lastChangeAt, now) < StreakGap ? _streak + 1 : 0;
        _lastChangeAt = now;
        var size = _streak >= 12 ? 10 : _streak >= 4 ? 5 : 1;
        var setting = rows[Selection].Setting!;
        var settings = get();
        apply(setting.Set(settings,
            Math.Clamp(setting.Step(setting.Get(settings), direction * size), setting.Minimum, setting.Maximum)));
    }

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
