using System.Globalization;
using Waddamburo.App.Flow;
using Waddamburo.App.Hosting;
using Waddamburo.Game.Flow;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using Row = Waddamburo.App.Home.OverlayRow;

namespace Waddamburo.App.Home;

/// <summary>
/// The arcade cabinet's service (test) menu, opened by Escape (the test switch) from anywhere: a global recovery
/// screen (the shell stops and clears the game first) whose EXIT applies its settings and starts the attract. In home
/// mode, the Escape menu's settings, without the cabinet's pages. The pages are Waddamburo's subset of the cabinet's;
/// how they look and take input is the <see cref="OverlayMenu"/>'s. A value is green at its default and yellow when
/// changed; cyan rows are information.
/// </summary>
internal sealed class ServiceMenu(SdlApplication application, string fontPath, Bookkeeping book) : IDisposable
{
    private static readonly ArcadeSettings Defaults = new();
    private readonly OverlayMenu _menu = new(application, fontPath);

    // Section: one of the settings page's sections (its title in the page's section).
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

    /// <summary>The libraries left out at startup and why (their rows show red, the reason as their hint).</summary>
    public IReadOnlyList<(string Folder, string Why)> LibraryProblems { get; init; } = [];

    // Why the library at folder was left out at startup, null when it wasn't.
    private string? problemOf(string? folder) => folder is null ? null : LibraryProblems
        .Where(problem => string.Equals(Path.GetFullPath(problem.Folder), Path.GetFullPath(folder), StringComparison.Ordinal))
        .Select(static problem => OverlayMenu.upper("NOT LOADED: " + problem.Why))
        .FirstOrDefault();

    // A folder row: red, with the reason as its hint, when its library was left out at startup.
    private Func<RenderColor>? folderColour(string? folder) => problemOf(folder) is null ? null : static () => OverlayMenu.Red;
    private string _defaultTja = "";
    // Home mode: the settings (no coins, credits, stages or bookkeeping).
    private bool _home;
    private Action<HomeMenuAction>? _command;
    private bool _canCalibrate;
    private static readonly string TimingSection = Strings.T("section.timing");
    private static readonly string[] Devices = ["KEYBOARD", "CONTROLLER", "MIDI"];

    // The settings page's sections, by title (the language and volume ones go in OTHERS and SOUND TEST).
    private static readonly string LanguageSection = Strings.T("section.language"), VolumeSection = Strings.T("section.volume"),
        SongsSection = Strings.T("section.songs"), DisplaySection = Strings.T("section.display"),
        GraphicsSection = Strings.T("section.graphics");

    private int _tick;
    private ArcadeSettings _settings = Defaults;
    private int _credits;
    private readonly int[] _hits = new int[8];
    private readonly bool[] _down = new bool[8];
    private Action<bool>? _drum;
    private Action<ArcadeSettings>? _exit;

    public bool IsOpen => _menu.IsOpen;

    /// <summary>
    /// OTHERS' GAME MODE, once confirmed: saves the settings with the other mode and restarts the game (null: the row is
    /// hidden, the settings can't be saved).
    /// </summary>
    public Action<ArcadeSettings>? SwitchMode { get; init; }

    /// <summary>A pad or button is waiting for its input (every key is that input then, Backspace too).</summary>
    public bool Listening => IsOpen && _listening;

    /// <summary>
    /// Opens on <paramref name="settings"/>; <paramref name="credits"/> were left (shown, then dropped).
    /// <paramref name="drum"/> sounds a don (true) or ka; <paramref name="exit"/> takes the edited settings as it closes.
    /// </summary>
    /// <remarks><paramref name="apply"/>: each change, for what takes effect at once (the drum bindings, tested here);
    /// <paramref name="rehash"/>: SONG LIBRARIES' rehash; <paramref name="defaultTja"/>: the TJA folder when none is set;
    /// <paramref name="command"/>: home mode's rows that start something of the home menu's (the calibration, the texture
    /// bake), the menu closing first.</remarks>
    public void Open(ArcadeSettings settings, int credits, Action<bool> drum, Action<ArcadeSettings> exit,
        Action<ArcadeSettings> apply, Action rehash, string defaultTja, Action<HomeMenuAction>? command = null,
        bool canCalibrate = false)
    {
        _canCalibrate = canCalibrate;
        (_settings, _credits, _drum, _exit, _apply, _rehash, _defaultTja) = (settings, credits, drum, exit, apply, rehash, defaultTja);
        (_home, _command) = (settings.Home, command);
        (_listening, _picking, _message) = (false, null, null);
        Array.Clear(_hits);
        // Home: the menus' ka and don as the cursor moves and picks.
        _menu.Sound = _home ? drum : null;
        _menu.Open(page(Page.Main), _home);
        Console.WriteLine($"Service menu opened ({credits} credit(s) dropped).");
    }

    /// <summary>
    /// One tick: <paramref name="pulses"/> the keys that went down, <paramref name="held"/> those held; <paramref name="escape"/>
    /// (home mode) goes back: out of a value, then the page, then the menu.
    /// </summary>
    public void Tick(SdlKeyboardSnapshot pulses, SdlKeyboardSnapshot held, bool escape = false)
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
        // The tests sound each hit; at home SOUND TEST's hits are the menu's own ka and don already.
        var testing = _menu.Page?.Tag is Page.Io || _menu.Page?.Tag is Page.Sound && !_home;
        for (var pad = 0; pad < 8; pad++)
        {
            var down = held.IsDown(SdlInputBindings.Pads[pad]);
            if (down && !_down[pad])
            {
                _hits[pad]++;
                if (testing)
                    _drum?.Invoke(pad % 4 is 1 or 2); // the middle pads are the drum's face (don), the outer its rim
            }
            _down[pad] = down;
        }
        _menu.Tick(pulses, held, escape && _home);
    }

    public RenderFrame Frame() => _menu.Frame();

    /// <summary>The menu's quads (home mode: over the game, which the renderer frosts).</summary>
    public List<RenderQuad> Quads() => _menu.Quads();

    private void open(Page page, string section = "") => _menu.Push(this.page(page, section));

    // A page of this menu: its title and rows, the header where its values have defaults, the drum tests, and the main
    // page's Escape (home: the menu closes, as EXIT).
    private OverlayPage page(Page page, string section = "") => new(
        () => page == Page.Section ? upper(section) : _home && page == Page.Main ? "SETTINGS" : title(page),
        () => rowsFor(page, section),
        Defaults: page is Page.Coin or Page.Game or Page.Sound or Page.Others or Page.Section,
        // I/O TEST tests the drum, so it doesn't move there; SOUND TEST is driven by it (each move a ka, each pick a don).
        DrumTest: page is Page.Io,
        Back: page == Page.Main ? close : null,
        Tag: page,
        // The main list keeps its left edge, as the cabinet's; the other pages centre their block.
        Fixed: page == Page.Main);

    private void close()
    {
        _menu.Close();
        _exit?.Invoke(_settings);
    }

    // GAME MODE: a yes/no page; yes saves the settings in the other mode and restarts the game.
    private void confirmMode() => _menu.Push(new OverlayPage(
        () => _home ? "SWITCH TO ARCADE MODE" : "SWITCH TO HOME MODE",
        () =>
        [
            new("THE GAME WILL RESTART. SWITCH NOW?", Cyan: true),
            new("NO", Gap: true, Enter: _menu.Pop),
            new("YES", Enter: () =>
            {
                _menu.Close();
                SwitchMode?.Invoke(_settings with { Home = !_home });
            }),
        ]));

    // Home: a row that starts something of the home menu's; the menu closes for it.
    private void run(HomeMenuAction action)
    {
        close();
        _command?.Invoke(action);
    }

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
            var target = _menu.Selection - 2; // the pad rows follow PLAYER and DEVICE, then the buttons
            set(s => target < 4
                ? s with { Controls = ControlBindings.Bind(s.Controls, _player * 4 + target, captured, replace: _guided > 0, _device) }
                : s with { Buttons = ControlBindings.BindButton(s.Buttons, buttons[target - 4], captured) });
            if (_guided > 1)
            {
                _menu.Selection++;
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

    // The buttons on DRUM CONTROLS (indexes in ArcadeSettings.Buttons): a cabinet's coin and service switches; at home
    // the buttons beside the drum.
    private int[] buttons => _home ? [.. Enumerable.Range(0, CoinButton)] : [CoinButton, CoinButton + 1];

    private string[] buttonNames => _home ? [.. HomeMenu.ButtonLabels.Select(static name => upper(Strings.T(name.Label)))] : ["COIN", "SERVICE"];

    // A pad's (0-3) or a button's (4 on, see buttons) inputs on the shown device, by name.
    private string padValue(int pad) =>
        ControlBindings.Inputs(pad < 4 ? _settings.Controls[_player * 4 + pad] : _settings.Buttons[buttons[pad - 4]]).Where(token => ControlBindings.OnDevice(token, _device))
            .Select(ControlBindings.Describe).ToArray() is { Length: > 0 } names
            // Any number can be bound: the first few, then how many more (the line has room for about four).
            ? upper(string.Join(" / ", names.Take(4))) + (names.Length > 4 ? $" +{names.Length - 4}" : "") : "NOT SET";

    private static string folderName(string? path) =>
        path is null ? "NOT SET" : upper(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
    private static int wrap(int value, int step, int min, int max) => value + step > max ? min : value + step < min ? max : value + step;

    private Row[] rowsFor(Page page, string section) => page switch
    {
        Page.Main =>
        [
            .. _home ? (Row[])[] : [new("COIN OPTIONS", Enter: () => open(Page.Coin))],
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
            .. _home ? (Row[])[] : [new("BOOKKEEPING", Enter: () => open(Page.Bookkeeping))],
            new("OTHERS", Enter: () => open(Page.Others)),
            new("EXIT", Gap: true, Enter: close),
            .. _home ? (Row[])[] :
            [
                new("LAST GAME STATUS", 440),
                new("COIN USE", 460),
                new("LEFT CREDIT", 460, () => $"{_credits}", 610),
                new("USE CREDIT", 460, () => $"{_settings.CreditsOnePlayer}", 610),
            ],
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
            new("EXIT", Gap: true, Enter: _menu.Pop),
        ],
        Page.Game =>
        [
            .. _home ? (Row[])[] :
            [
                new("NUMBER OF STAGES", 400, () => $"{{{_settings.SongsPerSession}}} STAGE(S)", 800,
                    Step: step => set(s => s with { SongsPerSession = wrap(s.SongsPerSession, step, 1, 5) }),
                    IsDefault: () => _settings.SongsPerSession == Defaults.SongsPerSession),
            ],
            .. shared(SongsSection),
            new("EXIT", 400, Gap: true, Enter: _menu.Pop),
        ],
        Page.Sound =>
        [
            .. shared(VolumeSection),
            new("EXIT", 400, Gap: true, Enter: _menu.Pop),
            new(Strings.Language == "ja" ? "面とふちを叩くと、太鼓を叩いた時の音が鳴ります。" : "Hit the drum's face and rim to hear the drum.", 400),
        ],
        Page.Io =>
        [
            .. Enumerable.Range(0, 8).Select(pad => new Row($"{pad / 4 + 1}P {PadNames[pad % 4]}",
                Value: () => (_down[pad] ? "{ON} " : "OFF ") + _hits[pad].ToString(CultureInfo.InvariantCulture))),
            new("EXIT", Gap: true, Enter: _menu.Pop),
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
            new("EXIT", Enter: _menu.Pop),
        ],
        Page.Others =>
        [
            new("VERSION", 400, () => SelfUpdate.WindowTitle, 560, Cyan: true),
            new("CLOCK", 400, () => DateTime.Now.ToString("yyyy/MM/dd ddd HH:mm:ss", CultureInfo.InvariantCulture).ToUpperInvariant(), 560, Cyan: true),
            .. shared(LanguageSection),
            .. SwitchMode is null ? (Row[])[] :
            [
                new("GAME MODE", 400, () => _home ? "{HOME}" : "{ARCADE}", 560, Enter: confirmMode, Colour: static () => OverlayMenu.Green,
                    Hint: "HOME: A PC, FREE PLAY AND ENDLESS SONGS. ARCADE: THE CABINET, WITH COINS AND STAGES. CHANGING IT RESTARTS THE GAME."),
            ],
            new("EXIT", 400, Gap: true, Enter: _menu.Pop),
        ],
        // DISPLAY takes the graphics settings too (one page for the picture).
        Page.Section =>
        [
            .. shared(section), .. section == DisplaySection ? shared(GraphicsSection) : [],
            // Home: the home menu's calibration and texture bake (they show on its own pages).
            .. _home && _canCalibrate && section == TimingSection
                ? [new Row(upper(Strings.T("settings.calibrate")), 400, Gap: true, Enter: () => run(HomeMenuAction.Calibrate),
                    Hint: Strings.T("settings.calibrate.hint"))] : (Row[])[],
            .. _home && section == DisplaySection
                ? [new Row(upper(Strings.T("settings.bake")), 400, Gap: true, Enter: () => run(HomeMenuAction.BakeTextures),
                    Hint: Strings.T("settings.bake.hint"))] : (Row[])[],
            new("EXIT", 400, Gap: true, Enter: _menu.Pop),
        ],
        Page.Network =>
        [
            new("SERVER", 400, () => _settings.Server is { } server ? server.ToString().ToUpperInvariant() : "NOT SET", 640, Cyan: true),
            .. _home ? (Row[])[] :
            [
                new("CABINET ID", 400, () => Online.CabinetPairing.CabinetId.ToUpperInvariant(), 640, Cyan: true),
                new("CABINET TOKEN", 400, () => _settings.CabinetToken is null ? "NOT SET" : "SET", 640, Cyan: true),
            ],
            new("STATUS", 400, () => $"{{{linkText()}}}", 640, Gap: true, Colour: linkColour),
            new("LAST CHECK", 400, () => Health?.LastCheck is { } at
                ? string.Create(CultureInfo.InvariantCulture, $"{at:HH:mm:ss} ({(int)(DateTime.Now - at).TotalSeconds}S AGO)") : "-", 640),
            new("RESPONSE TIME", 400, () => Health?.RoundTrip is { } trip
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)trip.TotalMilliseconds} MS") : "-", 640),
            new("TEST CONNECTION", 400, Gap: true, Enter: () => Health?.CheckNow(),
                Hint: _home ? "CHECKS THE SERVER NOW (OTHERWISE EVERY 15 SECONDS). THE SERVER IS SET IN CONFIG.CFG."
                    : "CHECKS THE SERVER AND THE CABINET TOKEN NOW (OTHERWISE EVERY 15 SECONDS). THE SERVER AND TOKEN ARE SET IN CONFIG.CFG."),
            new("EXIT", 400, Gap: true, Enter: _menu.Pop),
        ],
        Page.Drums =>
        [
            new("PLAYER", 400, () => $"{{{_player + 1}P}}", 640, _ => _player = 1 - _player, () => true),
            new("DEVICE", 400, () => $"{{{Devices[_device]}}}", 640, step => _device = wrap(_device, step, 0, 2), () => true),
            .. PadNames.Concat(buttonNames).Select((name, pad) => new Row(name, 400,
                () => _listening && _menu.Selection == pad + 2 ? (_tick / 18 % 2 == 0 ? Prompts[_device] : "") : padValue(pad), 640,
                Enter: () => listen(0), Hint: "ENTER, THEN PRESS THE INPUT FOR THIS PAD (ESCAPE CANCELS). IT JOINS THE PAD'S OTHERS.")),
            new("SET UP ALL PADS", 400, Gap: true, Enter: () => { _menu.Selection = 2; listen(4); },
                Hint: "ASKS FOR THE FOUR PADS IN TURN, REPLACING THEIR INPUTS ON THIS DEVICE."),
            new("RESET TO DEFAULT", 400, Enter: () => set(s => s with { Controls = ControlBindings.Reset(s.Controls, _player, _device) }),
                Hint: "THIS PLAYER'S PADS GET THEIR DEFAULT INPUTS ON THIS DEVICE BACK."),
            new("EXIT", 400, Gap: true, Enter: _menu.Pop),
        ],
        Page.Libraries =>
        [
            new("CUSTOM TJA SONGS", 400, () => folderName(_settings.TjaFolder ?? _defaultTja), 640,
                Enter: () => pick("tja", _settings.TjaFolder ?? _defaultTja),
                Hint: problemOf(_settings.TjaFolder ?? _defaultTja) ?? "ENTER OPENS THE FOLDER PICKER.", Colour: folderColour(_settings.TjaFolder ?? _defaultTja)),
            new("OSU!LAZER SONGS", 400, () => folderName(_settings.OsuFolder), 640,
                Enter: () => pick("osu", _settings.OsuFolder),
                Hint: problemOf(_settings.OsuFolder) ?? "ENTER OPENS THE FOLDER PICKER.", Colour: folderColour(_settings.OsuFolder)),
            new("OFFICIAL SONGS", 400, Gap: true),
            .. ArcadeSettings.Folders(_settings.StockFolders).Select(folder => new Row("  RELEASE", 400, () => folderName(folder), 640,
                Enter: () => set(s => s with { StockFolders = ArcadeSettings.JoinFolders(ArcadeSettings.Folders(s.StockFolders).Where(f => f != folder)) }),
                Hint: problemOf(folder) ?? "ENTER REMOVES IT FROM THE LIBRARY.", Colour: folderColour(folder))),
            .. ArcadeSettings.Folders(_settings.NijiiroFolder).Select(folder => new Row("  NIJIIRO", 400, () => folderName(folder), 640,
                Enter: () => set(s => s with { NijiiroFolder = ArcadeSettings.JoinFolders(ArcadeSettings.Folders(s.NijiiroFolder).Where(f => f != folder)) }),
                Hint: problemOf(folder) ?? "ENTER REMOVES IT FROM THE LIBRARY.", Colour: folderColour(folder))),
            new("ADD A RELEASE OR NIJIIRO", 400, Enter: () => pick("official", null),
                Hint: _message ?? "ANOTHER RELEASE'S GAME DATA OR A NIJIIRO INSTALLATION, JOINED TO THE GAME'S OWN SONGS."),
            new("REHASH LIBRARY", 400, Gap: true, Enter: () => _rehash?.Invoke(),
                Hint: "WORKS THE LIBRARY'S CHART HASHES OUT AGAIN (IN THE BACKGROUND)."),
            new("EXIT", 400, Gap: true, Enter: _menu.Pop),
        ],
        _ => [],
    };

    private static string upper(string text) => OverlayMenu.upper(text);

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

    private RenderColor linkColour() => Health?.IconType switch { 0 => OverlayMenu.Green, 2 => OverlayMenu.Red, _ => OverlayMenu.Yellow };

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

    public void Dispose() => _menu.Dispose();
}
