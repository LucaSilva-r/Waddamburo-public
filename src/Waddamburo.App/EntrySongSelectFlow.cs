using Waddamburo.Catalog;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Lumen;
using Waddamburo.Game.Scenes;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Runtime;
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
        int windowWidth,
        int windowHeight,
        int? frameLimit,
        int? tickLimit,
        string? screenshotPath,
        SdlKeyboardTimeline inputTimeline,
        string tjaRoot,
        string fontPath,
        string? jinglePath,
        string? soundRoot)
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
            "Waddamburo — Entry → Song Select",
            windowWidth,
            windowHeight,
            debugGpu: false,
            resizable: screenshotPath is null,
            highPixelDensity: screenshotPath is null);
        Console.WriteLine($"SDL_GPU driver: {application.GpuDriver}");
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
        if (entryJinglePath is not null)
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

        var entryId = new SceneId("entry");
        var songSelectId = new SceneId("song-select");
        var flow = new GameFlowSession();
        var catalog = new SceneCatalog(
            [
                new SceneDefinition(SceneDefinition.CurrentVersion, entryId, [
                    new SceneLayerDefinition(
                        "entry/packeddata.ddp",
                        "entry/entry.lm",
                        LumenMatrix.Identity,
                        "player-entry"),
                ]),
                new SceneDefinition(SceneDefinition.CurrentVersion, songSelectId, [
                    new SceneLayerDefinition(
                        "song_select/packeddata.ddp",
                        "song_select/song_select.lm",
                        LumenMatrix.Identity,
                        "song-select"),
                ]),
            ],
            [new SceneTransitionRoute(entryId, new LumenSceneRequest(1, 0, 0), songSelectId)]);
        var loader = new LumenGameSceneLoader(
            new DirectoryLumenMovieContentSource(Path.GetFullPath(assetRoot)),
            new CatalogHostFactory(
                flow,
                songCatalog,
                titleTextures,
                previewController is null ? TracePreviewController.Instance : previewController,
                soundController is null ? TraceSoundController.Instance : soundController,
                new ViewerFrontendServices(soundController)));
        var coordinator = new GameFlowCoordinator(catalog, loader, flow);
        coordinator.StartAsync(entryId).AsTask().GetAwaiter().GetResult();

        try
        {
            var active = requireLumenScene(coordinator);
            var textureIds = uploadTextures(application, active);
            var simulationTick = 0;

            RenderFrame createFrame(double interpolationFraction)
            {
                titleTextures.UploadCompleted();
                return LumenRenderFrameAdapter.Compose(
                    active.Player.CreateRenderSnapshot((float)interpolationFraction),
                    RenderColor.WaddamburoBlue,
                    index => index < textureIds.Length
                        ? textureIds[index]
                        : throw new InvalidDataException($"Scene snapshot references missing texture {index}."),
                    titleTextures.Resolve);
            }

            var result = application.Run(
                createFrame,
                keyboard =>
                {
                    simulationTick++;
                    var input = LumenInputAdapter.CreateSnapshot(inputTimeline.Apply(simulationTick, keyboard));
                    active.Player.Advance(input);
                    if (coordinator.Flow.State != GameFlowState.TransitionPending)
                        return;
                    if (soundController?.IsVoicePlaying == true)
                        return;

                    reportDiagnostics(active);
                    coordinator.ApplyPendingTransitionAsync().AsTask().GetAwaiter().GetResult();
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
                screenshotPath is null ? null : capture => ScreenshotWriter.Write(screenshotPath, capture));

            Console.WriteLine($"Active scene: {active.Id}");
            reportDiagnostics(active);
            if (result.DroppedTicks > 0)
                Console.Error.WriteLine($"Warning PLT_DROPPED_TICKS: dropped {result.DroppedTicks} simulation ticks.");
            return result.RenderedFrames;
        }
        finally
        {
            coordinator.StopAsync().AsTask().GetAwaiter().GetResult();
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

    private static RenderTextureId[] uploadTextures(SdlApplication application, LumenGameSceneInstance scene) =>
        [.. scene.Textures.Select(texture => application.UploadRgba8(
            checked((uint)texture.Width),
            checked((uint)texture.Height),
            texture.Rgba8.AsSpan()))];

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
        ILumenFrontendServices frontend) : ILumenLayerHostFactory
    {
        private readonly GameFlowSession _flow = flow;
        private readonly SongSelectCatalogView _catalog = catalog;
        private readonly ISongBoardTextureService _textures = textures;
        private readonly ISongPreviewController _previews = previews;
        private readonly ISongSelectSoundController _sounds = sounds;
        private readonly ILumenFrontendServices _frontend = frontend;

        public LumenLayerHost Create(SceneLayerDefinition layer) => layer.HostId switch
        {
            "player-entry" => new LumenLayerHost(
                new LumenFrontendHostBinding(_frontend, _flow),
                initializeEntry),
            "song-select" => createSongSelectHost(),
            _ => throw new KeyNotFoundException($"No Lumen host is configured for '{layer.HostId}'."),
        };

        private static void initializeEntry(LumenPlayer player)
        {
            if (!player.TryInvokeCallback("SetPrevious", [
                    LumenHostValue.FromNumber(0),
                    LumenHostValue.FromNumber(-1)]))
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
                    _previews),
                _sounds);
            return new LumenLayerHost(binding, binding.Attach);
        }
    }

    private sealed class ViewerFrontendServices(AuthoredSoundController? sounds) : ILumenFrontendServices
    {
        private readonly AuthoredSoundController? _sounds = sounds;

        public bool IsReady => true;

        public bool IsStartLumen => true;

        public bool IsFreePlay => false;

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

        public void StopVoice()
        {
            _sounds?.StopVoice();
            if (_sounds is null)
                Console.WriteLine("Lumen.StopVoice()");
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
