using Waddamburo.Game;
using System.Collections.Immutable;
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
using Waddamburo.Formats.Layout;
using Waddamburo.Game.Don;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.Scores;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Platform.Sdl.Rendering;

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
    bool Fullscreen = false);

/// <summary>
/// The running game: builds the services, owns the window loop, the active scene and what is drawn
/// over it (intermission, system indicators), the cabinet's coins and the credit's players, and hands
/// each tick to the active scene's <see cref="FlowScene"/>.
/// </summary>
internal sealed class GameShell : IDisposable
{
    public GameOptions Options { get; }
    public ArcadeSettings Arcade { get; private set; }

    public SdlApplication Application { get; }
    public SdlDonRenderer? DonRenderer { get; }
    public DonPresentationController? Don { get; }
    public AudioEngine? Audio { get; }
    public GameSounds? Sounds { get; }
    public SongPreviewController? Previews { get; }
    public SongTitleTextureCache Titles { get; }
    public CoinBank? Coins { get; }

    /// <summary>Per lane, the last saved play's previous best on its chart (for results).</summary>
    public long?[] PreviousBests { get; } = new long?[2];

    /// <summary>Chart key -> hash, stored with the scores (matches server bests to the library).</summary>
    public ChartHashes ChartHashes { get; }

    /// <summary>The score database and the server side (uploads, bests, rankings, cabinet pairing).</summary>
    public ScoreSync Sync { get; }

    /// <summary>The accounts stored on this PC (home mode; empty in arcade).</summary>
    public AccountBook? Accounts => Arcade.Home ? Options.Accounts : null;

    /// <summary>The stored account that joins the first drum by itself (home).</summary>
    public ScoreAccount? DefaultAccount => Accounts?.Default;

    // Home: the "who's playing?" screen before the entry (stored accounts, guests, friends, in-game login).
    private readonly PlayerSetupController? _playerSetup;

    private readonly PairingPill _pill;
    private readonly PerformanceOverlay _performance;

    public CatalogAssetRouter Assets { get; }
    public SongSelectCatalogView SongCatalog { get; }
    public TaikoGameplayPresentation Gameplay { get; }
    public GameplaySkinResolver Skins { get; }

    public DirectoryLumenMovieContentSource MovieContent { get; }

    // Prefetched movies' textures, uploaded a few per frame before their scene loads.
    private readonly Dictionary<LumenMovieContent, RenderTextureId[]> _uploadedAhead = new(ReferenceEqualityComparer.Instance);
    private static readonly TimeSpan UploadAheadBudget = TimeSpan.FromMilliseconds(4);

    /// <summary>Starts decoding a scene's movies in the background; their textures go up between frames.</summary>
    public void Prefetch(SceneDefinition scene)
    {
        releaseUploadedAhead();
        MovieContent.Prefetch(scene.Layers.Select(static layer => (layer.ArchiveId, layer.MovieId)));
    }

    // ponytail: one movie per step once over budget; a single huge movie can still take a frame.
    private void uploadAhead()
    {
        var start = Stopwatch.GetTimestamp();
        foreach (var content in MovieContent.DecodedPrefetches)
        {
            if (Stopwatch.GetElapsedTime(start) > UploadAheadBudget)
                return;
            if (!_uploadedAhead.ContainsKey(content))
                _uploadedAhead[content] = SceneTextures.Upload(Application, content);
        }
    }

    private void releaseUploadedAhead()
    {
        foreach (var textures in _uploadedAhead.Values)
            SceneTextures.Release(Application, textures);
        _uploadedAhead.Clear();
    }
    public EnsoLayout EnsoLayout { get; }
    public PlayRequestState PlayRequests { get; } = new();
    public SceneCatalog Catalog { get; }
    public LayerHostFactory Hosts { get; }
    public GameFlowCoordinator Coordinator { get; }
    public IntermissionOverlay Overlay { get; }

    /// <summary>Headless (--screenshot): time follows ticks, not the clock.</summary>
    public bool Headless => Options.ScreenshotPath is not null;

    public int Tick { get; private set; }
    public LumenGameSceneInstance Active { get; private set; } = null!;

    /// <summary>The song being played and its stage (1-8) in the credit.</summary>
    public TaikoSongInfo SongInfo { get; set; } = new(0, 1);

    /// <summary>Songs played this credit.</summary>
    public int SongsPlayed { get; set; }

    /// <summary>The credit's joined drums (0 left, 1 right).</summary>
    public SortedSet<int> JoinedSides { get; } = [];

    private readonly SongLibraries _libraries;
    private readonly IAudioOutput? _audioDevice;
    private readonly LumenGameSceneLoader _loader;
    private readonly CostumeIconTextures _costumeIcons;
    private readonly WaiwaiResultTextures _waiwaiResultTextures;
    private readonly TextFieldTextures _textFields;
    private readonly HomePauseOverlay? _homeOverlay;
    private readonly AttractFlow _attract;
    private readonly GameplayFlow _gameplay;
    private readonly Dictionary<SceneId, FlowScene> _scenes;
    private readonly WaiwaiOutcome? _diagnosticWaiwai;
    private RenderTextureId[] _textures = [];
    private RenderTextureId[] _heldTextures = [];
    private RenderFrame? _heldFrame;
    private bool _heldBlack; // the loading frame: an intermission still shown is drawn over it
    private RenderFrame? _lastPresentedFrame;
    private bool _lastPresentedHadIntermission;
    private int _holdUntilTick;
    private int _heldPresentationsRemaining;
    private SystemIndicators? _indicators;
    private RenderTextureId[] _indicatorTextures = [];
    private ResumeCountdown? _resume;
    private RenderTextureId[] _resumeTextures = [];
    private SceneId? _indicatorScene;
    private SceneId? _fadeTarget;
    private int _fadeStartTick;
    private bool _escapeWasDown;
    private readonly HomeMenu _menu;
    private readonly MenuAudio _menuAudio;
    private readonly QuickRestart _restart;
    private bool _wasFocused = true;
    private static readonly HashSet<SceneId> _attractScenes =
        [FlowScenes.Logo, FlowScenes.Title, FlowScenes.Caution, FlowScenes.Movie];
    private float _lastInterpolation;
    private float _pauseInterpolation;
    // Live presses reach only the per-frame callback; ticks see held keys. Latch drum hits there for
    // the attract loop (scripted --press pulses arrive in the tick instead).
    private int? _drumSideLatched;
    private bool _skipLatched;
    private bool _attractLatched;
    private bool _returnToAttract; // the entry gave up (applied on the next tick, outside its callbacks)
    private int _coinLatched;
    private int _queuedCoinSounds;
    private readonly bool _traceInput = Environment.GetEnvironmentVariable("WADDAMBURO_INPUT_TRACE") == "1";
    private readonly int _dumpTreeTick =
        int.TryParse(Environment.GetEnvironmentVariable("WADDAMBURO_DUMP_TREE"), out var dumpAt) ? dumpAt : -1;

    public static int Run(GameOptions options)
    {
        using var shell = new GameShell(options);
        return shell.run();
    }

    private GameShell(GameOptions options)
    {
        Options = options;
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
        Sync = new ScoreSync(Arcade, options.ScoresPath, Accounts);
        // The game's own songs live beside the Lumen data (<data>/lumendata/packed).
        var dataRoot = Path.GetFullPath(Path.Combine(assetRoot, "..", ".."));
        _libraries = SongLibraries.Load(dataRoot, Arcade, options.TjaRoot);
        var snapshot = _libraries.Snapshot;
        Assets = new CatalogAssetRouter(_libraries.Providers);
        ChartHashes = new ChartHashes(Sync.Scores, Assets.LoadChartAsync);
        SongCatalog = new SongSelectCatalogView(snapshot);
        // Server crowns need every chart's hash before song select lists it.
        if (Sync.Online)
            ChartHashes.HashLibraryInBackground(snapshot.Songs.Values);

        Application = new SdlApplication(
            Waddamburo.App.Hosting.SelfUpdate.WindowTitle,
            options.WindowWidth,
            options.WindowHeight,
            debugGpu: false,
            resizable: !Headless,
            highPixelDensity: !Headless,
            fullscreen: options.Fullscreen && !Headless);
        Console.WriteLine($"Renderer: {Application.GpuDriver}");
        DonRenderer = options.DonRoot is null ? null : Application.CreateDonRenderer(options.DonRoot);
        Don = DonRenderer is null ? null : new DonPresentationController(DonRenderer);
        // Diagnostic: WADDAMBURO_DON_COSTUME=head,body,paint (or a single whole-costume id) dresses P1 at start.
        if (Don is not null && Environment.GetEnvironmentVariable("WADDAMBURO_DON_COSTUME") is { } costumeSetting)
        {
            var ids = costumeSetting.Split(',').Select(int.Parse).ToArray();
            Don.SetCostume(0, ids.Length == 1 ? DonCostume.FromWhole(ids[0]) : new DonCostume(null, ids[0], ids[1], ids.ElementAtOrDefault(2)));
        }
        var needsAudio = !Headless || options.JinglePath is not null || options.SoundRoot is not null;
        _audioDevice = needsAudio ? openAudio() : null;
        if (Environment.GetEnvironmentVariable("WADDAMBURO_PROFILE") == "1" && _audioDevice is not null)
            Console.Error.WriteLine($"Profile audio: {_audioDevice.Driver}, "
                + $"{_audioDevice.HardwareFormat.SampleRate} Hz, "
                + $"{_audioDevice.HardwareBufferFrames} hardware frames.");
        Audio = _audioDevice is null ? null : new AudioEngine(_audioDevice);
        _menu = new HomeMenu(() => Arcade, settings =>
        {
            Arcade = settings;
            _menuAudio?.Apply(Arcade);
        }, saveSettings, don => Sounds?.Bank.Play("SE_COM", don ? 0 : 3, AudioBus.DrumHit, trace: false), options.TjaRoot);
        Previews = Audio is null
            ? null
            : new SongPreviewController(Audio, Assets, FindJingle("JINGLE_GENRE.nub"), FindJingle("JINGLE_WAIGENRE.nub"));
        Sounds = options.SoundRoot is null ? null : new GameSounds(Audio!, options.SoundRoot);
        _menuAudio = new MenuAudio(Audio, Sounds, SongCatalog, Assets);
        _menuAudio.Apply(Arcade);
        Titles = new SongTitleTextureCache(Application, options.FontPath, asynchronous: !Headless, english: Arcade.EnglishTitles);
        _pill = new PairingPill(Application, options.FontPath);
        _textFields = new TextFieldTextures(Application, options.FontPath);
        _homeOverlay = Arcade.Home ? new HomePauseOverlay(Application, options.FontPath, assetRoot) : null;
        _performance = new PerformanceOverlay(Application, () => Audio);
        Gameplay = new TaikoGameplayPresentation((lane, action) =>
            Sounds?.Gameplay.PlayDrum(lane, action is TaikoInputAction.LeftDon or TaikoInputAction.RightDon),
            Don, (lane, sound) => Sounds?.Gameplay.Play(lane, sound));
        _costumeIcons = new CostumeIconTextures(Application, dataRoot);
        _waiwaiResultTextures = new WaiwaiResultTextures(Application, dataRoot);
        Skins = new GameplaySkinResolver(Path.GetFullPath(assetRoot),
            Path.Combine(dataRoot, "config", "S11100-1", "musicinfo.xml"));
        EnsoLayout = GameplaySceneComposition.LoadLayout(assetRoot);

        TaikoPlayResult? diagnosticResult = options.StartScene switch
        {
            StartScene.ResultFail => new TaikoPlayResult(TaikoCourse.Normal, 0, 0, 0, 100, 0, 0, 0, false),
            StartScene.ResultClear => new TaikoPlayResult(TaikoCourse.Normal, 500_000, 90, 7, 3, 80, 0, 45, true),
            _ => null,
        };
        // --start-scene=waiwai-result; WADDAMBURO_WAIWAI_RESULT=segments,duet%,rare hits (e.g. 30,70,10).
        _diagnosticWaiwai = options.StartScene == StartScene.WaiwaiResult
            ? Environment.GetEnvironmentVariable("WADDAMBURO_WAIWAI_RESULT")?.Split(',') is [var segments, var duet, .. var rest]
                ? new(int.Parse(segments, CultureInfo.InvariantCulture), int.Parse(duet, CultureInfo.InvariantCulture), [.. string.Concat(rest).Select(static hit => hit == '1')])
                : new(50, 90, [true])
            : null;

        var flow = new GameFlowSession();
        Catalog = new SceneCatalog(
            FlowScenes.Initial([
                GameplaySceneComposition.Create(FlowScenes.Gameplay, Random.Shared, EnsoLayout),
                _diagnosticWaiwai is null ? FlowScenes.Results : FlowScenes.WaiwaiResults,
            ]),
            [
                new SceneTransitionRoute(FlowScenes.Entry, new LumenSceneRequest(1, 0, 0), FlowScenes.SongSelect),
                // The entry gives up (a card declined, or its dialog timed out, with nobody joined and no
                // credits: TerminateEntry -> SetNextScene(0)): back to the attract loop.
                new SceneTransitionRoute(FlowScenes.Entry, new LumenSceneRequest(0, 0, 0), FlowScenes.Logo),
            ]);
        Hosts = new LayerHostFactory(
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
            options.Countdown,
            () => Gameplay.Results is { Count: > 0 } results ? results
                : diagnosticResult is { } diagnostic ? [diagnostic] : [],
            Arcade,
            Coins)
        {
            WaiwaiOutcome = () => Gameplay.WaiwaiOutcome ?? _diagnosticWaiwai,
            Rankings = Sync.Online ? new SongRankings(Sync.RankingClient, ChartHashes) : null,
            PreviousBest = index => (uint)index < (uint)PreviousBests.Length ? PreviousBests[index] : null,
            Crowns = side => Sync.Scores is not { } scores ? null
                : TaikoGuest.Profiles[side] is { } profile ? scores.Crowns(profile.Baid)
                : Arcade.Home ? scores.Crowns(ScoreProfile.LocalGuestBaid) : null,
            CardClaimed = (side, card) =>
            {
                TaikoGuest.Profiles[side] = card;
                Sync.RefreshBests(card);
            },
            // A card given to the drum, or the account chosen for it in the home player setup.
            PlayerLook = side => TaikoGuest.Profiles[side]?.Look,
            // Home boards show the account's public name (or Guest), drawn by the entry overlay.
            PlayerName = side => Arcade.Home ? TaikoGuest.Profiles[side]?.DisplayName ?? "Guest" : TaikoGuest.Profiles[side]?.Name,
        };
        if (Arcade.Home && Options.Accounts is { } setupBook)
            _playerSetup = new PlayerSetupController(this, setupBook, options.FontPath, options.ScoresPath);
        MovieContent = new DirectoryLumenMovieContentSource(Path.GetFullPath(assetRoot));
        _loader = new LumenGameSceneLoader(MovieContent, Hosts);
        Coordinator = new GameFlowCoordinator(Catalog, _loader, flow);
        Overlay = new IntermissionOverlay(Application, _loader);

        _attract = new AttractFlow(this, AttractMovie.Discover(Path.Combine(dataRoot, "movie")));
        _gameplay = new GameplayFlow(this);
        _restart = new QuickRestart(_gameplay.Restart);
        var entry = new EntryFlow(this);
        var ending = new CreditEndFlow(this);
        _scenes = new()
        {
            [FlowScenes.Boot] = _attract,
            [FlowScenes.Logo] = _attract,
            [FlowScenes.Title] = _attract,
            [FlowScenes.Caution] = _attract,
            [FlowScenes.Movie] = _attract,
            [FlowScenes.Entry] = entry,
            [FlowScenes.SongSelect] = new SongSelectFlow(this, _gameplay),
            [FlowScenes.Gameplay] = _gameplay,
            [FlowScenes.Result] = ending,
            [FlowScenes.Retry] = ending,
            [FlowScenes.GameOver] = ending,
        };
    }

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
            StartScene.SongSelect => FlowScenes.SongSelect,
            StartScene.ResultFail or StartScene.ResultClear or StartScene.WaiwaiResult => FlowScenes.Result,
            StartScene.Retry => FlowScenes.Retry,
            StartScene.GameOver => FlowScenes.GameOver,
            _ => throw new ArgumentOutOfRangeException(nameof(Options.StartScene)),
        };
        Coordinator.StartAsync(initialScene).AsTask().GetAwaiter().GetResult();
        try
        {
            activate();
            Console.WriteLine($"Showing {Active.Id} at launch.");
            _indicators = new SystemIndicators((LumenGameSceneInstance)_loader
                .LoadAsync(SystemIndicators.Definition(new SceneId("system-indicators")), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult()) { Coins = Coins };
            if (Arcade.Home)
            {
                _resume = new ResumeCountdown((LumenGameSceneInstance)_loader
                    .LoadAsync(ResumeCountdown.Definition(new SceneId("resume-countdown")), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult());
                _resumeTextures = SceneTextures.Upload(Application, _resume.Scene);
            }
            Hosts.LayerLoading = host =>
            {
                if (Don is null) return;
                if (host is "result" or "retry" or GameplaySceneComposition.StaticHostId)
                    Don.MapPlayerZero = Hosts.PlayerSide == 1;
                else if (host is "player-entry" or "song-select" or "gameover")
                    Don.MapPlayerZero = false;
            };
            Hosts.CardDialog = open => _indicators.CardDialog(open);
            Hosts.ReturnToAttract = () => _returnToAttract = true;
            _indicators.NetworkIcon = Sync.Health is { } health ? () => health.IconType : null;
            Hosts.EntryJoined = side =>
            {
                JoinPlayer(side);
                if (Coins is not null)
                    _indicators.EntryJoined();
            };
            _indicatorTextures = SceneTextures.Upload(Application, _indicators.Scene);

            var result = Application.Run(
                createFrame,
                tick,
                Options.FrameLimit,
                Options.TickLimit,
                Options.ScreenshotPath is null ? null : capture => ScreenshotWriter.Write(Options.ScreenshotPath, capture),
                updateFrame: keyboard =>
                {
                    _drumSideLatched ??= drumSide(keyboard.Presses);
                    _skipLatched |= keyboard.Presses.Any(static press => press.Key == SdlKeyboardKey.Space);
                    _attractLatched |= keyboard.Presses.Any(static press => press.Key == SdlKeyboardKey.F1);
                    _pressLatch.UnionWith(keyboard.Presses.Select(static press => press.Key));
                    _coinLatched += keyboard.Presses.Count(static press => press.Key == SdlKeyboardKey.F2);
                    flowOf(Active.Id).UpdateFrame(keyboard);
                },
                profileFrame: () => Active.Id == FlowScenes.Gameplay && !Overlay.IsShown,
                togglePerformanceOverlay: _performance.Toggle,
                pointerMoved: _performance.SetPointer,
                performanceVisible: () => _performance.Visible,
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
            _attract.Dispose();
            Overlay.Dispose();
            SceneTextures.Release(Application, _textures);
            SceneTextures.Release(Application, _heldTextures);
            SceneTextures.Release(Application, _indicatorTextures);
            SceneTextures.Release(Application, _resumeTextures);
            _resume?.Scene.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _indicators?.Scene.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Coordinator.StopAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private readonly HashSet<SdlKeyboardKey> _pressLatch = [];
    private ImmutableHashSet<SdlKeyboardKey> _heldLastTick = [];

    /// <summary>
    /// Movies see a key for one tick per press, as a drum hit is a pulse: a held key would read as a
    /// new hit in every panel that starts polling while it is still down (the entry's card dialog
    /// closing under a decide hit joined P1 as well). Taps between ticks count too.
    /// </summary>
    private SdlKeyboardSnapshot drumPulses(SdlKeyboardSnapshot keys)
    {
        var held = keys.PressedKeys.ToImmutableHashSet();
        var pulses = held.Except(_heldLastTick).Union(_pressLatch).Union(keys.Presses.Select(static press => press.Key));
        _heldLastTick = held;
        _pressLatch.Clear();
        return new SdlKeyboardSnapshot(pulses, keys.Presses, keys.Timestamp);
    }

    private static int? drumSide(IEnumerable<SdlKeyPress> presses) => presses.Select(static press => press.Key switch
    {
        SdlKeyboardKey.D or SdlKeyboardKey.F or SdlKeyboardKey.J or SdlKeyboardKey.K => 0,
        SdlKeyboardKey.Z or SdlKeyboardKey.X or SdlKeyboardKey.C or SdlKeyboardKey.V => 1,
        _ => (int?)null,
    }).FirstOrDefault(static side => side is not null);

    // One 60 Hz tick: the input first (back to the attract loop, coins, the home controls, the player
    // setup, the quick restart), then the active scene, the overlays, and a pending scene switch.
    private void tick(SdlKeyboardSnapshot keyboard)
    {
        Tick++;
        var keys = Options.InputTimeline.Apply(Tick, keyboard);
        var hitSide = _drumSideLatched ?? drumSide(keys.Presses);
        _drumSideLatched = null;
        var skip = _skipLatched || keys.Presses.Any(static press => press.Key == SdlKeyboardKey.Space);
        _skipLatched = false;
        if (backToAttract(keys))
            return;
        coins(keys);
        // Diagnostic: WADDAMBURO_INPUT_TRACE=1 prints presses in --press format (KEY@tick).
        if (_traceInput)
            foreach (var press in keys.Presses)
                Console.WriteLine($"[input] {press.Key}@{Tick}");
        var scene = flowOf(Active.Id);
        var held = keys;
        keys = scene.MapKeys(drumPulses(keys));
        var escapeIsDown = keys.IsDown(SdlKeyboardKey.Escape);
        var escape = escapeIsDown && !_escapeWasDown;
        _escapeWasDown = escapeIsDown;
        followFocus();
        if (resumeCountdown(escape) || openMenu(escape) || menuInput(keys, held, escape))
            return;
        // Home: the player setup runs inside the entry and takes the drums; the movie only animates.
        if (_playerSetup is not null && _playerSetup.Tick(ref keys))
            return;
        if (_restart.Update(Arcade.Home && (Active.Id == FlowScenes.Result
                || Active.Id == FlowScenes.Gameplay && _gameplay.CanQuickRestart), held))
            return;
        advance(scene, keys);
        // Results wait while a quick restart is being held or asked for from the menu.
        var restarting = Arcade.Home && Active.Id == FlowScenes.Result && held.IsDown(SdlKeyboardKey.Q);
        if (Coordinator.Flow.State != GameFlowState.TransitionPending)
        {
            if (!restarting && !(Arcade.Home && Active.Id == FlowScenes.Result && _restart.MenuPending))
                scene.Tick(new FlowInput(keys, hitSide, skip, escape));
            return;
        }
        if (restarting || Arcade.Home && Active.Id == FlowScenes.Result && _restart.Holding)
            return;
        // A movie asked for the next scene (entry -> song select); its last voice finishes first.
        if (Sounds?.Bank.IsVoicePlaying == true)
            return;
        ReportDiagnostics();
        switchScene(() => Coordinator.ApplyPendingTransitionAsync().AsTask().GetAwaiter().GetResult());
        Console.WriteLine($"Activated scene '{Active.Id}' at tick {Tick}.");
    }

    // F1 (testing convenience, not cabinet behaviour) or an entry that gave up: drop the credit and
    // return to the attract loop from any menu scene (not mid-song, whose music the gameplay flow owns).
    private bool backToAttract(SdlKeyboardSnapshot keys)
    {
        var toAttract = _attractLatched || _returnToAttract
            || keys.Presses.Any(static press => press.Key == SdlKeyboardKey.F1);
        _attractLatched = _returnToAttract = false;
        if (!toAttract || Active.Id == FlowScenes.Gameplay || Active.Id == FlowScenes.Boot)
            return false;
        Sounds?.StopAll();
        Overlay.Clear();
        PlayRequests.CancelPending();
        Don?.SetDialogDon(false);
        ResetPlayers();
        SongsPlayed = 0; // a new credit starts at the first song
        Show(FlowScenes.Logo);
        return true;
    }

    // Going to the background: the sound fades out (when set); a home song pauses (openMenu).
    private void followFocus()
    {
        if (Application.Focused == _wasFocused)
            return;
        _wasFocused = Application.Focused;
        if (Arcade.MuteInBackground || _wasFocused)
            Audio?.Mixer.FadeOutput(_wasFocused ? 1 : 0, TimeSpan.FromMilliseconds(300));
    }

    // Resume's countdown: the song stays held until it ends; Escape or leaving the window pauses again.
    private bool resumeCountdown(bool escape)
    {
        if (_resume is not { Running: true } countdown)
            return false;
        if (escape || !_wasFocused)
        {
            countdown.Cancel();
            _menu.Open(gameplay: true);
        }
        else if (countdown.Advance((bank, cue) => Sounds?.Bank.Play(bank, cue, trace: false)))
            resumeHome();
        return true;
    }

    // Home: Escape opens the menu (pause in gameplay; settings and back to the title in the menus). A song
    // still unpausable when the window lost focus (under the rainbow) pauses once it can; once every note
    // and long note is over (the song's tail), leaving the window no longer pauses.
    private bool openMenu(bool escape)
    {
        if (!Arcade.Home || _menu.IsOpen
            || !(escape || !_wasFocused && Active.Id == FlowScenes.Gameplay && !_gameplay.ChartOver))
            return false;
        if (Active.Id == FlowScenes.Gameplay)
        {
            if (_restart.Black != 0 || !_gameplay.CanPause)
                return false;
            _restart.CancelHold();
            _pauseInterpolation = _lastInterpolation;
            _gameplay.SetPaused(true, _pauseInterpolation);
            _menu.Open(gameplay: true);
            return true;
        }
        // The attract, the entry (and its player setup) and Song Select: settings, back to the title.
        if (!_attractScenes.Contains(Active.Id) && Active.Id != FlowScenes.Entry && Active.Id != FlowScenes.SongSelect)
            return false;
        if (Overlay.IsShown || Coordinator.Flow.State == GameFlowState.TransitionPending)
            return true;
        _pauseInterpolation = _lastInterpolation;
        _menu.Open(gameplay: false, attract: _attractScenes.Contains(Active.Id));
        _menuAudio.HoldMenuMusic(true);
        return true;
    }

    // The open menu takes the input and carries out its choice.
    private bool menuInput(SdlKeyboardSnapshot keys, SdlKeyboardSnapshot held, bool escape)
    {
        if (!_menu.IsOpen)
            return false;
        // A folder picked in the system dialog opened from the settings.
        if (_pickingFolder is { } picking && SdlApplication.TryTakePickedFolder(out var folder))
        {
            Arcade = picking == HomeMenuAction.PickTjaFolder ? Arcade with { TjaFolder = folder }
                : Arcade with { NijiiroFolder = folder };
            saveSettings();
            _pickingFolder = null;
        }
        var action = _menu.Input(keys, escape, held);
        _menuAudio.Sample(_menu.PreviewBus, Tick);
        if (!_menu.IsOpen)
            _menuAudio.HoldMenuMusic(false);
        switch (action)
        {
            case HomeMenuAction.Resume when Active.Id == FlowScenes.Gameplay && _resume is { } resume:
                resume.Start();
                break;
            case HomeMenuAction.Resume:
                resumeHome();
                break;
            case HomeMenuAction.Restart:
                resumeHome();
                _restart.FromMenu();
                break;
            case HomeMenuAction.SongSelect:
                resumeHome();
                _gameplay.Abandon();
                break;
            case HomeMenuAction.PickTjaFolder or HomeMenuAction.PickNijiiroFolder:
                _pickingFolder = action;
                Application.PickFolder(action == HomeMenuAction.PickTjaFolder ? Arcade.TjaFolder ?? Options.TjaRoot
                    : Arcade.NijiiroFolder);
                break;
            case HomeMenuAction.Title:
                _playerSetup?.Close();
                _returnToAttract = true;
                break;
        }
        return true;
    }

    // The scene's movies and the overlays drawn with it advance one tick.
    private void advance(FlowScene scene, SdlKeyboardSnapshot keys)
    {
        scene.Advance(LumenInputAdapter.CreateSnapshot(keys,
            Active.Id == FlowScenes.Gameplay ? LumenInputMode.PresentationOnly : LumenInputMode.AuthoredControls));
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

    private void resumeHome()
    {
        if (Active.Id == FlowScenes.Gameplay)
            _gameplay.SetPaused(false);
    }

    private HomeMenuAction? _pickingFolder;

    private void saveSettings()
    {
        if (Options.ArcadePath is not { } path) return;
        try
        {
            ArcadeSettings.SaveMenuSettings(path, Arcade);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error SETTINGS_SAVE: {exception.Message}");
        }
    }

    private RenderFrame withHomeOverlay(RenderFrame frame) => _homeOverlay is null
        ? frame : new RenderFrame(frame.ClearColor,
            frame.Quads.Concat(_resume is { Running: true } countdown
                    ? SceneTextures.Compose(countdown.CreateSnapshot(1), _resumeTextures, "Resume", Titles.Resolve).Quads
                    : [])
                .Concat(_homeOverlay.Quads(_menu.IsOpen ? _menu : null, _restart.Black)),
            frame.ContentAspectRatio);

    // F2 = coin, in any scene (the cabinet handles coins apart from the game). The credit counts at
    // once; each coin's sound queues and plays in full, one after another.
    private void coins(SdlKeyboardSnapshot keys)
    {
        var inserted = _coinLatched + keys.Presses.Count(static press => press.Key == SdlKeyboardKey.F2);
        _coinLatched = 0;
        if (Coins is not null && inserted > 0)
        {
            for (var coin = 0; coin < inserted; coin++)
                Coins.InsertCoin();
            _queuedCoinSounds += inserted;
            Console.WriteLine($"Coin credited at tick {Tick}: {Coins.Credits} credit(s).");
            _indicators?.CoinsChanged();
            if (Active.Id == FlowScenes.Entry)
                Hosts.EntryCoinsChanged();
        }
        if (_queuedCoinSounds > 0 && Sounds?.Bank.IsCoinPlaying != true)
        {
            _queuedCoinSounds--;
            Sounds?.Bank.PlayCoin();
        }
    }

    /// <summary>Leaves the active scene and shows <paramref name="scene"/> (loaded fresh from the catalog).</summary>
    public void Show(SceneId scene)
    {
        switchScene(() => Coordinator.TransitionToAsync(scene).AsTask().GetAwaiter().GetResult());
        Console.WriteLine($"Showing {scene} at tick {Tick}.");
    }

    private void switchScene(Action transition)
    {
        if (_heldFrame is null && _lastPresentedFrame is not null)
        {
            // The old movie's textures must survive while its final frame is being presented.
            // A cleared intermission may already have released its textures, so hold black with
            // the network icon in that case, as the original game did while loading.
            _heldBlack = Overlay.IsShown || _lastPresentedHadIntermission || Active.Id == FlowScenes.Movie;
            if (_heldBlack)
                _heldFrame = blackLoadingFrame();
            else
            {
                _heldFrame = _lastPresentedFrame;
                _heldTextures = _textures;
            }
        }
        _holdUntilTick = Tick + 2;
        _heldPresentationsRemaining = 1;
        flowOf(Active.Id).Exit(Active.Id);
        if (!ReferenceEquals(_heldTextures, _textures))
            SceneTextures.Release(Application, _textures);
        _textures = [];
        transition();
        activate();
        Application.DiscardElapsed();
    }

    private void activate()
    {
        Active = Coordinator.ActiveScene as LumenGameSceneInstance
            ?? throw new InvalidOperationException("The active scene is not a Lumen scene instance.");
        var ahead = _uploadedAhead.Count;
        _textures = SceneTextures.Upload(Application, Active,
            content => _uploadedAhead.Remove(content, out var textures) ? textures : null);
        var taken = ahead - _uploadedAhead.Count;
        if (taken > 0)
            releaseUploadedAhead(); // this scene took its prefetch; the rest is unused
        flowOf(Active.Id).Enter(Active.Id);
    }

    /// <summary>Fades to black (the intermission fade's "in", 1 s), then shows <paramref name="scene"/>.</summary>
    public void FadeTo(SceneId scene)
    {
        if (_fadeTarget is not null) return;
        // Results -> revival opens on the revival's own shutter.
        if (scene == FlowScenes.Retry)
        {
            Show(scene);
            return;
        }
        Overlay.Show(FlowScenes.Fade).GotoLabel("in", play: true);
        _fadeTarget = scene;
        _fadeStartTick = Tick;
    }

    /// <summary>True while a fade is under way; shows its scene once the screen is black.</summary>
    public bool FinishFade()
    {
        if (_fadeTarget is not { } target) return false;
        if (Tick - _fadeStartTick < 60) return true;
        _fadeTarget = null;
        Show(target);
        Overlay.Clear();
        return true;
    }

    public bool FadePending => _fadeTarget is not null;

    public void CancelFade()
    {
        _fadeTarget = null;
        Overlay.Clear();
    }

    // The joined drums (0 left, 1 right): panels, Song Select's name boards and the gameplay Don slots
    // follow them, and the later scenes' hosts read them. A credit starts empty.
    public void JoinPlayer(int side)
    {
        JoinedSides.Add(side);
        applyPlayers();
    }

    public void ResetPlayers()
    {
        // A credit starts with guests (default Dons); cards and the home account attach as players join.
        Array.Clear(TaikoGuest.Profiles);
        for (var slot = 0; slot < 3; slot++)
            Don?.SetLook(slot, null);
        JoinedSides.Clear();
        applyPlayers();
    }

    private void applyPlayers()
    {
        var two = JoinedSides.Count == 2;
        var first = JoinedSides.Count == 0 ? 0 : JoinedSides.Min;
        Hosts.PlayerSide = first;
        Hosts.TwoPlayers = two;
        // Two players start in Waiwai's song select (traced); one player only has the normal one.
        Hosts.Waiwai = two;
        if (_indicators is not null)
        {
            _indicators.Side = first;
            _indicators.TwoPlayers = two;
        }
        if (Don is not null) Don.GameplaySide = first;
        Catalog.Replace(FlowScenes.SongSelectScene(JoinedSides.Count == 0 ? [0] : [.. JoinedSides], Hosts.Waiwai));
    }

    /// <summary>The system indicators (panels, coins, network), once loaded.</summary>
    public SystemIndicators? Indicators => _indicators;

    /// <summary>The indicators restart with the next scene (it reloads under the same id).</summary>
    public void ResetIndicatorScene() => _indicatorScene = null;

    /// <summary>The active scene's uploaded textures, by the scene's texture index.</summary>
    public RenderTextureId[] SceneTextureIds => _textures;

    /// <summary>Back to the attract loop on the next tick (outside the callbacks asking for it).</summary>
    public void ReturnToAttract() => _returnToAttract = true;

    public void ReportDiagnostics()
    {
        foreach (var layer in Active.Layers.Select((loaded, index) => (loaded, index)))
            foreach (var diagnostic in Active.Player.Layers[layer.index].Player.Diagnostics)
                Console.WriteLine(
                    $"{layer.loaded.Definition.MovieId}: {diagnostic.Severity} {diagnostic.Code} "
                    + $"at character {diagnostic.CharacterId} frame {diagnostic.Frame}: {diagnostic.Message}");
    }

    public RenderTextureId? ResolveSurface(LumenNativeSurfaceKey surface) =>
        _attract.Movie?.Resolve(surface) ?? Don?.Resolve(surface)
        ?? _costumeIcons.Resolve(surface) ?? _waiwaiResultTextures.Resolve(surface) ?? _textFields.Resolve(surface)
        ?? Titles.Resolve(surface);

    private RenderFrame createFrame(double interpolationFraction)
    {
        // An open menu freezes the scene between two ticks: hold the blend where it stopped.
        if (_menu.IsOpen || _resume?.Running == true) interpolationFraction = _pauseInterpolation;
        else _lastInterpolation = (float)interpolationFraction;
        if (_heldFrame is { } held)
        {
            var needsFirstPresentation = _heldPresentationsRemaining > 0;
            _heldPresentationsRemaining = 0;
            if (needsFirstPresentation || Tick < _holdUntilTick)
                // The rainbow stays up across its scene switch (only a fade is cleared with it).
                return _heldBlack && Overlay.IsShown
                    ? withHomeOverlay(new RenderFrame(held.ClearColor,
                        [.. Overlay.Quads((float)interpolationFraction, Titles.Resolve), .. held.Quads], held.ContentAspectRatio)
                    ) : withHomeOverlay(held);
            SceneTextures.Release(Application, _heldTextures);
            _heldTextures = [];
            _heldFrame = null;
        }
        var interpolation = (float)interpolationFraction;
        Titles.UploadCompleted();
        uploadAhead();
        if (DonRenderer is not null)
            DonRenderer.Interpolation = interpolation;
        var frame = SceneTextures.Compose(flowOf(Active.Id).CreateSnapshot(interpolation), _textures, "Scene", ResolveSurface);
        IEnumerable<RenderQuad> indicatorQuads(bool overIntermission) => _indicators is null ? []
            : SceneTextures.Compose(_indicators.CreateSnapshot(overIntermission, interpolation), _indicatorTextures,
                "Indicator", Titles.Resolve).Quads;
        // Depth order (traced): scene, msg_coins (-950), intermission (-2000), network/card (-3000).
        var result = new RenderFrame(
            frame.ClearColor,
            frame.Quads.Concat(_playerSetup?.Quads(interpolation) ?? []).Concat(indicatorQuads(false)).Concat(Overlay.Quads(interpolation, Titles.Resolve))
                .Concat(indicatorQuads(true))
                .Concat(pill())
                .Concat(_performance.Quads()).ToArray(),
            frame.ContentAspectRatio);
        _lastPresentedFrame = result;
        _lastPresentedHadIntermission = Overlay.IsShown;
        return withHomeOverlay(result);
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
        // The game takes cards in the attract loop and in entry (not from song select on), one at a time.
        var entry = flowOf(Active.Id) is EntryFlow;
        pairing.Accepting = Active.Id != FlowScenes.Boot && flowOf(Active.Id) is AttractFlow
            || entry && !Hosts.EntryCardPending;
        if (entry && pairing.TakeCardFailure())
            Hosts.RejectEntryCard();
        if (entry && pairing.TakeCard() is { } card && !Hosts.InsertEntryCard(card))
            Console.WriteLine("Pairing: the entry is busy with another card; this one was dropped.");
        pairing.UpdatePill(_pill);
        return _pill.Quads();
    }

    public void Dispose()
    {
        _playerSetup?.Dispose();
        _textFields.Dispose();
        Hosts?.Rankings?.Dispose();
        _homeOverlay?.Dispose();
        _pill.Dispose();
        _performance.Dispose();
        Titles.Dispose();
        Previews?.Dispose();
        Audio?.Dispose();
        _audioDevice?.Dispose();
        DonRenderer?.Dispose();
        Application.Dispose();
        _libraries.Dispose();
        Sync.Dispose();
    }
}
