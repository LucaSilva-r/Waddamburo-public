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
using Waddamburo.Game.Lumen;
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

    /// <summary>The local score database, open when a profile plays (null: guests, nothing saved).</summary>
    public ScoreStore? Scores { get; }

    /// <summary>Per lane, the last saved play's previous best on its chart (for results).</summary>
    public long?[] PreviousBests { get; } = new long?[2];

    /// <summary>Chart key -> hash, stored with the scores (matches server bests to the library).</summary>
    public ChartHashes ChartHashes { get; }

    /// <summary>The server scores upload to (null: offline or a guest).</summary>
    public ScoreClient? ScoreServer { get; }

    /// <summary>The accounts stored on this PC (home mode; empty in arcade).</summary>
    public AccountBook? Accounts => Arcade.Home ? Options.Accounts : null;

    /// <summary>The stored account that joins the first drum by itself (home).</summary>
    public ScoreAccount? DefaultAccount => Accounts?.Default;

    private Uri? _uploadServer;
    private readonly Dictionary<string, ScoreClient> _uploaders = [];

    /// <summary>
    /// Uploads a player's pending plays in the background: a cabinet uploads everyone's with its own
    /// token; a home PC uploads each account's with that account's token (a friend's short-lived one
    /// included). Guests (no token) stay local.
    /// </summary>
    public void Upload(ScoreProfile profile)
    {
        if (Scores is null)
            return;
        if (ScoreServer is { } cabinet)
        {
            cabinet.SyncInBackground(Scores, null);
            return;
        }
        if (profile.Token is { } token && clientFor(token) is { } uploader)
            uploader.SyncInBackground(Scores, profile.Baid);
    }

    // Rankings are read with the cabinet's token, or at home with any token at hand: a joined
    // player's, the default account's, then any stored account's.
    private ScoreClient? rankingClient() => ScoreServer ?? TaikoGuest.Profiles.Select(static profile => profile?.Token)
        .Append(Accounts?.Default?.Token).Concat(Accounts?.Accounts.Select(static account => account.Token) ?? [])
        .OfType<string>().Select(clientFor).FirstOrDefault(static client => client is not null);

    /// <summary>
    /// Downloads a player's server bests (crowns from every machine) into the store in the background;
    /// song select reads them when it loads. Guests have none.
    /// </summary>
    public void RefreshBests(ScoreProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (Scores is not { } scores || profile.Baid == ScoreProfile.LocalGuestBaid)
            return;
        var client = ScoreServer ?? (profile.Token is { } token ? clientFor(token) : null);
        if (client is null)
            return;
        long? baid = ScoreServer is null ? null : profile.Baid;
        _ = Task.Run(async () =>
        {
            try
            {
                scores.ReplaceRemoteBests(profile.Baid, await client.BestsAsync(baid).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"Warning BESTS: {profile.Name}'s server bests not loaded ({exception.Message}).");
            }
        });
    }

    /// <summary>A server client with a home player's token (one per token, reused); null offline.</summary>
    private ScoreClient? clientFor(string token)
    {
        if (_uploadServer is not { } server)
            return null;
        lock (_uploaders)
        {
            if (!_uploaders.TryGetValue(token, out var client))
                _uploaders[token] = client = new ScoreClient(ScoreClient.CreateHttp(server, Arcade.ServerInsecure, token));
            return client;
        }
    }

    private readonly ServerHealth? _health;

    // Home: the "who's playing?" screen before the entry (stored accounts, guests, friends, in-game login).
    private PlayerSetupFlow? _setup;
    private EntrySetupOverlay? _entryOverlay;

    // Cabinet mode only (cabinet_token set, no home account).
    private readonly CabinetPairing? _pairing;
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

    private readonly GlobalSongCatalog _globalCatalog;
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
    private AudioBus? _sampleBus;
    private AudioPlaybackHandle? _sampleMusic;
    private bool _menuMusicHeld;
    private bool _wasFocused = true;
    private static readonly HashSet<SceneId> _attractScenes =
        [FlowScenes.Logo, FlowScenes.Title, FlowScenes.Caution, FlowScenes.Movie];
    private int _sampleNextTick;
    private long _qPressedAt;
    private float _qStartAlpha;
    private bool _qNeedsRelease;
    private long _restartCancelAt;
    private float _restartCancelAlpha;
    private long _menuRestartAt;
    private long _restartRevealAt;
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
        var cabinet = !Arcade.Home && Arcade is { Server: not null, CabinetToken: not null };
        _health = Arcade.Server is { } healthServer ? new ServerHealth(ScoreClient.CreateHttp(healthServer, Arcade.ServerInsecure)) : null;
        // Home keeps every play on this PC, guests' too (baid 0, never uploaded).
        Scores = (Arcade.Home || cabinet) && options.ScoresPath is { } scoresPath ? new ScoreStore(scoresPath) : null;
        if (Scores is not null && Arcade.Server is { } server)
        {
            _uploadServer = server;
            if (cabinet)
            {
                var http = ScoreClient.CreateHttp(server, Arcade.ServerInsecure, Arcade.CabinetToken);
                ScoreServer = new ScoreClient(http);
                _pairing = new CabinetPairing(http);
            }
            // Plays left over from offline runs: a cabinet's (everyone's), or each stored account's.
            if (ScoreServer is not null)
                Upload(ScoreProfile.LocalGuest);
            foreach (var account in Arcade.Home ? options.Accounts?.Accounts ?? [] : [])
            {
                Upload(account.Profile);
                RefreshBests(account.Profile);
            }
            // Names, looks and avatars as the website has them now (revoked logins drop out).
            if (Arcade.Home && options.Accounts is { } book)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await book.RefreshAsync(account => clientFor(account.Token)!).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                    {
                        Console.Error.WriteLine($"Warning ACCOUNT: profiles not refreshed ({exception.Message}).");
                    }
                });
        }
        // The game's own songs live beside the Lumen data (<data>/lumendata/packed).
        var dataRoot = Path.GetFullPath(Path.Combine(assetRoot, "..", ".."));
        // Either source may be absent: a stock install without custom songs, or custom songs alone.
        ISongCatalogProvider[] providers = [
            .. StockCatalogProvider.IsStockData(dataRoot) ? [new StockCatalogProvider(dataRoot)] : Array.Empty<ISongCatalogProvider>(),
            // Custom TJA is a home-mode library of its own (arcade matches the game 1:1);
            // WADDAMBURO_CUSTOM_TJA=1 lists it in arcade too.
            .. (Arcade.Home || Environment.GetEnvironmentVariable("WADDAMBURO_CUSTOM_TJA") == "1") && Directory.Exists(Arcade.TjaFolder ?? options.TjaRoot)
                ? [new TjaCatalogProvider(Arcade.TjaFolder ?? options.TjaRoot)] : Array.Empty<ISongCatalogProvider>(),
            // A Nijiiro installation picked in the settings (home mode).
            .. Arcade.Home && Arcade.NijiiroFolder is { } nijiiro && new NijiiroCatalogProvider(nijiiro) is { Exists: true } installed
                ? [installed] : Array.Empty<ISongCatalogProvider>(),
        ];
        Assets = new CatalogAssetRouter(providers);
        ChartHashes = new ChartHashes(Scores, Assets.LoadChartAsync);
        _globalCatalog = new GlobalSongCatalog(providers);
        var snapshot = _globalCatalog.RefreshAsync().AsTask().GetAwaiter().GetResult();
        foreach (var status in snapshot.Providers)
            Console.WriteLine(status.Succeeded
                ? $"Catalog provider {status.Provider}: {status.SongCount} songs in {status.CategoryCount} categories."
                : $"Catalog provider {status.Provider} failed.");
        SongCatalog = new SongSelectCatalogView(snapshot);
        // Server crowns need every chart's hash before song select lists it.
        if (_uploadServer is not null)
            ChartHashes.HashLibraryInBackground(snapshot.Songs.Values);
        if (SongCatalog.Categories.IsEmpty)
            throw new InvalidOperationException(snapshot.Diagnostics.FirstOrDefault(static diagnostic => diagnostic.Severity == CatalogDiagnosticSeverity.Error)?.Message
                ?? "No song provider discovered any browsable categories.");
        Console.WriteLine($"Catalog revision {snapshot.Revision}: {SongCatalog.Categories.Length} categories.");
        foreach (var diagnostic in snapshot.Diagnostics)
            Console.Error.WriteLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");

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
            applyAudioSettings();
        }, saveSettings, don => Sounds?.Bank.Play("SE_COM", don ? 0 : 3, AudioBus.DrumHit, trace: false));
        Previews = Audio is null
            ? null
            : new SongPreviewController(Audio, Assets, FindJingle("JINGLE_GENRE.nub"), FindJingle("JINGLE_WAIGENRE.nub"));
        Sounds = options.SoundRoot is null ? null : new GameSounds(Audio!, options.SoundRoot);
        applyAudioSettings();
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
            Rankings = _uploadServer is null ? null : new SongRankings(rankingClient, ChartHashes),
            PreviousBest = index => (uint)index < (uint)PreviousBests.Length ? PreviousBests[index] : null,
            Crowns = side => Scores is null ? null
                : TaikoGuest.Profiles[side] is { } profile ? Scores.Crowns(profile.Baid)
                : Arcade.Home ? Scores.Crowns(ScoreProfile.LocalGuestBaid) : null,
            CardClaimed = (side, card) =>
            {
                TaikoGuest.Profiles[side] = card;
                RefreshBests(card);
            },
            // A card given to the drum, or the account chosen for it in the home player setup.
            PlayerLook = side => TaikoGuest.Profiles[side]?.Look,
            // Home boards show the account's public name (or Guest), drawn by the entry overlay.
            PlayerName = side => Arcade.Home ? TaikoGuest.Profiles[side]?.DisplayName ?? "Guest" : TaikoGuest.Profiles[side]?.Name,
        };
        if (Arcade.Home && Options.Accounts is { } setupBook)
        {
            _entryOverlay = new EntrySetupOverlay(Application, options.FontPath);
            _setup = new PlayerSetupFlow(setupBook, Arcade.Server, Arcade.ServerInsecure, clientFor,
                Path.Combine(Path.GetDirectoryName(options.ScoresPath) ?? ".", "avatars"));
            _setup.Confirmed += joinSetupPlayers;
            _setup.Refused += () => Sounds?.Frontend.PlayCue("SE_COM", 12); // SE_COM_COM_CARD_ERROR
            _setup.Cancelled += () =>
            {
                Hosts.EntrySetup = false;
                _returnToAttract = true;
            };
        }
        MovieContent = new DirectoryLumenMovieContentSource(Path.GetFullPath(assetRoot));
        _loader = new LumenGameSceneLoader(MovieContent, Hosts);
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
            SongsPlayed = 0; // a new credit starts at the first song
            Show(FlowScenes.Logo);
            return;
        }
        coins(keys);
        // Diagnostic: WADDAMBURO_INPUT_TRACE=1 prints presses in --press format (KEY@tick).
        if (_traceInput)
            foreach (var press in keys.Presses)
                Console.WriteLine($"[input] {press.Key}@{Tick}");
        var scene = flowOf(Active.Id);
        var heldKeys = keys;
        keys = scene.MapKeys(drumPulses(keys));
        var escapeIsDown = keys.IsDown(SdlKeyboardKey.Escape);
        var escape = escapeIsDown && !_escapeWasDown;
        _escapeWasDown = escapeIsDown;
        // Home: Escape opens the menu (pause in gameplay; settings and back to the title in the menus).
        // Going to the background: the sound fades out (when set) and a home song pauses.
        if (Application.Focused != _wasFocused)
        {
            _wasFocused = Application.Focused;
            if (Arcade.MuteInBackground || _wasFocused)
                Audio?.Mixer.FadeOutput(_wasFocused ? 1 : 0, TimeSpan.FromMilliseconds(300));
        }
        // Resume's countdown: the song stays held until it ends; Escape or leaving the window pauses again.
        if (_resume is { Running: true } countdown)
        {
            if (escape || !_wasFocused)
            {
                countdown.Cancel();
                _menu.Open(gameplay: true);
            }
            else if (countdown.Advance((bank, cue) => Sounds?.Bank.Play(bank, cue, trace: false)))
                resumeHome();
            return;
        }
        // A song still unpausable when the window lost focus (under the rainbow) pauses once it can.
        // Once every note and long note is over (the song's tail), leaving the window no longer pauses.
        if (Arcade.Home && !_menu.IsOpen
            && (escape || !_wasFocused && Active.Id == FlowScenes.Gameplay && !_gameplay.ChartOver))
        {
            if (Active.Id == FlowScenes.Gameplay)
            {
                if (restartBlack() == 0 && _gameplay.CanPause)
                {
                    _qPressedAt = 0;
                    _pauseInterpolation = _lastInterpolation;
                    _gameplay.SetPaused(true, _pauseInterpolation);
                    _menu.Open(gameplay: true);
                    return;
                }
            }
            // The attract, the entry (and its player setup) and Song Select: settings, back to the title.
            else if (_attractScenes.Contains(Active.Id) || Active.Id == FlowScenes.Entry || Active.Id == FlowScenes.SongSelect)
            {
                if (Overlay.IsShown || Coordinator.Flow.State == GameFlowState.TransitionPending)
                    return;
                _pauseInterpolation = _lastInterpolation;
                _menu.Open(gameplay: false, attract: _attractScenes.Contains(Active.Id));
                holdMenuMusic(true);
                return;
            }
        }
        if (_menu.IsOpen)
        {
            // A folder picked in the system dialog opened from the settings.
            if (_pickingFolder is { } picking && SdlApplication.TryTakePickedFolder(out var folder))
            {
                Arcade = picking == HomeMenuAction.PickTjaFolder ? Arcade with { TjaFolder = folder }
                    : Arcade with { NijiiroFolder = folder };
                saveSettings();
                _pickingFolder = null;
            }
            var action = _menu.Input(keys, escape);
            volumeSample(_menu.PreviewBus);
            if (!_menu.IsOpen)
                holdMenuMusic(false);
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
                    _menuRestartAt = Stopwatch.GetTimestamp();
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
                    if (_setup is { IsOpen: true } open)
                    {
                        open.Close();
                        Hosts.EntrySetup = false;
                    }
                    _returnToAttract = true;
                    break;
            }
            return;
        }
        // Home: Tab in the entry goes back to the player setup with the current players.
        if (_setup is { IsOpen: false } reopen && Active.Id == FlowScenes.Entry && keys.IsDown(SdlKeyboardKey.Tab))
        {
            ScoreProfile?[] profiles = [.. TaikoGuest.Profiles];
            int[] joined = [.. JoinedSides];
            Sounds?.StopAll();
            beginSetup();
            reopen.Reopen(profiles, joined);
            return;
        }
        // Home: the player setup runs inside the entry and takes the drums; the movie only animates.
        if (_setup is { } setup)
        {
            var open = setup.IsOpen;
            setup.Tick(open ? keys : SdlKeyboardSnapshot.Empty);
            if (open && setup.IsOpen && Active.Id == FlowScenes.Entry && Hosts.Entry is { SetupMode: true } entry)
            {
                applySetup(entry, setup.Columns());
                setupArrows().Advance();
                // The drums pick only once the screen is fully up: the entry on screen, no transition over
                // it, and its intro (Don entry motion, boards fading in) played.
                if (!Overlay.IsShown)
                    _setupShownTicks++;
                setup.InputEnabled = _setupShownTicks >= SetupIntroTicks;
            }
            if (open && Tick % 10 == 0 && Environment.GetEnvironmentVariable("WADDAMBURO_SETUP_TRACE") == "1")
                Console.WriteLine("[setup] " + Tick + " entry=" + (Hosts.Entry?.Ticks ?? -1) + ": " + string.Join(" | ", setup.Columns().Select(c => c.Choice.Kind + " " + c.Choice.Label + " ready=" + c.Ready)));
            if (open)
                keys = SdlKeyboardSnapshot.Empty;
        }
        if (updateQuickRestart(heldKeys)) return;
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
        if (Coordinator.Flow.State != GameFlowState.TransitionPending)
        {
            if (Arcade.Home && Active.Id == FlowScenes.Result
                && (heldKeys.IsDown(SdlKeyboardKey.Q) || _menuRestartAt != 0))
                return;
            scene.Tick(new FlowInput(keys, hitSide, skip, escape));
            return;
        }
        if (Arcade.Home && Active.Id == FlowScenes.Result
            && (heldKeys.IsDown(SdlKeyboardKey.Q) || _qPressedAt != 0))
            return;
        // A movie asked for the next scene (entry -> song select); its last voice finishes first.
        if (Sounds?.Bank.IsVoicePlaying == true)
            return;
        ReportDiagnostics();
        switchScene(() => Coordinator.ApplyPendingTransitionAsync().AsTask().GetAwaiter().GetResult());
        Console.WriteLine($"Activated scene '{Active.Id}' at tick {Tick}.");
    }

    // The entry's and Song Select's music and preview hold while their menu is open.
    private void holdMenuMusic(bool held)
    {
        if (Audio is null || _menuMusicHeld == held) return;
        _menuMusicHeld = held;
        Audio.Mixer.SetBusPaused(AudioBus.Bgm, held);
        Audio.Mixer.SetBusPaused(AudioBus.Preview, held);
    }

    private void resumeHome()
    {
        if (Active.Id == FlowScenes.Gameplay)
            _gameplay.SetPaused(false);
    }

    // The player's audio settings; volumes (percent) on a curve that sounds even: half way is about a quarter of the level.
    private void applyAudioSettings()
    {
        if (Audio?.Mixer is not { } mixer) return;
        static float level(int percent) => percent * percent / 10000f;
        mixer.MasterVolume = level(Arcade.MasterVolume);
        mixer.SetBusGain(AudioBus.Bgm, level(Arcade.MusicVolume));
        mixer.SetBusGain(AudioBus.Preview, level(Arcade.MusicVolume));
        mixer.SetBusGain(AudioBus.DrumHit, level(Arcade.DrumVolume));
        mixer.SetBusGain(AudioBus.MenuSound, level(Arcade.EffectsVolume));
        mixer.SetBusGain(AudioBus.Coin, level(Arcade.EffectsVolume));
        mixer.SetBusGain(AudioBus.Voice, level(Arcade.VoiceVolume));
        if (Sounds is not null)
            Sounds.Gameplay.Panning = Arcade.StereoPanning;
    }

    // While a volume is edited its bus keeps sounding: a random song, a menu sound, or a Don-chan
    // line, each repeated once it has finished.
    private void volumeSample(AudioBus? bus)
    {
        if (bus != _sampleBus)
        {
            if (_sampleMusic is { } music)
                Audio?.Mixer.Stop(music, TimeSpan.FromMilliseconds(20));
            if (_sampleBus == AudioBus.Voice)
                Sounds?.Bank.StopVoice();
            _sampleMusic = null;
            _sampleBus = bus;
            _sampleNextTick = 0;
        }
        if (bus is not { } playing || Audio is null || Tick < _sampleNextTick)
            return;
        switch (playing)
        {
            case AudioBus.Bgm when _sampleMusic is not { } music || !Audio.Mixer.IsPlaying(music):
                _sampleMusic = randomSong();
                _sampleNextTick = Tick + 60;
                break;
            case AudioBus.MenuSound:
                Sounds?.Bank.Play("SE_COM", 13, AudioBus.MenuSound, trace: false);
                _sampleNextTick = Tick + 60;
                break;
            case AudioBus.Voice when Sounds?.Bank is { IsVoicePlaying: false } bank:
                bank.Play("VO_SELECT", 1, trace: false);
                _sampleNextTick = Tick + 30;
                break;
        }
    }

    // A random catalog song from its preview point, on the music bus.
    // ponytail: opens the file on the tick (a short hitch); open it in the background if that shows.
    private AudioPlaybackHandle? randomSong()
    {
        var songs = SongCatalog.Categories.SelectMany(static category => category.Songs)
            .Where(static song => song.Descriptor.AudioAsset is not null).ToArray();
        if (songs.Length == 0 || Audio is null) return null;
        var song = songs[Random.Shared.Next(songs.Length)].Descriptor;
        try
        {
            var input = Assets.OpenReadAsync(song.AudioAsset!).AsTask().GetAwaiter().GetResult();
            BufferedAudioSource source;
            try
            {
                source = new BufferedAudioSource(input, Audio.Mixer.Format, song.PreviewStart ?? TimeSpan.Zero);
            }
            catch
            {
                input.Dispose();
                throw;
            }
            return Audio.Mixer.PlayStream(source, AudioBus.Bgm);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
        {
            Console.Error.WriteLine($"Volume sample '{song.Title.Primary}' unavailable: {exception.Message}");
            return null;
        }
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

    private bool updateQuickRestart(SdlKeyboardSnapshot held)
    {
        if (!Arcade.Home || Active.Id != FlowScenes.Result
            && (Active.Id != FlowScenes.Gameplay || !_gameplay.CanQuickRestart))
        {
            _qPressedAt = 0;
            _restartCancelAt = 0;
            _menuRestartAt = 0;
            return false;
        }
        var now = Stopwatch.GetTimestamp();
        if (_menuRestartAt != 0 && Stopwatch.GetElapsedTime(_menuRestartAt, now) >= TimeSpan.FromMilliseconds(500))
        {
            _menuRestartAt = 0;
            return restartNow();
        }
        if (!held.IsDown(SdlKeyboardKey.Q))
        {
            if (_qPressedAt != 0)
            {
                _restartCancelAlpha = qHoldAlpha(now);
                _restartCancelAt = now;
            }
            _qPressedAt = 0;
            _qNeedsRelease = false;
        }
        else if (!_qNeedsRelease && _qPressedAt == 0 && _menuRestartAt == 0 && _restartRevealAt == 0)
        {
            _qStartAlpha = cancelAlpha(now);
            _restartCancelAt = 0;
            _qPressedAt = now;
        }
        if (_qPressedAt != 0 && qHoldAlpha(now) >= 1)
        {
            _qPressedAt = 0;
            _qNeedsRelease = true;
            return restartNow();
        }
        if (_restartRevealAt != 0 && Stopwatch.GetElapsedTime(_restartRevealAt, now) >= TimeSpan.FromMilliseconds(500))
            _restartRevealAt = 0;
        if (_restartCancelAt != 0 && cancelAlpha(now) <= 0)
            _restartCancelAt = 0;
        return false;
    }

    private bool restartNow()
    {
        _qPressedAt = 0;
        _restartCancelAt = 0;
        _menuRestartAt = 0;
        if (_gameplay.Restart())
            _restartRevealAt = Stopwatch.GetTimestamp();
        return true;
    }

    private float qHoldAlpha(long now) => Math.Clamp(_qStartAlpha
        + (float)(Stopwatch.GetElapsedTime(_qPressedAt, now).TotalSeconds * 2), 0, 1);

    private float cancelAlpha(long now) => _restartCancelAt == 0 ? 0 : Math.Clamp(_restartCancelAlpha
        - (float)(Stopwatch.GetElapsedTime(_restartCancelAt, now).TotalSeconds * 2), 0, 1);

    private float restartBlack()
    {
        if (_menuRestartAt != 0)
            return Math.Clamp((float)(Stopwatch.GetElapsedTime(_menuRestartAt).TotalSeconds * 2), 0, 1);
        if (_qPressedAt != 0)
            return qHoldAlpha(Stopwatch.GetTimestamp());
        if (_restartCancelAt != 0)
            return cancelAlpha(Stopwatch.GetTimestamp());
        if (_restartRevealAt != 0)
            return Math.Clamp(1 - (float)(Stopwatch.GetElapsedTime(_restartRevealAt).TotalSeconds * 2), 0, 1);
        return 0;
    }

    private RenderFrame withHomeOverlay(RenderFrame frame) => _homeOverlay is null
        ? frame : new RenderFrame(frame.ClearColor,
            frame.Quads.Concat(_resume is { Running: true } countdown
                    ? SceneTextures.Compose(countdown.CreateSnapshot(1), _resumeTextures, "Resume", Titles.Resolve).Quads
                    : [])
                .Concat(_homeOverlay.Quads(_menu.IsOpen ? _menu : null, restartBlack())),
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
        var frame = SceneTextures.Compose(flowOf(Active.Id).CreateSnapshot(interpolation), _textures, "Scene", resolveSurface);
        IEnumerable<RenderQuad> indicatorQuads(bool overIntermission) => _indicators is null ? []
            : SceneTextures.Compose(_indicators.CreateSnapshot(overIntermission, interpolation), _indicatorTextures,
                "Indicator", Titles.Resolve).Quads;
        // Depth order (traced): scene, msg_coins (-950), intermission (-2000), network/card (-3000).
        var result = new RenderFrame(
            frame.ClearColor,
            frame.Quads.Concat(entryOverlay((float)interpolation)).Concat(indicatorQuads(false)).Concat(Overlay.Quads(interpolation, Titles.Resolve))
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
        if (_setup is not { } setup)
            return false;
        Sounds?.StopAll();
        // Diagnostic: WADDAMBURO_SETUP_BOTH=1 skips the screen with both drums joined (the DON_BOTH entry
        // start): the first two stored accounts, a guest where there is none.
        if (Environment.GetEnvironmentVariable("WADDAMBURO_SETUP_BOTH") == "1")
        {
            var stored = Accounts?.Accounts ?? [];
            startWithPlayers([.. Enumerable.Range(0, 2).Select(index => index < stored.Count ? stored[index].Profile : null)], [true, true]);
            return true;
        }
        Sounds?.StopAll();
        beginSetup();
        setup.Open(side);
        return true;
    }

    // The player setup is the entry itself: both stands up (DON_BOTH), nobody joined yet, the menus
    // hidden (EntrySceneHost.SetupMode). Confirming joins the chosen players in place (FinishSetup).
    private const int SetupIntroTicks = 90; // 1.5 s: the Don's entry motion (traced 0.85 s) and the boards
    private int _setupShownTicks;

    private void beginSetup()
    {
        _setupShownTicks = 0;
        ResetPlayers();
        SongsPlayed = 0;
        Array.Clear(_shownChoice);
        Array.Fill(_swapAt, -1);
        Array.Fill(_revealAt, -1);
        Hosts.EntrySetup = true;
        Hosts.EntryTrigger = 2;
        _indicatorScene = null;
        Show(FlowScenes.Entry);
    }

    // Each side's choice on its stand: its Don in the chosen look (options show as text instead) and
    // the entry's own costume flash and cue when it changes.
    private void applySetup(EntrySceneHost entry, SetupColumn[] columns)
    {
        for (var side = 0; side < 2; side++)
        {
            var column = columns[side];
            var choice = column.Choice;
            // A new choice starts the entry's costume change (smoke and cue); what stands on the drum only
            // swaps once the smoke covers it, so the new Don or text never pops in.
            if (_shownChoice[side] is not { } shown)
                showOnStand(side, choice);
            else if (sameStand(shown, choice))
                _swapAt[side] = -1;
            else if (_swapAt[side] < 0)
            {
                entry.CostumeChanged(side);
                _swapAt[side] = Tick + SetupSwapDelay;
                _revealAt[side] = Tick + SetupRevealDelay;
            }
            else if (Tick >= _swapAt[side])
            {
                showOnStand(side, choice);
                _swapAt[side] = -1;
            }
            entry.SetSetupSide(side, _shownChoice[side]!.HasDon, board: true);
        }
        // Both drums show their choice: no "hit the drum to start" bubble during the setup.
        _indicators?.SetupPanels(false, false);
    }

    // The costume smoke covers the stand from ~6 ticks and bursts at ~54: the Don swaps under it, and text
    // (drawn over the scene, so it would show through the smoke) appears with the burst.
    private const int SetupSwapDelay = 12, SetupRevealDelay = 54;
    private readonly SetupChoice?[] _shownChoice = new SetupChoice?[2];
    private readonly long[] _swapAt = [-1, -1], _revealAt = [-1, -1];

    private void showOnStand(int side, SetupChoice choice)
    {
        _shownChoice[side] = choice;
        if (choice.HasDon)
            Don?.SetLook(side, choice.Look);
    }

    // Two choices look the same on the stand: the same Don look, or the same option text.
    private static bool sameStand(SetupChoice a, SetupChoice b) =>
        a.HasDon == b.HasDon && (a.HasDon ? a.Look == b.Look : a.Kind == b.Kind);

    // The entry's own animated arrows (SetupArrows), per loaded entry; anchored on each stand.
    private SetupArrows? _setupArrows;
    private LumenGameSceneInstance? _setupArrowsScene;
    private static readonly (float X, float Y)[] ArrowAnchors = [(150, 430), (1130, 430)];
    private const float ArrowGap = 150, ArrowScale = 0.45f;
    private const int AutoJoinStar = 295; // entry.lm texture: the yellow four-point sparkle (40x40)
    private const float AutoJoinStarSize = 44;
    private static readonly (float X, float Y)[] AutoJoinStarAt = [(85, 345), (1065, 345)];

    private SetupArrows setupArrows()
    {
        if (_setupArrows is null || _setupArrowsScene != Active)
        {
            _setupArrows = new SetupArrows(Active.Layers[0].Content.Definition);
            _setupArrowsScene = Active;
        }
        return _setupArrows;
    }

    // Home players' names over the entry's boards, and the setup's arrows and option text.
    private IEnumerable<RenderQuad> entryOverlay(float interpolation)
    {
        if (_entryOverlay is not { } overlay || Active.Id != FlowScenes.Entry)
            return [];
        // The tag follows the live choice; the stand shows what is on it now (it swaps mid-smoke), and its
        // text only once the smoke has burst.
        var live = _setup is { IsOpen: true } setup && Hosts.Entry is { SetupMode: true } ? setup.Columns() : null;
        var columns = live?.Select((column, side) => column with { Choice = _shownChoice[side] ?? column.Choice }).ToArray();
        bool[] standVisible = [Tick >= _revealAt[0], Tick >= _revealAt[1]];
        string?[] tags = live is not null
            ? [.. live.Select(static column => column.Choice.Label)]
            : [.. Enumerable.Range(0, 2).Select(side => JoinedSides.Contains(side) ? TaikoGuest.Profiles[side]?.DisplayName ?? "Guest" : null)];
        var quads = overlay.Quads(columns, tags, standVisible);
        if (columns is null)
            return quads;
        // The account that joins by itself (S): the entry's yellow sparkle over the Don's top-left, twinkling.
        // The live choice decides (S toggles it at once); the stand must already show that account.
        for (var side = 0; side < 2; side++)
            if (live![side].Choice is { IsDefault: true, HasDon: true } choice && columns[side].Choice.Baid == choice.Baid
                && standVisible[side] && AutoJoinStar < _textures.Length)
            {
                var size = AutoJoinStarSize * (0.85f + 0.15f * MathF.Sin((Tick + interpolation) * 0.08f));
                var (x, y) = AutoJoinStarAt[side];
                quads = quads.Append(RenderQuad.FromRectangles(_textures[AutoJoinStar],
                    new RenderRectangle((x - size / 2) / 1280f, (y - size / 2) / 720f, size / 1280f, size / 720f),
                    RenderRectangle.Full, RenderColor.White, RenderColor.Transparent));
            }
        // The arrows wait for the stands' slide-in (the same intro gate as the drums).
        if (_setupShownTicks < SetupIntroTicks)
            return quads;
        // The arrows while a drum is choosing (not locked in, no code on screen). The entry is its scene's
        // first layer, so the arrow sprite's texture indices are the scene's.
        var arrows = setupArrows();
        for (var side = 0; side < 2; side++)
            if (!columns[side].Ready && columns[side].Code is null)
                quads = quads.Concat(SceneTextures.Compose(arrows.Snapshot(side, ArrowAnchors[side].X, ArrowAnchors[side].Y,
                    ArrowGap, ArrowScale, interpolation), _textures, "Arrows", resolveSurface).Quads);
        return quads;
    }

    // The setup's players: their profiles and looks go in first, then the entry starts with them joined
    // (SCENE_TRIGGER_DON_1P 0 / _DON_2P 1 / _DON_BOTH 2: the movie joins those drums itself).
    private void startWithPlayers(ScoreProfile?[] profiles, bool[] playing)
    {
        Sounds?.Attract.PlayExit();
        Hosts.EntrySetup = false;
        _indicatorScene = null; // the entry reloads under the same id: its indicators start over
        ResetPlayers();
        SongsPlayed = 0;
        for (var side = 0; side < 2; side++)
        {
            if (!playing[side])
                continue;
            TaikoGuest.Profiles[side] = profiles[side];
            if (profiles[side] is { } profile)
                RefreshBests(profile); // a visitor's first, a stored account's again
            Don?.SetLook(side, profiles[side]?.Look);
            JoinPlayer(side);
        }
        Hosts.EntryTrigger = playing[0] && playing[1] ? 2 : playing[1] ? 1 : 0;
        Console.WriteLine($"Player setup: 1P {describe(0)}, 2P {describe(1)}.");
        string describe(int side) => !playing[side] ? "not playing" : profiles[side] is { } profile ? $"{profile.Name} (baid {profile.Baid})" : "guest";
        Show(FlowScenes.Entry);
    }

    // The setup's players join the entry already on screen (its music and Don motions carry on).
    private void joinSetupPlayers(ScoreProfile?[] profiles, bool[] playing)
    {
        if (Hosts.Entry is not { SetupMode: true } entry || Active.Id != FlowScenes.Entry)
        {
            startWithPlayers(profiles, playing);
            return;
        }
        Hosts.EntrySetup = false;
        for (var side = 0; side < 2; side++)
        {
            if (!playing[side])
                continue;
            TaikoGuest.Profiles[side] = profiles[side];
            if (profiles[side] is { } profile)
                RefreshBests(profile);
            Don?.SetLook(side, profiles[side]?.Look);
        }
        entry.FinishSetup(playing); // PlayerJoined -> JoinPlayer per side
        Console.WriteLine($"Player setup: 1P {describe(0)}, 2P {describe(1)}.");
        string describe(int side) => !playing[side] ? "not playing" : profiles[side] is { } profile ? $"{profile.Name} (baid {profile.Baid})" : "guest";
    }

    /// <summary>A paired card was rejected by the server (shown once).</summary>
    public bool TakeCardFailure() => _pairing?.TakeCardFailure() == true;

    /// <summary>A card was paired during the attract loop: it starts the credit (SCENE_TRIGGER_CARD).</summary>
    public ScoreProfile? TakeCard() => _pairing?.TakeCard();

    private IEnumerable<RenderQuad> pill()
    {
        if (_pairing is null)
            return _pill.Quads(); // home: the account picker shows and hides it
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
        _setup?.Dispose();
        _textFields.Dispose();
        Hosts?.Rankings?.Dispose();
        _entryOverlay?.Dispose();
        _homeOverlay?.Dispose();
        _health?.Dispose();
        _pill.Dispose();
        _performance.Dispose();
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
