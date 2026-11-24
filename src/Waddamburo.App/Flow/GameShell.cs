using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics;
using Waddamburo.App.Cli;
using Waddamburo.App.Hosting;
using Waddamburo.App.Presentation;
using Waddamburo.App.Scenes;
using Waddamburo.App.Tools;
using Waddamburo.Catalog;
using Waddamburo.Formats.Layout;
using Waddamburo.Game.Dan;
using Waddamburo.Game.Don;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.Scores;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.App.Home;

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
    BenchmarkMode Benchmark = BenchmarkMode.Off,
    FlowSmokeMode FlowSmoke = FlowSmokeMode.Off,
    string? FlowSmokeSong = null);

/// <summary>--benchmark: off, run and quit, or run and start the game again (Settings > Run Benchmark).</summary>
// Paced: the benchmark at the player's own vsync, frame cap and frame queue (it quits at the end), to count missed refreshes.
internal enum BenchmarkMode { Off, Quit, Restart, Paced }

/// <summary>
/// The running game: builds the services, owns the window loop, the active scene and what is drawn
/// over it (intermission, system indicators), the cabinet's coins and the credit's players, and hands
/// each tick to the active scene's <see cref="FlowScene"/>.
/// </summary>
internal sealed partial class GameShell : IDisposable
{
    public GameOptions Options { get; }
    /// <summary>The settings (the home menu changes them while the game runs).</summary>
    public ArcadeSettings Arcade { get; set; }

    public SdlApplication Application { get; }

    /// <summary>The effects and layout editors over the song on screen.</summary>
    public EffectsEditorHost Editor { get; private set; } = null!;

    /// <summary>The audio output the settings ask for, with its fallbacks and live switch.</summary>
    public AudioOutputHost AudioOutput { get; }

    private Rumble _rumble = null!;
    private PresenceReporter _presence = null!;
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

    public SdlDonRenderer? DonRenderer { get; }
    public DonPresentationController? Don { get; }
    public AudioEngine? Audio { get; }
    public GameSounds? Sounds { get; }
    public SongPreviewController? Previews { get; }
    public SongTitleTextureCache Titles { get; }
    public CoinBank? Coins { get; internal set; } // the cabinet controls replace it as the service menu closes



    /// <summary>Chart key -> hash, stored with the scores (matches server bests to the library).</summary>
    public ChartHashes ChartHashes { get; }

    /// <summary>Hashes the whole library again (Settings > Song Libraries), then matches the dan courses again.</summary>
    public void RehashLibrary() => ChartHashes.Rehash(_libraries.Snapshot.Songs.Values, _danCourses.Rematch);

    private readonly DanCourses _danCourses;
    private readonly CurrentEvent _event;

    /// <summary>The score database and the server side (uploads, bests, rankings, cabinet pairing).</summary>
    public ScoreSync Sync { get; }

    /// <summary>Songs play themselves: --autoplay, or the home setting (not in an online match).</summary>
    /// <summary>Home with the arcade HUD off: no timer, song number or free-play line (they don't count there).</summary>
    /// <summary>The active scene and its mode, for the log: "gameplay (AI battle, dev autoplay)".</summary>
    public string DescribeScene()
    {
        // The audio thread asks during boot too, before there is a switcher.
        if (Scenes?.Active is not { } active)
            return "starting";
        var gameplay = active.Id == FlowScenes.Gameplay && _scenes.TryGetValue(active.Id, out var flow) ? flow as GameplayFlow : null;
        string[] modes =
        [
            Arcade.Home ? "home" : "arcade",
            .. Credit.AiBattle ? ["AI battle"] : Array.Empty<string>(),
            .. Credit.Waiwai ? ["Waiwai"] : Array.Empty<string>(),
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
    private bool _performanceSetting;
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

    /// <summary>The hosts of the movies on stage, which the flows poll.</summary>
    public LoadedHosts Loaded { get; } = new();
    public GameFlowCoordinator Coordinator { get; }
    public IntermissionOverlay Overlay { get; }

    /// <summary>Headless (--screenshot): time follows ticks, not the clock.</summary>
    public bool Headless => Options.ScreenshotPath is not null;

    public int Tick { get; private set; }
    public LumenGameSceneInstance Active => Scenes.Active;


    /// <summary>Who is playing this credit (joined drums, songs played, a replay's player, online two-player).</summary>
    public PlayerSession Players { get; }

    /// <summary>The credit's mode, players and what a play hands its results (see <see cref="CreditSession"/>).</summary>
    public CreditSession Credit { get; } = new();

    /// <summary>Where the next Song Select starts.</summary>
    public SongSelectMemory SelectMemory { get; private set; } = null!;

    private readonly SongLibraries _libraries;
    private readonly LumenGameSceneLoader _loader;

    /// <summary>AI battle's AI-don levels (ghosttuning.xml; empty when the data has none).</summary>
    public IReadOnlyList<Waddamburo.Game.Gameplay.GhostLevel> GhostLevels { get; } = [];

    /// <summary>The AI battle player's standing (the playing drum's player; at home a guest is this PC's).</summary>
    public Waddamburo.Services.Scores.AiProgress AiStanding() =>
        (TaikoGuest.Profiles[Credit.PlayerSide] ?? (Arcade.Home ? Waddamburo.Game.Scores.ScoreProfile.LocalGuest : null)) is { } profile
            && Sync.Scores is { } scores ? scores.AiProgress(profile.Baid) : Waddamburo.Services.Scores.AiProgress.None;

    private bool _aiBattleInstalled = true;

    /// <summary>AI battle's rank ladder (the installation's ghostinfo.xml).</summary>
    public Waddamburo.Game.Gameplay.AiBattleRanks AiRanks { get; } = Waddamburo.Game.Gameplay.AiBattleRanks.Green;
    // The game's own art and text in the movies' native fills.
    private readonly NativeArt _art;
    private readonly AttractFlow _attract;
    private readonly GameplayFlow _gameplay;
    private readonly HomeControls _home;
    private readonly SongSearch _search;
    private readonly ScoreListOverlay _scores;
    // Overlays that take the keyboard (topmost last) and the painter Waddamburo's own overlays draw with.
    public CustomUploadPrompt CustomUploadPrompt { get; }

    /// <summary>Settings › Send to device / Receive from device.</summary>
    public DeviceTransfer DeviceTransfer { get; }
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
    private readonly InputLatches _input = new();
    private bool _returnToAttract; // the entry gave up (applied on the next tick, outside its callbacks)
    // Arcade: the service menu (Escape, the test switch), the coin switches and the cabinet's running totals.
    private readonly ServiceMenu? _service;
    private readonly CabinetControls _cabinet;

    /// <summary>The service menu (arcade) or the settings overlay (home).</summary>
    public ServiceMenu? Service => _service;

    /// <summary>The cabinet's bookkeeping (the service menu's counts).</summary>
    public Bookkeeping Book { get; }
    private readonly bool _traceInput = DiagnosticSwitches.InputTrace;
    private readonly int _dumpTreeTick =
        int.TryParse(DiagnosticSwitches.DumpTree, out var dumpAt) ? dumpAt : -1;

    /// <summary>The game closed to start again (the caller relaunches it once everything is released).</summary>
    public static bool RestartRequested { get; private set; }

    /// <summary>The arguments the relaunch adds (--benchmark=restart from the settings), or none.</summary>
    public static string[] RestartArguments { get; private set; } = [];

    /// <summary>The randomness of what should repeat in a benchmark (demo songs, scene parts); shared otherwise.</summary>
    public Random Random { get; private set; } = Random.Shared;

    private readonly BenchmarkRun? _benchmark;
    private readonly FlowSmokeRun? _flowSmoke;

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
        var drum = Credit.PlayerSide == 1 && !Credit.TwoPlayers && Active.Id != FlowScenes.Retry ? 4 : 0;
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
                    + $"audio {Arcade.AudioOutput switch
                    {
                        AudioOutputMode.Exclusive => $"exclusive period {Arcade.AudioExclusivePeriod}, device {Arcade.AudioDevice ?? "default"}",
                        AudioOutputMode.Asio => $"asio {Arcade.AsioDriver ?? "first driver"} buffer {Arcade.AsioBufferFrames}",
                        _ => $"buffer {Arcade.AudioBufferFrames}, device {Arcade.AudioDevice ?? "default"}",
                    }}",
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

    // osu! libraries are read only once chosen (#46): the first start finding an install not set yet offers it, off
    // until the player turns it on. Escape asks again next start.
    // The screens' movies decoded and up, their jingles decoded, before the game starts: no file is read where a screen
    // changes (a Switch's SD card took 0.3-1.2 s there).
    private void askOsuLibraries(BootChecks boot)
    {
        var lazer = string.IsNullOrEmpty(Arcade.OsuFolder) ? Waddamburo.Providers.OsuLazer.OsuLazerCatalogProvider.FindDataFolder(null) : null;
        var stable = string.IsNullOrEmpty(Arcade.OsuStableFolder) ? Waddamburo.Providers.OsuLazer.OsuLazerCatalogProvider.FindStableFolder(null) : null;
        if (lazer is null && stable is null)
            return;
        bool useLazer = false, useStable = false;
        // Settings' own option rows: Enter picks one, the arrows switch it, green at its default (off).
        OverlayRow option(string name, string path, Func<bool> on, Action flip) =>
            new(OverlayMenu.upper(Strings.T(name)), 400, () => $"{{{Strings.T(on() ? "service.on" : "service.off")}}}", 640,
                Step: _ => flip(), IsDefault: () => !on(), Hint: OverlayMenu.upper(Strings.T("osu_found.hint", path)));
        boot.Ask(close => new OverlayPage(() => OverlayMenu.upper(Strings.T("osu_found.title")), () =>
        [
            .. lazer is null ? (OverlayRow[])[] : [option("osu_found.lazer", lazer, () => useLazer, () => useLazer = !useLazer)],
            .. stable is null ? (OverlayRow[])[] : [option("osu_found.stable", stable, () => useStable, () => useStable = !useStable)],
            new(Strings.T("service.ok"), 400, Gap: true, Enter: () =>
            {
                SaveArcade(Arcade with
                {
                    OsuFolder = useLazer ? lazer : Arcade.OsuFolder,
                    OsuStableFolder = useStable ? stable : Arcade.OsuStableFolder,
                    OsuLibrariesAsked = true,
                });
                close();
            }),
        ], Defaults: true));
    }

    public void ApplyDisplay()
    {
        // The setting shows or hides it when it changes; F5 (or a phone's development build) in between is left alone.
        if (Arcade.PerformanceOverlay != _performanceSetting)
        {
            _performanceSetting = Arcade.PerformanceOverlay;
            if (_performance.Visible != _performanceSetting)
                _performance.Toggle();
        }
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
        // Mobile starts a new shell in the same process after an explicit restart.
        RestartRequested = false;
        RestartArguments = [];
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

    private readonly int _crashAt = int.TryParse(DiagnosticSwitches.CrashAt, out var crashAt) ? crashAt : -1;

    // The startup's check screen, until the attract (or the scene asked for) shows.
    private BootChecks? _boot;

    private GameShell(GameOptions options)
    {
        Options = options;
        Players = new PlayerSession(this);
        AudioOutput = new AudioOutputHost(this);
        Arcade = options.Arcade ?? new ArcadeSettings();
        if (options.FlowSmoke != FlowSmokeMode.Off)
            Arcade = FlowSmokeRun.Configure(Arcade, options.FlowSmoke);
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
            // Not in scripted runs (benchmark, flow smoke, tick or frame limits): nobody is there to answer.
            if (Arcade.Home && !PlatformInfo.IsMobile && !Arcade.OsuLibrariesAsked && Interactive
                && Options.Benchmark == BenchmarkMode.Off && Options.FlowSmoke == FlowSmokeMode.Off)
                askOsuLibraries(_boot);
            _libraries = _boot.Background("SONG LIBRARY", () => SongLibraries.Load(dataRoot, Arcade, options.TjaRoot, _boot.Progress,
                () => _boot.Note(Strings.T("boot.building_cache"))));
            // Not fatal: the libraries that load are listed (the settings say what's wrong with the others).
            if (_libraries.Problems is { Count: > 0 } problems)
            {
                // A toast too: the boot's NG flashes by, and the library's other songs still list as if nothing were wrong.
                foreach (var (folder, why) in problems)
                    Sync.Notices.AddLocal(Strings.T("library.left_out", Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), why), NoticeSeverity.Critical);
                _boot.Warn(problems.Count == 1 ? "1 library left out" : $"{problems.Count} libraries left out",
                    problems.Select(static problem => $"{problem.Folder}: {problem.Why}"));
            }
            // The installations' folders are checked in the background after the start: the notices menu shows how far.
            if (_libraries.MetadataCache is { } metadataCache)
            {
                Waddamburo.Game.Flow.JobBoard.Job? checking = null;
                var jobs = Sync.Jobs;
                metadataCache.CheckProgress = (done, total) =>
                {
                    lock (jobs)
                    {
                        checking ??= jobs.Start(Strings.T("job.check_libraries"), total, quiet: true);
                        checking.Report(done);
                        if (done >= total)
                            checking.Complete();
                    }
                };
            }
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
                ? Path.Combine(Path.GetDirectoryName(scores)!, "dan-courses.json") : null, Online.UpdateGate.Server(Arcade), Arcade.ServerInsecure);
            _event = new CurrentEvent(Online.UpdateGate.Server(Arcade), Arcade.ServerInsecure);

            _boot.Begin("I/O");
            if (ApplyControls(warn: true) is { Count: > 0 } controls)
                _boot.Warn(controls.Count == 1 ? "Binding not understood" : $"{controls.Count} bindings not understood", controls);
            _boot.Begin("DON-CHAN");
            DonRenderer = options.DonRoot is null ? null : Application.CreateDonRenderer(options.DonRoot);
            Don = DonRenderer is null ? null : new DonPresentationController(DonRenderer);
            // Diagnostic: WADDAMBURO_DON_COSTUME=head,body,paint (or a single whole-costume id) dresses P1 at start.
            if (Don is not null && DiagnosticSwitches.DonCostume is { } costumeSetting)
            {
                var ids = costumeSetting.Split(',').Select(int.Parse).ToArray();
                Don.SetCostume(0, ids.Length == 1 ? DonCostume.FromWhole(ids[0]) : new DonCostume(null, ids[0], ids[1], ids.ElementAtOrDefault(2)));
            }
            // Diagnostic: WADDAMBURO_TITLE=plate:text and/or WADDAMBURO_NAME=name give P1 a guest profile with them.
            var diagnosticName = DiagnosticSwitches.Name;
            if (DiagnosticSwitches.Title is [var plate, ':', .. var titleText])
                TaikoGuest.Profiles[0] = ScoreProfile.LocalGuest with { Title = titleText, TitlePlate = plate - '0', AccountName = diagnosticName };
            else if (diagnosticName is { Length: > 0 })
                TaikoGuest.Profiles[0] = ScoreProfile.LocalGuest with { AccountName = diagnosticName };
            // Diagnostic: WADDAMBURO_DON_PUCHI=id puts that puchi chara beside P1 at start.
            if (Don is not null && int.TryParse(DiagnosticSwitches.DonPuchi, out var puchiSetting))
                Don.SetAccessory(0, puchiSetting);
            _boot.Begin("AUDIO DEVICE");
            var needsAudio = !Headless || options.JinglePath is not null || options.SoundRoot is not null;
            var audioDevice = AudioOutput.Start(needsAudio);
            if (DiagnosticSwitches.Profile && audioDevice is not null)
                Console.Error.WriteLine($"Profile audio: {audioDevice.Driver}, "
                    + $"{audioDevice.HardwareFormat.SampleRate} Hz, "
                    + $"{audioDevice.HardwareBufferFrames} hardware frames.");
            Audio = audioDevice is null ? null : new AudioEngine(audioDevice) { TraceContext = () => $"{DescribeScene()} at tick {Tick}" };
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
            _art = new NativeArt(Application, options.FontPath, dataRoot);
            GhostLevels = Waddamburo.Game.Gameplay.GhostTuning.Load(dataRoot);
            AiRanks = Waddamburo.Game.Gameplay.AiBattleRanks.Load(dataRoot);
            var support = Waddamburo.Game.Gameplay.SupportInfo.Load(dataRoot);
            _performance = new PerformanceOverlay(Application, () => Audio)
            {
                Online = () => Room?.Session?.Link is { State: Waddamburo.Services.Online.PeerLinkState.Direct or Waddamburo.Services.Online.PeerLinkState.Relayed } link
                    ? (link.State == Waddamburo.Services.Online.PeerLinkState.Relayed, link.RoundTrip) : null,
            };
            // ponytail: phones have no F5 yet; development builds start with the performance overlay on there.
            if (PlatformInfo.IsMobile && BuildInfo.Dev)
                _performance.Toggle();
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
            {
                // 音なし (reported on Discord): the hits are silent in songs only, the menus keep their drum.
                if (Arcade.DrumSoundInSongs)
                    Sounds?.Gameplay.PlayDrum(lane, action is TaikoInputAction.LeftDon or TaikoInputAction.RightDon);
                _rumble.Hit(lane, action);
            },
                Don, (lane, sound, combo) =>
                {
                    Sounds?.Gameplay.Play(lane, sound, combo);
                    _rumble.Sound(lane, sound);
                },
                () => TimeSpan.FromMilliseconds(Arcade.DrumDebounceMs));
            // Other releases' installs (home's stock folders) name skins for the songs Green lacks.
            Skins = new GameplaySkinResolver(Path.GetFullPath(assetRoot), GameplaySkinResolver.MusicInfos(
                [dataRoot, .. Arcade.Home ? ArcadeSettings.Folders(Arcade.StockFolders).Select(Waddamburo.Providers.Stock.StockCatalogProvider.FindDataRoot).OfType<string>() : []]));
            EnsoLayout = GameplaySceneComposition.LoadLayout(assetRoot);

            (var diagnosticResult, _diagnosticWaiwai) = diagnosticResults(options.StartScene);

            // A Green from before the update that added AI battle (the web archive's): the mode select leaves it out.
            _aiBattleInstalled = Cli.GreenData.HasAiBattle(Path.GetDirectoryName(dataRoot)!);
            if (!_aiBattleInstalled)
            {
                _boot.Begin("AI BATTLE");
                _boot.Warn(Strings.T("boot.ai_battle_missing"));
            }

            _boot.Begin("MOVIES");
            var flow = new GameFlowSession();
            Catalog = createSceneCatalog(options.StartScene);
            SelectMemory = new SongSelectMemory(snapshot, () => Arcade);
            Hosts = createHosts(flow, snapshot, diagnosticResult);
            if (Options.StartScene is StartScene.DaniResultFail or StartScene.DaniResultClear)
                Credit.DanResult = diagnosticDan(Options.StartScene == StartScene.DaniResultClear);
            // Stock songs as supportinfo.xml lists them; any other song three steps below each course it has.
            // ponytail: three everywhere for custom songs, whether or not simplifying changes their charts.
            Hosts.SupportCounts = song => song.Key.Source == SongSourceKind.Stock
                ? support.GetValueOrDefault(song.Key.StableId) ?? [0, 0, 0, 0]
                : [.. Enumerable.Range(0, 4).Select(course => course > 0 && song.Charts.Any(chart => chart.Course == (TaikoCourse)course) ? 3 : 0)];
            if (Arcade.Home && Options.Accounts is { } setupBook)
                _playerSetup = new PlayerSetupController(this, setupBook, options.FontPath, options.ServiceFontPath ?? options.FontPath, options.ScoresPath);
            // The settings file sits in USRDIR/waddamburo, the caches under it unless cache_folder moves them.
            var cacheRoot = options.ArcadePath is { } settings
                ? ArcadeSettings.CacheRoot(Path.GetDirectoryName(Path.GetFullPath(settings))!, Arcade.CacheFolder) : null;
            (MovieContent, Upscale) = GameMovieContent.Create(Application, Path.GetFullPath(assetRoot), cacheRoot, () => Arcade);
            _presenter = new ScenePresenter(Application, MovieContent, () => Arcade.UpscaleTextures && Application.ShowUpscaled);
            Scenes = new SceneSwitcher(this, _presenter, flowOf, blackLoadingFrame);
            MovieContent.Reuse = _presenter.Reuse;
            ApplyDisplay();
            _loader = new LumenGameSceneLoader(MovieContent, Hosts);
            Coordinator = new GameFlowCoordinator(Catalog, _loader, flow);
            Overlay = new IntermissionOverlay(Application, _loader);
            // The intermissions are loaded once, up front: the rainbow must never stall a song's start.
            foreach (var intermission in new[] { FlowScenes.Rainbow, FlowScenes.Shutter, FlowScenes.Fade, FlowScenes.DanCover, FlowScenes.DanExitCover, FlowScenes.DanShutter })
                Overlay.Preload(intermission);
            Sounds?.PreloadMusic();

            _gameplay = new GameplayFlow(this);
            _rumble = new Rumble(this, _gameplay);
            _presence = new PresenceReporter(this, _gameplay);
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
            _home = new HomeControls(this, _gameplay, calibration, assetRoot);
            Editor = new EffectsEditorHost(this, _gameplay, () => _home.CloseForEffectsEditor());
            _search = new SongSearch(this, _libraries.Snapshot);
            _scores = new ScoreListOverlay(this, Painter, new OptionIcons(Application, options.AssetRoot));
            Room = new RoomOverlay(this, Painter);
            Match = new OnlineMatch(this);
            CustomUploadPrompt = new CustomUploadPrompt(this, options.ServiceFontPath ?? options.FontPath);
            DeviceTransfer = new DeviceTransfer(this, _libraries.SyncLibraries(dataRoot));
            _inputOverlays = [_search, _scores, Room, CustomUploadPrompt];
            Reload = new SongSelectReload(this);
            var entry = new EntryFlow(this);
            var ending = new CreditEndFlow(this, _gameplay);
            var songSelect = SongSelectFlow = new SongSelectFlow(this, _gameplay, calibration);
            _scenes = flowTable(_attract, entry, songSelect, _gameplay, calibration, benchmarkSong, ending);
            if (options.FlowSmoke != FlowSmokeMode.Off)
            {
                Random = new Random(20261118); // the same demo songs and scene parts every run
                _flowSmoke = new FlowSmokeRun(this, _gameplay, songSelect, options.FlowSmoke, options.FlowSmokeSong);
            }
        }
        catch (Exception exception) when (exception is not BootClosedException)
        {
            throw _boot.Fail(exception);
        }
    }

    // --start-scene=result-fail/-almost/-clear show these results; --start-scene=waiwai-result shows Waiwai's
    // (WADDAMBURO_WAIWAI_RESULT=segments,duet%,rare hits, e.g. 30,70,10).
    private static (TaikoPlayResult? Result, WaiwaiOutcome? Waiwai) diagnosticResults(StartScene start)
    {
        TaikoPlayResult? result = start switch
        {
            StartScene.ResultFail => new TaikoPlayResult(TaikoCourse.Normal, 0, 0, 0, 100, 0, 0, 0, false),
            StartScene.ResultAlmost => new TaikoPlayResult(TaikoCourse.Normal, 250_000, 60, 20, 40, 30, 0, 20, false),
            StartScene.ResultClear => new TaikoPlayResult(TaikoCourse.Normal, 500_000, 90, 7, 3, 80, 0, 45, true),
            _ => null,
        };
        WaiwaiOutcome? waiwai = start == StartScene.WaiwaiResult
            ? DiagnosticSwitches.WaiwaiResult?.Split(',') is [var segments, var duet, .. var rest]
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
                // Mode select's ごほうびショップ (traced SetNextScene(3, 0, 0)): the reward shop, then Song Select.
                new SceneTransitionRoute(FlowScenes.Entry, new LumenSceneRequest(3, 0, 0), FlowScenes.RewardShop),
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
            Options.Countdown,
            () => Gameplay.Results is { Count: > 0 } results ? Match.WithPartnerResult(results)
                // ponytail: WADDAMBURO_RESULT_PLAYERS=2 shows the diagnostic result for both players.
                : diagnosticResult is { } diagnostic
                    ? DiagnosticSwitches.ResultPlayers == "2" ? [diagnostic, diagnostic] : [diagnostic]
                    : [],
            () => Arcade,
            Coins,
            Credit,
            SelectMemory,
            Loaded)
        {
            WaiwaiOutcome = () => Gameplay.WaiwaiOutcome ?? _diagnosticWaiwai,
            DanBests = () => TaikoGuest.Profiles[Credit.PlayerSide] is { } player && Sync.Scores is { } scores
                ? scores.DanBests(player.Baid) : new Dictionary<string, int>(),
            Rankings = Sync.Online ? new SongRankings(Sync.RankingClient, ChartHashes) : null,
            AiPanels = () => AiStanding() is var (points, wins, _)
                ? new(AiRanks.RankOf(points), AiRanks.PointsToNextRank(points), wins, Waddamburo.Game.Gameplay.AiBattleRanks.WinsToNextReward(wins), Waddamburo.Game.Lumen.AiBattlePanels.NewPlayer.Tension)
                : Waddamburo.Game.Lumen.AiBattlePanels.NewPlayer,
            CopyrightPages = _art.CopyrightPages,
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
                FetchShop(side, card);
            },
            // A card given to the drum, or the account chosen for it in the home player setup.
            Dan = _danCourses,
            Event = () => _event.Current,
            PlayerLook = side => TaikoGuest.Profiles[side]?.Look,
            OnlineAvailable = () => Room.Available,
            AiBattleInstalled = _aiBattleInstalled,
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
        [FlowScenes.RewardShop] = ending,
    };

    public string? FindJingle(string fileName)
    {
        if (Options.SoundRoot is null)
            return null;
        var path = Path.Combine(Path.GetFullPath(Options.SoundRoot), "bgm", "nub", fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// The audio output's settings changed: the game stops everything, goes back to the attract and opens the new output
    /// in place (no restart, no library scan). False when it can't (no audio, or the new output runs at another sample
    /// rate than the mix): the caller restarts.
    /// </summary>
    public bool SwitchAudioOutput()
    {
        if (Audio is null)
            return false;
        resetForService();
        return AudioOutput.Switch(Audio);
    }

    private FlowScene flowOf(SceneId scene) => _scenes[scene];

    /// <summary>
    /// A home account's live fields (Don Medals, locks, rewards to show) from the server, in the background: the stored
    /// account keeps only its name and looks. At the player setup and each time Song Select comes back.
    /// </summary>
    public void RefreshPlayer(int side)
    {
        // A cabinet's cards come with their profile; only an account's token reads /me.
        if (TaikoGuest.Profiles[side] is not { Token: not null } player || Sync.ServerFor(player) is not var (client, baid) || baid is not null)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await client.MeAsync().ConfigureAwait(false) is { } current && TaikoGuest.Profiles[side] is { } joined && joined.Baid == current.Baid)
                    TaikoGuest.Profiles[side] = joined with { Medals = current.Medals, LastMedalDay = current.LastMedalDay,
                        Locked = current.Locked, PendingRewards = current.PendingRewards };
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"Warning ACCOUNT: player {side + 1}'s profile not refreshed ({exception.Message}).");
            }
        });
    }

    /// <summary>The first joined drum whose player has rewards to show (Song Select's "New rewards"); null: none.</summary>
    public int? PendingRewardsSide() => Players.JoinedSides.Cast<int?>()
        .FirstOrDefault(side => TaikoGuest.Profiles[side!.Value] is { PendingRewards.Count: > 0 } profile && Sync.ServerFor(profile) is not null);

    /// <summary>
    /// An arcade credit's end, asked for its rewards in the background (CreditSession.EndRewards): the first carded drum
    /// on a server uploads its songs, then TaikOnline checks the credit's titles. Null: nobody to ask.
    /// </summary>
    public Task<(int Side, List<Waddamburo.Game.Scores.PendingReward> Rewards)>? AskCreditEndRewards()
    {
        var creditId = Credit.CreditId;
        foreach (var side in Players.JoinedSides)
        {
            if (TaikoGuest.Profiles[side] is not { Baid: > 0 } profile || Sync.ServerFor(profile) is not var (client, baid)
                || Sync.Scores is not { } store)
                continue;
            return Task.Run(async () =>
            {
                try
                {
                    return (side, await client.CreditEndAsync(store, baid, creditId).ConfigureAwait(false));
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    Console.Error.WriteLine($"Warning REWARDS: the credit's rewards did not come ({exception.Message}).");
                    return (side, new List<Waddamburo.Game.Scores.PendingReward>());
                }
            });
        }
        return null;
    }

    /// <summary>Tells the server the reward animation was shown (set by <see cref="ShowRewards"/>, run as it ends).</summary>
    public Action? RewardsShown { get; set; }

    /// <summary>
    /// Song Select's "New rewards": reward_shop's medal screen hands the player's pending rewards over (no medals paid),
    /// then Song Select comes back; the server hears they were shown once it ends.
    /// ponytail: one player's rewards per visit; a second player's wait for the next.
    /// </summary>
    public void ShowRewards()
    {
        if (PendingRewardsSide() is not { } side || TaikoGuest.Profiles[side] is not { PendingRewards: { } rewards } profile
            || Sync.ServerFor(profile) is not var (client, baid))
            return;
        Credit.RewardPlayer = new Waddamburo.Game.Lumen.RewardShopPlayer(side, profile.Medals ?? 0,
            new Waddamburo.Game.Scores.MedalCredit(0, false, false, false),
            [.. rewards.Select(static reward => new Waddamburo.Game.Lumen.RewardItem(reward.Type, reward.Id, reward.MusicId))], Alone: true);
        // Once the animation is over (CreditEndFlow leaving it): they were shown.
        RewardsShown = () =>
        {
            if (TaikoGuest.Profiles[side] is { } shown && shown.Baid == profile.Baid)
                TaikoGuest.Profiles[side] = shown with { PendingRewards = null };
            _ = Task.Run(async () =>
            {
                try
                {
                    await client.RewardsSeenAsync(rewards, baid).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    Console.Error.WriteLine($"Warning REWARDS: the server did not hear the rewards were shown ({exception.Message}).");
                }
            });
        };
        Catalog.Replace(FlowScenes.HomeRewards);
        Scenes.FadeTo(FlowScenes.RewardShop);
    }

    /// <summary>
    /// The entry offers the reward shop once its goods are in: a card read (arcade) or an account chosen in the home player
    /// setup, on a server.
    /// </summary>
    public void FetchShop(int side, ScoreProfile player)
    {
        // A server without the shop answers 404: the fetch fails and the entry offers no shop.
        if (Sync.ServerFor(player) is not var (client, baid))
            return;
        var goods = Task.Run(() => client.ShopAsync(baid));
        _ = goods.ContinueWith(task => Console.WriteLine(task.IsCompletedSuccessfully
            ? $"Reward shop for side {side}: {task.Result.Goods.Count} goods."
            : $"Reward shop for side {side} unavailable: {task.Exception?.GetBaseException().Message}"), TaskScheduler.Default);
        Credit.Shops[side] = new(goods, no => client.PurchaseAsync(no, baid, goods.Result.Goods.FirstOrDefault(item => item.No == no)));
    }

    private int run()
    {
        var initialScene = Options.StartScene switch
        {
            StartScene.Boot => FlowScenes.Boot,
            StartScene.Attract => FlowScenes.Logo,
            StartScene.Entry => FlowScenes.Entry,
            StartScene.SongSelect or StartScene.AiSongSelect => FlowScenes.SongSelect,
            StartScene.DaniSelect => FlowScenes.DaniSelect,
            StartScene.ResultFail or StartScene.ResultAlmost or StartScene.ResultClear or StartScene.WaiwaiResult
                or StartScene.DaniResultFail or StartScene.DaniResultClear => FlowScenes.Result,
            StartScene.Retry => FlowScenes.Retry,
            StartScene.GameOver or StartScene.DaniGameOver or StartScene.RewardGameOver => FlowScenes.GameOver,
            StartScene.AiRemind => FlowScenes.AiRemind,
            StartScene.RewardShop or StartScene.RewardShop2P => FlowScenes.RewardShop,
            StartScene.WaiwaiTutorial => FlowScenes.WaiwaiTutorial,
            StartScene.AiTutorial => FlowScenes.AiTutorial,
            _ => throw new ArgumentOutOfRangeException(nameof(Options.StartScene)),
        };
        if (Options.StartScene == StartScene.DaniGameOver)
            Catalog.Replace(FlowScenes.DaniGameOver);
        if (Options.StartScene == StartScene.RewardGameOver)
        {
            // A first credit ever: 900 medals before it, every bonus.
            // Traced session-rewards-6's two kigurumi (43, 44), plus a head and a song.
            Credit.RewardPlayer = new Waddamburo.Game.Lumen.RewardShopPlayer(0, 900,
                Waddamburo.Game.Scores.DonMedals.ForCredit(null, DateOnly.FromDateTime(DateTime.Now)), [new(3, 43), new(3, 44), new(5, 119), new(1, 819, "csmclr")]);
            Catalog.Replace(FlowScenes.RewardGameOver);
        }
        if (Options.StartScene is StartScene.RewardShop or StartScene.RewardShop2P)
        {
            // Green's Winter shop (traced session-rewards-1); purchases succeed locally. 1P: 4950 medals, the first body owned;
            // 2P (reward-shop-2p): 600 medals, the first head owned.
            Waddamburo.App.Flow.Credit.CreditShop shop(int medals, int ownedNo)
            {
                List<Waddamburo.Game.Scores.ShopGoods> goods =
                [
                    new(1, 5, 117, 500, false), new(2, 4, 117, 500, false), new(3, 5, 135, 500, false), new(4, 4, 146, 500, false),
                    new(5, 1, 865, 1300, false, "589him"), new(6, 1, 864, 1300, false, "tecdrv"),
                    new(7, 1, 858, 1500, false, "kooryu"), new(8, 1, 866, 1500, false, "kooryu"),
                ];
                goods[ownedNo - 1] = goods[ownedNo - 1] with { Owned = true };
                return new(Task.FromResult(new Waddamburo.Game.Scores.ShopState(4, "Winter reward shop", medals, goods)),
                    no => Task.FromResult(new Waddamburo.Game.Scores.ShopPurchase("ok", medals -= goods[no - 1].Price)));
            }
            Credit.Shops[0] = shop(4950, 2);
            if (Options.StartScene == StartScene.RewardShop2P)
            {
                Credit.Shops[1] = shop(600, 1);
                Credit.TwoPlayers = true;
            }
        }
        if (Options.StartScene == StartScene.WaiwaiTutorial) Credit.Waiwai = true;
        if (Options.StartScene is StartScene.AiSongSelect or StartScene.AiTutorial)
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
                .AsTask().GetAwaiter().GetResult()) { Coins = Coins, HideCoinType = () => HideArcadeHud, AiBattle = Credit.AiBattle };
            _home.LoadResume(_loader);
            Hosts.LayerLoading = host =>
            {
                if (Don is null) return;
                // Song Select is back after an online song: one player again.
                if (host is "song-select" && Players.OnlineTwoPlayers)
                    Players.PlayOnlineAsTwoPlayers(false);
                if (host is "result" or "retry" or GameplaySceneComposition.StaticHostId)
                    Don.MapPlayerZero = Credit.PlayerSide == 1 && !Credit.TwoPlayers;
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
                interpolation => countFrame(interpolation),
                tick,
                Options.FrameLimit,
                Options.TickLimit,
                Options.ScreenshotPath is null ? null : capture => ScreenshotWriter.Write(Options.ScreenshotPath, capture),
                updateFrame: keyboard =>
                {
                    keyboard = Editor.Frame(keyboard);
                    _input.Frame(keyboard);
                    if (_gameplay.Reviewing && !keyboard.Presses.IsEmpty && !GameActions.Pressed(keyboard, GameAction.Screenshot))
                        _reviewBar.Poke();
                    if (GameActions.Pressed(keyboard, GameAction.Screenshot))
                        Application.CaptureNextFrame = saveScreenshot;
                    if (!keyboard.Presses.IsEmpty)
                        _presence.Pressed();
                    // The notice sidebar (live presses only reach this callback; not over a lane, nor while the
                    // search field takes the letters). The attract's demo play is not a song being played.
                    if (_notices is not null && (!Scenes.OnLane || _gameplay.Demo) && !_search.IsOpen && !Room.Typing && GameActions.Pressed(keyboard, GameAction.Notices))
                        _notices.Toggle();
                    // Arcade: a card shortcut's button inserts its card (card_1p / card_2p) as if read.
                    if (Sync.Pairing is { } cards)
                        foreach (var (button, code) in (ReadOnlySpan<(SdlKeyboardKey, string?)>)[(SdlKeyboardKey.CardP1, Arcade.Card1P), (SdlKeyboardKey.CardP2, Arcade.Card2P)])
                            if (code is not null && keyboard.Presses.Any(press => press.Key == button) && !cards.Insert(code))
                                Console.WriteLine("Card shortcut: no card is taken now (or one is waiting already).");
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
            if (DiagnosticSwitches.Profile)
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
            _search?.Dispose();
            Book.Save();
            Book.Flush();
            _indicators?.Scene.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Coordinator.StopAsync().AsTask().GetAwaiter().GetResult();
        }
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

    // Frames built and the time spent building them (scene snapshots, quads; not the GPU): one line at exit, the
    // number to compare when optimizing.
    private long _framesBuilt, _frameBuildTicks, _firstFrameAt;

    private RenderFrame countFrame(double interpolation)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_firstFrameAt == 0)
            _firstFrameAt = start;
        var frame = onTop(createFrame(interpolation));
        _frameBuildTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
        _framesBuilt++;
        return frame;
    }

    public void Dispose()
    {
        if (_framesBuilt > 0)
        {
            var seconds = System.Diagnostics.Stopwatch.GetElapsedTime(_firstFrameAt).TotalSeconds;
            Console.WriteLine(System.FormattableString.Invariant(
                $"Frames: {_framesBuilt} in {seconds:F1} s ({_framesBuilt / seconds:F0} fps), building {System.Diagnostics.Stopwatch.GetElapsedTime(0, _frameBuildTicks).TotalMilliseconds / _framesBuilt:F3} ms a frame."));
        }
        Editor?.Close();
        _playerSetup?.Dispose();
        _art?.Dispose();
        Painter.Dispose();
        Hosts?.Rankings?.Dispose();
        _pill.Dispose();
        _performance.Dispose();
        _notices?.Dispose();
        _timingMarks.Dispose();
        Titles.Dispose();
        Previews?.Dispose();
        Audio?.Dispose();
        AudioOutput.Dispose();
        DonRenderer?.Dispose();
        Bake?.Stop(); // it upscales through the tool disposed next
        SceneTextures.Upscaler?.Dispose();
        CustomUploadPrompt.Dispose();
        DeviceTransfer.Dispose(); // both own an OverlayMenu, whose textures go through Application
        Room.Dispose();
        Application.Dispose();
        _libraries.Dispose();
        Sync.Dispose();
        _presence?.Dispose();
    }
}
