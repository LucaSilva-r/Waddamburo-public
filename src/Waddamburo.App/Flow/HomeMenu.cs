using System.Diagnostics;
using System.Globalization;
using Waddamburo.Game.Flow;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Flow;

internal enum HomeMenuAction
{
    None,
    Resume,
    Restart,
    SongSelect,
    Title,
}

/// <summary>
/// Home mode's Escape menu: the pause choices (gameplay) or the session choices (entry, Song Select),
/// and a settings page. Rims or Up/Down move, centre or Enter picks; on a setting, centre starts
/// editing and the rims (or Left/Right at any time) change it: by 1, or by 5 then 10 when pressed
/// quickly again and again. Each move plays a Ka, each pick a Don.
/// </summary>
internal sealed class HomeMenu(Func<ArcadeSettings> get, Action<ArcadeSettings> apply, Action save, Action<bool> drum)
{
    // Presses closer together than this keep a streak going; its length picks the step.
    private static readonly TimeSpan StreakGap = TimeSpan.FromMilliseconds(200);

    private sealed record Setting(string Label, Func<ArcadeSettings, int> Get, Func<ArcadeSettings, int, ArcadeSettings> Set,
        Func<int, int, int> Step, int Minimum, Func<int, string> Format, int Maximum, AudioBus? Bus = null);

    private static readonly Setting[] Settings =
    [
        volume("Master Volume", static s => s.MasterVolume, static (s, v) => s with { MasterVolume = v }, AudioBus.Bgm),
        volume("Music Volume", static s => s.MusicVolume, static (s, v) => s with { MusicVolume = v }, AudioBus.Bgm),
        volume("Drum Volume", static s => s.DrumVolume, static (s, v) => s with { DrumVolume = v }, null),
        volume("Effects Volume", static s => s.EffectsVolume, static (s, v) => s with { EffectsVolume = v }, AudioBus.MenuSound),
        volume("Don-chan Voice", static s => s.VoiceVolume, static (s, v) => s with { VoiceVolume = v }, AudioBus.Voice),
        offset("Audio Offset", static s => s.AudioOffsetMs, static (s, v) => s with { AudioOffsetMs = v }),
        offset("Input Offset", static s => s.InputOffsetMs, static (s, v) => s with { InputOffsetMs = v }),
        toggle("Stereo Panning", static s => s.StereoPanning, static (s, v) => s with { StereoPanning = v }),
        toggle("Mute in Background", static s => s.MuteInBackground, static (s, v) => s with { MuteInBackground = v }),
        new("Audio Buffer", static s => s.AudioBufferFrames, static (s, v) => s with { AudioBufferFrames = v },
            static (value, step) => step > 0 ? value * 2 : value / 2, 64,
            static value => $"{value} (restart)", 2048),
    ];

    private string[] _choices = [];
    private bool _settingsPage;
    private bool _editing;
    private long _lastChangeAt;
    private int _streak;

    public bool IsOpen { get; private set; }

    public int Selection { get; private set; }

    public string Title => _settingsPage ? "Settings" : "Paused";

    /// <summary>The rows as drawn: an edited setting is shown between arrows.</summary>
    public string[] Rows => _settingsPage
        ? [.. Settings.Select((setting, index) => index == Selection && _editing
            ? $"< {setting.Label}: {setting.Format(setting.Get(get()))} >"
            : $"{setting.Label}: {setting.Format(setting.Get(get()))}"), "Back"]
        : _choices;

    /// <summary>
    /// The bus to keep a sample playing on while its volume is edited (music for the master; none for
    /// the drum, whose Ka on each change is already its sample).
    /// </summary>
    public AudioBus? PreviewBus => IsOpen && _settingsPage && _editing && Selection < Settings.Length
        ? Settings[Selection].Bus : null;

    public void Open(bool gameplay, bool attract = false)
    {
        _choices = gameplay ? ["Resume", "Restart Song", "Settings", "Song Select"]
            : attract ? ["Resume", "Settings"] : ["Resume", "Settings", "Return to Title"];
        IsOpen = true;
        Selection = 0;
        _settingsPage = _editing = false;
    }

    /// <summary>One tick of menu input (key pulses); returns what the shell should do.</summary>
    public HomeMenuAction Input(SdlKeyboardSnapshot keys, bool escape)
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
                        Selection = 0;
                        break;
                    case "Restart Song": return close(HomeMenuAction.Restart);
                    case "Song Select": return close(HomeMenuAction.SongSelect);
                    case "Return to Title": return close(HomeMenuAction.Title);
                    default: return close(HomeMenuAction.Resume);
                }
            return HomeMenuAction.None;
        }
        var back = Selection == Settings.Length;
        if (_editing)
        {
            if (escape || decide)
                _editing = false;
            else if (up || down || arrows != 0)
                change(arrows != 0 ? arrows : up ? -1 : 1);
        }
        else if (escape || decide && back)
        {
            _settingsPage = false;
            Selection = Array.IndexOf(_choices, "Settings");
            save();
        }
        else if (up || down)
            Selection = (Selection + (up ? Settings.Length : 1)) % (Settings.Length + 1);
        else if (decide)
            _editing = true;
        else if (arrows != 0 && !back)
            change(arrows);
        return HomeMenuAction.None;
    }

    private void change(int direction)
    {
        var now = Stopwatch.GetTimestamp();
        _streak = _lastChangeAt != 0 && Stopwatch.GetElapsedTime(_lastChangeAt, now) < StreakGap ? _streak + 1 : 0;
        _lastChangeAt = now;
        var size = _streak >= 12 ? 10 : _streak >= 4 ? 5 : 1;
        var setting = Settings[Selection];
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
        AudioBus? bus) =>
        new(label, read, write, static (value, step) => value + step, 0, static value => $"{value}%", 100, bus);

    private static Setting toggle(string label, Func<ArcadeSettings, bool> read, Func<ArcadeSettings, bool, ArcadeSettings> write) =>
        new(label, s => read(s) ? 1 : 0, (s, v) => write(s, v != 0), static (value, _) => 1 - value, 0,
            static value => value != 0 ? "On" : "Off", 1);

    private static Setting offset(string label, Func<ArcadeSettings, int> read, Func<ArcadeSettings, int, ArcadeSettings> write) =>
        new(label, read, write, static (value, step) => value + step, -500,
            static value => value.ToString("+0;-0;0", CultureInfo.InvariantCulture) + " ms", 500);
}
