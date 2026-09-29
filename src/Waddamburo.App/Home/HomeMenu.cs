using System.Diagnostics;
using System.Globalization;
using Waddamburo.Game.Flow;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Providers.Stock;

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
    /// <summary>Start upscaling every texture the game uses (the bake page shows it).</summary>
    BakeTextures,
    /// <summary>Close the game and start it again (settings that apply at start, memory after a bake).</summary>
    RestartGame,
}

/// <summary>
/// Home mode's Escape menu: the pause choices (gameplay) or the session choices (entry, Song Select),
/// and a settings page. Rims or Up/Down move, centre or Enter picks; on a setting, centre starts
/// editing and the rims (or Left/Right at any time) change it: by 1, or by 5 then 10 when pressed
/// quickly again and again. Each move plays a Ka, each pick a Don.
/// </summary>
/// <param name="cachedTextures">Textures in the upscale cache; null when upscaling is unavailable.</param>
internal sealed class HomeMenu(Func<ArcadeSettings> get, Action<ArcadeSettings> apply, Action save, Action<bool> drum,
    string defaultTjaFolder, Func<int?> cachedTextures)
{
    // Presses closer together than this keep a streak going; its length picks the step.
    private static readonly TimeSpan StreakGap = TimeSpan.FromMilliseconds(200);

    private sealed record Setting(string Label, Func<ArcadeSettings, int> Get, Func<ArcadeSettings, int, ArcadeSettings> Set,
        Func<int, int, int> Step, int Minimum, Func<int, string> Format, int Maximum, AudioBus? Bus = null, string Hint = "");

    private const string Restart = " Applies after a restart.";

    /// <summary>A song library the player points at a folder (picked in the system's folder dialog).</summary>
    private sealed record Library(string Label, Func<ArcadeSettings, string?> Get, HomeMenuAction Pick, bool Nijiiro);

    /// <summary>A row that starts something (the texture bake).</summary>
    private sealed record Command(string Label, HomeMenuAction Action, string Hint);

    // One settings row: a section header, a setting, a song library, a command, or Back (all null).
    private sealed record Row(string? Header = null, Setting? Setting = null, Library? Library = null, Command? Command = null);

    // Background upscaling may take all threads but one (the game needs its own).
    private static readonly int UpscaleThreadsMaximum = Math.Max(1, Environment.ProcessorCount - 1);

    private static readonly Row[] Rows_ =
    [
        new("Songs"),
        new(Library: new("Custom TJA songs", static s => s.TjaFolder, HomeMenuAction.PickTjaFolder, Nijiiro: false)),
        new(Library: new("Nijiiro songs", static s => s.NijiiroFolder, HomeMenuAction.PickNijiiroFolder, Nijiiro: true)),
        new("Volume"),
        new(Setting: volume("Master Volume", static s => s.MasterVolume, static (s, v) => s with { MasterVolume = v }, AudioBus.Bgm,
            "Everything the game plays.")),
        new(Setting: volume("Music Volume", static s => s.MusicVolume, static (s, v) => s with { MusicVolume = v }, AudioBus.Bgm,
            "Songs, previews and menu music.")),
        new(Setting: volume("Drum Volume", static s => s.DrumVolume, static (s, v) => s with { DrumVolume = v }, null,
            "Your drum hits.")),
        new(Setting: volume("Effects Volume", static s => s.EffectsVolume, static (s, v) => s with { EffectsVolume = v },
            AudioBus.MenuSound, "Menu and game sound effects.")),
        new(Setting: volume("Don-chan Voice", static s => s.VoiceVolume, static (s, v) => s with { VoiceVolume = v }, AudioBus.Voice,
            "Don-chan's calls and cheers.")),
        new("Timing"),
        new(Setting: offset("Audio Offset", static s => s.AudioOffsetMs, static (s, v) => s with { AudioOffsetMs = v },
            "How late you hear the music: notes and judgement move this much later.")),
        new(Setting: offset("Input Offset", static s => s.InputOffsetMs, static (s, v) => s with { InputOffsetMs = v },
            "How late your hits arrive: they are judged this much earlier.")),
        new("Sound"),
        new(Setting: toggle("Stereo Panning", static s => s.StereoPanning, static (s, v) => s with { StereoPanning = v },
            "With two players, each side's sounds come from its own speaker.")),
        new(Setting: toggle("Mute in Background", static s => s.MuteInBackground, static (s, v) => s with { MuteInBackground = v },
            "Fade the sound out while the window is in the background.")),
        new(Setting: new("Audio Buffer", static s => s.AudioBufferFrames, static (s, v) => s with { AudioBufferFrames = v },
            static (value, step) => step > 0 ? value * 2 : value / 2, 64,
            static value => $"{value} frames", 2048, Hint: "Smaller is lower latency; raise it if the sound crackles." + Restart)),
        new(Setting: new("Exclusive Audio", static s => s.AudioExclusive ? 1 : 0, static (s, v) => s with { AudioExclusive = v != 0 },
            static (value, _) => 1 - value, 0, static value => value != 0 ? "On" : "Off", 1,
            Hint: "Windows: the sound device for the game alone, for the lowest latency." + Restart)),
        new("Graphics"),
        new(Setting: toggle("Upscaled Textures", static s => s.UpscaleTextures, static (s, v) => s with { UpscaleTextures = v },
            "Sharper textures, upscaled 3x on this PC and kept in its cache. Applies to screens loaded next.")),
        new(Setting: new("Background Upscaling", static s => s.UpscaleThreads, static (s, v) => s with { UpscaleThreads = v },
            static (value, step) => value + Math.Sign(step), 0,
            static value => value == 0 ? "Off" : value == 1 ? "1 thread" : $"{value} threads", UpscaleThreadsMaximum,
            Hint: "CPU threads that upscale textures not in the cache yet while you play (paused during songs).")),
        new(Command: new("Bake All Textures", HomeMenuAction.BakeTextures,
            "Upscale every texture the game uses now, with most of the CPU. Stop at any time; finished ones are kept.")),
        new(),
    ];

    public enum ItemKind { Header, Setting, Library, Command, Back }

    /// <summary>A song library's state: red (not set up), orange (set up, something missing), green.</summary>
    public enum LibraryState { Missing, Warning, Ready }

    /// <summary>One settings row as drawn, with what it does.</summary>
    public readonly record struct Item(ItemKind Kind, string Label, string? Value, string Hint, LibraryState State = default);

    // The folders the running game loaded its songs from: a change needs a restart.
    private readonly ArcadeSettings _atStart = get();
    private readonly Dictionary<(string Path, bool Nijiiro), bool> _valid = [];

    private string[] _choices = [];
    private bool _settingsPage;
    private bool _editing;
    private long _lastChangeAt;
    private int _streak;

    public bool IsOpen { get; private set; }

    public int Selection { get; private set; }

    public string Title => Bake is not null ? "Upscaling Textures" : _restartPrompt ? "Restart?" : _settingsPage ? "Settings" : "Paused";

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

    /// <summary>A line above the choices (the restart prompt's reason), or null.</summary>
    public string? Message => _restartPrompt ? "Some changes need a restart to apply." : null;

    // Leaving the settings asks for a restart when a setting that applies at start changed, or after a
    // bake (the memory it used goes back with the restart); "Later" is not asked again for the same state.
    private bool _restartPrompt;
    private string? _declined;
    private string[] _pauseChoices = [];

    private string restartState()
    {
        var now = get();
        var changed = now.AudioBufferFrames != _atStart.AudioBufferFrames || now.AudioExclusive != _atStart.AudioExclusive
            || now.TjaFolder != _atStart.TjaFolder || now.NijiiroFolder != _atStart.NijiiroFolder;
        return changed || _baked
            ? $"{now.AudioBufferFrames}|{now.AudioExclusive}|{now.TjaFolder}|{now.NijiiroFolder}|{_baked}" : "";
    }

    /// <summary>The pause page's choices (the settings page draws <see cref="Items"/>).</summary>
    public string[] Rows => _choices;

    public bool SettingsPage => _settingsPage;

    /// <summary>The selected setting is being changed (drawn between arrows).</summary>
    public bool Editing => _editing;

    public Item[] Items => [.. Rows_.Select(item)];

    private Item item(Row row) => row switch
    {
        { Header: { } header } => new(ItemKind.Header, header, null, ""),
        { Setting: { } setting } => new(ItemKind.Setting, setting.Label, setting.Format(setting.Get(get())), setting.Hint),
        { Library: { } library } => libraryItem(library),
        { Command: { } command } => cachedTextures() is { } cached
            ? new(ItemKind.Command, command.Label, $"{cached:N0} cached", command.Hint)
            : new(ItemKind.Command, command.Label, "Unavailable", "Upscaling is unavailable on this machine."),
        _ => new(ItemKind.Back, "Back", null, "Save and return."),
    };

    private Item libraryItem(Library library)
    {
        var chosen = library.Get(get());
        var path = chosen ?? (library.Nijiiro ? null : defaultTjaFolder);
        var what = library.Nijiiro ? "your Nijiiro installation" : "the folder with your .tja songs";
        if (path is null)
            return new(ItemKind.Library, library.Label, "Not set up", $"Centre: choose {what}.", LibraryState.Missing);
        path = Path.TrimEndingDirectorySeparator(path);
        if (!valid(path, library.Nijiiro))
            return new(ItemKind.Library, library.Label, "Not set up",
                $"No {(library.Nijiiro ? "Nijiiro data" : ".tja songs")} there. Centre: choose {what}.",
                LibraryState.Missing);
        // Nijiiro music is G.719, which only the user's own vgmstream-cli decodes.
        if (library.Nijiiro && !VgmstreamCli.IsInstalled)
            return new(ItemKind.Library, library.Label, "Needs vgmstream-cli",
                "The music needs vgmstream-cli next to the game: get it from github.com/vgmstream/vgmstream/releases.",
                LibraryState.Warning);
        return new(ItemKind.Library, library.Label, Path.GetFileName(path),
            chosen != library.Get(_atStart) ? "Restart the game to load these songs." : "Centre: choose another folder.",
            LibraryState.Ready);
    }

    // ponytail: remembered per path for the session (the menu redraws every frame); a folder filled
    // while the game runs shows as set up after choosing it again.
    private bool valid(string path, bool nijiiro)
    {
        if (!_valid.TryGetValue((path, nijiiro), out var ok))
        {
            try
            {
                ok = nijiiro ? new NijiiroCatalogProvider(path).Exists
                    : Directory.Exists(path) && Directory.EnumerateFiles(path, "*.tja", SearchOption.AllDirectories).Any();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ok = false;
            }
            _valid[(path, nijiiro)] = ok;
        }
        return ok;
    }

    /// <summary>
    /// The bus to keep a sample playing on while its volume is edited (music for the master; none for
    /// the drum, whose Ka on each change is already its sample).
    /// </summary>
    public AudioBus? PreviewBus => IsOpen && _settingsPage && _editing ? Rows_[Selection].Setting?.Bus : null;

    public void Open(bool gameplay, bool attract = false)
    {
        _choices = gameplay ? ["Resume", "Restart Song", "Settings", "Song Select"]
            : attract ? ["Resume", "Settings"] : ["Resume", "Settings", "Return to Title"];
        IsOpen = true;
        Selection = 0;
        _settingsPage = _editing = _restartPrompt = false;
        VgmstreamCli.Recheck(); // it may have been installed while the game ran
    }

    /// <summary>One tick of menu input (key pulses); returns what the shell should do.</summary>
    public HomeMenuAction Input(SdlKeyboardSnapshot keys, bool escape, SdlKeyboardSnapshot held)
    {
        var up = keys.IsDown(SdlKeyboardKey.Up) || keys.IsDown(SdlKeyboardKey.D) || keys.IsDown(SdlKeyboardKey.Z);
        var down = keys.IsDown(SdlKeyboardKey.Down) || keys.IsDown(SdlKeyboardKey.K) || keys.IsDown(SdlKeyboardKey.V);
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
                Selection = Array.IndexOf(_choices, "Settings");
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
                    case "Settings":
                        _settingsPage = true;
                        Selection = move(0, 1);
                        break;
                    case "Restart Song": return close(HomeMenuAction.Restart);
                    case "Song Select": return close(HomeMenuAction.SongSelect);
                    case "Return to Title": return close(HomeMenuAction.Title);
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
        var row = Rows_[Selection];
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
        else if (escape || decide && row == Rows_[^1])
        {
            _settingsPage = false;
            Selection = Array.IndexOf(_choices, "Settings");
            save();
            if (restartState() is { Length: > 0 } state && state != _declined)
            {
                _restartPrompt = true;
                _pauseChoices = _choices;
                _choices = ["Restart Now", "Later"];
                Selection = 0;
            }
        }
        else if (up || down)
            Selection = move(Selection + (up ? -1 : 1), up ? -1 : 1);
        else if (decide && row.Library is { } library)
            return library.Pick;
        else if (decide && row.Command is { } command && cachedTextures() is not null)
            return command.Action;
        else if (decide && row.Setting is not null)
            _editing = true;
        else if (arrows != 0 && row.Setting is not null)
            change(arrows);
        return HomeMenuAction.None;
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
    private static int move(int index, int direction)
    {
        index = (index % Rows_.Length + Rows_.Length) % Rows_.Length;
        while (Rows_[index].Header is not null)
            index = ((index + direction) % Rows_.Length + Rows_.Length) % Rows_.Length;
        return index;
    }

    private void change(int direction)
    {
        var now = Stopwatch.GetTimestamp();
        _streak = _lastChangeAt != 0 && Stopwatch.GetElapsedTime(_lastChangeAt, now) < StreakGap ? _streak + 1 : 0;
        _lastChangeAt = now;
        var size = _streak >= 12 ? 10 : _streak >= 4 ? 5 : 1;
        var setting = Rows_[Selection].Setting!;
        var settings = get();
        apply(setting.Set(settings,
            Math.Clamp(setting.Step(setting.Get(settings), direction * size), setting.Minimum, setting.Maximum)));
    }

    private HomeMenuAction close(HomeMenuAction action)
    {
        IsOpen = false;
        return action;
    }

    private static Setting volume(string label, Func<ArcadeSettings, int> read, Func<ArcadeSettings, int, ArcadeSettings> write,
        AudioBus? bus, string hint) =>
        new(label, read, write, static (value, step) => value + step, 0, static value => $"{value}%", 100, bus, hint);

    private static Setting toggle(string label, Func<ArcadeSettings, bool> read, Func<ArcadeSettings, bool, ArcadeSettings> write,
        string hint) =>
        new(label, s => read(s) ? 1 : 0, (s, v) => write(s, v != 0), static (value, _) => 1 - value, 0,
            static value => value != 0 ? "On" : "Off", 1, Hint: hint);

    private static Setting offset(string label, Func<ArcadeSettings, int> read, Func<ArcadeSettings, int, ArcadeSettings> write,
        string hint) =>
        new(label, read, write, static (value, step) => value + step, -500,
            static value => value.ToString("+0;-0;0", CultureInfo.InvariantCulture) + " ms", 500, Hint: hint);
}
