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
using Waddamburo.Providers.Stock;
using Waddamburo.Providers.Tja;

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
    ScoreAccount? Account = null,
    string? ScoresPath = null,
    bool Autoplay = false);

/// <summary>
/// The running game: builds the services, owns the window loop, the active scene and what is drawn
/// over it (intermission, system indicators), the cabinet's coins and the credit's players, and hands
/// each tick to the active scene's <see cref="FlowScene"/>.
/// </summary>
internal sealed class GameShell : IDisposable
{
    public GameOptions Options { get; }
    public ArcadeSettings Arcade { get; }

    public SdlApplication Application { get; }
    public SdlDonRenderer? DonRenderer { get; }
    public DonPresentationController? Don { get; }
    public AudioEngine? Audio { get; }
    public GameSounds? Sounds { get; }
    public SongPreviewController? Previews { get; }
    public SongTitleTextureCache Titles { get; }
    public CoinBank? Coins { get; }

    /// <summary>The local score database, open when a profile plays (null: guests, nothing saved).</summary>
    public ScoreStore? Scores { get; }

    /// <summary>The server scores upload to (null: offline or a guest).</summary>
    public ScoreClient? ScoreServer { get; }

    private readonly ServerHealth? _health;

    // Cabinet mode only (cabinet_token set, no home account).
    private readonly CabinetPairing? _pairing;
    private readonly PairingPill _pill;

    public CatalogAssetRouter Assets { get; }
    public SongSelectCatalogView SongCatalog { get; }
    public TaikoGameplayPresentation Gameplay { get; }
    public GameplaySkinResolver Skins { get; }
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

    private readonly GlobalSongCatalog _globalCatalog;
    private readonly SdlAudioDevice? _audioDevice;
    private readonly LumenGameSceneLoader _loader;
    private readonly CostumeIconTextures _costumeIcons;
    private readonly WaiwaiResultTextures _waiwaiResultTextures;
    private readonly AttractFlow _attract;
    private readonly GameplayFlow _gameplay;
    private readonly Dictionary<SceneId, FlowScene> _scenes;
    private readonly WaiwaiOutcome? _diagnosticWaiwai;
    private RenderTextureId[] _textures = [];
    private SystemIndicators? _indicators;
    private RenderTextureId[] _indicatorTextures = [];
    private SceneId? _indicatorScene;
    private SceneId? _fadeTarget;
    private int _fadeStartTick;
    private bool _escapeWasDown;
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
        var assetRoot = options.AssetRoot;
        // The cabinet's credit counter: lives until the process exits (coin mode only).
        Coins = Arcade.FreePlay ? null : new CoinBank(Arcade);
        var cabinet = options.Account is null && Arcade is { Server: not null, CabinetToken: not null };
        _health = Arcade.Server is { } healthServer ? new ServerHealth(ScoreClient.CreateHttp(healthServer, Arcade.ServerInsecure)) : null;
        Scores = (options.Account is not null || cabinet) && options.ScoresPath is { } scoresPath ? new ScoreStore(scoresPath) : null;
        if (Scores is not null && Arcade.Server is { } server)
        {
            var http = ScoreClient.CreateHttp(server, Arcade.ServerInsecure, options.Account?.Token ?? Arcade.CabinetToken);
            ScoreServer = new ScoreClient(http);
            // Plays left over from offline runs: the account's, or (a cabinet) everyone's.
            ScoreServer.SyncInBackground(Scores, options.Account?.Baid);
            if (cabinet)
                _pairing = new CabinetPairing(http);
        }
        // The game's own songs live beside the Lumen data (<data>/lumendata/packed).
        var dataRoot = Path.GetFullPath(Path.Combine(assetRoot, "..", ".."));
        // Either source may be absent: a stock install without custom songs, or custom songs alone.
        ISongCatalogProvider[] providers = [
            .. StockCatalogProvider.IsStockData(dataRoot) ? [new StockCatalogProvider(dataRoot)] : Array.Empty<ISongCatalogProvider>(),
            // ponytail: custom TJA is off while the engine matches the game 1:1 (Waiwai has its own
            // charts TJA cannot supply); set WADDAMBURO_CUSTOM_TJA=1 to list them anyway.
            .. Environment.GetEnvironmentVariable("WADDAMBURO_CUSTOM_TJA") == "1" && Directory.Exists(options.TjaRoot)
                ? [new TjaCatalogProvider(options.TjaRoot)] : Array.Empty<ISongCatalogProvider>(),
        ];
        Assets = new CatalogAssetRouter(providers);
        _globalCatalog = new GlobalSongCatalog(providers);
        var snapshot = _globalCatalog.RefreshAsync().AsTask().GetAwaiter().GetResult();
        foreach (var status in snapshot.Providers)
            Console.WriteLine(status.Succeeded
                ? $"Catalog provider {status.Provider}: {status.SongCount} songs in {status.CategoryCount} categories."
                : $"Catalog provider {status.Provider} failed.");
        SongCatalog = new SongSelectCatalogView(snapshot);
        if (SongCatalog.Categories.IsEmpty)
            throw new InvalidOperationException(snapshot.Diagnostics.FirstOrDefault(static diagnostic => diagnostic.Severity == CatalogDiagnosticSeverity.Error)?.Message
                ?? "No song provider discovered any browsable categories.");
        Console.WriteLine($"Catalog revision {snapshot.Revision}: {SongCatalog.Categories.Length} categories.");
        foreach (var diagnostic in snapshot.Diagnostics)
            Console.Error.WriteLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");

        Application = new SdlApplication(
            "Waddamburo",
            options.WindowWidth,
            options.WindowHeight,
            debugGpu: false,
            resizable: !Headless,
            highPixelDensity: !Headless);
        Console.WriteLine($"SDL_GPU driver: {Application.GpuDriver}");
        DonRenderer = options.DonRoot is null ? null : Application.CreateDonRenderer(options.DonRoot);
        Don = DonRenderer is null ? null : new DonPresentationController(DonRenderer);
        // Diagnostic: WADDAMBURO_DON_COSTUME=head,body,paint (or a single whole-costume id) dresses P1 at start.
        if (Don is not null && Environment.GetEnvironmentVariable("WADDAMBURO_DON_COSTUME") is { } costumeSetting)
        {
            var ids = costumeSetting.Split(',').Select(int.Parse).ToArray();
            Don.SetCostume(0, ids.Length == 1 ? DonCostume.FromWhole(ids[0]) : new DonCostume(null, ids[0], ids[1], ids.ElementAtOrDefault(2)));
        }
        var needsAudio = !Headless || options.JinglePath is not null || options.SoundRoot is not null;
        _audioDevice = needsAudio ? new SdlAudioDevice() : null;
        if (Environment.GetEnvironmentVariable("WADDAMBURO_PROFILE") == "1" && _audioDevice is not null)
            Console.Error.WriteLine($"Profile audio: {_audioDevice.Driver}, "
                + $"{_audioDevice.HardwareFormat.SampleRate} Hz, "
                + $"{_audioDevice.HardwareBufferFrames} hardware frames.");
        Audio = _audioDevice is null ? null : new AudioEngine(_audioDevice);
        Previews = Audio is null
            ? null
            : new SongPreviewController(Audio, Assets, FindJingle("JINGLE_GENRE.nub"), FindJingle("JINGLE_WAIGENRE.nub"));
        Sounds = options.SoundRoot is null ? null : new GameSounds(Audio!, options.SoundRoot);
        Titles = new SongTitleTextureCache(Application, options.FontPath, asynchronous: !Headless);
        _pill = new PairingPill(Application, options.FontPath);
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
            Crowns = side => Scores is not null && TaikoGuest.Profiles[side] is { } profile ? Scores.Crowns(profile.Baid) : null,
            CardClaimed = (side, card) => TaikoGuest.Profiles[side] = card,
            // A card given to the drum, else the home account for the credit's first player.
            PlayerLook = side => TaikoGuest.Profiles[side]?.Look
                ?? (options.Account is { } home && TaikoGuest.Profiles.All(static profile => profile is null) ? home.Look : null),
        };
        _loader = new LumenGameSceneLoader(new DirectoryLumenMovieContentSource(Path.GetFullPath(assetRoot)), Hosts);
        Coordinator = new GameFlowCoordinator(Catalog, _loader, flow);
        Overlay = new IntermissionOverlay(Application, _loader);

        _attract = new AttractFlow(this, AttractMovie.Discover(Path.Combine(dataRoot, "movie")));
        _gameplay = new GameplayFlow(this);
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
            _indicators.NetworkIcon = _health is null ? null : () => _health.IconType;
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
                profileFrame: () => Active.Id == FlowScenes.Gameplay && !Overlay.IsShown);

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
            SceneTextures.Release(Application, _indicatorTextures);
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

    private void tick(SdlKeyboardSnapshot keyboard)
    {
        Tick++;
        var keys = Options.InputTimeline.Apply(Tick, keyboard);
        var hitSide = _drumSideLatched ?? drumSide(keys.Presses);
        _drumSideLatched = null;
        var skip = _skipLatched || keys.Presses.Any(static press => press.Key == SdlKeyboardKey.Space);
        _skipLatched = false;
        // F1 (testing convenience, not cabinet behaviour) or an entry that gave up: drop the credit and
        // return to the attract loop from any menu scene (not mid-song, whose music the gameplay flow owns).
        var toAttract = _attractLatched || _returnToAttract
            || keys.Presses.Any(static press => press.Key == SdlKeyboardKey.F1);
        _attractLatched = _returnToAttract = false;
        if (toAttract && Active.Id != FlowScenes.Gameplay && Active.Id != FlowScenes.Boot)
        {
            Sounds?.StopAll();
            Overlay.Clear();
            PlayRequests.CancelPending();
            Don?.SetDialogDon(false);
            ResetPlayers();
            Show(FlowScenes.Logo);
            return;
        }
        coins(keys);
        // Diagnostic: WADDAMBURO_INPUT_TRACE=1 prints presses in --press format (KEY@tick).
        if (_traceInput)
            foreach (var press in keys.Presses)
                Console.WriteLine($"[input] {press.Key}@{Tick}");
        var scene = flowOf(Active.Id);
        keys = scene.MapKeys(drumPulses(keys));
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
        if (_indicators is not null)
        {
            if (_indicatorScene != Active.Id)
            {
                _indicatorScene = Active.Id;
                _indicators.SetScene(scene.IndicatorsFor(Active.Id));
            }
            _indicators.Advance();
        }
        var escapeIsDown = keys.IsDown(SdlKeyboardKey.Escape);
        var escape = escapeIsDown && !_escapeWasDown;
        _escapeWasDown = escapeIsDown;
        if (Coordinator.Flow.State != GameFlowState.TransitionPending)
        {
            scene.Tick(new FlowInput(keys, hitSide, skip, escape));
            return;
        }
        // A movie asked for the next scene (entry -> song select); its last voice finishes first.
        if (Sounds?.Bank.IsVoicePlaying == true)
            return;
        ReportDiagnostics();
        switchScene(() => Coordinator.ApplyPendingTransitionAsync().AsTask().GetAwaiter().GetResult());
        Console.WriteLine($"Activated scene '{Active.Id}' at tick {Tick}.");
    }

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
        flowOf(Active.Id).Exit(Active.Id);
        SceneTextures.Release(Application, _textures);
        _textures = [];
        transition();
        activate();
    }

    private void activate()
    {
        Active = Coordinator.ActiveScene as LumenGameSceneInstance
            ?? throw new InvalidOperationException("The active scene is not a Lumen scene instance.");
        _textures = SceneTextures.Upload(Application, Active);
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

    // The joined drums (0 left, 1 right): panels, Song Select's name boards and the gameplay Don slots
    // follow them, and the later scenes' hosts read them. A credit starts empty.
    public void JoinPlayer(int side)
    {
        // Home: the logged-in account is the first player to join (a second one plays as a guest).
        if (Options.Account is { } account && !TaikoGuest.Profiles.Any(static profile => profile is not null))
        {
            TaikoGuest.Profiles[side] = account.Profile;
            Don?.SetLook(side, account.Look);
        }
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

    public void ReportDiagnostics()
    {
        foreach (var layer in Active.Layers.Select((loaded, index) => (loaded, index)))
            foreach (var diagnostic in Active.Player.Layers[layer.index].Player.Diagnostics)
                Console.WriteLine(
                    $"{layer.loaded.Definition.MovieId}: {diagnostic.Severity} {diagnostic.Code} "
                    + $"at character {diagnostic.CharacterId} frame {diagnostic.Frame}: {diagnostic.Message}");
    }

    private RenderTextureId? resolveSurface(LumenNativeSurfaceKey surface) =>
        _attract.Movie?.Resolve(surface) ?? Don?.Resolve(surface)
        ?? _costumeIcons.Resolve(surface) ?? _waiwaiResultTextures.Resolve(surface) ?? Titles.Resolve(surface);

    private RenderFrame createFrame(double interpolationFraction)
    {
        var interpolation = (float)interpolationFraction;
        Titles.UploadCompleted();
        if (DonRenderer is not null)
            DonRenderer.Interpolation = interpolation;
        var frame = SceneTextures.Compose(flowOf(Active.Id).CreateSnapshot(interpolation), _textures, "Scene", resolveSurface);
        IEnumerable<RenderQuad> indicatorQuads(bool overIntermission) => _indicators is null ? []
            : SceneTextures.Compose(_indicators.CreateSnapshot(overIntermission, interpolation), _indicatorTextures,
                "Indicator", Titles.Resolve).Quads;
        // Depth order (traced): scene, msg_coins (-950), intermission (-2000), network/card (-3000).
        return new RenderFrame(
            frame.ClearColor,
            frame.Quads.Concat(indicatorQuads(false)).Concat(Overlay.Quads(interpolation, Titles.Resolve))
                .Concat(indicatorQuads(true)).Concat(pill()).ToArray(),
            frame.ContentAspectRatio);
    }

    /// <summary>A paired card was rejected by the server (shown once).</summary>
    public bool TakeCardFailure() => _pairing?.TakeCardFailure() == true;

    /// <summary>A card was paired during the attract loop: it starts the credit (SCENE_TRIGGER_CARD).</summary>
    public ScoreProfile? TakeCard() => _pairing?.TakeCard();

    private IEnumerable<RenderQuad> pill()
    {
        if (_pairing is null)
            return [];
        // The game takes cards in the attract loop and in entry (not from song select on), one at a time.
        var entry = flowOf(Active.Id) is EntryFlow;
        _pairing.Accepting = Active.Id != FlowScenes.Boot && flowOf(Active.Id) is AttractFlow
            || entry && !Hosts.EntryCardPending;
        if (entry && _pairing.TakeCardFailure())
            Hosts.RejectEntryCard();
        if (entry && _pairing.TakeCard() is { } card && !Hosts.InsertEntryCard(card))
            Console.WriteLine("Pairing: the entry is busy with another card; this one was dropped.");
        _pairing.UpdatePill(_pill);
        return _pill.Quads();
    }

    public void Dispose()
    {
        _pairing?.Dispose();
        _health?.Dispose();
        _pill.Dispose();
        Titles.Dispose();
        Previews?.Dispose();
        Audio?.Dispose();
        _audioDevice?.Dispose();
        DonRenderer?.Dispose();
        Application.Dispose();
        _globalCatalog.Dispose();
        Scores?.Dispose();
    }
}
