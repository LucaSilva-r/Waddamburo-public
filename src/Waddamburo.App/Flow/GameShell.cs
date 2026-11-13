using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics;
using Waddamburo.App.Audio;
using Waddamburo.App.Cli;
using Waddamburo.App.Gameplay;
using Waddamburo.App.Hosting;
using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.App.Tools;
using Waddamburo.Catalog;
using Waddamburo.Editor;
using Waddamburo.Formats.Layout;
using Waddamburo.Game.Online;
using Waddamburo.Game.Dan;
using Waddamburo.Game.Don;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.Scores;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.App.Home;
using Waddamburo.App.Online;

namespace Waddamburo.App.Flow;

/// <summary>How the game is launched (command line).</summary>
internal sealed record GameOptions(
    string AssetRoot,
    string? DonRoot,
    int WindowWidth,
    int WindowHeight,
    int? FrameLimit,
    int? TickLimit,
    string? ScreenshotPath,
    SdlKeyboardTimeline InputTimeline,
    string TjaRoot,
    string FontPath,
    string? JinglePath,
    string? SoundRoot,
    bool Countdown = true,
    StartScene StartScene = StartScene.Boot,
    ArcadeSettings? Arcade = null,
    string? ArcadePath = null,
    AccountBook? Accounts = null,
    string? ScoresPath = null,
    bool Autoplay = false,
    bool Fullscreen = false,
    string? ServiceFontPath = null,
    bool RecordPerformance = false,
    BenchmarkMode Benchmark = BenchmarkMode.Off);

/// <summary>--benchmark: off, run and quit, or run and start the game again (Settings > Run Benchmark).</summary>
// Paced: the benchmark at the player's own vsync, frame cap and frame queue (it quits at the end), to count missed refreshes.
internal enum BenchmarkMode { Off, Quit, Restart, Paced }

/// <summary>
/// The running game: builds the services, owns the window loop, the active scene and what is drawn
/// over it (intermission, system indicators), the cabinet's coins and the credit's players, and hands
/// each tick to the active scene's <see cref="FlowScene"/>.
/// </summary>
internal sealed class GameShell : IDisposable
{
    public GameOptions Options { get; }
    /// <summary>The settings (the home menu changes them while the game runs).</summary>
    public ArcadeSettings Arcade { get; set; }

    public SdlApplication Application { get; }
    private EffectsEditorSession? _effectsEditor;

    public bool EffectsEditorOpen => _effectsEditor is not null;

    /// <summary>The layout editor shows the parts that only appear now and then (combo announcement, resume timer).</summary>
    public bool ShowHiddenElements => _effectsEditor?.ShowHidden == true;

    /// <summary>The effects editor's toggle: the scene's movies don't advance while it's open.</summary>
    public bool FreezeAnimations { get; set; }

    /// <summary>The effects editor on the playing chart, or (<paramref name="layout"/>) on the player's gameplay layout.</summary>
    public void OpenEffectsEditor(bool layout = false)
    {
        if (_effectsEditor is null && (layout ? _gameplay.CanEditLayout : _gameplay.CanEditEffects) && _home.CloseForEffectsEditor())
        {
            _gameplay.BeginEffectsEditing(autoplay: layout);
            _effectsEditor = layout
                ? new EffectsEditorSession(Application, _gameplay.LayoutEditorChart, _gameplay.Training!, _gameplay.LayoutFiles(),
                    _gameplay.ReplaceEditorEffects, _gameplay.LayoutElements, layout: true, layoutName: _gameplay.ActiveLayout, renameLayout: renameLayout,
                    twoPlayers: _gameplay.TwoPlayerLayouts)
                : new EffectsEditorSession(Application, stockLayout(), _gameplay.Training!, _gameplay.EditorFiles(),
                    _gameplay.ReplaceEditorEffects, _gameplay.EditorElements, _gameplay.LoadEditorTja, _gameplay.EditorTjaPath());
        }
    }

    // The modchart editor works on the stock screen: the song drops the player's layout (and its 4:3) as it opens.
    private Waddamburo.Catalog.PlayableChart stockLayout()
    {
        var chart = _gameplay.EditorChartWithoutLayout;
        _gameplay.ReplaceEditorEffects(chart.Effects);
        return chart;
    }

    // The layout editor's name field: the active layout's file renamed, the settings following it.
    private EffectEditorFiles? renameLayout(string name)
    {
        if (_gameplay.ActiveLayout is not { } from) return null;
        try
        {
            if (Waddamburo.App.Gameplay.PlayerLayouts.Rename(_gameplay.LayoutFolder, from, name) is not { } renamed) return null;
            Arcade = _gameplay.WithActiveLayout(Arcade, renamed);
            if (Options.ArcadePath is { } config) ArcadeSettings.SaveMenuSettings(config, Arcade);
            return _gameplay.LayoutFiles();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Layout not renamed: {exception.Message}");
            return null;
        }
    }

    // The screenshot button (#27): the frame as presented, a PNG in screenshots/ beside config.cfg, encoded off the
    // render thread.
    private void saveScreenshot(RenderCapture capture)
    {
        var folder = Path.Combine(Path.GetDirectoryName(Options.ArcadePath ?? Options.ScoresPath) ?? ".", "screenshots");
        var name = $"Screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}";
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                ScreenshotWriter.WritePng(Path.Combine(folder, name + ".png"), capture);
                Console.WriteLine($"Screenshot: {Path.Combine(folder, name)}.png");
                Sync.Notices.AddLocal(Strings.T("screenshot.saved", name));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Screenshot not saved: {exception.Message}");
                Sync.Notices.AddLocal(exception.Message, NoticeSeverity.Warning);
            }
        });
    }

    /// <summary>Applies settings the game changed by itself and keeps them in config.cfg.</summary>
    public void SaveArcade(ArcadeSettings settings)
    {
        Arcade = settings;
        if (Options.ArcadePath is not { } config) return;
        try
        {
            ArcadeSettings.SaveMenuSettings(config, settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error SETTINGS_SAVE: {exception.Message}");
        }
    }

    private void closeEffectsEditor()
    {
        _effectsEditor?.Dispose();
        _effectsEditor = null;
        FreezeAnimations = false;
    }

    public SdlDonRenderer? DonRenderer { get; }
    public DonPresentationController? Don { get; }
    public AudioEngine? Audio { get; }
    public GameSounds? Sounds { get; }
    public SongPreviewController? Previews { get; }
    public SongTitleTextureCache Titles { get; }
    public CoinBank? Coins { get; internal set; } // the cabinet controls replace it as the service menu closes

    /// <summary>Per lane, the best before the last saved play on its chart, or that play's score when it is the first (for results).</summary>
    public long?[] Bests { get; } = new long?[2];

    /// <summary>Each lane's place on the chart's online top three after its play (null: not known).</summary>
    public RankingPlacement?[] Placements { get; } = new RankingPlacement?[2];

    /// <summary>Chart key -> hash, stored with the scores (matches server bests to the library).</summary>
    public ChartHashes ChartHashes { get; }

    /// <summary>Hashes the whole library again (Settings > Song Libraries), then matches the dan courses again.</summary>
    public void RehashLibrary() => ChartHashes.Rehash(_libraries.Snapshot.Songs.Values, _danCourses.Rematch);

    private readonly DanCourses _danCourses;

    /// <summary>The score database and the server side (uploads, bests, rankings, cabinet pairing).</summary>
    public ScoreSync Sync { get; }

    /// <summary>Songs play themselves: --autoplay, or the home setting (not in an online match).</summary>
    /// <summary>Home with the arcade HUD off: no timer, song number or free-play line (they don't count there).</summary>
    /// <summary>The active scene and its mode, for the log: "gameplay (AI battle, dev autoplay)".</summary>
    public string DescribeScene()
    {
        if (Active is not { } active)
            return "starting";
        var gameplay = active.Id == FlowScenes.Gameplay && _scenes.TryGetValue(active.Id, out var flow) ? flow as GameplayFlow : null;
        string[] modes =
        [
            Arcade.Home ? "home" : "arcade",
            .. Hosts.AiBattle ? ["AI battle"] : Array.Empty<string>(),
            .. Hosts.Waiwai ? ["Waiwai"] : Array.Empty<string>(),
            .. gameplay?.Demo == true ? ["attract demo"] : Array.Empty<string>(),
            .. gameplay is not null && Autoplay ? ["autoplay"] : Array.Empty<string>(),
            .. gameplay is not null && DevAutoplay ? ["dev autoplay"] : Array.Empty<string>(),
        ];
        return $"{active.Id} ({string.Join(", ", modes)})";
    }

    public bool HideArcadeHud => Arcade.Home && !Arcade.ArcadeHud;

    /// <summary>Development builds: the player's lane is drummed live by the bot (Settings > Developer).</summary>
    public bool DevAutoplay => BuildInfo.Dev && Arcade.DevAutoplay;

    public bool Autoplay => Options.Autoplay || (Arcade.Home && Arcade.Autoplay && Match?.InSong != true);

    /// <summary>The accounts stored on this PC (home mode; empty in arcade).</summary>
    public AccountBook? Accounts => Arcade.Home ? Options.Accounts : null;

    /// <summary>The home pause/settings menu is open (it takes the keys).</summary>
    public bool HomeMenuOpen => _home.MenuOpen;

    /// <summary>The stored account that joins the first drum by itself (home).</summary>
    public ScoreAccount? DefaultAccount => Accounts?.Default;

    // Home: the "who's playing?" screen before the entry (stored accounts, guests, friends, in-game login).
    private readonly PlayerSetupController? _playerSetup;

    private readonly PairingPill _pill;
    private readonly PerformanceOverlay _performance;
    // Home only: a cabinet's notices are for its operator (their view comes later).
    private readonly NoticeOverlay? _notices;
    private readonly TimingMarkOverlay _timingMarks;
    private readonly ReviewOverlay _reviewBar;

    private readonly ConcurrentDictionary<string, CatalogAssetKey> _uploadCharts;

    public void RememberUploadChart(ChartKey chart, CatalogAssetKey asset) => _uploadCharts.TryAdd(chart.ToString(), asset);

    private bool CanUploadChart(string chartKey) => chartKey.StartsWith("Stock:", StringComparison.Ordinal)
        || chartKey.StartsWith("Nijiiro:", StringComparison.Ordinal)
        || _uploadCharts.TryGetValue(chartKey, out var asset) && !Assets.UploadBlocked(asset);

    public CatalogAssetRouter Assets { get; }

    /// <summary>The players' own modcharts: &lt;chart hash&gt;.wddmod, one per chart and course (ChartModFile).</summary>

    public string? Modcharts { get; private set; }
    public SongSelectCatalogView SongCatalog { get; }
    public TaikoGameplayPresentation Gameplay { get; }
    public GameplaySkinResolver Skins { get; }

    public DirectoryLumenMovieContentSource MovieContent { get; }

    /// <summary>The active scene's textures and the frame held across a scene switch.</summary>
    private readonly ScenePresenter _presenter;

    /// <summary>The scene on screen and the switches between scenes (show, fade, prefetch).</summary>
    public SceneSwitcher Scenes { get; }

    /// <summary>The texture upscaler and its cache (null: unavailable on this machine).</summary>
    public UpscaleTool? Upscale { get; private set; }

    /// <summary>A texture bake started from the Settings menu (running or finished).</summary>
    public TextureBake? Bake { get; set; }

    public EnsoLayout EnsoLayout { get; }
    public PlayRequestState PlayRequests { get; } = new();
    public SceneCatalog Catalog { get; }
    public LayerHostFactory Hosts { get; }
    public GameFlowCoordinator Coordinator { get; }
    public IntermissionOverlay Overlay { get; }

    /// <summary>Headless (--screenshot): time follows ticks, not the clock.</summary>
    public bool Headless => Options.ScreenshotPath is not null;

    public int Tick { get; private set; }
    public LumenGameSceneInstance Active => Scenes.Active;

    /// <summary>The song being played and its stage (1-8) in the credit.</summary>
    public TaikoSongInfo SongInfo { get; set; } = new(0, 1);

    /// <summary>Who is playing this credit (joined drums, songs played, a replay's player, online two-player).</summary>
    public PlayerSession Players { get; }

    private readonly SongLibraries _libraries;
    private readonly IAudioOutput? _audioDevice;
    private readonly LumenGameSceneLoader _loader;
    private readonly CostumeIconTextures _costumeIcons;
    private readonly DanChallengeArt _danArt;
    private readonly CopyrightArt _copyright;
    private readonly WaiwaiResultTextures _waiwaiResultTextures;
    private readonly TextFieldTextures _textFields;
    private readonly NameTextTextures _nameTexts;
    private readonly NutData _nutData;

    /// <summary>AI battle's AI-don levels (ghosttuning.xml; empty when the data has none).</summary>
    public IReadOnlyList<Waddamburo.Game.Gameplay.GhostLevel> GhostLevels { get; } = [];

    /// <summary>The AI battle player's standing (the playing drum's player; at home a guest is this PC's).</summary>
    public Waddamburo.Game.Scores.AiProgress AiStanding() =>
        (TaikoGuest.Profiles[Hosts.PlayerSide] ?? (Arcade.Home ? Waddamburo.Game.Scores.ScoreProfile.LocalGuest : null)) is { } profile
            && Sync.Scores is { } scores ? scores.AiProgress(profile.Baid) : Waddamburo.Game.Scores.AiProgress.None;

    /// <summary>AI battle's rank ladder (the installation's ghostinfo.xml).</summary>
    public Waddamburo.Game.Gameplay.AiBattleRanks AiRanks { get; } = Waddamburo.Game.Gameplay.AiBattleRanks.Green;
    private readonly ToneArt _toneArt;
    private readonly AttractFlow _attract;
    private readonly GameplayFlow _gameplay;
    private readonly HomeControls _home;
    private readonly SongSearch _search;
    private readonly ScoreListOverlay _scores;
    // Overlays that take the keyboard (topmost last) and the painter Waddamburo's own overlays draw with.
    public CustomUploadPrompt CustomUploadPrompt { get; }
    private readonly IInputOverlay[] _inputOverlays;

    /// <summary>Song Select's flow (an online host's cover is cancelled through it).</summary>
    public SongSelectFlow SongSelectFlow { get; private set; } = null!;

    /// <summary>A song played with the room's other player.</summary>
    public OnlineMatch Match { get; private set; } = null!;

    /// <summary>The home menu's artwork (panel, buttons, the entry's drum stand), when the game data has it.</summary>

    /// <summary>The online lobby, and the room it holds.</summary>
    public RoomOverlay Room { get; private set; } = null!;
    public OverlayPainter Painter { get; }

    /// <summary>Song Select's quick reload (library, grouping, order, search).</summary>
    public SongSelectReload Reload { get; }
    private readonly Dictionary<SceneId, FlowScene> _scenes;
    private readonly WaiwaiOutcome? _diagnosticWaiwai;
    private SystemIndicators? _indicators;
    private RenderTextureId[] _indicatorTextures = [];
    private SceneId? _indicatorScene;
    private static readonly HashSet<SceneId> _attractScenes =
        [FlowScenes.Logo, FlowScenes.Title, FlowScenes.Caution, FlowScenes.Movie];
    private readonly InputLatches _input = new();
    private bool _returnToAttract; // the entry gave up (applied on the next tick, outside its callbacks)
    // Arcade: the service menu (Escape, the test switch), the coin switches and the cabinet's running totals.
    private readonly ServiceMenu? _service;
    private readonly CabinetControls _cabinet;

    /// <summary>The service menu (arcade) or the settings overlay (home).</summary>
    public ServiceMenu? Service => _service;

    /// <summary>The cabinet's bookkeeping (the service menu's counts).</summary>
    public Bookkeeping Book { get; }
    private readonly bool _traceInput = Environment.GetEnvironmentVariable("WADDAMBURO_INPUT_TRACE") == "1";
    private readonly int _dumpTreeTick =
        int.TryParse(Environment.GetEnvironmentVariable("WADDAMBURO_DUMP_TREE"), out var dumpAt) ? dumpAt : -1;

    /// <summary>The game closed to start again (the caller relaunches it once everything is released).</summary>
    public static bool RestartRequested { get; private set; }

    /// <summary>The arguments the relaunch adds (--benchmark=restart from the settings), or none.</summary>
    public static string[] RestartArguments { get; private set; } = [];

    /// <summary>The randomness of what should repeat in a benchmark (demo songs, scene parts); shared otherwise.</summary>
    public Random Random { get; private set; } = Random.Shared;

    private readonly BenchmarkRun? _benchmark;

    /// <summary>Settings > Run Benchmark: the game starts again into the benchmark, then again as usual.</summary>
    public void RequestBenchmark()
    {
        RestartArguments = ["--benchmark=restart"];
        RequestRestart();
    }

    /// <summary>Closes the game to start it again (see <see cref="RestartRequested"/>).</summary>
    public void RequestRestart()
    {
        RestartRequested = true;
        Application.RequestQuit();
    }

    /// <summary>Applies the drum pads' keys, buttons and MIDI notes (<paramref name="warn"/>: report the ones not understood).</summary>
    /// <returns>The bindings not understood (reported only with <paramref name="warn"/>).</returns>
    public List<string> ApplyControls(bool warn = false)
    {
        List<string> problems = [];
        GameActions.Buttons = Arcade.Buttons;
        Application.Bindings = SdlInputBindings.Parse(Arcade.Controls, Arcade.Buttons,
            warn ? problem =>
            {
                problems.Add(problem);
                Console.Error.WriteLine($"Warning CONTROLS: {ArcadeSettings.FileName}: {problem}.");
            } : null);
        Application.PadsAsGamepad = !Arcade.PadMenusAsDrum;
        Application.MouseControls = Arcade.MouseControls;
        return problems;
    }

    // In the menus the arrows and Enter also work the drum: Left/Up and Right/Down are its rims, Enter its
    // centre (player 1's, or player 2's when they play alone; the revival is always driven as player 1's).
    private SdlKeyboardSnapshot menuKeys(SdlKeyboardSnapshot keys)
    {
        var drum = Hosts.PlayerSide == 1 && !Hosts.TwoPlayers && Active.Id != FlowScenes.Retry ? 4 : 0;
        Application.ClickPad = drum + 1;
        IEnumerable<SdlKeyboardKey> drumToo(SdlKeyboardKey key) => key switch
        {
            SdlKeyboardKey.Left or SdlKeyboardKey.Up => [key, SdlInputBindings.Pads[drum]],
            SdlKeyboardKey.Right or SdlKeyboardKey.Down => [key, SdlInputBindings.Pads[drum + 3]],
            SdlKeyboardKey.Enter => [key, SdlInputBindings.Pads[drum + 1]],
            // The wheel is the first player's rims (the input adapter); a lone right-drum player's too.
            SdlKeyboardKey.WheelUp when drum != 0 => [key, SdlInputBindings.Pads[drum]],
            SdlKeyboardKey.WheelDown when drum != 0 => [key, SdlInputBindings.Pads[drum + 3]],
            _ => [key],
        };
        return new SdlKeyboardSnapshot(keys.PressedKeys.SelectMany(drumToo),
            keys.Presses.SelectMany(press => drumToo(press.Key).Select(key => press with { Key = key })), keys.Timestamp);
    }

    // --start-scene=dani-result-fail or dani-result-clear: 五級 with made-up numbers.
    private static DanCourseResult diagnosticDan(bool clear)
    {
        var course = new DanCourse(7, "五級", "", [new("soul_gauge", [94], [97]), new("bads", [55], [44]), new("hits", [610], [640])],
            [new("a", 3, null, 400, []), new("b", 3, null, 400, []), new("c", 3, null, 400, [])]);
        var total = clear ? new DanProgress(100, 900, 200, 20, 400, 120, 900_000) : new DanProgress(51, 209, 37, 59, 136, 165, 646_920);
        DanSongCounts song(int goods, int oks, int bads, int rolls) => new(goods, oks, bads, rolls);
        return new DanCourseResult(course, [
            new(0, 3, 7, clear ? song(300, 60, 8, 40) : song(65, 5, 52, 45)),
            new(1, 3, 8, clear ? song(300, 70, 6, 40) : song(144, 32, 7, 120)),
            new(2, 3, 9, clear ? song(300, 70, 6, 40) : default)],
            clear ? 3 : 2, total, DanCourseResult.LevelOf(course, total));
    }

    /// <summary>Applies the display settings (vsync, fullscreen, letterbox) and sizes the upscaled textures for them.</summary>
    /// <summary>
    /// Starts or ends the performance report as the setting (or --benchmark-record) asks; ending it writes the file.
    /// </summary>
    public void ApplyRecording()
    {
        var wanted = (Options.RecordPerformance || Options.Benchmark != BenchmarkMode.Off || Arcade.RecordPerformance) && !Headless;
        if (wanted && Application.Recorder is null)
        {
            var version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(GameShell).Assembly)?.InformationalVersion ?? "?";
            Application.Recorder = new PerformanceRecorder(
                Path.Combine(ReleaseConsole.CrashFolder, $"{(Options.Benchmark != BenchmarkMode.Off ? "benchmark" : "performance")}-{DateTime.Now:yyyyMMdd-HHmmss}.txt"),
                Options.Benchmark != BenchmarkMode.Off ? "benchmark (attract 3 min, song select scroll, stress song)" : "session",
                [$"version\t{version}", .. Application.DescribeRendering(),
                    $"settings\t{(Options.Benchmark is BenchmarkMode.Quit or BenchmarkMode.Restart ? "uncapped (benchmark), " : "")}vsync {Arcade.Vsync}, frame queue {Arcade.FrameLatency}, fps cap {Arcade.FpsCap}, fullscreen {Arcade.Fullscreen}"
                    + $"{(Arcade.Fullscreen ? Arcade.ExclusiveFullscreen ? " (exclusive)" : " (borderless)" : "")}, "
                    + $"textures {(MovieContent.KeepCompressed ? "BC1/BC3 kept" : "decoded to RGBA")}, "
                    + $"refresh {Arcade.RefreshRate}, mode {(Arcade.Home ? "home" : "arcade")}, skin {Arcade.GameplaySkin}, "
                    + $"audio buffer {Arcade.AudioBufferFrames}{(Arcade.AudioExclusive ? " exclusive" : "")}",
                    // Upscaled textures are 9x the pixels (BC7 on the GPU when it has it, else RGBA): they change memory and load times most.
                    $"upscaled textures\t{(Upscale is not { } upscale ? "unavailable (no model)"
                        : !Arcade.UpscaleTextures ? "off" : $"on, shown {Application.ShowUpscaled}, scale {upscale.Scale.ToString("0.#", CultureInfo.InvariantCulture)}x, "
                            + $"{upscale.CachedCount} cached, {(Application.SupportsBc7 ? "BC7" : "RGBA")} on the GPU, {Arcade.UpscaleThreads} threads")}"]);
            PerformanceRecorder.Scene = Active?.Id.ToString() ?? "-";
        }
        else if (!wanted && Application.Recorder is { } recorder)
        {
            Application.Recorder = null;
            recorder.Dispose();
        }
    }

    public void ApplyDisplay()
    {
        if (Headless)
            return;
        // Exclusive fullscreen on Windows only: Linux compositors only emulate it.
        var exclusive = Arcade.ExclusiveFullscreen && OperatingSystem.IsWindows();
        // A benchmark runs uncapped (no vsync, no fps cap; the settings stay as they are): frame rates then
        // compare across machines and changes instead of all reading the refresh rate.
        var uncapped = Options.Benchmark is BenchmarkMode.Quit or BenchmarkMode.Restart;
        var display = new DisplaySettings(Arcade.Vsync && !uncapped, Arcade.Fullscreen, exclusive, Arcade.FullscreenWidth,
            Arcade.FullscreenHeight, Arcade.RefreshRate, Arcade.LetterboxSize / 100f, Arcade.LetterboxX / 100f, Arcade.LetterboxY / 100f,
            exclusive && Arcade.Fullscreen || uncapped ? 0 : Arcade.FpsCap, Arcade.RenderHeight, Arcade.FrameLatency);
        Application.ApplyDisplay(display);
        // The stage is 720 lines: 1080p loads textures at 1.5x, 1440p at 2x, 4K at the model's 3x.
        if (Upscale is not null)
            Upscale.Scale = Math.Clamp(Math.Ceiling(Application.StageHeight(display) / 720d * 2) / 2, 1, 3);
    }

    public static int Run(GameOptions options)
    {
        GameShell? shell = null;
        try
        {
            shell = new GameShell(options);
            shell.run(); // its frame count is for the profiler, not the exit code
            return 0;
        }
        // The window closed while the game loaded, or after its error screen (BootChecks.Fail).
        catch (BootClosedException closed)
        {
            return closed.Data.Contains(BootChecks.FailedKey) ? 1 : 0;
        }
        // A crash while the game runs: everything stops and the error screen takes the window, as a cabinet's would
        // (scripted runs, screenshots or a tick or frame limit, fail as before rather than wait on it).
        catch (Exception exception) when (shell is { Interactive: true } running)
        {
            running.crashed(exception);
            return 1;
        }
        finally
        {
            shell?.Dispose();
        }
    }

    // A person is at the window (no screenshot, tick or frame limit): a crash can wait on its error screen.
    private bool Interactive => !Headless && Options.TickLimit is null && Options.FrameLimit is null;

    private void crashed(Exception exception)
    {
        Audio?.Mixer.StopAll();
        var log = ReleaseConsole.WriteCrashFile(exception);
        Console.Error.WriteLine($"Crash: {exception}");
        CrashScreen.Show(Application, Options.ServiceFontPath ?? Options.FontPath, "99-1", BootChecks.Shown("GAME"), exception, log);
    }

    private readonly int _crashAt = int.TryParse(Environment.GetEnvironmentVariable("WADDAMBURO_CRASH_AT"), out var crashAt) ? crashAt : -1;

    // The startup's check screen, until the attract (or the scene asked for) shows.
    private BootChecks? _boot;

    private GameShell(GameOptions options)
    {
        Options = options;
        Players = new PlayerSession(this);
        Arcade = options.Arcade ?? new ArcadeSettings();
        // Home: a PC plays free, without countdowns (the endless credit is the host factory's).
        if (Arcade.Home)
        {
            Arcade = Arcade with { FreePlay = true };
            options = options with { Countdown = false };
            Options = options;
        }
        var assetRoot = options.AssetRoot;
        // The cabinet's credit counter: lives until the process exits (coin mode only).
        Coins = Arcade.FreePlay ? null : new CoinBank(Arcade);
        // The game's own songs live beside the Lumen data (<data>/lumendata/packed).
        var dataRoot = Path.GetFullPath(Path.Combine(assetRoot, "..", ".."));
        // The window first, and the check screen in it while everything else loads (it took ~8 s on a big library
        // before anything showed, 4 s of them without a window).
        Application = new SdlApplication(
            Waddamburo.App.Hosting.SelfUpdate.WindowTitle,
            options.WindowWidth,
            options.WindowHeight,
            debugGpu: false,
            resizable: !Headless,
            highPixelDensity: !Headless,
            fullscreen: options.Fullscreen && !Headless);
        Console.WriteLine($"Renderer: {Application.GpuDriver}");
        Console.WriteLine($"System priority: {SystemPriority.Request()}.");
        _boot = new BootChecks(Application, options.ServiceFontPath ?? options.FontPath, interactive: Interactive);
        try
        {
            _boot.Begin("NETWORK SERVICE");
            Sync = new ScoreSync(Arcade, options.ScoresPath, Accounts, () => Arcade.UploadCustomSongs, CanUploadChart);
            // Not fatal: the game plays offline (scores wait for the server). Its first answer, up to 3 s.
            if (Sync.Health is { } health)
            {
                var link = _boot.Await(() =>
                {
                    for (var waited = 0; health.State == Online.LinkState.Checking && waited < 3000; waited += 50)
                        Thread.Sleep(50);
                    return health.State;
                });
                if (link != Online.LinkState.Online)
                    _boot.Warn(link switch
                    {
                        Online.LinkState.Checking or Online.LinkState.Timeout => "No answer",
                        Online.LinkState.NoInternet => "No internet",
                        Online.LinkState.Unreachable => "Server unreachable",
                        Online.LinkState.ServerError => $"Server error {health.StatusCode}",
                        Online.LinkState.NoToken => "No cabinet token",
                        _ => "Cabinet token refused",
                    });
            }
            _libraries = _boot.Background("SONG LIBRARY", () => SongLibraries.Load(dataRoot, Arcade, options.TjaRoot, _boot.Progress));
            // Not fatal: the libraries that load are listed (the settings say what's wrong with the others).
            if (_libraries.Problems is { Count: > 0 } problems)
                _boot.Warn(problems.Count == 1 ? "1 library left out" : $"{problems.Count} libraries left out",
                    problems.Select(static problem => $"{problem.Folder}: {problem.Why}"));
            _boot.Begin("SONG CATALOG");
            var snapshot = _libraries.Snapshot;
            Assets = new CatalogAssetRouter([.. _libraries.Providers, ReceivedSongs]);
            _uploadCharts = new ConcurrentDictionary<string, CatalogAssetKey>(snapshot.Songs.Values.SelectMany(static song => song.Charts)
                .Select(static chart => new KeyValuePair<string, CatalogAssetKey>(chart.Key.ToString(), chart.ChartAsset)), StringComparer.Ordinal);
            Sync.UploadPending();
            // Next to the settings file (USRDIR/waddamburo), like the favourites; none without one.
            Modcharts = Options.ArcadePath is { } settingsFile ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsFile))!, "modcharts") : null;
            // The one layout of earlier versions becomes the first of the list, and stays the active one.
            if (Modcharts is not null && Options.ArcadePath is { } config && Waddamburo.App.Gameplay.PlayerLayouts.Migrate(Modcharts) is { } migrated
                && Arcade.Layout is null)
            {
                Arcade = Arcade with { Layout = migrated };
                ArcadeSettings.SaveMenuSettings(config, Arcade);
            }
            ChartHashes = new ChartHashes(Sync.Scores, Assets.LoadChartAsync) { Jobs = Sync.Jobs };
            SongCatalog = new SongSelectCatalogView(snapshot);
            // Server crowns need every chart's hash before song select lists it.
            if (Sync.Online)
                ChartHashes.HashLibraryInBackground(snapshot.Songs.Values);
            _danCourses = new DanCourses(snapshot, ChartHashes, options.ScoresPath is { } scores
                ? Path.Combine(Path.GetDirectoryName(scores)!, "dan-courses.json") : null, Arcade.Server, Arcade.ServerInsecure);

            _boot.Begin("I/O");
            if (ApplyControls(warn: true) is { Count: > 0 } controls)
                _boot.Warn(controls.Count == 1 ? "Binding not understood" : $"{controls.Count} bindings not understood", controls);
            _boot.Begin("DON-CHAN");
            DonRenderer = options.DonRoot is null ? null : Application.CreateDonRenderer(options.DonRoot);
            Don = DonRenderer is null ? null : new DonPresentationController(DonRenderer);
            // Diagnostic: WADDAMBURO_DON_COSTUME=head,body,paint (or a single whole-costume id) dresses P1 at start.
            if (Don is not null && Environment.GetEnvironmentVariable("WADDAMBURO_DON_COSTUME") is { } costumeSetting)
            {
                var ids = costumeSetting.Split(',').Select(int.Parse).ToArray();
                Don.SetCostume(0, ids.Length == 1 ? DonCostume.FromWhole(ids[0]) : new DonCostume(null, ids[0], ids[1], ids.ElementAtOrDefault(2)));
            }
            // Diagnostic: WADDAMBURO_TITLE=plate:text and/or WADDAMBURO_NAME=name give P1 a guest profile with them.
            var diagnosticName = Environment.GetEnvironmentVariable("WADDAMBURO_NAME");
            if (Environment.GetEnvironmentVariable("WADDAMBURO_TITLE") is [var plate, ':', .. var titleText])
                TaikoGuest.Profiles[0] = ScoreProfile.LocalGuest with { Title = titleText, TitlePlate = plate - '0', AccountName = diagnosticName };
            else if (diagnosticName is { Length: > 0 })
                TaikoGuest.Profiles[0] = ScoreProfile.LocalGuest with { AccountName = diagnosticName };
            // Diagnostic: WADDAMBURO_DON_PUCHI=id puts that puchi chara beside P1 at start.
            if (Don is not null && int.TryParse(Environment.GetEnvironmentVariable("WADDAMBURO_DON_PUCHI"), out var puchiSetting))
                Don.SetAccessory(0, puchiSetting);
            _boot.Begin("AUDIO DEVICE");
            var needsAudio = !Headless || options.JinglePath is not null || options.SoundRoot is not null;
            _audioDevice = needsAudio ? openAudio() : null;
            if (Environment.GetEnvironmentVariable("WADDAMBURO_PROFILE") == "1" && _audioDevice is not null)
                Console.Error.WriteLine($"Profile audio: {_audioDevice.Driver}, "
                    + $"{_audioDevice.HardwareFormat.SampleRate} Hz, "
                    + $"{_audioDevice.HardwareBufferFrames} hardware frames.");
            Audio = _audioDevice is null ? null : new AudioEngine(_audioDevice) { TraceContext = () => $"{DescribeScene()} at tick {Tick}" };
            Previews = Audio is null
                ? null
                : new SongPreviewController(Audio, Assets, FindJingle("JINGLE_GENRE.nub"), FindJingle("JINGLE_WAIGENRE.nub"),
                    FindJingle("JINGLE_AIBTL_GENRE.nub"));
            _boot.Begin("SOUND DATA");
            Sounds = options.SoundRoot is null ? null : new GameSounds(Audio!, options.SoundRoot);
            _boot.Begin("GRAPHICS");
            Titles = new SongTitleTextureCache(Application, options.FontPath, asynchronous: !Headless, english: Arcade.EnglishTitles,
                squash: () => Arcade.SquashTitles, assetRoot: assetRoot);
            _pill = new PairingPill(Application, options.FontPath);
            _textFields = new TextFieldTextures(Application, options.FontPath);
            _nameTexts = new NameTextTextures(Application, options.FontPath);
            _toneArt = new ToneArt(_nutData = new NutData(Application, dataRoot));
            GhostLevels = Waddamburo.Game.Gameplay.GhostTuning.Load(dataRoot);
            AiRanks = Waddamburo.Game.Gameplay.AiBattleRanks.Load(dataRoot);
            var support = Waddamburo.Game.Gameplay.SupportInfo.Load(dataRoot);
            _performance = new PerformanceOverlay(Application, () => Audio)
            {
                Online = () => Room?.Session?.Link is { State: Waddamburo.Game.Online.PeerLinkState.Direct or Waddamburo.Game.Online.PeerLinkState.Relayed } link
                    ? (link.State == Waddamburo.Game.Online.PeerLinkState.Relayed, link.RoundTrip) : null,
            };
            _timingMarks = new TimingMarkOverlay(Application);
            Painter = new OverlayPainter(Application, options.FontPath, asynchronous: !Headless);
            Book = Bookkeeping.Load(options.ArcadePath is { } settingsPath
                ? Path.Combine(Path.GetDirectoryName(settingsPath)!, Bookkeeping.FileName) : null);
            // Arcade: the service menu (Escape); home: the Escape menu's settings overlay.
            _cabinet = new CabinetControls(this, _input, resetForService, settings => _home!.Apply(settings));
            _service = new ServiceMenu(Application, options.ServiceFontPath ?? options.FontPath, Book)
            {
                LibraryProblems = _libraries.Problems,
                SwitchMode = options.ArcadePath is { } modePath ? settings => _cabinet.SwitchMode(modePath, settings) : null,
            };
            _cabinet.Menu = _service;
            if (!Arcade.Home)
            {
                Book.PowerOnCount++;
                Book.Save();
            }
            _reviewBar = new ReviewOverlay(Painter);
            if (Arcade.Home)
                _notices = new NoticeOverlay(Painter, Sync.Notices, Sync.Jobs,
                    baid => Accounts?.Accounts.FirstOrDefault(account => account.Baid == baid)?.Name, Sync.MarkShown);
            _boot.Begin("GAMEPLAY DATA");
            Gameplay = new TaikoGameplayPresentation((lane, action) =>
                Sounds?.Gameplay.PlayDrum(lane, action is TaikoInputAction.LeftDon or TaikoInputAction.RightDon),
                Don, (lane, sound, combo) => Sounds?.Gameplay.Play(lane, sound, combo),
                () => TimeSpan.FromMilliseconds(Arcade.DrumDebounceMs));
            _costumeIcons = new CostumeIconTextures(Application, _nutData, dataRoot);
            _danArt = new DanChallengeArt(_nutData);
            _copyright = new CopyrightArt(_nutData);
            _waiwaiResultTextures = new WaiwaiResultTextures(_nutData);
            // Other releases' installs (home's stock folders) name skins for the songs Green lacks.
            Skins = new GameplaySkinResolver(Path.GetFullPath(assetRoot), GameplaySkinResolver.MusicInfos(
                [dataRoot, .. Arcade.Home ? ArcadeSettings.Folders(Arcade.StockFolders).Select(Waddamburo.Providers.Stock.StockCatalogProvider.FindDataRoot).OfType<string>() : []]));
            EnsoLayout = GameplaySceneComposition.LoadLayout(assetRoot);

            (var diagnosticResult, _diagnosticWaiwai) = diagnosticResults(options.StartScene);

            _boot.Begin("MOVIES");
            var flow = new GameFlowSession();
            Catalog = createSceneCatalog(options.StartScene);
            Hosts = createHosts(flow, snapshot, diagnosticResult);
            // Stock songs as supportinfo.xml lists them; any other song three steps below each course it has.
            // ponytail: three everywhere for custom songs, whether or not simplifying changes their charts.
            Hosts.SupportCounts = song => song.Key.Source == SongSourceKind.Stock
                ? support.GetValueOrDefault(song.Key.StableId) ?? [0, 0, 0, 0]
                : [.. Enumerable.Range(0, 4).Select(course => course > 0 && song.Charts.Any(chart => chart.Course == (TaikoCourse)course) ? 3 : 0)];
            if (Arcade.Home && Options.Accounts is { } setupBook)
                _playerSetup = new PlayerSetupController(this, setupBook, options.FontPath, options.ScoresPath);
            // The settings file sits in USRDIR/waddamburo, the caches under it unless cache_folder moves them.
            var cacheRoot = options.ArcadePath is { } settings
                ? ArcadeSettings.CacheRoot(Path.GetDirectoryName(Path.GetFullPath(settings))!, Arcade.CacheFolder) : null;
            (MovieContent, Upscale) = GameMovieContent.Create(Application, Path.GetFullPath(assetRoot), cacheRoot, () => Arcade);
            _presenter = new ScenePresenter(Application, MovieContent);
            Scenes = new SceneSwitcher(this, _presenter, flowOf, () => _home?.Covering == true, blackLoadingFrame);
            MovieContent.Reuse = _presenter.Reuse;
            ApplyDisplay();
            _loader = new LumenGameSceneLoader(MovieContent, Hosts);
            Coordinator = new GameFlowCoordinator(Catalog, _loader, flow);
            Overlay = new IntermissionOverlay(Application, _loader);
            // The intermissions are loaded once, up front: the rainbow must never stall a song's start.
            foreach (var intermission in new[] { FlowScenes.Rainbow, FlowScenes.Shutter, FlowScenes.Fade, FlowScenes.DanCover, FlowScenes.DanExitCover, FlowScenes.DanShutter })
                Overlay.Preload(intermission);

            _gameplay = new GameplayFlow(this);
            _attract = new AttractFlow(this, _gameplay, AttractMovie.Discover(Path.Combine(dataRoot, "movie")),
                AttractMovie.Opening(Path.Combine(dataRoot, "movie"), Path.Combine(assetRoot, "attract", "title", "packeddata.ddp")));
            var calibration = new CalibrationFlow(this);
            var benchmarkSong = new BenchmarkFlow(this);
            if (options.Benchmark != BenchmarkMode.Off)
            {
                // The same demo songs and scene parts every run.
                Random = new Random(20261105);
                _benchmark = new BenchmarkRun(this, benchmarkSong, options.Benchmark == BenchmarkMode.Restart);
            }
            _home = new HomeControls(this, _gameplay, calibration, options.FontPath, assetRoot);
            _search = new SongSearch(this, _libraries.Snapshot, Painter);
            _scores = new ScoreListOverlay(this, Painter, new OptionIcons(Application, options.AssetRoot));
            Room = new RoomOverlay(this, Painter);
            Match = new OnlineMatch(this);
            CustomUploadPrompt = new CustomUploadPrompt(this, options.ServiceFontPath ?? options.FontPath);
            _inputOverlays = [_search, _scores, Room, CustomUploadPrompt];
            Reload = new SongSelectReload(this);
            var entry = new EntryFlow(this);
            var ending = new CreditEndFlow(this, _gameplay);
            var songSelect = SongSelectFlow = new SongSelectFlow(this, _gameplay, calibration);
            _scenes = flowTable(_attract, entry, songSelect, _gameplay, calibration, benchmarkSong, ending);
        }
        catch (Exception exception) when (exception is not BootClosedException)
        {
            throw _boot.Fail(exception);
        }
    }

    // --start-scene=result-fail/-clear show these results; --start-scene=waiwai-result shows Waiwai's
    // (WADDAMBURO_WAIWAI_RESULT=segments,duet%,rare hits, e.g. 30,70,10).
    private static (TaikoPlayResult? Result, WaiwaiOutcome? Waiwai) diagnosticResults(StartScene start)
    {
        TaikoPlayResult? result = start switch
        {
            StartScene.ResultFail => new TaikoPlayResult(TaikoCourse.Normal, 0, 0, 0, 100, 0, 0, 0, false),
            StartScene.ResultClear => new TaikoPlayResult(TaikoCourse.Normal, 500_000, 90, 7, 3, 80, 0, 45, true),
            _ => null,
        };
        WaiwaiOutcome? waiwai = start == StartScene.WaiwaiResult
            ? Environment.GetEnvironmentVariable("WADDAMBURO_WAIWAI_RESULT")?.Split(',') is [var segments, var duet, .. var rest]
                ? new(int.Parse(segments, CultureInfo.InvariantCulture), int.Parse(duet, CultureInfo.InvariantCulture), [.. string.Concat(rest).Select(static hit => hit == '1')])
                : new(50, 90, [true])
            : null;
        return (result, waiwai);
    }

    // The scenes the flows show, and the routes the movies' own scene requests take.
    private SceneCatalog createSceneCatalog(StartScene start) =>
        new(
            FlowScenes.Initial([
                GameplaySceneComposition.Create(FlowScenes.Gameplay, Random.Shared, EnsoLayout),
                // Placeholder: the audio calibration replaces it with a fresh lane each time.
                GameplaySceneComposition.Create(FlowScenes.Calibration, Random.Shared, EnsoLayout),
                // Placeholder too: the benchmark puts its lane here.
                GameplaySceneComposition.Create(FlowScenes.Benchmark, Random.Shared, EnsoLayout),
                start is StartScene.DaniResultFail or StartScene.DaniResultClear ? FlowScenes.DaniResults
                    : _diagnosticWaiwai is null ? FlowScenes.Results : FlowScenes.WaiwaiResults,
            ]),
            [
                new SceneTransitionRoute(FlowScenes.Entry, new LumenSceneRequest(1, 0, 0), FlowScenes.SongSelect),
                // Mode select's AI battle (traced SetNextScene(10, 0, 0)): its own song select (Players.ChooseMode).
                new SceneTransitionRoute(FlowScenes.Entry, new LumenSceneRequest(10, 0, 0), FlowScenes.SongSelect),
                // The entry gives up (a card declined, or its dialog timed out, with nobody joined and no
                // credits: TerminateEntry -> SetNextScene(0)): back to the attract loop.
                new SceneTransitionRoute(FlowScenes.Entry, new LumenSceneRequest(0, 0, 0), FlowScenes.Logo),
            ]);

    // The movies' hosts (the game side of every Lumen movie): what they ask the game and what it answers.
    private LayerHostFactory createHosts(GameFlowSession flow, SongCatalogSnapshot snapshot, TaikoPlayResult? diagnosticResult) =>
        new(
            flow,
            snapshot,
            Titles,
            Previews is null ? TracePreviewController.Instance : Previews,
            Sounds is null ? TraceSoundController.Instance : Sounds.Frontend,
            Sounds,
            new ViewerFrontendServices(Sounds?.Frontend, Arcade.FreePlay),
            Don,
            PlayRequests,
            () => SongInfo,
            Options.Countdown,
            () => Gameplay.Results is { Count: > 0 } results ? Match.WithPartnerResult(results)
                : diagnosticResult is { } diagnostic ? [diagnostic] : [],
            () => Arcade,
            Coins)
        {
            WaiwaiOutcome = () => Gameplay.WaiwaiOutcome ?? _diagnosticWaiwai,
            DanBests = () => TaikoGuest.Profiles[Hosts!.PlayerSide] is { } player && Sync.Scores is { } scores
                ? scores.DanBests(player.Baid) : new Dictionary<string, int>(),
            DanResult = Options.StartScene is StartScene.DaniResultFail or StartScene.DaniResultClear
                ? diagnosticDan(Options.StartScene == StartScene.DaniResultClear) : null,
            Rankings = Sync.Online ? new SongRankings(Sync.RankingClient, ChartHashes) : null,
            SongsStarted = () => Players.SongsPlayed,
            AiPanels = () => AiStanding() is var (points, wins, _)
                ? new(AiRanks.RankOf(points), AiRanks.PointsToNextRank(points), wins, Waddamburo.Game.Gameplay.AiBattleRanks.WinsToNextReward(wins), Waddamburo.Game.Lumen.AiBattlePanels.NewPlayer.Tension)
                : Waddamburo.Game.Lumen.AiBattlePanels.NewPlayer,
            ShowOni = () => Arcade.ShowOni,
            RandomSongFolder = () => Arcade.RandomSongFolder,
            HideArcadeHud = () => HideArcadeHud,
            TimerFreeze = () => Arcade.TimerFreeze,
            HomeTutorialSeen = () => Arcade.Home && Arcade.TutorialSeen,
            CopyrightPages = _copyright.Pages,
            Best = index => (uint)index < (uint)Bests.Length ? Bests[index] : null,
            Placement = index => (uint)index < (uint)Placements.Length ? Placements[index] : null,
            // Next to the settings file (USRDIR/waddamburo); memory only without one. Home only: the list is
            // the machine's, not a player's, so a cabinet's guests and card players get no folder (#6).
            Favourites = !Arcade.Home ? null : new SongFavourites(Options.ArcadePath is { } arcade
                ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(arcade))!, "favourites.txt") : null),
            Crowns = side => Sync.Scores is not { } scores ? null
                : TaikoGuest.Profiles[side] is { } profile ? scores.Crowns(profile.Baid)
                : Arcade.Home ? scores.Crowns(ScoreProfile.LocalGuestBaid) : null,
            RecentCharts = side => Sync.Scores is not { } scores ? null
                : TaikoGuest.Profiles[side] is { } profile ? scores.RecentCharts(profile.Baid)
                : Arcade.Home ? scores.RecentCharts(ScoreProfile.LocalGuestBaid) : null,
            CardClaimed = (side, card) =>
            {
                TaikoGuest.Profiles[side] = card;
                Sync.RefreshBests(card);
            },
            // A card given to the drum, or the account chosen for it in the home player setup.
            Dan = _danCourses,
            PlayerLook = side => TaikoGuest.Profiles[side]?.Look,
            OnlineAvailable = () => Room.Available,
            InOnlineRoom = () => Room.Session is not null,
            Received = ReceivedSongs.Find,
            // Home boards show the account's public name (or Guest), drawn by the entry overlay.
            PlayerName = side => Arcade.Home ? TaikoGuest.Profiles[side]?.DisplayName ?? Strings.T("setup.guest") : TaikoGuest.Profiles[side]?.DisplayName,
        };

    // Which flow runs each scene.
    private static Dictionary<SceneId, FlowScene> flowTable(AttractFlow attract, EntryFlow entry, SongSelectFlow songSelect,
        GameplayFlow gameplay, CalibrationFlow calibration, BenchmarkFlow benchmark, CreditEndFlow ending) => new()
    {
        [FlowScenes.Boot] = attract,
        [FlowScenes.Logo] = attract,
        [FlowScenes.Title] = attract,
        [FlowScenes.Caution] = attract,
        [FlowScenes.Movie] = attract,
        [FlowScenes.Entry] = entry,
        [FlowScenes.SongSelect] = songSelect,
        [FlowScenes.Tutorial] = songSelect,
        [FlowScenes.WaiwaiTutorial] = songSelect,
        [FlowScenes.AiTutorial] = songSelect,
        [FlowScenes.DaniSelect] = songSelect,
        [FlowScenes.Gameplay] = gameplay,
        [FlowScenes.Calibration] = calibration,
        [FlowScenes.Benchmark] = benchmark,
        [FlowScenes.Result] = ending,
        [FlowScenes.Retry] = ending,
        [FlowScenes.GameOver] = ending,
        [FlowScenes.AiRemind] = ending,
    };

    public string? FindJingle(string fileName)
    {
        if (Options.SoundRoot is null)
            return null;
        var path = Path.Combine(Path.GetFullPath(Options.SoundRoot), "bgm", "nub", fileName);
        return File.Exists(path) ? path : null;
    }

    private IAudioOutput openAudio()
    {
        if (Arcade.AudioExclusive && OperatingSystem.IsWindows())
        {
            try
            {
                var exclusive = new WasapiExclusiveOutput();
                Console.WriteLine($"Audio: WASAPI exclusive, {exclusive.HardwareBufferFrames} frames "
                    + $"({1000.0 * exclusive.HardwareBufferFrames / exclusive.Format.SampleRate:0.##} ms), {exclusive.Driver}.");
                return exclusive;
            }
            catch (InvalidOperationException exception)
            {
                Console.Error.WriteLine($"Audio: exclusive output unavailable, using shared. {exception.Message}");
            }
        }
        return new SdlAudioDevice(requestedBufferFrames: Arcade.AudioBufferFrames);
    }

    private FlowScene flowOf(SceneId scene) => _scenes[scene];

    private int run()
    {
        var initialScene = Options.StartScene switch
        {
            StartScene.Boot => FlowScenes.Boot,
            StartScene.Attract => FlowScenes.Logo,
            StartScene.Entry => FlowScenes.Entry,
            StartScene.SongSelect or StartScene.AiSongSelect => FlowScenes.SongSelect,
            StartScene.DaniSelect => FlowScenes.DaniSelect,
            StartScene.ResultFail or StartScene.ResultClear or StartScene.WaiwaiResult
                or StartScene.DaniResultFail or StartScene.DaniResultClear => FlowScenes.Result,
            StartScene.Retry => FlowScenes.Retry,
            StartScene.GameOver or StartScene.DaniGameOver => FlowScenes.GameOver,
            StartScene.AiRemind => FlowScenes.AiRemind,
            StartScene.WaiwaiTutorial => FlowScenes.WaiwaiTutorial,
            StartScene.AiTutorial => FlowScenes.AiTutorial,
            _ => throw new ArgumentOutOfRangeException(nameof(Options.StartScene)),
        };
        if (Options.StartScene == StartScene.DaniGameOver)
            Catalog.Replace(FlowScenes.DaniGameOver);
        if (Options.StartScene == StartScene.WaiwaiTutorial) Hosts.Waiwai = true;
        if (Options.StartScene == StartScene.AiTutorial) Hosts.AiBattle = true;
        if (Options.StartScene == StartScene.AiSongSelect)
            Players.ChooseMode(aiBattle: true);
        _boot?.Begin("SYSTEM SETUP");
        try
        {
            Coordinator.StartAsync(initialScene).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (_boot is not null)
        {
            throw _boot.Fail(exception);
        }
        try
        {
            Scenes.Activate();
            Console.WriteLine($"Showing {Active.Id} at launch.");
            _indicators = new SystemIndicators((LumenGameSceneInstance)_loader
                .LoadAsync(SystemIndicators.Definition(new SceneId("system-indicators")), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult()) { Coins = Coins, HideCoinType = () => HideArcadeHud, AiBattle = Hosts.AiBattle };
            _home.LoadResume(_loader);
            Hosts.LayerLoading = host =>
            {
                if (Don is null) return;
                // Song Select is back after an online song: one player again.
                if (host is "song-select" && Players.OnlineTwoPlayers)
                    Players.PlayOnlineAsTwoPlayers(false);
                if (host is "result" or "retry" or GameplaySceneComposition.StaticHostId)
                    Don.MapPlayerZero = Hosts.PlayerSide == 1 && !Hosts.TwoPlayers;
                else if (host is "player-entry" or "song-select" or "gameover" or "tutorial" or "dani-select")
                    Don.MapPlayerZero = false;
            };
            Hosts.CardDialog = open => _indicators.CardDialog(open);
            Hosts.ReturnToAttract = () => _returnToAttract = true;
            _indicators.NetworkIcon = Sync.Health is { } health ? () => health.IconType : null;
            Hosts.EntryJoined = side =>
            {
                Players.Join(side);
                // Home: the player setup drives the panels itself (SetupPanels).
                if (Coins is not null || _indicators.Rejoin || !Arcade.Home)
                    _indicators.EntryJoined();
            };
            _indicatorTextures = SceneTextures.Upload(Application, _indicators.Scene);
            // Everything is up: the check screen goes, the first scene takes the window.
            _boot?.Done();
            _boot?.Dispose();
            _boot = null;

            ApplyRecording();
            var result = Application.Run(
                interpolation => onTop(createFrame(interpolation)),
                tick,
                Options.FrameLimit,
                Options.TickLimit,
                Options.ScreenshotPath is null ? null : capture => ScreenshotWriter.Write(Options.ScreenshotPath, capture),
                updateFrame: keyboard =>
                {
                    if (_effectsEditor is { } editor)
                    {
                        if (keyboard.Presses.Any(static press => press.Key == SdlKeyboardKey.Escape))
                        {
                            editor.RequestClose();
                            keyboard = SdlKeyboardSnapshot.Empty;
                        }
                        if (editor.CloseRequested || !_gameplay.CanEditLayout)
                        {
                            closeEffectsEditor();
                            keyboard = SdlKeyboardSnapshot.Empty;
                        }
                    }
                    _input.Frame(keyboard);
                    if (_gameplay.Reviewing && !keyboard.Presses.IsEmpty && !GameActions.Pressed(keyboard, GameAction.Screenshot))
                        _reviewBar.Poke();
                    if (GameActions.Pressed(keyboard, GameAction.Screenshot))
                        Application.CaptureNextFrame = saveScreenshot;
                    if (!keyboard.Presses.IsEmpty)
                        _sinceInput.Restart();
                    // The notice sidebar (live presses only reach this callback; not over a lane, nor while the
                    // search field takes the letters).
                    if (_notices is not null && !Scenes.OnLane && !_search.IsOpen && !Room.Typing && GameActions.Pressed(keyboard, GameAction.Notices))
                        _notices.Toggle();
                    if (Arcade.Home || _service?.IsOpen != true)
                        flowOf(Active.Id).UpdateFrame(keyboard);
                },
                profileFrame: () => Active.Id == FlowScenes.Gameplay && !Overlay.IsShown,
                togglePerformanceOverlay: _performance.Toggle,
                pointerMoved: _performance.SetPointer,
                performanceVisible: () => _performance.Visible,
                pointerClicked: inspectAt,
                toggleInspect: () => Console.WriteLine($"Inspect {((LumenPlayer.Inspect = !LumenPlayer.Inspect) ? "on: left-click lists what is drawn there" : "off")}."),
                performanceSample: _performance.Record);

            Console.WriteLine($"Active scene: {Active.Id}");
            _gameplay.ReportFinal(Active.Id);
            ReportDiagnostics();
            if (Environment.GetEnvironmentVariable("WADDAMBURO_PROFILE") == "1")
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                using var process = Process.GetCurrentProcess();
                Console.Error.WriteLine($"Profile exit {Active.Id}: managed {GC.GetTotalMemory(false) / 1048576d:F1} MiB, "
                    + $"RSS {process.WorkingSet64 / 1048576d:F1} MiB.");
            }
            if (result.DroppedTicks > 0)
                Console.Error.WriteLine($"Warning PLT_DROPPED_TICKS: dropped {result.DroppedTicks} simulation ticks.");
            return result.RenderedFrames;
        }
        finally
        {
            Application.Recorder?.Dispose();
            Application.Recorder = null;
            _attract.Dispose();
            Overlay.Dispose();
            _presenter.Dispose();
            SceneTextures.Release(Application, _indicatorTextures);
            _home.Dispose();
            _service?.Dispose();
            Book.Save();
            _indicators?.Scene.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Coordinator.StopAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // No key or drum for this long at home, outside a song, shows the player as away.
    private static readonly TimeSpan AfkAfter = TimeSpan.FromMinutes(5);
    private readonly System.Diagnostics.Stopwatch _sinceInput = System.Diagnostics.Stopwatch.StartNew();

    // Discord's status: home mode only (a cabinet has no player's Discord), never in a headless run.
    private DiscordPresence? _discord;

    private void reportPresence()
    {
        if (!Arcade.Home || Headless)
            return;
        _discord ??= new DiscordPresence();
        var partner = Room.Session?.Partner?.DisplayName;
        var versus = partner is { Length: > 0 } ? $"Online vs {partner}" : Players.JoinedSides.Count > 1 ? "Two players" : null;
        var activity = _gameplay.Presence is { } playing ? new PresenceActivity(playing.What, versus ?? "Solo", playing.Since)
            : Active.Id == FlowScenes.SongSelect ? new PresenceActivity("Choosing a song", versus)
            : Active.Id == FlowScenes.DaniSelect ? new PresenceActivity("Choosing a dan course")
            : Active.Id == FlowScenes.Result ? new PresenceActivity("Looking at the results", versus)
            : Room.Session is not null ? new PresenceActivity("In an online room", versus)
            : new PresenceActivity("In the menus");
        _discord.Set(activity, Arcade.DiscordPresence);
    }

    private void reportStatus()
    {
        reportPresence();
        if (!Arcade.Home)
            return;
        // A song counts as activity, so leaving one is never AFK. Key presses are noted per frame (ticks only see held keys).
        if (_gameplay.Playing)
            _sinceInput.Restart();
        // The room, once the other player is there: the website names them (the server checks both are in it).
        var status = _gameplay.Playing ? PlayerStatus.Playing(_gameplay.PlayingChart)
            : _sinceInput.Elapsed >= AfkAfter ? PlayerStatus.Afk : PlayerStatus.Online;
        Sync.ReportStatus(Room.Session is { Partner: not null, Room: { } room } ? status with { Room = room.Code } : status);
    }

    // One 60 Hz tick: the input first (back to the attract loop, coins, the home controls, the player
    // setup, the quick restart), then the active scene, the overlays, and a pending scene switch.
    private void tick(SdlKeyboardSnapshot keyboard)
    {
        Tick++;
        Sounds?.Bank.Pump();
        // Diagnostic: WADDAMBURO_CRASH_AT=<tick> crashes the game there, to see its error screen.
        if (_crashAt == Tick)
            throw new InvalidOperationException($"WADDAMBURO_CRASH_AT asked the game to crash at tick {Tick}.");
        var keys = Options.InputTimeline.Apply(Tick, keyboard);
        _benchmark?.Tick();
        Book.PowerOnTicks++;
        if (_gameplay.Playing)
            Book.PlayTicks++;
        if (Tick % 3600 == 0)
            Book.Save();
        if (_cabinet.TickServiceMenu(keys))
            return;
        var presses = _input.Take(keys);
        reportStatus();
        Match.Tick();
        // F1 (back to the attract loop) is a home shortcut; a cabinet only gets there through its movies.
        if (backToAttract(Arcade.Home && presses.Attract))
            return;
        _cabinet.TakeCoins(_input.TakeCoins(keys));
        // Diagnostic: WADDAMBURO_INPUT_TRACE=1 prints presses in --press format (KEY@tick).
        if (_traceInput)
            foreach (var press in keys.Presses)
                Console.WriteLine($"[input] {press.Key}@{Tick}");
        var scene = flowOf(Active.Id);
        // A reviewed play takes the mouse wheel (seeking) like a menu does.
        Application.MenuInput = !Scenes.OnLane || _home.MenuOpen || _gameplay.Reviewing && !_gameplay.Attempting;
        var held = keys;
        keys = scene.MapKeys(_input.Pulses(keys));
        // Home: Backspace is Escape too (a Tatacon's B button), except while it edits text or is being bound.
        var escape = _input.EscapePressed(keys, menuKey: Arcade.Home && !_search.IsOpen && !Room.Typing && !_home.CapturingBinding ? GameActions.Key(GameAction.Menu) : null);
        // Focus is followed whichever overlay takes the keys (the mute in background).
        _home.FollowFocus();
        // Song Select's search field takes the keyboard while open (its Escape closes it, not the menu).
        if (Reload.Tick() || !_home.MenuOpen && overlaysTake(keys, escape))
        {
            advance(scene, SdlKeyboardSnapshot.Empty);
            return;
        }
        if (_home.Tick(keys, held, escape))
        {
            // Online, the menu doesn't stop the song: its movies keep moving and it ends as it would. The attract
            // and its demo play go on too (muted).
            if (Match.InSong && Active.Id == FlowScenes.Gameplay || _home.AttractUnderMenu)
            {
                advance(scene, SdlKeyboardSnapshot.Empty);
                scene.Tick(new FlowInput(SdlKeyboardSnapshot.Empty, null, false, false));
            }
            return;
        }
        if (!Scenes.OnLane)
            keys = menuKeys(keys);
        // Home: the player setup runs inside the entry and takes the drums; the movie only animates.
        if (_playerSetup is not null && _playerSetup.Tick(ref keys))
            return;
        if (_home.QuickRestart(held))
            return;
        advance(scene, keys);
        var switching = Coordinator.Flow.State == GameFlowState.TransitionPending;
        if (_home.HoldsResults(held, switching))
            return;
        if (!switching)
        {
            scene.Tick(new FlowInput(keys, presses.DrumSide, presses.Skip, escape));
            return;
        }
        // A movie asked for the next scene (entry -> song select); its last voice finishes first.
        if (Sounds?.Bank.IsVoicePlaying == true)
            return;
        if (Active.Id == FlowScenes.Entry && Coordinator.Flow.PendingTransition is { } request)
            Players.ChooseMode(aiBattle: request.SceneNumber == 10);
        // The entry and Song Select fade to black first, as the menu's Restart, Song Select and Title do;
        // not under an intermission (the rainbow into a song covers its own switch).
        if (!Overlay.IsShown && PlayRequests.Pending is null && (Active.Id == FlowScenes.Entry || Active.Id == FlowScenes.SongSelect))
        {
            _home.FadeThen(() =>
            {
                if (Coordinator.Flow.State == GameFlowState.TransitionPending)
                    Scenes.ApplyPending();
            });
            return;
        }
        Scenes.ApplyPending();
    }

    // An open overlay takes the tick's input; with none open, each may open on its own key (Tab, R).
    private bool overlaysTake(SdlKeyboardSnapshot keys, bool escape) =>
        _inputOverlays.LastOrDefault(static overlay => overlay.IsOpen) is { } open
            ? open.Tick(keys, escape) || true
            : _inputOverlays.Any(overlay => overlay.Tick(keys, escape));

    // F1 (testing convenience, not cabinet behaviour) or an entry that gave up: drop the credit and
    // return to the attract loop from any menu scene (not mid-song, whose music the gameplay flow owns).
    private bool backToAttract(bool f1)
    {
        var toAttract = f1 || _returnToAttract;
        _returnToAttract = false;
        if (!toAttract || Scenes.OnLane || Active.Id == FlowScenes.Boot)
            return false;
        // Fade out first; the switch below then runs under the black (Covering).
        if (!_home.Covering)
        {
            _home.FadeThen(() => GoToAttract());
            return false;
        }
        Sounds?.StopAll();
        SongSelectFlow.AbandonCover();
        Don?.SetDialogDon(false);
        Players.Reset();
        Players.SongsPlayed = 0; // a new credit starts at the first song
        Scenes.Show(FlowScenes.Logo);
        return true;
    }

    // The scene's movies and the overlays drawn with it advance one tick.
    private void advance(FlowScene scene, SdlKeyboardSnapshot keys)
    {
        scene.Advance(LumenInputAdapter.CreateSnapshot(keys,
            Scenes.OnLane ? LumenInputMode.PresentationOnly : LumenInputMode.AuthoredControls));
        Overlay.Advance();
        // Diagnostic: WADDAMBURO_DUMP_TREE=<tick> prints the active scene's display lists.
        if (_dumpTreeTick == Tick)
            foreach (var (layer, index) in Active.Layers.Select((layer, index) => (layer, index)))
            {
                Console.WriteLine($"== layer {index} {layer.Definition.MovieId}");
                foreach (var line in Active.Player.Layers[index].Player.DescribeDisplayList())
                    Console.WriteLine(line);
            }
        if (_indicators is null)
            return;
        if (_indicatorScene != Active.Id)
        {
            _indicatorScene = Active.Id;
            _indicators.SetScene(scene.IndicatorsFor(Active.Id));
        }
        _indicators.Advance();
    }

    // Everything the game is doing stops and its state goes: sounds, fades, menus, the song (dropped unsaved), a
    // pending scene switch or play, the room, the credit's players; the active flow cleans up as it leaves for the
    // attract's logo, which waits (not ticked) under the service menu.
    private void resetForService()
    {
        Audio?.Mixer.StopAll();
        Sounds?.StopAll();
        Scenes.CancelFade();
        _home.Reset();
        _search.Close();
        ClosePlayerSetup();
        if (Room.Session is not null)
            Room.Leave();
        if (Active.Id == FlowScenes.Gameplay)
            _gameplay.Discard();
        Coordinator.Flow.CancelPendingTransition();
        PlayRequests.CancelPending();
        Overlay.Clear();
        Don?.SetDialogDon(false);
        Players.Reset();
        Players.SongsPlayed = 0;
        _returnToAttract = false;
        Coins?.Clear();
        Scenes.Show(FlowScenes.Logo);
        Audio?.Mixer.StopAll(); // anything the switch started
        Console.WriteLine($"Service menu: game reset at tick {Tick}.");
    }

    /// <summary>Songs another player sent this session, in memory only.</summary>
    public ReceivedSongs ReceivedSongs { get; } = new();

    /// <summary>A song by its key: one of the libraries', else one received this session; null when neither has it.</summary>
    public SongSelectSong? FindSong(SongKey song) =>
        SongCatalog.Categories.SelectMany(static category => category.Songs).FirstOrDefault(entry => entry.Descriptor.Key == song)
        ?? (ReceivedSongs.Find(song) is { } received ? SongSelectCatalogView.CreateSong(received) : null);


    /// <summary>The system indicators (panels, coins, network), once loaded.</summary>
    public SystemIndicators? Indicators => _indicators;

    /// <summary>The indicators restart with the next scene (it reloads under the same id).</summary>
    public void ResetIndicatorScene() => _indicatorScene = null;

    /// <summary>The active scene's uploaded textures, by the scene's texture index.</summary>
    public RenderTextureId[] SceneTextureIds => _presenter.Textures;

    /// <summary>Back to the attract loop on the next tick (outside the callbacks asking for it).</summary>
    public void ReturnToAttract() => _returnToAttract = true;

    /// <summary>Fades picture and sound out, runs <paramref name="action"/> under the black, fades the new scene in.</summary>
    public void FadeThen(Action action) => _home.FadeThen(action);

    /// <summary>Back to the attract loop now (false when a song or boot is on: nothing happens).</summary>
    public bool GoToAttract() => backToAttract(true);

    /// <summary>Back to the title from the menu: an open player setup closes.</summary>
    public void ClosePlayerSetup() => _playerSetup?.Close();

    public void ReportDiagnostics()
    {
        foreach (var layer in Active.Layers.Select((loaded, index) => (loaded, index)))
            foreach (var diagnostic in Active.Player.Layers[layer.index].Player.Diagnostics)
                Console.WriteLine(
                    $"{layer.loaded.Definition.MovieId}: {diagnostic.Severity} {diagnostic.Code} "
                    + $"at character {diagnostic.CharacterId} frame {diagnostic.Frame}: {diagnostic.Message}");
    }

    private LumenRenderSnapshot? _inspected;

    // Inspect mode: every scene quad under the click, topmost first (the stage is assumed to fill the window).
    private void inspectAt(float x, float y)
    {
        if (!LumenPlayer.Inspect || _inspected is not { } scene)
            return;
        float px = x * scene.StageWidth, py = y * scene.StageHeight;
        static float cross(LumenRenderVertex a, LumenRenderVertex b, float px, float py) =>
            (b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X);
        var hits = scene.Quads.Where(quad => quad.MaskOperation == LumenRenderMaskOperation.Draw).Where(quad =>
        {
            var edges = new[]
            {
                cross(quad.TopLeft, quad.TopRight, px, py), cross(quad.TopRight, quad.BottomRight, px, py),
                cross(quad.BottomRight, quad.BottomLeft, px, py), cross(quad.BottomLeft, quad.TopLeft, px, py),
            };
            return edges.All(static edge => edge >= 0) || edges.All(static edge => edge <= 0);
        }).Reverse().ToArray();
        Console.WriteLine($"Inspect at stage ({px:0}, {py:0}): {hits.Length} quads, topmost first.");
        foreach (var quad in hits)
            Console.WriteLine($"  {quad.Source ?? "?"}{(quad.NativeSurface is { } surface ? $" -> surface {surface.Value}" : $" (scene texture {quad.TextureIndex})")}");
    }

    // A decoded movie: cached upscales, then its Lumen patch. The layout check comes first (upscales change
    // texture sizes); a patched movie keeps its upscales as RGBA (the patch derives textures from them) and
    // its derived textures are not upscaled again (a split layer upscaled alone gets sharpened edges).
    public RenderTextureId? ResolveSurface(LumenNativeSurfaceKey surface) =>
        _attract.Movie?.Resolve(surface) ?? Don?.Resolve(surface)
        ?? _danArt.Resolve(surface) ?? _copyright.Resolve(surface) ?? _costumeIcons.Resolve(surface) ?? _waiwaiResultTextures.Resolve(surface) ?? _textFields.Resolve(surface) ?? _nameTexts.Resolve(surface) ?? _toneArt.Resolve(surface)
        ?? Titles.Resolve(surface);

    // One displayed frame. Depth order (traced): scene, msg_coins (-950), intermission (-2000),
    // network/card (-3000); then Waddamburo's own overlays and the home menu over everything.
    private readonly List<RenderQuad> _frameQuads = [];

    // The scene's Don-chans (their native surfaces) and its name board layers, on their own.
    private System.Collections.Immutable.ImmutableArray<RenderQuad> players(LumenRenderSnapshot scene, float interpolation)
    {
        var dons = scene.Quads.Where(quad => quad.NativeSurface is { } surface && Don?.Resolve(surface) is not null);
        var boards = Active.Layers.Select((layer, index) => (layer, index))
            .Where(entry => Path.GetFileNameWithoutExtension(entry.layer.Definition.MovieId) == "player_name")
            .Select(entry => Active.Player.Layers[entry.index]);
        var boardQuads = new LumenScenePlayer(scene.StageWidth, scene.StageHeight, boards).CreateRenderSnapshot(interpolation).Quads;
        return SceneTextures.Compose(new LumenRenderSnapshot(scene.StageWidth, scene.StageHeight, [.. dons, .. boardQuads]),
            _presenter.Textures, "Players", ResolveSurface).Quads;
    }

    private RenderFrame createFrame(double interpolationFraction)
    {
        if (_service is { IsOpen: true } service && !Arcade.Home)
        {
            // The menu's frame is the one shown: the switch under it lets go of its held frame, and the switch out
            // of it (EXIT) holds this one, not the scene from before the menu.
            _presenter.Held(Tick);
            var menu = service.Frame();
            _presenter.Presented(menu, withIntermission: false);
            return menu;
        }
        // Freeze animations (the editors): everything stands on one tick, Don and the overlays too.
        var interpolation = FreezeAnimations && EffectsEditorOpen ? 1 : _home.Interpolation(interpolationFraction);
        if (_presenter.Held(Tick) is { } held)
        {
            _home.HoldBlack();
            // The rainbow stays up across its scene switch (only a fade is cleared with it).
            return _home.Draw(held.Black && Overlay.IsShown
                ? new RenderFrame(held.Frame.ClearColor,
                    [.. Overlay.Quads(interpolation, Titles.Resolve), .. held.Frame.Quads], held.Frame.ContentAspectRatio)
                : held.Frame);
        }
        Titles.UploadCompleted();
        _presenter.UploadAhead();
        if (SceneTextures.Upscaler is { } upscaler)
        {
            upscaler.Paused = Scenes.OnLane || Bake?.Running == true;
            upscaler.Apply(Application);
        }
        if (DonRenderer is not null)
            DonRenderer.Interpolation = interpolation;
        var scene = flowOf(Active.Id).CreateSnapshot(interpolation);
        if (LumenPlayer.Inspect)
            _inspected = scene;
        // The scene's quads go straight into the reused frame list (no frame of their own).
        var quads = _frameQuads;
        quads.Clear();
        SceneTextures.ComposeInto(scene, quads, _presenter.Textures, "Scene", ResolveSurface);
        IEnumerable<RenderQuad> indicatorQuads(bool overIntermission) => _indicators is null ? []
            : Unsqueezed(SceneTextures.Compose(_indicators.CreateSnapshot(overIntermission, interpolation), _indicatorTextures,
                "Indicator", Titles.Resolve).Quads);
        if (Active.Id == FlowScenes.Gameplay)
            quads.AddRange(_timingMarks.Quads(Gameplay.TimingMarks, Arcade.TimingIndicator));
        if (!EffectsEditorOpen && _gameplay.Reviewing && _gameplay.Review is { } review)
            quads.AddRange(_reviewBar.Quads(review, _gameplay.Training, _gameplay.AutoplayReview && _gameplay.Training is null));
        if (_playerSetup is not null)
            quads.AddRange(_playerSetup.Quads(interpolation));
        quads.AddRange(indicatorQuads(false));
        quads.AddRange(Overlay.Quads(interpolation, Titles.Resolve));
        // The online lobby shades (frosts) Song Select, all but its Don-chans and name boards: those come again over it.
        var frosted = 0;
        if (Room.ShadesScene && Active.Id == FlowScenes.SongSelect)
        {
            frosted = quads.Count;
            quads.AddRange(Room.Shade());
            quads.AddRange(players(scene, interpolation));
        }
        foreach (var overlay in _inputOverlays)
            quads.AddRange(overlay.Quads());
        quads.AddRange(indicatorQuads(true));
        quads.AddRange(pill());
        if (_notices is not null)
            // A replay being watched shows its toasts (a report's answer, a sanction); a song being played holds them.
            quads.AddRange(_notices.Quads(Scenes.OnLane && !(_gameplay.Reviewing && !_gameplay.Attempting)));
        // The frame reads the reused list without a copy: valid until the next one (the presenter detaches what it holds).
        var result = RenderFrame.Shared(RenderColor.Black, quads, (double)scene.StageWidth / scene.StageHeight,
            blurredQuads: frosted, backdropBrightness: HomeControls.BackdropBrightness);
        _presenter.Presented(result, Overlay.IsShown);
        Painter.EndFrame();
        return _home.Draw(result);
    }

    /// <summary>
    /// A 4:3 layout squeezes the stage to 3/4 of its width: what isn't part of the layout is widened back by 4/3 to keep
    /// its shape, from its side's edge (the corner indicators: network, coins) or about the centre (the menus).
    /// </summary>
    public IEnumerable<RenderQuad> Unsqueezed(IEnumerable<RenderQuad> quads, bool centred = false)
    {
        if (!Application.Squeeze43)
            return quads;
        RenderVertex v(RenderVertex vertex) => vertex with
        {
            X = centred ? .5f + (vertex.X - .5f) * 4 / 3 : vertex.X >= .5f ? 1 - (1 - vertex.X) * 4 / 3 : vertex.X * 4 / 3,
        };
        return quads.Select(quad => quad with
            { TopLeft = v(quad.TopLeft), TopRight = v(quad.TopRight), BottomRight = v(quad.BottomRight), BottomLeft = v(quad.BottomLeft) });
    }

    // Drawn last over every frame (the pause menu, the service menu, a held frame), never frosted with it.
    private RenderFrame onTop(RenderFrame frame)
    {
        var top = new List<RenderQuad>();
        if (BuildInfo.Nightly)
        {
            // A song too: a nightly's plays never reach TaikOnline (ScoreClient.IsDevVersion).
            top.Add(Painter.Rect(400, 0, 480, 26, new RenderColor(0.55f, 0.12f, 0.1f, 0.85f)));
            top.Add(Painter.Text(Strings.T("nightly.banner"), 640, 13, 468, 22));
        }
        if (uploadBanner() is { } uploadMessage)
        {
            var y = BuildInfo.Nightly ? 28 : 0;
            top.Add(Painter.Rect(380, y, 520, 26, new RenderColor(0.55f, 0.12f, 0.1f, 0.85f)));
            top.Add(Painter.Text(Strings.T(uploadMessage), 640, y + 13, 508, 22));
        }
        top.AddRange(_performance.Quads());
        if (top.Count == 0)
            return frame;
        return new RenderFrame(frame.ClearColor, frame.Quads.AddRange(top), frame.ContentAspectRatio)
        {
            BlurredQuads = frame.BlurredQuads,
            BackdropBrightness = frame.BackdropBrightness,
        };
    }

    private string? _uploadBanner;
    private string? _uploadBannerContext;
    private int _uploadBannerTick = -60;

    private string? uploadBanner()
    {
        var song = Active.Id == FlowScenes.SongSelect
            ? Hosts.SongSelect?.CourseSelectSong ?? Hosts.SongSelect?.SongUnderCursor : null;
        var request = Active.Id == FlowScenes.Gameplay || Active.Id == FlowScenes.Result ? _gameplay.CurrentRequest : null;
        var context = $"{Active.Id}:{song?.Descriptor.Key}:{request?.Song}:{Arcade.UploadCustomSongs}";
        // Marker checks touch the filesystem; refresh once a second and immediately when the context changes.
        if (_uploadBannerContext == context && Tick - _uploadBannerTick < 60) return _uploadBanner;
        (_uploadBannerContext, _uploadBannerTick) = (context, Tick);
        var source = song?.Descriptor.Key.Source ?? request?.Song.Source
            ?? (Active.Id == FlowScenes.SongSelect ? Hosts.Library : (SongSourceKind?)null);
        if (source is not (SongSourceKind.Tja or SongSourceKind.OsuLazer or SongSourceKind.OsuStable))
            return _uploadBanner = null;
        if (!Arcade.UploadCustomSongs) return _uploadBanner = "custom_upload.banner_all";
        var blocked = request is not null ? _gameplay.UploadBlocked
            : song?.Descriptor.Charts.Any(chart => Assets.UploadBlocked(chart.ChartAsset)) == true;
        return _uploadBanner = blocked ? "custom_upload.banner_chart" : null;
    }

    private RenderFrame blackLoadingFrame()
    {
        var network = _indicators is null ? []
            : SceneTextures.Compose(_indicators.CreateNetworkSnapshot(1), _indicatorTextures,
                "Network", Titles.Resolve).Quads;
        return new RenderFrame(RenderColor.Black, network, 1280d / 720d);
    }

    /// <summary>Home: a drum hit in the attract loop opens the player setup (that drum starts on the default account).</summary>
    public bool OpenPlayerSetup(int side)
    {
        _playerSetup?.Open(side);
        return _playerSetup is not null;
    }

    /// <summary>A paired card was rejected by the server (shown once).</summary>
    public bool TakeCardFailure() => Sync.Pairing?.TakeCardFailure() == true;

    /// <summary>A card was paired during the attract loop: it starts the credit (SCENE_TRIGGER_CARD).</summary>
    public ScoreProfile? TakeCard() => Sync.Pairing?.TakeCard();

    private IEnumerable<RenderQuad> pill()
    {
        if (Sync.Pairing is not { } pairing)
            return _pill.Quads(); // home: the account picker shows and hides it
        // The game takes cards in the attract loop (its demo play too) and in entry (not from song select on), one at
        // a time.
        var entry = flowOf(Active.Id) is EntryFlow;
        pairing.Accepting = Active.Id != FlowScenes.Boot && flowOf(Active.Id) is AttractFlow or GameplayFlow { Demo: true }
            || entry && !Hosts.EntryCardPending;
        if (entry && pairing.TakeCardFailure())
            Hosts.RejectEntryCard();
        if (entry && pairing.TakeCard() is { } card)
        {
            // A card already on a drum: the game reads it and refuses it (このデータはすでに読みこんでいます).
            if (TaikoGuest.Profiles.Any(profile => profile?.Baid == card.Baid))
            {
                Console.WriteLine($"Pairing: {card.Name} (baid {card.Baid}) is already playing; the card was refused.");
                Hosts.RefuseEntryCard();
            }
            else if (!Hosts.InsertEntryCard(card))
                Console.WriteLine("Pairing: the entry is busy with another card; this one was dropped.");
        }
        pairing.UpdatePill(_pill);
        return _pill.Quads();
    }

    public void Dispose()
    {
        closeEffectsEditor();
        _playerSetup?.Dispose();
        _textFields.Dispose();
        _nameTexts.Dispose();
        Painter.Dispose();
        Hosts?.Rankings?.Dispose();
        _pill.Dispose();
        _performance.Dispose();
        _notices?.Dispose();
        _timingMarks.Dispose();
        Titles.Dispose();
        Previews?.Dispose();
        Audio?.Dispose();
        _audioDevice?.Dispose();
        DonRenderer?.Dispose();
        Bake?.Stop(); // it upscales through the tool disposed next
        SceneTextures.Upscaler?.Dispose();
        Application.Dispose();
        _libraries.Dispose();
        CustomUploadPrompt.Dispose();
        Room.Dispose();
        Sync.Dispose();
        _discord?.Dispose();
    }
}
