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
    bool Fullscreen = false);

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
    public SdlDonRenderer? DonRenderer { get; }
    public DonPresentationController? Don { get; }
    public AudioEngine? Audio { get; }
    public GameSounds? Sounds { get; }
    public SongPreviewController? Previews { get; }
    public SongTitleTextureCache Titles { get; }
    public CoinBank? Coins { get; }

    /// <summary>Per lane, the best before the last saved play on its chart, or that play's score when it is the first (for results).</summary>
    public long?[] Bests { get; } = new long?[2];

    /// <summary>Each lane's place on the chart's online top three after its play (null: not known).</summary>
    public RankingPlacement?[] Placements { get; } = new RankingPlacement?[2];

    /// <summary>Chart key -> hash, stored with the scores (matches server bests to the library).</summary>
    public ChartHashes ChartHashes { get; }

    /// <summary>The score database and the server side (uploads, bests, rankings, cabinet pairing).</summary>
    public ScoreSync Sync { get; }

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

    public CatalogAssetRouter Assets { get; }
    public SongSelectCatalogView SongCatalog { get; }
    public TaikoGameplayPresentation Gameplay { get; }
    public GameplaySkinResolver Skins { get; }

    public DirectoryLumenMovieContentSource MovieContent { get; }

    /// <summary>The active scene's textures and the frame held across a scene switch.</summary>
    private readonly ScenePresenter _presenter;

    /// <summary>Starts decoding a scene's movies in the background; their textures go up between frames.</summary>
    public void Prefetch(SceneDefinition scene) => _presenter.Prefetch(scene);

    /// <summary>The texture upscaler and its cache (null: unavailable on this machine).</summary>
    public UpscaleTool? Upscale { get; private set; }

    /// <summary>A texture bake started from the Settings menu (running or finished).</summary>
    public TextureBake? Bake { get; set; }

    /// <summary>The scene being prefetched, until one takes it.</summary>
    public SceneId? Prefetched => _presenter.Prefetched;

    /// <summary>The prefetched scene is decoded and uploaded (its switch will not stall).</summary>
    public bool PrefetchReady => _presenter.PrefetchReady;

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
    // A new credit (0) offers the tutorial again.
    public int SongsPlayed
    {
        get;
        set
        {
            field = value;
            if (value == 0)
                Hosts.TutorialSeen = false;
        }
    }

    /// <summary>The credit's joined drums (0 left, 1 right).</summary>
    public SortedSet<int> JoinedSides { get; } = [];

    private readonly SongLibraries _libraries;
    private readonly IAudioOutput? _audioDevice;
    private readonly LumenGameSceneLoader _loader;
    private readonly CostumeIconTextures _costumeIcons;
    private readonly WaiwaiResultTextures _waiwaiResultTextures;
    private readonly TextFieldTextures _textFields;
    private readonly NameTextTextures _nameTexts;
    private readonly ToneArt _toneArt;
    private readonly AttractFlow _attract;
    private readonly GameplayFlow _gameplay;
    private readonly HomeControls _home;
    private readonly SongSearch _search;
    private readonly ScoreListOverlay _scores;
    // Overlays that take the keyboard (topmost last) and the painter Waddamburo's own overlays draw with.
    private readonly IInputOverlay[] _inputOverlays;
    public OverlayPainter Painter { get; }

    /// <summary>Song Select's quick reload (library, grouping, order, search).</summary>
    public SongSelectReload Reload { get; }
    private readonly Dictionary<SceneId, FlowScene> _scenes;
    private readonly WaiwaiOutcome? _diagnosticWaiwai;
    private SystemIndicators? _indicators;
    private RenderTextureId[] _indicatorTextures = [];
    private SceneId? _indicatorScene;
    private SceneId? _fadeTarget;
    private int _fadeStartTick;
    private static readonly HashSet<SceneId> _attractScenes =
        [FlowScenes.Logo, FlowScenes.Title, FlowScenes.Caution, FlowScenes.Movie];
    private readonly InputLatches _input = new();
    private bool _returnToAttract; // the entry gave up (applied on the next tick, outside its callbacks)
    private int _queuedCoinSounds;
    private readonly bool _traceInput = Environment.GetEnvironmentVariable("WADDAMBURO_INPUT_TRACE") == "1";
    private readonly int _dumpTreeTick =
        int.TryParse(Environment.GetEnvironmentVariable("WADDAMBURO_DUMP_TREE"), out var dumpAt) ? dumpAt : -1;

    /// <summary>The game closed to start again (the caller relaunches it once everything is released).</summary>
    public static bool RestartRequested { get; private set; }

    /// <summary>Closes the game to start it again (see <see cref="RestartRequested"/>).</summary>
    public void RequestRestart()
    {
        RestartRequested = true;
        Application.RequestQuit();
    }

    /// <summary>Applies the drum pads' keys, buttons and MIDI notes (<paramref name="warn"/>: report the ones not understood).</summary>
    public void ApplyControls(bool warn = false)
    {
        Application.Bindings = SdlInputBindings.Parse(Arcade.Controls,
            warn ? problem => Console.Error.WriteLine($"Warning CONTROLS: {ArcadeSettings.FileName}: {problem}.") : null);
        Application.PadsAsGamepad = !Arcade.PadMenusAsDrum;
    }

    // In the menus the arrows and Enter also work the drum: Left/Up and Right/Down are its rims, Enter its
    // centre (player 1's, or player 2's when they play alone; the revival is always driven as player 1's).
    private SdlKeyboardSnapshot menuKeys(SdlKeyboardSnapshot keys)
    {
        var drum = Hosts.PlayerSide == 1 && !Hosts.TwoPlayers && Active.Id != FlowScenes.Retry ? 4 : 0;
        IEnumerable<SdlKeyboardKey> drumToo(SdlKeyboardKey key) => key switch
        {
            SdlKeyboardKey.Left or SdlKeyboardKey.Up => [key, SdlInputBindings.Pads[drum]],
            SdlKeyboardKey.Right or SdlKeyboardKey.Down => [key, SdlInputBindings.Pads[drum + 3]],
            SdlKeyboardKey.Enter => [key, SdlInputBindings.Pads[drum + 1]],
            _ => [key],
        };
        return new SdlKeyboardSnapshot(keys.PressedKeys.SelectMany(drumToo),
            keys.Presses.SelectMany(press => drumToo(press.Key).Select(key => press with { Key = key })), keys.Timestamp);
    }

    /// <summary>Applies the display settings (vsync, fullscreen, letterbox) and sizes the upscaled textures for them.</summary>
    public void ApplyDisplay()
    {
        if (Headless)
            return;
        // Exclusive fullscreen on Windows only: Linux compositors only emulate it.
        var exclusive = Arcade.ExclusiveFullscreen && OperatingSystem.IsWindows();
        var display = new DisplaySettings(Arcade.Vsync, Arcade.Fullscreen, exclusive, Arcade.FullscreenWidth,
            Arcade.FullscreenHeight, Arcade.RefreshRate, Arcade.LetterboxSize / 100f, Arcade.LetterboxX / 100f, Arcade.LetterboxY / 100f,
            exclusive && Arcade.Fullscreen ? 0 : Arcade.FpsCap);
        Application.ApplyDisplay(display);
        // The stage is 720 lines: 1080p loads textures at 1.5x, 1440p at 2x, 4K at the model's 3x.
        if (Upscale is not null)
            Upscale.Scale = Math.Clamp(Math.Ceiling(Application.StageHeight(display) / 720d * 2) / 2, 1, 3);
    }

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
        ChartHashes = new ChartHashes(Sync.Scores, Assets.LoadChartAsync) { Jobs = Sync.Jobs };
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
        ApplyControls(warn: true);
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
        var needsAudio = !Headless || options.JinglePath is not null || options.SoundRoot is not null;
        _audioDevice = needsAudio ? openAudio() : null;
        if (Environment.GetEnvironmentVariable("WADDAMBURO_PROFILE") == "1" && _audioDevice is not null)
            Console.Error.WriteLine($"Profile audio: {_audioDevice.Driver}, "
                + $"{_audioDevice.HardwareFormat.SampleRate} Hz, "
                + $"{_audioDevice.HardwareBufferFrames} hardware frames.");
        Audio = _audioDevice is null ? null : new AudioEngine(_audioDevice);
        Previews = Audio is null
            ? null
            : new SongPreviewController(Audio, Assets, FindJingle("JINGLE_GENRE.nub"), FindJingle("JINGLE_WAIGENRE.nub"));
        Sounds = options.SoundRoot is null ? null : new GameSounds(Audio!, options.SoundRoot);
        Titles = new SongTitleTextureCache(Application, options.FontPath, asynchronous: !Headless, english: Arcade.EnglishTitles,
            squash: () => Arcade.SquashTitles);
        _pill = new PairingPill(Application, options.FontPath);
        _textFields = new TextFieldTextures(Application, options.FontPath);
        _nameTexts = new NameTextTextures(Application, options.FontPath);
        _toneArt = new ToneArt(Application, dataRoot);
        _performance = new PerformanceOverlay(Application, () => Audio);
        _timingMarks = new TimingMarkOverlay(Application);
        Painter = new OverlayPainter(Application, options.FontPath);
        _reviewBar = new ReviewOverlay(Painter);
        if (Arcade.Home)
            _notices = new NoticeOverlay(Painter, Sync.Notices, Sync.Jobs,
                baid => Accounts?.Accounts.FirstOrDefault(account => account.Baid == baid)?.Name, Sync.MarkShown);
        Gameplay = new TaikoGameplayPresentation((lane, action) =>
            Sounds?.Gameplay.PlayDrum(lane, action is TaikoInputAction.LeftDon or TaikoInputAction.RightDon),
            Don, (lane, sound, combo) => Sounds?.Gameplay.Play(lane, sound, combo),
            () => TimeSpan.FromMilliseconds(Arcade.DrumDebounceMs));
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
                // Placeholder: the audio calibration replaces it with a fresh lane each time.
                GameplaySceneComposition.Create(FlowScenes.Calibration, Random.Shared, EnsoLayout),
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
            SongsStarted = () => SongsPlayed,
            ShowOni = () => Arcade.ShowOni,
            Best = index => (uint)index < (uint)Bests.Length ? Bests[index] : null,
            Placement = index => (uint)index < (uint)Placements.Length ? Placements[index] : null,
            // Next to the settings file (USRDIR/waddamburo); memory only without one.
            Favourites = new SongFavourites(options.ArcadePath is { } arcade
                ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(arcade))!, "favourites.txt") : null),
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
            PlayerName = side => Arcade.Home ? TaikoGuest.Profiles[side]?.DisplayName ?? Strings.T("setup.guest") : TaikoGuest.Profiles[side]?.Name,
        };
        if (Arcade.Home && Options.Accounts is { } setupBook)
            _playerSetup = new PlayerSetupController(this, setupBook, options.FontPath, options.ScoresPath);
        // Lumen patches edit the user's movies in memory as they decode (their files stay untouched).
        MovieContent = new DirectoryLumenMovieContentSource(Path.GetFullPath(assetRoot)) { Decoded = content => patch(content, null) };
        _presenter = new ScenePresenter(Application, MovieContent);
        // Upscaling runs when the model files are installed (see UpscaleTool).
        // The settings file sits in USRDIR/waddamburo, the caches under it unless cache_folder moves them.
        var cacheRoot = options.ArcadePath is { } settings
            ? ArcadeSettings.CacheRoot(Path.GetDirectoryName(Path.GetFullPath(settings))!, Arcade.CacheFolder) : null;
        if (UpscaleTool.Find(cacheRoot is null ? null : Path.Combine(cacheRoot, "upscaled")) is { } upscale)
        {
            Upscale = upscale;
            MovieContent.Loaded = upscale.RecordUsed;
            // Cached upscales replace textures as movies decode (opt-in): the GPU only ever gets those.
            MovieContent.Decoded = content => patch(content,
                Arcade.UpscaleTextures && Application.ShowUpscaled ? (upscale, Application.SupportsBc7) : null);
            SceneTextures.Upscaler = new TextureUpscaler(upscale, () => Arcade);
            Console.WriteLine($"Texture upscaling {(Arcade.UpscaleTextures ? "on" : "off")}, "
                + $"{Arcade.UpscaleThreads} background thread(s), cache {upscale.Cache}.");
        }
        ApplyDisplay();
        _loader = new LumenGameSceneLoader(MovieContent, Hosts);
        Coordinator = new GameFlowCoordinator(Catalog, _loader, flow);
        Overlay = new IntermissionOverlay(Application, _loader);
        // The intermissions are loaded once, up front: the rainbow must never stall a song's start.
        foreach (var intermission in new[] { FlowScenes.Rainbow, FlowScenes.Shutter, FlowScenes.Fade })
            Overlay.Preload(intermission);

        _attract = new AttractFlow(this, AttractMovie.Discover(Path.Combine(dataRoot, "movie")),
            AttractMovie.Opening(Path.Combine(dataRoot, "movie"), Path.Combine(assetRoot, "attract", "title", "packeddata.ddp")));
        _gameplay = new GameplayFlow(this);
        var calibration = new CalibrationFlow(this);
        _home = new HomeControls(this, _gameplay, calibration, options.FontPath, assetRoot);
        _search = new SongSearch(this, _libraries.Snapshot, Painter);
        _scores = new ScoreListOverlay(this, Painter, new OptionIcons(Application, options.AssetRoot));
        _inputOverlays = [_search, _scores];
        Reload = new SongSelectReload(this);
        var entry = new EntryFlow(this);
        var ending = new CreditEndFlow(this, _gameplay);
        var songSelect = new SongSelectFlow(this, _gameplay, calibration);
        _scenes = new()
        {
            [FlowScenes.Boot] = _attract,
            [FlowScenes.Logo] = _attract,
            [FlowScenes.Title] = _attract,
            [FlowScenes.Caution] = _attract,
            [FlowScenes.Movie] = _attract,
            [FlowScenes.Entry] = entry,
            [FlowScenes.SongSelect] = songSelect,
            [FlowScenes.Tutorial] = songSelect,
            [FlowScenes.Gameplay] = _gameplay,
            [FlowScenes.Calibration] = calibration,
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
            _home.LoadResume(_loader);
            Hosts.LayerLoading = host =>
            {
                if (Don is null) return;
                if (host is "result" or "retry" or GameplaySceneComposition.StaticHostId)
                    Don.MapPlayerZero = Hosts.PlayerSide == 1;
                else if (host is "player-entry" or "song-select" or "gameover" or "tutorial")
                    Don.MapPlayerZero = false;
            };
            Hosts.CardDialog = open => _indicators.CardDialog(open);
            Hosts.ReturnToAttract = () => _returnToAttract = true;
            _indicators.NetworkIcon = Sync.Health is { } health ? () => health.IconType : null;
            Hosts.EntryJoined = side =>
            {
                JoinPlayer(side);
                if (Coins is not null || _indicators.Rejoin)
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
                    _input.Frame(keyboard);
                    // F8 (or E, a Tatacon's right shoulder): the notice sidebar (live presses only reach this
                    // callback; not over a lane, nor while the search field takes the letters).
                    if (_notices is not null && !onLane && keyboard.Presses.Any(press => press.Key == SdlKeyboardKey.F8
                        || press.Key == SdlKeyboardKey.E && !_search.IsOpen))
                        _notices.Toggle();
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
            _attract.Dispose();
            Overlay.Dispose();
            _presenter.Dispose();
            SceneTextures.Release(Application, _indicatorTextures);
            _home.Dispose();
            _indicators?.Scene.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Coordinator.StopAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // One 60 Hz tick: the input first (back to the attract loop, coins, the home controls, the player
    // setup, the quick restart), then the active scene, the overlays, and a pending scene switch.
    private void tick(SdlKeyboardSnapshot keyboard)
    {
        Tick++;
        var keys = Options.InputTimeline.Apply(Tick, keyboard);
        var presses = _input.Take(keys);
        if (backToAttract(presses.Attract))
            return;
        coins(_input.TakeCoins(keys));
        // Diagnostic: WADDAMBURO_INPUT_TRACE=1 prints presses in --press format (KEY@tick).
        if (_traceInput)
            foreach (var press in keys.Presses)
                Console.WriteLine($"[input] {press.Key}@{Tick}");
        var scene = flowOf(Active.Id);
        // A reviewed play takes the mouse wheel (seeking) like a menu does.
        Application.MenuInput = !onLane || _home.MenuOpen || _gameplay.Reviewing && !_gameplay.Attempting;
        var held = keys;
        keys = scene.MapKeys(_input.Pulses(keys));
        // Home: Backspace is Escape too (a Tatacon's B button), except while it edits text or is being bound.
        var escape = _input.EscapePressed(keys, backspace: Arcade.Home && !_search.IsOpen && !_home.CapturingBinding);
        // Focus is followed whichever overlay takes the keys (the mute in background).
        _home.FollowFocus();
        // Song Select's search field takes the keyboard while open (its Escape closes it, not the menu).
        if (Reload.Tick() || !_home.MenuOpen && overlaysTake(keys, escape))
        {
            advance(scene, SdlKeyboardSnapshot.Empty);
            return;
        }
        if (_home.Tick(keys, held, escape))
            return;
        if (!onLane)
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
        ReportDiagnostics();
        switchScene(() => Coordinator.ApplyPendingTransitionAsync().AsTask().GetAwaiter().GetResult());
        Console.WriteLine($"Activated scene '{Active.Id}' at tick {Tick}.");
    }

    // An open overlay takes the tick's input; with none open, each may open on its own key (Tab, R).
    private bool overlaysTake(SdlKeyboardSnapshot keys, bool escape) =>
        _inputOverlays.LastOrDefault(static overlay => overlay.IsOpen) is { } open
            ? open.Tick(keys, escape) || true
            : _inputOverlays.Any(overlay => overlay.Tick(keys, escape));

    // A gameplay lane is on screen: a song or the audio calibration (drums drive it, not the movies' controls).
    private bool onLane => Active.Id == FlowScenes.Gameplay || Active.Id == FlowScenes.Calibration;

    // F1 (testing convenience, not cabinet behaviour) or an entry that gave up: drop the credit and
    // return to the attract loop from any menu scene (not mid-song, whose music the gameplay flow owns).
    private bool backToAttract(bool f1)
    {
        var toAttract = f1 || _returnToAttract;
        _returnToAttract = false;
        if (!toAttract || onLane || Active.Id == FlowScenes.Boot)
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

    // The scene's movies and the overlays drawn with it advance one tick.
    private void advance(FlowScene scene, SdlKeyboardSnapshot keys)
    {
        scene.Advance(LumenInputAdapter.CreateSnapshot(keys,
            onLane ? LumenInputMode.PresentationOnly : LumenInputMode.AuthoredControls));
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

    // F2 = coin, in any scene (the cabinet handles coins apart from the game). The credit counts at
    // once; each coin's sound queues and plays in full, one after another.
    private void coins(int inserted)
    {
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
        _presenter.Hold(Tick, black: Overlay.IsShown || Active.Id == FlowScenes.Movie, blackLoadingFrame);
        flowOf(Active.Id).Exit(Active.Id);
        _presenter.ReleaseScene();
        transition();
        activate();
        Application.DiscardElapsed();
    }

    private void activate()
    {
        Active = Coordinator.ActiveScene as LumenGameSceneInstance
            ?? throw new InvalidOperationException("The active scene is not a Lumen scene instance.");
        _presenter.Activate(Active);
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

    // The drum's own player while a replay shows another's (restored back in Song Select).
    private (int Side, ScoreProfile? Profile)? _watchedOver;

    /// <summary>A replay shows its player on <paramref name="side"/>: their name boards and Don, through its results.</summary>
    public void WatchAs(int side, ScoreProfile player)
    {
        _watchedOver ??= (side, TaikoGuest.Profiles[side]);
        TaikoGuest.Profiles[side] = player;
        Don?.SetLook(side, player.Look);
    }

    /// <summary>The drum's own player is back after a replay (nothing when none was watched).</summary>
    public void StopWatching()
    {
        if (_watchedOver is not var (side, profile))
            return;
        _watchedOver = null;
        TaikoGuest.Profiles[side] = profile;
        Don?.SetLook(side, profile?.Look);
    }

    public void ResetPlayers()
    {
        // A credit starts with guests (default Dons); cards and the home account attach as players join,
        // with the website's current looks.
        Array.Clear(TaikoGuest.Profiles);
        Array.Clear(TaikoGuest.Options);
        Array.Clear(TaikoGuest.Tones);
        Sync.RefreshProfiles();
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
    public RenderTextureId[] SceneTextureIds => _presenter.Textures;

    /// <summary>Back to the attract loop on the next tick (outside the callbacks asking for it).</summary>
    public void ReturnToAttract() => _returnToAttract = true;

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
    private static void patch(Waddamburo.Game.LumenMovieContent content, (UpscaleTool Tool, bool Bc7)? upscale)
    {
        var patching = Waddamburo.Game.Patching.SongSelectGenrePatch.AppliesTo(content);
        upscale?.Tool.ApplyCached(content, upscale.Value.Bc7 && !patching);
        if (!patching)
            return;
        var own = content.Textures.Length;
        Waddamburo.Game.Patching.SongSelectGenrePatch.Apply(content);
        for (var index = own; index < content.Textures.Length; index++)
            if (System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(content.Textures[index].Rgba8) is { } pixels)
                upscale?.Tool.MarkUpscaled(pixels);
    }

    public RenderTextureId? ResolveSurface(LumenNativeSurfaceKey surface) =>
        _attract.Movie?.Resolve(surface) ?? Don?.Resolve(surface)
        ?? _costumeIcons.Resolve(surface) ?? _waiwaiResultTextures.Resolve(surface) ?? _textFields.Resolve(surface) ?? _nameTexts.Resolve(surface) ?? _toneArt.Resolve(surface)
        ?? Titles.Resolve(surface);

    // One displayed frame. Depth order (traced): scene, msg_coins (-950), intermission (-2000),
    // network/card (-3000); then Waddamburo's own overlays and the home menu over everything.
    private RenderFrame createFrame(double interpolationFraction)
    {
        var interpolation = _home.Interpolation(interpolationFraction);
        if (_presenter.Held(Tick) is { } held)
            // The rainbow stays up across its scene switch (only a fade is cleared with it).
            return _home.Draw(held.Black && Overlay.IsShown
                ? new RenderFrame(held.Frame.ClearColor,
                    [.. Overlay.Quads(interpolation, Titles.Resolve), .. held.Frame.Quads], held.Frame.ContentAspectRatio)
                : held.Frame);
        Titles.UploadCompleted();
        _presenter.UploadAhead();
        if (SceneTextures.Upscaler is { } upscaler)
        {
            upscaler.Paused = onLane || Bake?.Running == true;
            upscaler.Apply(Application);
        }
        if (DonRenderer is not null)
            DonRenderer.Interpolation = interpolation;
        var scene = flowOf(Active.Id).CreateSnapshot(interpolation);
        if (LumenPlayer.Inspect)
            _inspected = scene;
        var frame = SceneTextures.Compose(scene, _presenter.Textures, "Scene", ResolveSurface);
        IEnumerable<RenderQuad> indicatorQuads(bool overIntermission) => _indicators is null ? []
            : SceneTextures.Compose(_indicators.CreateSnapshot(overIntermission, interpolation), _indicatorTextures,
                "Indicator", Titles.Resolve).Quads;
        var result = new RenderFrame(
            frame.ClearColor,
            [
                .. frame.Quads,
                .. Active.Id == FlowScenes.Gameplay ? _timingMarks.Quads(Gameplay.TimingMarks, Arcade.TimingIndicator) : [],
                .. _gameplay.Reviewing && _gameplay.Review is { } review ? _reviewBar.Quads(review, _gameplay.Training) : [],
                .. _playerSetup?.Quads(interpolation) ?? [],
                .. indicatorQuads(false),
                .. Overlay.Quads(interpolation, Titles.Resolve),
                .. _inputOverlays.SelectMany(static overlay => overlay.Quads()),
                .. indicatorQuads(true),
                .. pill(),
                .. _notices?.Quads(onLane) ?? [],
                .. _performance.Quads(),
            ],
            frame.ContentAspectRatio);
        _presenter.Presented(result, Overlay.IsShown);
        Painter.EndFrame();
        return _home.Draw(result);
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
        Sync.Dispose();
    }
}
