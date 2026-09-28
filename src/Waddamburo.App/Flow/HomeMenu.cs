using System.Globalization;
using Waddamburo.Game.Flow;
using Waddamburo.Platform.Sdl;

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
/// editing and the rims (or Left/Right at any time) change it.
/// </summary>
internal sealed class HomeMenu(Func<ArcadeSettings> get, Action<ArcadeSettings> apply, Action save)
{
    private sealed record Setting(string Label, Func<ArcadeSettings, int> Get, Func<ArcadeSettings, int, ArcadeSettings> Set,
        Func<int, int, int> Step, int Minimum, Func<int, string> Format, int Maximum);

    private static readonly Setting[] Settings =
    [
        volume("Master Volume", static s => s.MasterVolume, static (s, v) => s with { MasterVolume = v }),
        volume("Music Volume", static s => s.MusicVolume, static (s, v) => s with { MusicVolume = v }),
        volume("Drum Volume", static s => s.DrumVolume, static (s, v) => s with { DrumVolume = v }),
        volume("Effects Volume", static s => s.EffectsVolume, static (s, v) => s with { EffectsVolume = v }),
        volume("Don-chan Voice", static s => s.VoiceVolume, static (s, v) => s with { VoiceVolume = v }),
        offset("Audio Offset", static s => s.AudioOffsetMs, static (s, v) => s with { AudioOffsetMs = v }),
        offset("Input Offset", static s => s.InputOffsetMs, static (s, v) => s with { InputOffsetMs = v }),
        new("Audio Buffer", static s => s.AudioBufferFrames, static (s, v) => s with { AudioBufferFrames = v },
            static (value, direction) => direction > 0 ? value * 2 : value / 2, 64,
            static value => $"{value} (restart)", 2048),
    ];

    private string[] _choices = [];
    private bool _settingsPage;
    private bool _editing;

    public bool IsOpen { get; private set; }

    public int Selection { get; private set; }

    public string Title => _settingsPage ? "Settings" : "Paused";

    /// <summary>The rows as drawn: an edited setting is shown between arrows.</summary>
    public string[] Rows => _settingsPage
        ? [.. Settings.Select((setting, index) => index == Selection && _editing
            ? $"< {setting.Label}: {setting.Format(setting.Get(get()))} >"
            : $"{setting.Label}: {setting.Format(setting.Get(get()))}"), "Back"]
        : _choices;

    public void Open(bool gameplay)
    {
        _choices = gameplay ? ["Resume", "Restart Song", "Settings", "Song Select"] : ["Resume", "Settings", "Return to Title"];
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
        var setting = Settings[Selection];
        var settings = get();
        apply(setting.Set(settings,
            Math.Clamp(setting.Step(setting.Get(settings), direction), setting.Minimum, setting.Maximum)));
    }

    private HomeMenuAction close(HomeMenuAction action)
    {
        IsOpen = false;
        return action;
    }

    // ponytail: one step per press (no key repeat); add repeat if calibrating by 5 ms steps gets tedious.
    private static Setting volume(string label, Func<ArcadeSettings, int> read, Func<ArcadeSettings, int, ArcadeSettings> write) =>
        new(label, read, write, static (value, direction) => value + direction * 5, 0, static value => $"{value}%", 100);

    private static Setting offset(string label, Func<ArcadeSettings, int> read, Func<ArcadeSettings, int, ArcadeSettings> write) =>
        new(label, read, write, static (value, direction) => value + direction * 5, -500,
            static value => value.ToString("+0;-0;0", CultureInfo.InvariantCulture) + " ms", 500);
}
