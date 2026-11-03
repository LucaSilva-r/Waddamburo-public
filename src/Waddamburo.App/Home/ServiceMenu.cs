using System.Globalization;
using System.Text;
using Waddamburo.App.Flow;
using Waddamburo.App.Hosting;
using Waddamburo.Game.Flow;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.Platform.Sdl.Text;

namespace Waddamburo.App.Home;

/// <summary>
/// The arcade cabinet's service (test) menu, opened by Escape (the test switch) from anywhere: a global recovery
/// screen that stops everything, drops the credits and, once left, restarts the game. Plain text on black as on the
/// cabinet: arrows choose, Enter enters a page or a value (arrows change it, Enter again sets it); the drums only
/// sound in the I/O and sound tests. The selected row flashes red every 500 ms; a value is green at its default and
/// yellow when changed; cyan rows are information. The pages are Waddamburo's subset of the cabinet's.
/// </summary>
internal sealed class ServiceMenu(SdlApplication application, string fontPath, Bookkeeping book) : IDisposable
{
    private static readonly RenderColor White = new(1, 1, 1, 1), Red = new(0.92f, 0.05f, 0.05f, 1),
        Green = new(0.1f, 0.92f, 0.1f, 1), Yellow = new(0.95f, 0.92f, 0.1f, 1), Cyan = new(0.05f, 0.85f, 0.9f, 1);
    private static readonly ArcadeSettings Defaults = new();
    // Stage units (1280x720): the cabinet's 1080p layout at two thirds.
    private const float FontSize = 20, Half = 10.6f, RowSpacing = 30, FirstRow = 150;

    // A row: its label at x, its value (a {braced} part takes the value colour) at valueX; Step changes the value,
    // Enter acts; an info row (neither) is not selectable.
    private sealed record Row(string Label, float X = 480, Func<string>? Value = null, float ValueX = 690,
        Action<int>? Step = null, Func<bool>? IsDefault = null, Action? Enter = null, bool Gap = false, bool Cyan = false,
        string Hint = "", Func<RenderColor>? Colour = null);

    // Section: one of the settings page's sections (its title in _section).
    private enum Page { Main, Coin, Game, Sound, Io, Bookkeeping, Others, Section, Drums, Libraries, Network }

    /// <summary>The server check behind NETWORK OPTIONS (null: no server set).</summary>
    public Online.ServerHealth? Health { get; set; }

    // DRUM CONTROLS: the player and device shown, a pad waiting for its input (guided: pads still to ask for in SET UP
    // ALL PADS, the waiting one included).
    private int _player, _device, _guided;
    private bool _listening;
    // SONG LIBRARIES: the folder picker is open for this library (tja, osu, official); a problem with the last pick.
    private string? _picking;
    private string? _message;
    private Action<ArcadeSettings>? _apply;
    private Action? _rehash;
    private string _defaultTja = "";
    private static readonly string[] Devices = ["KEYBOARD", "CONTROLLER", "MIDI"];

    // The settings page's sections, by title (the language and volume ones go in OTHERS and SOUND TEST).
    private static readonly string LanguageSection = Strings.T("section.language"), VolumeSection = Strings.T("section.volume"),
        SongsSection = Strings.T("section.songs"), DisplaySection = Strings.T("section.display"),
        GraphicsSection = Strings.T("section.graphics");
    private string _section = "";
    // Held arrows repeat (a volume goes 0-100 by ones): after 24 ticks, every 4.
    private SdlKeyboardKey? _repeatKey;
    private int _repeatTicks;

    private readonly Dictionary<(int Scalar, uint Scale), (RenderTextureId? Texture, GlyphMasks Glyph)> _glyphs = [];
    private Page _page;
    private int _selection;
    private bool _editing;
    private int _tick;
    private ArcadeSettings _settings = Defaults;
    private int _credits;
    private readonly int[] _hits = new int[8];
    private readonly bool[] _down = new bool[8];
    private Action<bool>? _drum;
    private Action<ArcadeSettings>? _exit;

    public bool IsOpen { get; private set; }

    /// <summary>
    /// Opens on <paramref name="settings"/>; <paramref name="credits"/> were left (shown, then dropped).
    /// <paramref name="drum"/> sounds a don (true) or ka; <paramref name="exit"/> saves the edited settings and restarts.
    /// </summary>
    /// <remarks><paramref name="apply"/>: each change, for what takes effect at once (the drum bindings, tested here);
    /// <paramref name="rehash"/>: SONG LIBRARIES' rehash; <paramref name="defaultTja"/>: the TJA folder when none is set.</remarks>
    public void Open(ArcadeSettings settings, int credits, Action<bool> drum, Action<ArcadeSettings> exit,
        Action<ArcadeSettings> apply, Action rehash, string defaultTja)
    {
        (_settings, _credits, _drum, _exit, _apply, _rehash, _defaultTja) = (settings, credits, drum, exit, apply, rehash, defaultTja);
        (_listening, _picking, _message) = (false, null, null);
        (_page, _selection, _editing, IsOpen) = (Page.Main, 0, false, true);
        Array.Clear(_hits);
        Console.WriteLine($"Service menu opened ({credits} credit(s) dropped).");
    }

    /// <summary>One tick: <paramref name="pulses"/> the keys that went down, <paramref name="held"/> those held.</summary>
    public void Tick(SdlKeyboardSnapshot pulses, SdlKeyboardSnapshot held)
    {
        _tick++;
        if (_picking is { } library && SdlApplication.TryTakePickedFolder(out var folder))
        {
            _picking = null;
            _message = null;
            set(s => library switch
            {
                "tja" => s with { TjaFolder = folder },
                "osu" => s with { OsuFolder = folder },
                _ => HomeControls.AddOfficialFolder(s, folder, problem => _message = problem),
            });
        }
        if (_listening)
        {
            listening();
            return;
        }
        SdlKeyboardKey[] arrows = [SdlKeyboardKey.Up, SdlKeyboardKey.Down, SdlKeyboardKey.Left, SdlKeyboardKey.Right];
        if (_repeatKey is { } repeating && held.IsDown(repeating))
            _repeatTicks++;
        else
            (_repeatKey, _repeatTicks) = (arrows.FirstOrDefault(key => pulses.IsDown(key)) is var first && pulses.IsDown(first) ? first : null, 0);
        bool pressed(SdlKeyboardKey key) => pulses.IsDown(key)
            || key == _repeatKey && _repeatTicks >= 24 && (_repeatTicks - 24) % 4 == 0;
        for (var pad = 0; pad < 8; pad++)
        {
            var down = held.IsDown(SdlInputBindings.Pads[pad]);
            if (down && !_down[pad])
            {
                _hits[pad]++;
                if (_page is Page.Sound or Page.Io)
                    _drum?.Invoke(pad % 4 is 1 or 2); // the middle pads are the drum's face (don), the outer its rim
            }
            _down[pad] = down;
        }
        var rows = Rows();
        var selectable = Enumerable.Range(0, rows.Length).Where(index => rows[index].Step is not null || rows[index].Enter is not null).ToArray();
        _selection = Math.Clamp(_selection, 0, selectable.Length - 1);
        var row = rows[selectable[_selection]];
        if (_editing)
        {
            var step = pressed(SdlKeyboardKey.Up) || pressed(SdlKeyboardKey.Right) ? 1
                : pressed(SdlKeyboardKey.Down) || pressed(SdlKeyboardKey.Left) ? -1 : 0;
            if (step != 0)
                row.Step!(step);
            if (pressed(SdlKeyboardKey.Enter))
                _editing = false;
            return;
        }
        if (pressed(SdlKeyboardKey.Up))
            _selection = (_selection + selectable.Length - 1) % selectable.Length;
        if (pressed(SdlKeyboardKey.Down))
            _selection = (_selection + 1) % selectable.Length;
        if (!pressed(SdlKeyboardKey.Enter))
            return;
        if (row.Step is not null)
            _editing = true;
        else
            row.Enter!();
    }

    private void open(Page page, string section = "") => (_page, _selection, _section) = (page, 0, section);
    private void back() => open(Page.Main);
    private void set(Func<ArcadeSettings, ArcadeSettings> change)
    {
        _settings = change(_settings);
        _apply?.Invoke(_settings);
    }

    // The platform holds the next input back for the pad; Escape there cancels (ending SET UP ALL PADS).
    private void listening()
    {
        if (application.Capturing)
            return;
        if (application.TakeCaptured() is not { } captured)
            _listening = false;
        else if (ControlBindings.DeviceOf(captured) != _device)
            application.BeginCapture(); // another device's input is not for this page
        else
        {
            _drum?.Invoke(true);
            var pad = _selection - 2; // the pad rows follow PLAYER and DEVICE, then COIN and SERVICE
            set(s => pad < 4
                ? s with { Controls = ControlBindings.Bind(s.Controls, _player * 4 + pad, captured, replace: _guided > 0, _device) }
                : s with { Buttons = ControlBindings.BindButton(s.Buttons, CoinButton + pad - 4, captured, _device) });
            if (_guided > 1)
            {
                _selection++;
                listen(_guided - 1);
            }
            else
                _listening = false;
        }
    }

    private void listen(int pads)
    {
        (_guided, _listening) = (pads, true);
        application.BeginCapture();
    }

    private void pick(string library, string? start)
    {
        _picking = library;
        application.PickFolder(start);
    }

    // The coin switch's place in ArcadeSettings.Buttons (the service switch's follows).
    private const int CoinButton = 7;

    // A pad's (0-3) or the coin and service switches' (4, 5) inputs on the shown device, by name.
    private string padValue(int pad) =>
        ControlBindings.Inputs(pad < 4 ? _settings.Controls[_player * 4 + pad] : _settings.Buttons[CoinButton + pad - 4]).Where(token => ControlBindings.OnDevice(token, _device))
            .Select(ControlBindings.Describe).ToArray() is { Length: > 0 } names ? upper(string.Join(" / ", names)) : "NOT SET";

    private static string folderName(string? path) =>
        path is null ? "NOT SET" : upper(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
    private static int wrap(int value, int step, int min, int max) => value + step > max ? min : value + step < min ? max : value + step;

    private Row[] Rows() => _page switch
    {
        Page.Main =>
        [
            new("COIN OPTIONS", Enter: () => open(Page.Coin)),
            new("GAME OPTIONS", Enter: () => open(Page.Game)),
            new("NETWORK OPTIONS", Enter: () => open(Page.Network)),
            new("I/O TEST", Enter: () => open(Page.Io)),
            new("SOUND TEST", Enter: () => open(Page.Sound)),
            // The rest of Waddamburo's settings, a page per section of the home settings.
            .. HomeMenu.Sections.Where(static section => section.Title != LanguageSection && section.Title != VolumeSection
                    && section.Title != SongsSection && section.Title != GraphicsSection)
                .Select(section => new Row(upper(section.Title), Enter: () => open(Page.Section, section.Title))),
            new("DRUM CONTROLS", Enter: () => open(Page.Drums)),
            new("SONG LIBRARIES", Enter: () => open(Page.Libraries)),
            new("BOOKKEEPING", Enter: () => open(Page.Bookkeeping)),
            new("OTHERS", Enter: () => open(Page.Others)),
            new("EXIT", Gap: true, Enter: () => { IsOpen = false; _exit?.Invoke(_settings); }),
            new("LAST GAME STATUS", 440),
            new("COIN USE", 460),
            new("LEFT CREDIT", 460, () => $"{_credits}", 610),
            new("USE CREDIT", 460, () => $"{_settings.CreditsOnePlayer}", 610),
        ],
        Page.Coin =>
        [
            new("COIN VALUE", Value: () => $"1 COIN AS {{{_settings.CreditsPerCoin}}} CREDIT(S)",
                Step: step => set(s => s with { CreditsPerCoin = wrap(s.CreditsPerCoin, step, 1, 9) }),
                IsDefault: () => _settings.CreditsPerCoin == Defaults.CreditsPerCoin),
            new("1P GAME COST", Value: () => $"{{{_settings.CreditsOnePlayer}}} CREDIT(S)",
                Step: step => set(s => wrap(s.CreditsOnePlayer, step, 1, 9) is var cost
                    ? s with { CreditsOnePlayer = cost, CreditsTwoPlayers = Math.Max(cost, s.CreditsTwoPlayers) } : s),
                IsDefault: () => _settings.CreditsOnePlayer == Defaults.CreditsOnePlayer),
            // Two players never cost less than one (the settings file refuses it).
            new("2P GAME COST", Value: () => $"{{{_settings.CreditsTwoPlayers}}} CREDIT(S)",
                Step: step => set(s => s with { CreditsTwoPlayers = wrap(s.CreditsTwoPlayers, step, s.CreditsOnePlayer, 9) }),
                IsDefault: () => _settings.CreditsTwoPlayers == Defaults.CreditsTwoPlayers),
            new("FREE PLAY", Value: () => _settings.FreePlay ? "{ON}" : "{OFF}",
                Step: _ => set(s => s with { FreePlay = !s.FreePlay }), IsDefault: () => _settings.FreePlay == Defaults.FreePlay),
            new("EXIT", Gap: true, Enter: back),
        ],
        Page.Game =>
        [
            new("NUMBER OF STAGES", 400, () => $"{{{_settings.SongsPerSession}}} STAGE(S)", 800,
                Step: step => set(s => s with { SongsPerSession = wrap(s.SongsPerSession, step, 1, 5) }),
                IsDefault: () => _settings.SongsPerSession == Defaults.SongsPerSession),
            .. shared(SongsSection),
            new("EXIT", 400, Gap: true, Enter: back),
        ],
        Page.Sound =>
        [
            .. shared(VolumeSection),
            new("EXIT", 400, Gap: true, Enter: back),
            new(Strings.Language == "ja" ? "面とふちを叩くと、太鼓を叩いた時の音が鳴ります。" : "Hit the drum's face and rim to hear the drum.", 400),
        ],
        Page.Io =>
        [
            .. Enumerable.Range(0, 8).Select(pad => new Row($"{pad / 4 + 1}P {PadNames[pad % 4]}",
                Value: () => (_down[pad] ? "{ON} " : "OFF ") + _hits[pad].ToString(CultureInfo.InvariantCulture))),
            new("EXIT", Gap: true, Enter: back),
        ],
        Page.Bookkeeping =>
        [
            new("POWER ON TIME", Value: () => time(book.PowerOnTicks), ValueX: 730),
            new("TOTAL PLAY TIME", Value: () => time(book.PlayTicks), ValueX: 730),
            new("AVE. PLAY TIME", Value: () => time(book.PlayCount == 0 ? 0 : book.PlayTicks / book.PlayCount), ValueX: 730),
            new("PLAY ON RATIO", Value: () => string.Create(CultureInfo.InvariantCulture,
                $"{(book.PowerOnTicks == 0 ? 0 : 100d * book.PlayTicks / book.PowerOnTicks):0.0}%"), ValueX: 730),
            new("PLAY COUNT", Value: () => $"{book.PlayCount}", ValueX: 730),
            new("COIN COUNT", Value: () => $"{book.CoinCount}", ValueX: 730),
            new("FREE PLAY COUNT", Value: () => $"{book.FreePlayCount}", ValueX: 730),
            new("POWER ON COUNT", Value: () => $"{book.PowerOnCount}", ValueX: 730, Cyan: true),
            new("BOOKKEEPING INITIALIZE", Gap: true, Enter: book.Reset),
            new("EXIT", Enter: back),
        ],
        Page.Others =>
        [
            new("VERSION", 400, () => SelfUpdate.WindowTitle, 560, Cyan: true),
            new("CLOCK", 400, () => DateTime.Now.ToString("yyyy/MM/dd ddd HH:mm:ss", CultureInfo.InvariantCulture).ToUpperInvariant(), 560, Cyan: true),
            .. shared(LanguageSection),
            new("EXIT", 400, Gap: true, Enter: back),
        ],
        // DISPLAY takes the graphics settings too (one page for the picture).
        Page.Section => [.. shared(_section), .. _section == DisplaySection ? shared(GraphicsSection) : [],
            new("EXIT", 400, Gap: true, Enter: back)],
        Page.Network =>
        [
            new("SERVER", 400, () => _settings.Server is { } server ? server.ToString().ToUpperInvariant() : "NOT SET", 640, Cyan: true),
            new("CABINET ID", 400, () => Online.CabinetPairing.CabinetId.ToUpperInvariant(), 640, Cyan: true),
            new("CABINET TOKEN", 400, () => _settings.CabinetToken is null ? "NOT SET" : "SET", 640, Cyan: true),
            new("STATUS", 400, () => $"{{{linkText()}}}", 640, Gap: true, Colour: linkColour),
            new("LAST CHECK", 400, () => Health?.LastCheck is { } at
                ? string.Create(CultureInfo.InvariantCulture, $"{at:HH:mm:ss} ({(int)(DateTime.Now - at).TotalSeconds}S AGO)") : "-", 640),
            new("RESPONSE TIME", 400, () => Health?.RoundTrip is { } trip
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)trip.TotalMilliseconds} MS") : "-", 640),
            new("TEST CONNECTION", 400, Gap: true, Enter: () => Health?.CheckNow(),
                Hint: "CHECKS THE SERVER AND THE CABINET TOKEN NOW (OTHERWISE EVERY 15 SECONDS). THE SERVER AND TOKEN ARE SET IN CONFIG.CFG."),
            new("EXIT", 400, Gap: true, Enter: back),
        ],
        Page.Drums =>
        [
            new("PLAYER", 400, () => $"{{{_player + 1}P}}", 640, _ => _player = 1 - _player, () => true),
            new("DEVICE", 400, () => $"{{{Devices[_device]}}}", 640, step => _device = wrap(_device, step, 0, 2), () => true),
            .. PadNames.Concat(["COIN", "SERVICE"]).Select((name, pad) => new Row(name, 400,
                () => _listening && _selection == pad + 2 ? (_tick / 30 % 2 == 0 ? Prompts[_device] : "") : padValue(pad), 640,
                Enter: () => listen(0), Hint: "ENTER, THEN PRESS THE INPUT FOR THIS PAD (ESCAPE CANCELS). IT JOINS THE PAD'S OTHERS.")),
            new("SET UP ALL PADS", 400, Gap: true, Enter: () => { _selection = 2; listen(4); },
                Hint: "ASKS FOR THE FOUR PADS IN TURN, REPLACING THEIR INPUTS ON THIS DEVICE."),
            new("RESET TO DEFAULT", 400, Enter: () => set(s => s with { Controls = ControlBindings.Reset(s.Controls, _player, _device) }),
                Hint: "THIS PLAYER'S PADS GET THEIR DEFAULT INPUTS ON THIS DEVICE BACK."),
            new("EXIT", 400, Gap: true, Enter: back),
        ],
        Page.Libraries =>
        [
            new("CUSTOM TJA SONGS", 400, () => folderName(_settings.TjaFolder ?? _defaultTja), 640,
                Enter: () => pick("tja", _settings.TjaFolder ?? _defaultTja), Hint: "ENTER OPENS THE FOLDER PICKER."),
            new("OSU!LAZER SONGS", 400, () => folderName(_settings.OsuFolder), 640,
                Enter: () => pick("osu", _settings.OsuFolder), Hint: "ENTER OPENS THE FOLDER PICKER."),
            new("OFFICIAL SONGS", 400, Gap: true),
            .. ArcadeSettings.Folders(_settings.StockFolders).Select(folder => new Row("  RELEASE", 400, () => folderName(folder), 640,
                Enter: () => set(s => s with { StockFolders = ArcadeSettings.JoinFolders(ArcadeSettings.Folders(s.StockFolders).Where(f => f != folder)) }),
                Hint: "ENTER REMOVES IT FROM THE LIBRARY.")),
            .. ArcadeSettings.Folders(_settings.NijiiroFolder).Select(folder => new Row("  NIJIIRO", 400, () => folderName(folder), 640,
                Enter: () => set(s => s with { NijiiroFolder = ArcadeSettings.JoinFolders(ArcadeSettings.Folders(s.NijiiroFolder).Where(f => f != folder)) }),
                Hint: "ENTER REMOVES IT FROM THE LIBRARY.")),
            new("ADD A RELEASE OR NIJIIRO", 400, Enter: () => pick("official", null),
                Hint: _message ?? "ANOTHER RELEASE'S GAME DATA OR A NIJIIRO INSTALLATION, JOINED TO THE GAME'S OWN SONGS."),
            new("REHASH LIBRARY", 400, Gap: true, Enter: () => _rehash?.Invoke(),
                Hint: "WORKS THE LIBRARY'S CHART HASHES OUT AGAIN (IN THE BACKGROUND)."),
            new("EXIT", 400, Gap: true, Enter: back),
        ],
        _ => [],
    };

    private static string upper(string text) => text.ToUpperInvariant();

    // The link as the last check found it: green online, yellow for what the cabinet can fix (its token), red when the
    // server can't be reached.
    private string linkText() => Health is not { } health ? "OFFLINE (NO SERVER SET)" : health.State switch
    {
        Online.LinkState.Checking => "CHECKING...",
        Online.LinkState.Online => _settings.Home ? "ONLINE" : "ONLINE (CABINET AUTHORIZED)",
        Online.LinkState.NoInternet => "NO INTERNET (SERVER NAME NOT FOUND)",
        Online.LinkState.Unreachable => "SERVER UNREACHABLE",
        Online.LinkState.Timeout => "NO ANSWER (TIMED OUT)",
        Online.LinkState.ServerError => string.Create(CultureInfo.InvariantCulture, $"SERVER ERROR ({health.StatusCode})"),
        Online.LinkState.NoToken => "NO CABINET TOKEN",
        _ => "UNAUTHORIZED (CABINET TOKEN REFUSED)",
    };

    private RenderColor linkColour() => Health?.IconType switch { 0 => Green, 2 => Red, _ => Yellow };

    // A section of the home settings as service rows: shown ones only, stepped within their range; green at the
    // value a fresh settings file has.
    private IEnumerable<Row> shared(string section) => HomeMenu.Sections.Where(entry => entry.Title == section)
        .SelectMany(static entry => entry.Settings)
        .Where(setting => setting.Shown?.Invoke(_settings) ?? true)
        .Select(setting => new Row(upper(setting.Label), 400, () => $"{{{upper(setting.Show(_settings))}}}", 800,
            step => set(s => setting.Set(s, Math.Clamp(setting.Step(setting.Get(s), step), setting.Minimum, setting.Maximum))),
            () => setting.Get(_settings) == setting.Get(Defaults), Hint: setting.Hint));

    private static readonly string[] PadNames = ["KA (LEFT)", "DON (LEFT)", "DON (RIGHT)", "KA (RIGHT)"];
    private static readonly string[] Prompts = ["PRESS A KEY...", "PRESS A BUTTON...", "HIT THE PAD..."];

    // H:MM'SS, as the cabinet counts its hours.
    private static string time(long ticks)
    {
        var span = TimeSpan.FromSeconds(ticks / 60d);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}'{span.Seconds:00}");
    }

    private static string title(Page page) => page switch
    {
        Page.Main => "MENU",
        Page.Coin => "COIN OPTIONS",
        Page.Game => "GAME OPTIONS",
        Page.Sound => "SOUND TEST",
        Page.Io => "I/O TEST",
        Page.Bookkeeping => "BOOKKEEPING",
        Page.Others => "OTHERS",
        Page.Network => "NETWORK OPTIONS",
        Page.Drums => "DRUM CONTROLS",
        Page.Libraries => "SONG LIBRARIES",
        _ => "",
    };

    public RenderFrame Frame()
    {
        var quads = new List<RenderQuad>();
        var scale = Waddamburo.App.Presentation.OverlayPainter.Scale720(application);
        var flash = _tick / 30 % 2 == 0;
        centred(quads, _page == Page.Section ? upper(_section) : title(_page), 60, White, scale);
        if (_page is Page.Coin or Page.Game or Page.Sound or Page.Others or Page.Section)
        {
            var header = "[DEFAULT IN GREEN]";
            var x = 640 - header.Length * Half / 2;
            text(quads, "[", x, 90, White, scale);
            text(quads, "DEFAULT IN GREEN", x + Half, 90, Green, scale);
            text(quads, "]", x + Half * 17, 90, White, scale);
        }
        var rows = Rows();
        var selectable = Enumerable.Range(0, rows.Length).Where(index => rows[index].Step is not null || rows[index].Enter is not null).ToList();
        var selected = selectable.Count == 0 ? -1 : selectable[Math.Clamp(_selection, 0, selectable.Count - 1)];
        // A long page (the main menu) closes its rows up to stay above the hint lines and the footer.
        var slots = rows.Length + rows.Skip(1).Count(static row => row.Gap);
        var bottom = rows.Any(static row => row.Hint.Length > 0) ? 560 : 610;
        var spacing = Math.Min(RowSpacing, (bottom - FirstRow) / (float)Math.Max(1, slots - 1));
        var y = FirstRow;
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            if (row.Gap && index > 0)
                y += spacing;
            var label = index == selected && (_editing || flash) ? Red : row.Cyan ? Cyan : White;
            text(quads, row.Label, row.X, y, label, scale);
            if (row.Value?.Invoke() is { } value)
            {
                var accent = row.Colour?.Invoke() ?? (row.Cyan ? Cyan : row.IsDefault is null ? Green : row.IsDefault() ? Green : Yellow);
                if (index == selected && _editing && !flash)
                    accent = Red;
                var x = row.ValueX;
                // A plain information value (a count) is white; a setting's, or a {braced} part, takes the colour.
                foreach (var (part, coloured) in segments(value, whole: row.IsDefault is not null))
                    x = text(quads, part, x, y, coloured ? accent : row.Cyan ? Cyan : White, scale);
            }
            y += spacing;
        }
        // The selected setting's hint, in up to two lines.
        if (selected >= 0 && rows[selected].Hint is { Length: > 0 } hint)
        {
            var lines = wrapped(hint, 100);
            for (var line = 0; line < Math.Min(2, lines.Count); line++)
                centred(quads, lines[line], 590 + line * 26, White, scale);
        }
        centred(quads, _editing ? "UP/DOWN : CHANGE   ENTER : SET" : "UP/DOWN : CHOOSE   ENTER : ENTER", 660, White, scale);
        return new RenderFrame(RenderColor.Black, quads, 1280d / 720d);
    }

    // Lines of at most `cells` half-width cells, broken at the last space (anywhere in Japanese, which has none).
    private static List<string> wrapped(string text, int cells)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var rune in text.EnumerateRunes())
        {
            if (width(line + rune) > cells * Half)
            {
                var space = line.LastIndexOf(' ');
                (var done, line) = space > 0 ? (line[..space], line[(space + 1)..]) : (line, "");
                lines.Add(done);
            }
            line += rune.ToString();
        }
        lines.Add(line);
        return lines;
    }

    // "1 COIN AS {1} CREDIT(S)": the braced part takes the value colour; a value without braces is coloured whole
    // (when it is a setting's).
    private static IEnumerable<(string Text, bool Coloured)> segments(string value, bool whole)
    {
        if (!value.Contains('{'))
        {
            yield return (value, whole);
            yield break;
        }
        var parts = value.Split('{', '}');
        for (var index = 0; index < parts.Length; index++)
            if (parts[index].Length != 0)
                yield return (parts[index], index % 2 == 1);
    }

    private void centred(List<RenderQuad> quads, string value, float baseline, RenderColor colour, uint scale) =>
        text(quads, value, 640 - width(value) / 2, baseline, colour, scale);

    private static float width(string value) => value.EnumerateRunes().Sum(static rune => wide(rune) ? Half * 2 : Half);

    // Full-width characters (kana, kanji, full-width forms) take two cells.
    private static bool wide(Rune rune) => rune.Value >= 0x1100 && rune.Value is not (>= 0xFF61 and <= 0xFFDC);

    // A fixed-pitch line from its baseline, each glyph centred in its cell (the cabinet's font is monospaced); returns
    // where the line ends.
    private float text(List<RenderQuad> quads, string value, float x, float baseline, RenderColor colour, uint scale)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            var cell = wide(rune) ? Half * 2 : Half;
            if (!Rune.IsWhiteSpace(rune) && glyph(rune.Value, scale) is ({ } texture, var masks))
            {
                float s = scale;
                var left = x + (cell - masks.Advance / s) / 2 + masks.Left / s;
                quads.Add(RenderQuad.FromRectangles(texture,
                    new(left / 1280, (baseline - masks.Top / s) / 720, masks.Width / s / 1280, masks.Height / s / 720),
                    RenderRectangle.Full, colour, RenderColor.Transparent));
            }
            x += cell;
        }
        return x;
    }

    // Each character rasterized once per scale: white with its coverage as alpha (premultiplied), tinted when drawn.
    private (RenderTextureId? Texture, GlyphMasks Glyph) glyph(int scalar, uint scale)
    {
        if (_glyphs.TryGetValue((scalar, scale), out var cached))
            return cached;
        var masks = NativeVerticalTextRasterizer.RenderGlyph(fontPath, scalar, FontSize * scale, 0);
        RenderTextureId? texture = null;
        if (masks.Width > 0 && masks.Height > 0)
        {
            var pixels = new byte[masks.Fill.Length * 4];
            for (var index = 0; index < masks.Fill.Length; index++)
                pixels[index * 4] = pixels[index * 4 + 1] = pixels[index * 4 + 2] = pixels[index * 4 + 3] = masks.Fill[index];
            texture = application.UploadRgba8((uint)masks.Width, (uint)masks.Height, pixels);
        }
        return _glyphs[(scalar, scale)] = (texture, masks);
    }

    public void Dispose()
    {
        foreach (var (texture, _) in _glyphs.Values)
            if (texture is { } id)
                application.ReleaseTexture(id);
        _glyphs.Clear();
    }
}
