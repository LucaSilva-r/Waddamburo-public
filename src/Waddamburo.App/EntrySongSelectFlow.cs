using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Gameplay;
using Waddamburo.Game.Don;
using Waddamburo.Game.Lumen;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Media;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.Providers.Tja;

/// <summary>
/// Diagnostic composition for the measured Green Entry-to-Song-Select contract.
/// Asset paths and request routing live here, outside the runtime and host adapters.
/// </summary>
internal static class EntrySongSelectFlow
{
    public static int Run(
        string assetRoot,
        string? donRoot,
        int windowWidth,
        int windowHeight,
        int? frameLimit,
        int? tickLimit,
        string? screenshotPath,
        SdlKeyboardTimeline inputTimeline,
        string tjaRoot,
        string fontPath,
        string? jinglePath,
        string? soundRoot,
        bool countdown = true,
        StartScene startScene = StartScene.Boot)
    {
        var tja = new TjaCatalogProvider(tjaRoot);
        using var globalCatalog = new GlobalSongCatalog([tja]);
        var snapshot = globalCatalog.RefreshAsync().AsTask().GetAwaiter().GetResult();
        var status = snapshot.Providers.Single(provider => provider.Provider == tja.Id);
        if (!status.Succeeded)
            throw new InvalidOperationException(snapshot.Diagnostics.First(diagnostic => diagnostic.Provider == tja.Id).Message);
        var songCatalog = new SongSelectCatalogView(snapshot);
        if (songCatalog.Categories.IsEmpty)
            throw new InvalidOperationException("The custom TJA provider did not discover any browsable categories.");
        Console.WriteLine($"Catalog revision {snapshot.Revision}: {status.SongCount} songs in {status.CategoryCount} categories.");
        foreach (var diagnostic in snapshot.Diagnostics)
            Console.Error.WriteLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");

        using var application = new SdlApplication(
            "Waddamburo",
            windowWidth,
            windowHeight,
            debugGpu: false,
            resizable: screenshotPath is null,
            highPixelDensity: screenshotPath is null);
        Console.WriteLine($"SDL_GPU driver: {application.GpuDriver}");
        using var donRenderer = donRoot is null ? null : application.CreateDonRenderer(donRoot);
        var donPresentation = donRenderer is null ? null : new DonPresentationController(donRenderer);
        // Diagnostic: WADDAMBURO_DON_COSTUME=head,body,paint (or a single whole-costume id) dresses P1 at start.
        if (donPresentation is not null && Environment.GetEnvironmentVariable("WADDAMBURO_DON_COSTUME") is { } costumeSetting)
        {
            var ids = costumeSetting.Split(',').Select(int.Parse).ToArray();
            donPresentation.SetCostume(0, ids.Length == 1 ? DonCostume.FromWhole(ids[0]) : new DonCostume(null, ids[0], ids[1], ids.ElementAtOrDefault(2)));
        }
        var needsAudio = screenshotPath is null || jinglePath is not null || soundRoot is not null;
        using var audioDevice = needsAudio ? new SdlAudioDevice() : null;
        using var audioEngine = audioDevice is null ? null : new AudioEngine(audioDevice);
        var entryJinglePath = jinglePath ?? findJingle(soundRoot, "JINGLE_ENTRY.nub");
        var songSelectJinglePath = findJingle(soundRoot, "JINGLE_GENRE.nub");
        using var previewController = audioEngine is null
            ? null
            : new SongPreviewController(audioEngine, tja, songSelectJinglePath);
        var soundController = soundRoot is null
            ? null
            : new AuthoredSoundController(audioEngine!, soundRoot);
        AudioPlaybackHandle? sceneBgm = null;
        if (startScene == StartScene.Entry && entryJinglePath is not null)
        {
            if (jinglePath is null)
                sceneBgm = audioEngine!.PlayLoop(entryJinglePath, AudioBus.Bgm);
            else
                sceneBgm = audioEngine!.PlayOneShot(entryJinglePath, AudioBus.Bgm);
            Console.WriteLine(
                $"Entry jingle: {Path.GetFileName(entryJinglePath)} via {audioDevice!.Driver}, " +
                $"{audioDevice.HardwareBufferFrames} hardware buffer frames.");
        }
        using var titleTextures = new SongTitleTextureCache(
            application,
            fontPath,
            asynchronous: screenshotPath is null);
        var gameplayPresentation = new TaikoGameplayPresentation(action =>
            soundController?.PlayDrum(action is TaikoInputAction.LeftDon or TaikoInputAction.RightDon),
            donPresentation, sound => soundController?.PlayGameplayEvent(sound));

        var bootId = new SceneId("boot");
        var logoId = new SceneId("attract-logo");
        var titleId = new SceneId("attract-title");
        var cautionId = new SceneId("attract-caution");
        var movieId = new SceneId("attract-movie");
        var attractMovies = AttractMovie.Discover(Path.GetFullPath(Path.Combine(assetRoot, "..", "..", "movie")));
        // Shuffled like the cabinet's rotation; refilled when every CM has played once.
        var attractMovieQueue = new Queue<string>();
        string? lastAttractMovie = null;
        AttractMovie? attractMovie = null;
        var entryId = new SceneId("entry");
        var songSelectId = new SceneId("song-select");
        var gameplayId = new SceneId("gameplay");
        var resultId = new SceneId("result");
        var retryId = new SceneId("retry");
        var gameOverId = new SceneId("gameover");
        var rainbowTransitionId = new SceneId("rainbow-transition");
        var rainbowDefinition = RainbowTransitionComposition.Create(rainbowTransitionId);
        // Traced: a finished song closes the intermission shutter (Close(0)), then the results load
        // under it; result.lm opens on the same closed-shutter art, so the shutter is just dropped.
        var shutterDefinition = new SceneDefinition(SceneDefinition.CurrentVersion, new SceneId("shutter"), [
            new SceneLayerDefinition("intermission/packeddata.ddp", "shutter/shutter.lm", LumenMatrix.Identity,
                RainbowTransitionComposition.StaticHostId),
        ]);
        // Traced: leaving the results or the revival (except results -> revival, which opens on its own
        // shutter) plays the intermission fade's "in" (1 s to black), swaps scenes under it, then
        // resets the fade to "wait" (transparent).
        var fadeDefinition = new SceneDefinition(SceneDefinition.CurrentVersion, new SceneId("fade"), [
            new SceneLayerDefinition("intermission/packeddata.ddp", "scene_change_fade/scene_change_fade.lm",
                LumenMatrix.Identity, RainbowTransitionComposition.StaticHostId),
        ]);
        var ensoLayout = GameplaySceneComposition.LoadLayout(assetRoot);
        var costumeIcons = new CostumeIconTextures(application, Path.GetFullPath(Path.Combine(assetRoot, "..", "..")));
        var songInfo = new TaikoSongInfo(0, 1);
        TaikoPlayResult? diagnosticResult = startScene switch
        {
            StartScene.ResultFail => new TaikoPlayResult(
                TaikoCourse.Normal, 0, 0, 0, 100, 0, 0, 0, false),
            StartScene.ResultClear => new TaikoPlayResult(
                TaikoCourse.Normal, 500_000, 90, 7, 3, 80, 0, 45, true),
            _ => null,
        };
        var songsPlayed = 0;
        var flow = new GameFlowSession();
        var playRequests = new PlayRequestState();
        var catalog = new SceneCatalog(
            [
                // Traced boot and attract loop: kidou (notice, logos) once, then logo_namco -> title ->
                // keikoku -> attract CM -> logo_namco ..., each full screen at depth 1000.
                attractScene(bootId, "kidou", "boot"),
                attractScene(logoId, "logo_namco"),
                attractScene(titleId, "title"),
                attractScene(cautionId, "keikoku"),
                attractScene(movieId, "movie", "boot"),
                new SceneDefinition(SceneDefinition.CurrentVersion, entryId, [
                    // Traced entry composition: the scene movie, then its indicator parts by depth
                    // (100 .. 50; the game draws them over the scene). entry_info / shop_info (-890)
                    // start hidden and are left out until something shows them.
                    new SceneLayerDefinition(
                        "entry/packeddata.ddp",
                        "entry/entry.lm",
                        LumenMatrix.Identity,
                        "player-entry"),
                    indicatorPart("indicator"),
                    indicatorPart("player_name", 640, 360),
                    indicatorPart("player_name", 640, 360),
                    indicatorPart("time_counter"),
                    indicatorPart("over_msg"),
                ]),
                new SceneDefinition(SceneDefinition.CurrentVersion, songSelectId, [
                    new SceneLayerDefinition(
                        "song_select/packeddata.ddp",
                        "song_select/song_select.lm",
                        LumenMatrix.Identity,
                        "song-select"),
                    indicatorPart("indicator"),
                    // Traced final P1 name-board position is (-580, 278) in the cabinet's
                    // centered coordinates, or (60, 638) in our top-left scene coordinates.
                    indicatorPart("player_name", 60, 638),
                    indicatorPart("player_name", 640, 360),
                    indicatorPart("time_counter"),
                ]),
                GameplaySceneComposition.Create(gameplayId, Random.Shared, ensoLayout),
                // Traced 1P results: the results movie and its "press to continue" overlay, full screen.
                new SceneDefinition(SceneDefinition.CurrentVersion, resultId, [
                    new SceneLayerDefinition("enso_result/packeddata.ddp", "result/result.lm", LumenMatrix.Identity, "result"),
                    new SceneLayerDefinition("waitinput/packeddata.ddp", "waitinput/waitinput.lm", LumenMatrix.Identity, "waitinput"),
                ]),
                // Traced end of a credit: the revival drum roll after a failed first song, then game over.
                new SceneDefinition(SceneDefinition.CurrentVersion, retryId, [
                    new SceneLayerDefinition("enso_result/packeddata.ddp", "retry_game/retry_game.lm", LumenMatrix.Identity, "retry"),
                ]),
                new SceneDefinition(SceneDefinition.CurrentVersion, gameOverId, [
                    new SceneLayerDefinition("reward_shop/packeddata.ddp", "shop_gameover/shop_gameover.lm", LumenMatrix.Identity, "gameover"),
                ]),
            ],
            [new SceneTransitionRoute(entryId, new LumenSceneRequest(1, 0, 0), songSelectId)]);
        var skins = new GameplaySkinResolver(Path.GetFullPath(assetRoot),
            Path.GetFullPath(Path.Combine(assetRoot, "..", "..", "config", "S11100-1", "musicinfo.xml")));
        var hostFactory = new CatalogHostFactory(
                flow,
                songCatalog,
                titleTextures,
                previewController is null ? TracePreviewController.Instance : previewController,
                soundController is null ? TraceSoundController.Instance : soundController,
                new ViewerFrontendServices(soundController),
                donPresentation,
                playRequests,
                () => songInfo,
                countdown,
                () => gameplayPresentation.Result ?? diagnosticResult);
        var loader = new LumenGameSceneLoader(
            new DirectoryLumenMovieContentSource(Path.GetFullPath(assetRoot)), hostFactory);
        var coordinator = new GameFlowCoordinator(catalog, loader, flow);
        var initialScene = startScene switch
        {
            StartScene.Boot => bootId,
            StartScene.Attract => logoId,
            StartScene.Entry => entryId,
            StartScene.SongSelect => songSelectId,
            StartScene.ResultFail or StartScene.ResultClear => resultId,
            StartScene.Retry => retryId,
            StartScene.GameOver => gameOverId,
            _ => throw new ArgumentOutOfRangeException(nameof(startScene)),
        };
        coordinator.StartAsync(initialScene).AsTask().GetAwaiter().GetResult();

        LumenGameSceneInstance active = null!;
        RenderTextureId[] textureIds = [];
        LumenGameSceneInstance? rainbow = null;
        RenderTextureId[] rainbowTextureIds = [];
        SystemIndicators? indicators = null;
        RenderTextureId[] indicatorTextureIds = [];
        SceneId? indicatorScene = null;

        try
        {
            active = requireLumenScene(coordinator);
            textureIds = uploadTextures(application, active);
            if (active.Id == songSelectId)
                previewController?.StartBackground();
            else if (active.Id == resultId)
                soundController?.StartResultMusic();
            Console.WriteLine($"Showing {active.Id} at launch.");
            indicators = new SystemIndicators((LumenGameSceneInstance)loader
                .LoadAsync(SystemIndicators.Definition(new SceneId("system-indicators")), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult());
            indicatorTextureIds = uploadTextures(application, indicators.Scene);
            var rainbowSequence = new RainbowTransitionSequence();
            LumenNativeSurfaceKey? rainbowTitle = null;
            PlayableChart[] activeGameplayCharts = [];
            AudioStreamTransport? gameplayMusic = null;
            GameplayTimeline? gameplayTimeline = null;
            var gameplayClock = new System.Diagnostics.Stopwatch();
            var gameplayStartTick = 0;
            var resultStartTick = 0;
            var shutterStartTick = -1;
            var shutterClosing = false;
            SceneId? fadeTarget = null;
            var fadeStartTick = 0;
            var escapeWasDown = false;
            var traceInput = Environment.GetEnvironmentVariable("WADDAMBURO_INPUT_TRACE") == "1";
            var dumpTreeTick = int.TryParse(Environment.GetEnvironmentVariable("WADDAMBURO_DUMP_TREE"), out var dumpAt) ? dumpAt : -1;
            var simulationTick = 0;
            // Live presses reach only the per-frame callback; ticks see held keys. Latch drum hits
            // there for the attract loop (scripted --press pulses arrive in the tick instead).
            var drumHitLatched = false;
            var skipLatched = false;
            static bool isDrum(SdlKeyPress press) => press.Key is SdlKeyboardKey.D
                or SdlKeyboardKey.F or SdlKeyboardKey.J or SdlKeyboardKey.K;
            void goTo(SceneId scene)
            {
                if (active.Id == resultId)
                    soundController?.StopResultMusic();
                if (active.Id == gameOverId)
                    soundController?.StopGameOverMusic();
                if (active.Id == titleId)
                    soundController?.StopAttractStream();
                attractMovie?.Dispose();
                attractMovie = null;
                coordinator.TransitionToAsync(scene).AsTask().GetAwaiter().GetResult();
                releaseTextures(application, textureIds);
                active = requireLumenScene(coordinator);
                textureIds = uploadTextures(application, active);
                if (scene == movieId)
                {
                    if (attractMovieQueue.Count == 0)
                    {
                        var order = attractMovies.ToArray();
                        Random.Shared.Shuffle(order);
                        // No CM twice in a row across refills.
                        if (order.Length > 1 && order[0] == lastAttractMovie)
                            (order[0], order[^1]) = (order[^1], order[0]);
                        foreach (var movie in order)
                            attractMovieQueue.Enqueue(movie);
                    }
                    var path = lastAttractMovie = attractMovieQueue.Dequeue();
                    try
                    {
                        attractMovie = new AttractMovie(application, audioEngine, path);
                        active.Player.Layers.Single().Player.SetNativeFill(AttractMovieFill, AttractMovie.Surface);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException
                        or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
                    {
                        Console.Error.WriteLine($"Attract movie {Path.GetFileName(path)} is unavailable: {exception.Message}");
                    }
                }
                if (scene == songSelectId)
                    previewController?.StartBackground();
                else if (scene == entryId && entryJinglePath is not null)
                    sceneBgm = jinglePath is null
                        ? audioEngine!.PlayLoop(entryJinglePath, AudioBus.Bgm)
                        : audioEngine!.PlayOneShot(entryJinglePath, AudioBus.Bgm);
                Console.WriteLine($"Showing {scene} at tick {simulationTick}.");
            }

            void fadeTo(SceneId scene)
            {
                if (fadeTarget is not null) return;
                if (scene == retryId)
                {
                    goTo(scene);
                    return;
                }
                releaseTextures(application, rainbowTextureIds);
                rainbow?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                rainbow = (LumenGameSceneInstance)loader.LoadAsync(fadeDefinition, CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
                rainbowTextureIds = uploadTextures(application, rainbow);
                rainbow.Player.Layers.Single().Player.GotoLabel("in", play: true);
                fadeTarget = scene;
                fadeStartTick = simulationTick;
            }

            // True once a pending fade to black has finished and its scene is shown.
            bool finishFade()
            {
                if (fadeTarget is not { } target) return false;
                if (simulationTick - fadeStartTick < 60) return true;
                fadeTarget = null;
                goTo(target);
                releaseTextures(application, rainbowTextureIds);
                rainbowTextureIds = [];
                rainbow?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                rainbow = null;
                return true;
            }

            TimeSpan chartTime() => gameplayTimeline?.ChartTime(
                rainbowSequence.State is not (RainbowTransitionState.Revealing or RainbowTransitionState.Complete)
                ? TimeSpan.Zero : gameplayMusic is not null ? audioEngine!.GetPosition(gameplayMusic)
                : screenshotPath is not null ? TimeSpan.FromSeconds((simulationTick - gameplayStartTick) / 60d)
                : gameplayClock.Elapsed) ?? TimeSpan.Zero;

            RenderFrame createFrame(double interpolationFraction)
            {
                titleTextures.UploadCompleted();
                if (donRenderer is not null)
                    donRenderer.Interpolation = (float)interpolationFraction;
                var frame = LumenRenderFrameAdapter.Compose(
                    active.Id == gameplayId
                        ? gameplayPresentation.CreateSnapshot(chartTime(), (float)interpolationFraction)
                        : active.Player.CreateRenderSnapshot((float)interpolationFraction),
                    RenderColor.WaddamburoBlue,
                    index => index < textureIds.Length
                        ? textureIds[index]
                        : throw new InvalidDataException($"Scene snapshot references missing texture {index}."),
                    surface => attractMovie?.Resolve(surface) ?? donPresentation?.Resolve(surface)
                        ?? costumeIcons.Resolve(surface) ?? titleTextures.Resolve(surface));
                IEnumerable<RenderQuad> indicatorQuads(bool overIntermission) => indicators is null ? []
                    : LumenRenderFrameAdapter.Compose(
                        indicators.CreateSnapshot(overIntermission, (float)interpolationFraction),
                        RenderColor.WaddamburoBlue,
                        index => index < indicatorTextureIds.Length
                            ? indicatorTextureIds[index]
                            : throw new InvalidDataException($"Indicator snapshot references missing texture {index}."),
                        titleTextures.Resolve).Quads;
                // Depth order (traced): scene, msg_coins (-950), intermission (-2000), network/card (-3000).
                IEnumerable<RenderQuad> rainbowQuads = rainbow is null ? [] : LumenRenderFrameAdapter.Compose(
                    rainbow.Player.CreateRenderSnapshot((float)interpolationFraction),
                    RenderColor.WaddamburoBlue,
                    index => index < rainbowTextureIds.Length
                        ? rainbowTextureIds[index]
                        : throw new InvalidDataException($"Rainbow snapshot references missing texture {index}."),
                    titleTextures.Resolve).Quads;
                return new RenderFrame(
                    frame.ClearColor,
                    frame.Quads.Concat(indicatorQuads(false)).Concat(rainbowQuads).Concat(indicatorQuads(true)).ToArray(),
                    frame.ContentAspectRatio);
            }

            var result = application.Run(
                createFrame,
                keyboard =>
                {
                    simulationTick++;
                    var keyboardState = inputTimeline.Apply(simulationTick, keyboard);
                    var drumHit = drumHitLatched || keyboardState.Presses.Any(isDrum);
                    drumHitLatched = false;
                    var skipPressed = skipLatched || keyboardState.Presses.Any(static press => press.Key == SdlKeyboardKey.Space);
                    skipLatched = false;
                    // Diagnostic: WADDAMBURO_INPUT_TRACE=1 prints presses in --press format (KEY@tick).
                    if (traceInput)
                        foreach (var press in keyboardState.Presses)
                            Console.WriteLine($"[input] {press.Key}@{simulationTick}");
                    var input = LumenInputAdapter.CreateSnapshot(keyboardState,
                        active.Id == gameplayId
                            ? LumenInputMode.PresentationOnly
                            : LumenInputMode.AuthoredControls);
                    if (active.Id == gameplayId)
                    {
                        var animationFrames = gameplayPresentation.AdvanceAnimations(chartTime(), input);
                        donRenderer?.Advance(animationFrames);
                    }
                    else
                    {
                        active.Player.Advance(input);
                        donRenderer?.Advance();
                    }
                    rainbow?.Player.Advance();
                    // Diagnostic: WADDAMBURO_DUMP_TREE=<tick> prints the active scene's display lists.
                    if (dumpTreeTick == simulationTick)
                        foreach (var (layer, index) in active.Layers.Select((layer, index) => (layer, index)))
                        {
                            Console.WriteLine($"== layer {index} {layer.Definition.MovieId}");
                            foreach (var line in active.Player.Layers[index].Player.DescribeDisplayList())
                                Console.WriteLine(line);
                        }
                    if (indicators is not null)
                    {
                        if (indicatorScene != active.Id)
                        {
                            indicatorScene = active.Id;
                            indicators.SetScene(active.Id == bootId ? IndicatorScene.Boot
                                : active.Id == logoId ? IndicatorScene.Attract
                                : active.Id == titleId || active.Id == cautionId || active.Id == movieId
                                    ? IndicatorScene.AttractPrompt
                                : active.Id == entryId ? IndicatorScene.Entry
                                : active.Id == gameplayId ? IndicatorScene.Gameplay
                                : active.Id == resultId || active.Id == retryId || active.Id == gameOverId ? IndicatorScene.Result
                                : IndicatorScene.SongSelect);
                        }
                        indicators.Advance();
                    }
                    var escapeIsDown = keyboardState.IsDown(SdlKeyboardKey.Escape);
                    var escapePressed = escapeIsDown && !escapeWasDown;
                    escapeWasDown = escapeIsDown;
                    if (coordinator.Flow.State != GameFlowState.TransitionPending)
                    {
                        if (active.Id == songSelectId && playRequests.Pending is { } pendingRequest)
                        {
                            if (rainbowSequence.State is RainbowTransitionState.Idle or RainbowTransitionState.Complete)
                            {
                                if (pendingRequest.Players.Length != 1)
                                    throw new NotSupportedException("Gameplay supports one local player.");
                                reportDiagnostics(active);
                                var selected = songCatalog.Categories
                                    .SelectMany(category => category.Songs)
                                    .Single(song => song.Descriptor.Key == pendingRequest.Song);
                                rainbowTitle = titleTextures.GetTransitionTitle(selected);
                                _ = titleTextures.Resolve(rainbowTitle.Value);
                                rainbowSequence.Begin(simulationTick);
                                Console.WriteLine($"Queued rainbow cover for '{pendingRequest.Song}' at tick {simulationTick}.");
                            }
                            if (rainbowSequence.ShouldStartCover(simulationTick))
                            {
                                rainbow = (LumenGameSceneInstance)loader.LoadAsync(rainbowDefinition, CancellationToken.None)
                                    .AsTask().GetAwaiter().GetResult();
                                var player = rainbow.Player.Layers.Single().Player;
                                player.SetNativeFill(
                                    RainbowTransitionComposition.SongTitleFill,
                                    rainbowTitle ?? throw new InvalidOperationException("Rainbow transition has no song title."));
                                player.GotoLabel(RainbowTransitionComposition.CoverLabel, play: true);
                                rainbowTextureIds = uploadTextures(application, rainbow);
                                rainbowSequence.StartCover();
                                if (sceneBgm is { } previousBgm)
                                    audioEngine?.Mixer.Stop(previousBgm, TimeSpan.FromMilliseconds(20));
                                sceneBgm = null;
                                audioEngine?.Mixer.StopBus(AudioBus.Preview, TimeSpan.FromMilliseconds(20));
                                audioEngine?.Mixer.StopBus(AudioBus.Bgm, TimeSpan.FromMilliseconds(20));
                                Console.WriteLine($"Rainbow cover started at tick {simulationTick}.");
                            }
                            if (rainbow is not null && rainbowSequence.FinishCoverWhenStopped(
                                    rainbow.Player.Layers.Single().Player.IsPlaying,
                                    simulationTick))
                            {
                                PlayableChart[] loadedCharts;
                                try
                                {
                                    loadedCharts = pendingRequest.Players
                                        .Select(player => tja.LoadChartAsync(player.Chart, player.ChartAsset)
                                            .AsTask().GetAwaiter().GetResult())
                                        .ToArray();
                                }
                                catch (Exception exception) when (exception is InvalidDataException
                                    or NotSupportedException or IOException or OverflowException or ArgumentException)
                                {
                                    Console.Error.WriteLine($"Cannot play selected chart: {exception.Message}");
                                    playRequests.CancelPending();
                                    releaseTextures(application, rainbowTextureIds);
                                    rainbowTextureIds = [];
                                    rainbow.DisposeAsync().AsTask().GetAwaiter().GetResult();
                                    rainbow = null;
                                    rainbowSequence = new RainbowTransitionSequence();
                                    coordinator.TransitionToAsync(songSelectId).AsTask().GetAwaiter().GetResult();
                                    releaseTextures(application, textureIds);
                                    active = requireLumenScene(coordinator);
                                    textureIds = uploadTextures(application, active);
                                    previewController?.StartBackground();
                                    return;
                                }
                                // Every song gets its themed skin or a fresh random original mix.
                                var played = songCatalog.Categories
                                    .SelectMany(category => category.Songs.Select(song => (category.Name, song)))
                                    .First(entry => entry.song.Descriptor.Key == pendingRequest.Song);
                                var theme = skins.Resolve(played.song.Descriptor, played.Name);
                                soundController?.PrepareGameplayDrums();
                                // ponytail: the stage counts every song since launch (no credits yet); 8 stage frames.
                                songInfo = new TaikoSongInfo(GameplaySceneComposition.GenreIndex(played.Name),
                                    Math.Min(++songsPlayed, 8));
                                Console.WriteLine($"Gameplay skin: {theme?.Archive ?? "random enso_original"}.");
                                catalog.Replace(GameplaySceneComposition.Create(gameplayId, Random.Shared, ensoLayout, theme));
                                coordinator.TransitionToAsync(gameplayId).AsTask().GetAwaiter().GetResult();
                                var request = playRequests.ActivatePending();
                                activeGameplayCharts = loadedCharts;
                                releaseTextures(application, textureIds);
                                active = requireLumenScene(coordinator);
                                // song_info's 720x64 title slot: fixed height, right-aligned, squeezed to fit.
                                var songInfoIndex = active.Layers.ToList().FindIndex(layer =>
                                    Path.GetFileNameWithoutExtension(layer.Definition.MovieId) == "song_info");
                                if (songInfoIndex >= 0)
                                {
                                    var gameplayTitle = titleTextures.GetGameplayTitle(played.song);
                                    _ = titleTextures.Resolve(gameplayTitle);
                                    active.Player.Layers[songInfoIndex].Player.SetNativeFill("song_name", gameplayTitle);
                                }
                                textureIds = uploadTextures(application, active);
                                gameplayTimeline = new GameplayTimeline(loadedCharts[0].AuthoredOffset, TimeSpan.FromSeconds(3));
                                gameplayStartTick = simulationTick;
                                gameplayClock.Reset();
                                gameplayPresentation.Start(loadedCharts[0], active, request.Players[0].Course);
                                Console.WriteLine(
                                    $"Loaded covered gameplay for '{request.Song}' with {request.Players.Length} player(s), "
                                    + $"{activeGameplayCharts.Sum(static chart => chart.NoteCount)} notes at tick {simulationTick}.");
                            }
                        }
                        else if (active.Id == resultId)
                        {
                            if (rainbow is not null && fadeTarget is null && simulationTick - resultStartTick >= 2)
                            {
                                releaseTextures(application, rainbowTextureIds);
                                rainbowTextureIds = [];
                                rainbow.DisposeAsync().AsTask().GetAwaiter().GetResult();
                                rainbow = null;
                            }
                            if (finishFade()) return;
                            // The movie ends itself (_global.isAllEnd; 15-21 s traced by closing message).
                            if (escapePressed || RetryGameHostBinding.IsEnd(active.Player.Layers[0].Player))
                            {
                                var play = gameplayPresentation.Result ?? diagnosticResult
                                    ?? throw new InvalidOperationException("Results without a finished play.");
                                fadeTo(TaikoCredit.EndMessage(songInfo.Stage, play.Cleared) switch
                                {
                                    2 => retryId,
                                    1 => songSelectId,
                                    _ => gameOverId,
                                });
                            }
                            return;
                        }
                        else if (active.Id == retryId)
                        {
                            if (finishFade()) return;
                            if (RetryGameHostBinding.IsEnd(active.Player.Layers.Single().Player))
                                fadeTo(hostFactory.Retry?.Succeeded == true ? songSelectId : gameOverId);
                            return;
                        }
                        else if (active.Id == gameOverId)
                        {
                            if (hostFactory.GameOver?.Ended == true)
                            {
                                // Traced: the credit's end returns to the attract loop at logo_namco.
                                songsPlayed = 0;
                                goTo(logoId);
                            }
                            return;
                        }
                        else if (active.Id == bootId)
                        {
                            // Space skips the boot screens (testing convenience, not cabinet behaviour).
                            if (skipPressed || AttractHostBinding.IsBootEnd(active.Player.Layers.Single().Player))
                                goTo(logoId);
                            return;
                        }
                        else if (active.Id == logoId || active.Id == titleId || active.Id == cautionId || active.Id == movieId)
                        {
                            attractMovie?.Advance();
                            // ponytail: any drum key starts (free play); coins and cards are not modelled.
                            if (drumHit)
                            {
                                // The attract's voices, effects and music end with it.
                                soundController?.StopAll();
                                soundController?.PlayAttractExit();
                                goTo(entryId);
                            }
                            else if (active.Id == movieId)
                            {
                                if (attractMovie?.Finished != false)
                                    goTo(logoId);
                            }
                            else if (hostFactory.Attract?.Finished == true)
                                // Traced: keikoku is followed by one attract CM, then logo_namco again.
                                goTo(active.Id == logoId ? titleId : active.Id == titleId ? cautionId
                                    : attractMovies.Length != 0 ? movieId : logoId);
                            return;
                        }
                        else if (active.Id == gameplayId)
                        {
                            if (rainbow is not null && rainbowSequence.ShouldStartReveal(simulationTick))
                            {
                                var request = playRequests.Active
                                    ?? throw new InvalidOperationException("Covered gameplay has no active play request.");
                                gameplayStartTick = simulationTick;
                                gameplayMusic = startGameplayAudio(audioEngine, tja, request, gameplayTimeline!, activeGameplayCharts[0].Duration);
                                gameplayClock.Restart();
                                rainbow.Player.Layers.Single().Player.GotoLabel(
                                    RainbowTransitionComposition.RevealLabel,
                                    play: true);
                                rainbowSequence.StartReveal();
                                Console.WriteLine($"Rainbow reveal and gameplay started at tick {simulationTick}.");
                                return;
                            }
                            if (rainbow is not null && rainbowSequence.FinishRevealWhenStopped(
                                    rainbow.Player.Layers.Single().Player.IsPlaying))
                            {
                                releaseTextures(application, rainbowTextureIds);
                                rainbowTextureIds = [];
                                rainbow.DisposeAsync().AsTask().GetAwaiter().GetResult();
                                rainbow = null;
                                Console.WriteLine($"Rainbow reveal completed at tick {simulationTick}.");
                            }
                            if (rainbowSequence.State is not
                                (RainbowTransitionState.Revealing or RainbowTransitionState.Complete))
                            {
                                return;
                            }
                            var elapsed = chartTime();
                            if (screenshotPath is not null)
                                gameplayPresentation.Advance(keyboardState, elapsed);
                            if (gameplayMusic?.Failure is not null || audioEngine?.Failure is not null)
                                throw new IOException("Gameplay audio failed.", gameplayMusic?.Failure ?? audioEngine?.Failure);
                            var chartFinished = activeGameplayCharts.Length != 0
                                && elapsed >= activeGameplayCharts.Max(static chart => chart.Duration)
                                    + TimeSpan.FromSeconds(1);
                            var musicFinished = gameplayMusic is not { } music
                                || audioEngine is null
                                || !audioEngine.Mixer.IsPlaying(music.Handle);
                            if (!escapePressed && shutterStartTick < 0
                                && chartFinished && musicFinished && !gameplayPresentation.OverlayActive)
                            {
                                releaseTextures(application, rainbowTextureIds);
                                rainbow?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                                rainbow = (LumenGameSceneInstance)loader.LoadAsync(shutterDefinition, CancellationToken.None)
                                    .AsTask().GetAwaiter().GetResult();
                                rainbowTextureIds = uploadTextures(application, rainbow);
                                shutterStartTick = simulationTick;
                                shutterClosing = false;
                            }
                            // Close registers on the shutter's first frame; retry until it exists.
                            if (shutterStartTick >= 0 && !shutterClosing && rainbow is not null)
                                shutterClosing = rainbow.Player.Layers.Single().Player.TryInvokeCallback("Close", [LumenHostValue.FromNumber(0)]);
                            // ponytail: 70 ticks = traced Close → results load (1.17 s); the close itself takes ~1 s.
                            if (escapePressed || shutterStartTick >= 0 && simulationTick - shutterStartTick >= 70)
                            {
                                shutterStartTick = -1;
                                if (gameplayMusic is { } currentMusic)
                                    audioEngine?.Mixer.Stop(currentMusic.Handle, TimeSpan.FromMilliseconds(20));
                                gameplayMusic = null;
                                gameplayClock.Reset();
                                // The shutter stays over the results until their first frames are drawn.
                                if (escapePressed)
                                {
                                    releaseTextures(application, rainbowTextureIds);
                                    rainbowTextureIds = [];
                                    rainbow?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                                    rainbow = null;
                                }
                                rainbowSequence = new RainbowTransitionSequence();
                                reportDiagnostics(active);
                                gameplayPresentation.ReportDiagnostics();
                                gameplayPresentation.Stop();
                                playRequests.ClearActive();
                                // Escape skips straight back; a finished song shows its results first.
                                if (!escapePressed)
                                    soundController?.PlayGameplayEvent(GameplaySoundEvent.SongFinished);
                                coordinator.TransitionToAsync(escapePressed ? songSelectId : resultId).AsTask().GetAwaiter().GetResult();
                                activeGameplayCharts = [];
                                releaseTextures(application, textureIds);
                                active = requireLumenScene(coordinator);
                                textureIds = uploadTextures(application, active);
                                resultStartTick = simulationTick;
                                if (active.Id == songSelectId)
                                    previewController?.StartBackground();
                                else if (active.Id == resultId)
                                    soundController?.StartResultMusic();
                                Console.WriteLine($"Gameplay ended at tick {simulationTick}; showing {active.Id}.");
                            }
                        }
                        return;
                    }

                    if (soundController?.IsVoicePlaying == true)
                        return;

                    reportDiagnostics(active);
                    coordinator.ApplyPendingTransitionAsync().AsTask().GetAwaiter().GetResult();
                    releaseTextures(application, textureIds);
                    active = requireLumenScene(coordinator);
                    textureIds = uploadTextures(application, active);
                    if (active.Id == songSelectId)
                    {
                        if (sceneBgm is { } previousBgm)
                            audioEngine?.Mixer.Stop(previousBgm, TimeSpan.FromMilliseconds(20));
                        sceneBgm = null;
                        previewController?.StartBackground();
                        if (songSelectJinglePath is not null)
                            Console.WriteLine($"Song Select jingle: {Path.GetFileName(songSelectJinglePath)}.");
                    }
                    Console.WriteLine($"Activated scene '{active.Id}' at tick {simulationTick}.");
                },
                frameLimit,
                tickLimit,
                screenshotPath is null ? null : capture => ScreenshotWriter.Write(screenshotPath, capture),
                updateFrame: keyboard =>
                {
                    drumHitLatched |= keyboard.Presses.Any(isDrum);
                    skipLatched |= keyboard.Presses.Any(static press => press.Key == SdlKeyboardKey.Space);
                    if (screenshotPath is null && active.Id == gameplayId
                        && rainbowSequence.State is RainbowTransitionState.Revealing or RainbowTransitionState.Complete)
                        gameplayPresentation.Advance(keyboard, chartTime());
                });

            Console.WriteLine($"Active scene: {active.Id}");
            if (active.Id == gameplayId)
                Console.WriteLine($"Loaded gameplay charts: {activeGameplayCharts.Length}.");
            reportDiagnostics(active);
            if (active.Id == gameplayId) gameplayPresentation.ReportDiagnostics();
            if (result.DroppedTicks > 0)
                Console.Error.WriteLine($"Warning PLT_DROPPED_TICKS: dropped {result.DroppedTicks} simulation ticks.");
            return result.RenderedFrames;
        }
        finally
        {
            attractMovie?.Dispose();
            releaseTextures(application, rainbowTextureIds);
            if (rainbow is not null)
                rainbow.DisposeAsync().AsTask().GetAwaiter().GetResult();
            releaseTextures(application, textureIds);
            releaseTextures(application, indicatorTextureIds);
            if (indicators is not null)
                indicators.Scene.DisposeAsync().AsTask().GetAwaiter().GetResult();
            coordinator.StopAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // movie.lm's full-screen video slot; every shipped CM is 1280x720, so its other sizes stay empty.
    private const string AttractMovieFill = "movie1280x720";

    private static SceneDefinition attractScene(SceneId id, string movie, string host = "attract") =>
        new(SceneDefinition.CurrentVersion, id, [
            new SceneLayerDefinition($"attract/{movie}/packeddata.ddp", $"{movie}/{movie}.lm", LumenMatrix.Identity, host),
        ]);

    private static SceneLayerDefinition indicatorPart(string movie, float x = 0, float y = 0) => new(
        "indicator/packeddata.ddp",
        $"{movie}/{movie}.lm",
        LumenMatrix.Identity with { X = x, Y = y },
        IndicatorParts.HostId);

    private static AudioStreamTransport? startGameplayAudio(
        AudioEngine? audioEngine,
        TjaCatalogProvider tja,
        PlayRequest request,
        GameplayTimeline timeline,
        TimeSpan duration)
    {
        if (audioEngine is null || request.AudioAsset is not { } audioAsset)
            return null;
        var inputStream = tja.OpenReadAsync(audioAsset).AsTask().GetAwaiter().GetResult();
        BufferedAudioSource? source = null;
        try
        {
            try
            {
                source = new BufferedAudioSource(inputStream, audioEngine.Mixer.Format);
            }
            catch
            {
                inputStream.Dispose();
                throw;
            }
            source.Ready.GetAwaiter().GetResult();
            var playback = audioEngine.PlayTransport(new ScheduledAudioSource(source,
                timeline.AudioStart, timeline.LeadIn + duration + TimeSpan.FromSeconds(1)));
            source = null;
            return playback;
        }
        finally
        {
            source?.Dispose();
        }
    }

    private static string? findJingle(string? soundRoot, string fileName)
    {
        if (soundRoot is null)
            return null;
        var path = Path.Combine(Path.GetFullPath(soundRoot), "bgm", "nub", fileName);
        return File.Exists(path) ? path : null;
    }

    private static LumenGameSceneInstance requireLumenScene(GameFlowCoordinator coordinator) =>
        coordinator.ActiveScene as LumenGameSceneInstance
        ?? throw new InvalidOperationException("The active scene is not a Lumen scene instance.");

    private static RenderTextureId[] uploadTextures(SdlApplication application, LumenGameSceneInstance scene)
    {
        var uploaded = new List<RenderTextureId>();
        try
        {
            foreach (var texture in scene.Textures)
                uploaded.Add(application.UploadRgba8(checked((uint)texture.Width),
                    checked((uint)texture.Height), texture.Rgba8.AsSpan()));
            return [.. uploaded];
        }
        catch
        {
            releaseTextures(application, uploaded);
            throw;
        }
    }

    private static void releaseTextures(SdlApplication application, IEnumerable<RenderTextureId> textures)
    {
        foreach (var texture in textures)
            application.ReleaseTexture(texture);
    }

    private static void reportDiagnostics(LumenGameSceneInstance scene)
    {
        foreach (var layer in scene.Layers.Select((loaded, index) => (loaded, index)))
        {
            foreach (var diagnostic in scene.Player.Layers[layer.index].Player.Diagnostics)
            {
                Console.WriteLine(
                    $"{layer.loaded.Definition.MovieId}: {diagnostic.Severity} {diagnostic.Code} "
                    + $"at character {diagnostic.CharacterId} frame {diagnostic.Frame}: {diagnostic.Message}");
            }
        }
    }

    private sealed class CatalogHostFactory(
        GameFlowSession flow,
        SongSelectCatalogView catalog,
        ISongBoardTextureService textures,
        ISongPreviewController previews,
        ISongSelectSoundController sounds,
        ILumenFrontendServices frontend,
        IDonPresentationController? don,
        IPlayRequestSink playRequests,
        Func<TaikoSongInfo> songInfo,
        bool countdown,
        Func<TaikoPlayResult?> playResult) : ILumenLayerHostFactory
    {
        private readonly GameFlowSession _flow = flow;
        private readonly SongSelectCatalogView _catalog = catalog;
        private readonly ISongBoardTextureService _textures = textures;
        private readonly ISongPreviewController _previews = previews;
        private readonly ISongSelectSoundController _sounds = sounds;
        private readonly ILumenFrontendServices _frontend = frontend;
        private readonly IDonPresentationController? _don = don;
        private readonly IPlayRequestSink _playRequests = playRequests;

        public LumenLayerHost Create(SceneLayerDefinition layer)
        {
            // A front-end scene movie starts the indicator parts its following layers attach to.
            if (layer.HostId == "player-entry")
            {
                _parts = new IndicatorParts(IndicatorPartsScene.Entry, countdown);
                _entry = new EntrySceneHost(_parts)
                {
                    CostumeIcon = static (type, id, name) => type is < 0 or > 2 ? null
                        : name ? CostumeIconTextures.NameKey(type, id) : CostumeIconTextures.Key(type, id),
                };
            }
            else if (layer.HostId == "song-select")
                _parts = new IndicatorParts(IndicatorPartsScene.SongSelect, countdown);
            return createHost(layer);
        }

        private LumenLayerHost createHost(SceneLayerDefinition layer) => layer.HostId switch
        {
            "player-entry" => entryHost(_entry!),
            IndicatorParts.HostId => partHost(_parts
                ?? throw new InvalidOperationException("Indicator part loaded without its scene movie."),
                Path.GetFileNameWithoutExtension(layer.MovieId)),
            "song-select" => createSongSelectHost(),
            GameplaySceneComposition.StaticHostId => new LumenLayerHost(new TaikoGameplayHostBinding(songInfo)),
            RainbowTransitionComposition.StaticHostId => new LumenLayerHost(null),
            SystemIndicators.HostId => new LumenLayerHost(null),
            "result" => resultHost(),
            "waitinput" => new LumenLayerHost(null), // the game never calls its Start (traced)
            "retry" => retryHost(),
            "gameover" => gameOverHost(),
            "attract" => attractHost(),
            "boot" => new LumenLayerHost(null),
            _ => throw new KeyNotFoundException($"No Lumen host is configured for '{layer.HostId}'."),
        };

        private LumenLayerHost resultHost()
        {
            var play = playResult() ?? throw new InvalidOperationException("Results loaded without a finished play.");
            var stage = songInfo().Stage;
            // ponytail: guest name until profiles exist (traced default どんちゃん).
            var binding = new ResultHostBinding(() => play, "どんちゃん", stage,
                TaikoCredit.EndMessage(stage, play.Cleared), _don, _sounds as IResultSoundController);
            return new LumenLayerHost(binding, binding.Attach);
        }

        /// <summary>The loaded revival scene's host (the flow polls its outcome).</summary>
        public RetryGameHostBinding? Retry { get; private set; }

        /// <summary>The loaded game-over scene's host.</summary>
        public GameOverHostBinding? GameOver { get; private set; }

        private LumenLayerHost retryHost()
        {
            var binding = Retry = new RetryGameHostBinding(_don, _sounds as IRetrySoundController);
            return new LumenLayerHost(binding, binding.Attach);
        }

        private LumenLayerHost gameOverHost()
        {
            var binding = GameOver = new GameOverHostBinding(_sounds as IGameOverSoundController);
            return new LumenLayerHost(binding, binding.Attach);
        }

        /// <summary>The loaded attract movie's host.</summary>
        public AttractHostBinding? Attract { get; private set; }

        private LumenLayerHost attractHost()
        {
            var binding = Attract = new AttractHostBinding(_sounds as IAttractSoundController);
            return new LumenLayerHost(binding);
        }

        private EntrySceneHost? _entry;
        private IndicatorParts? _parts;

        private LumenLayerHost entryHost(EntrySceneHost entry) => new(
            new LumenFrontendHostBinding(_frontend, _flow, _don, entry),
            player =>
            {
                initializeEntry(player);
                entry.AttachEntry(player);
            });

        private static LumenLayerHost partHost(IndicatorParts parts, string movie) =>
            new(null, player => parts.Attach(movie, player));

        private void initializeEntry(LumenPlayer player)
        {
            if (_don is not null)
                DonLumenBinding.Attach(player, _don, DonPresentationLayout.OpposedPlayers);
            // SetPrevious(scene, trigger): entry starts as if P1 hit the drum in the attract loop
            // (SCENE_TRIGGER_DON_1P = 0), the free-play path; the movie then joins P1 via EntryCoin.
            // ponytail: the attract loop only leaves on a P1 drum hit, so the trigger is fixed.
            if (!player.TryInvokeCallback("SetPrevious", [
                    LumenHostValue.FromNumber(0),
                    LumenHostValue.FromNumber(0)]))
            {
                throw new InvalidOperationException("Entry did not export SetPrevious.");
            }
            if (!player.TryInvokeCallback("SetPlayer", [
                    LumenHostValue.FromNumber(0),
                    LumenHostValue.FromBoolean(false),
                    LumenHostValue.FromBoolean(false),
                    LumenHostValue.FromBoolean(false)]))
            {
                throw new InvalidOperationException("Entry did not export SetPlayer.");
            }
        }

        private LumenLayerHost createSongSelectHost()
        {
            var binding = new SongSelectHostBinding(
                new SongSelectSession(
                    _catalog,
                    _textures,
                    _previews,
                    _playRequests),
                _sounds,
                _don,
                parts: _parts);
            return new LumenLayerHost(binding, binding.Attach);
        }
    }

    private sealed class DonPresentationController(SdlDonRenderer renderer) : IDonPresentationController
    {
        private readonly SdlDonRenderer _renderer = renderer;
        private readonly LumenNativeSurfaceKey[] _surfaces =
        [
            new("don:0"),
            new("don:1"),
        ];

        public void Reset(DonPresentationLayout layout) =>
            _renderer.Reset(mirrorPlayerTwoCamera: layout != DonPresentationLayout.OpposedPlayers,
                layout: cameraLayout(layout));

        public void SetCameraLayout(DonPresentationLayout layout) => _renderer.SetCameraLayout(cameraLayout(layout));

        private static DonCameraLayout cameraLayout(DonPresentationLayout layout) => layout switch
        {
            DonPresentationLayout.Gameplay => DonCameraLayout.Gameplay,
            DonPresentationLayout.Retry => DonCameraLayout.Retry,
            DonPresentationLayout.RetrySuccess => DonCameraLayout.RetrySuccess,
            _ => DonCameraLayout.Standard,
        };

        public LumenNativeSurfaceKey GetSurface(int playerIndex) => _surfaces[playerIndex];

        public void SetMotion(DonMotionRequest request) =>
            _renderer.SetMotion(request.PlayerIndex, request.OneShot, request.Loop);

        public void SetIdle(int playerIndex, string loop) => _renderer.SetIdle(playerIndex, loop);

        public void SetCostume(int playerIndex, DonCostume costume)
        {
            try
            {
                _renderer.SetCostume(playerIndex, costume.Whole, costume.Head, costume.Body, costume.Paint);
            }
            catch (FileNotFoundException exception)
            {
                // A costume the installed data lacks keeps the current look.
                Console.Error.WriteLine($"Don costume {costume}: {exception.Message}");
            }
        }

        public RenderTextureId? Resolve(LumenNativeSurfaceKey surface)
        {
            for (var index = 0; index < _surfaces.Length; index++)
                if (surface == _surfaces[index])
                    return _renderer.GetTexture(index);
            return null;
        }
    }

    private sealed class ViewerFrontendServices(AuthoredSoundController? sounds) : ILumenFrontendServices
    {
        private readonly AuthoredSoundController? _sounds = sounds;

        public bool IsReady => true;

        public bool IsStartLumen => true;

        public bool IsFreePlay => true; // traced: the stock setup answers 1

        public void Initialize()
        {
        }

        public bool TryEnterPlayer() => true;

        public void RequestSound(LumenFrontendSoundRequest request)
        {
            if (_sounds is not null)
            {
                _sounds.RequestSound(request);
                return;
            }
            Console.WriteLine(
                $"Lumen.{request.Kind}({string.Join(", ", request.Arguments.Select(formatHostValue))})");
        }

        public void StopVoice(int? cue = null)
        {
            if (_sounds is null)
                Console.WriteLine($"Lumen.StopVoice({cue})");
            else if (cue is { } number)
                _sounds.StopVoice(number);
            else
                _sounds.StopVoice();
        }

        public LumenHostValue CallExternalInterface(LumenHostCall hostCall)
        {
            Console.WriteLine(
                $"ExternalInterface.call({string.Join(", ", hostCall.Arguments.Select(formatHostValue))})");
            return LumenHostValue.Undefined;
        }
    }

    private sealed class TraceSoundController : ISongSelectSoundController
    {
        public static TraceSoundController Instance { get; } = new();

        public void RequestSound(SongSelectSoundRequest request)
        {
            Console.WriteLine(
                $"Lumen.{request.Kind}({string.Join(", ", request.Arguments.Select(formatHostValue))})");
        }

        public void SelectCategoryVoice(string category) =>
            Console.WriteLine($"Category voice requested for \"{category}\".");

        public void StopVoice() => Console.WriteLine("Lumen.StopVoice()");
    }

    private sealed class TracePreviewController : ISongPreviewController
    {
        public static TracePreviewController Instance { get; } = new();

        public void SetPreview(SongPreviewRequest? request)
        {
            if (request is not null)
                Console.WriteLine($"Song preview requested at {request.Start.TotalSeconds:0.###}s for {request.Song}.");
        }
    }

    private static string formatHostValue(LumenHostValue value) => value.Kind switch
    {
        LumenHostValueKind.Undefined => "undefined",
        LumenHostValueKind.Null => "null",
        LumenHostValueKind.Boolean => value.AsBoolean() ? "true" : "false",
        LumenHostValueKind.Number => value.AsNumber().ToString("G15", System.Globalization.CultureInfo.InvariantCulture),
        LumenHostValueKind.Text => $"\"{value.AsString()}\"",
        _ => "undefined",
    };
}
