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
        string? jinglePath)
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
        using var audioDevice = jinglePath is null ? null : new SdlAudioDevice();
        using var audioEngine = jinglePath is null ? null : new AudioEngine(audioDevice!);
        if (audioEngine is not null)
        {
            audioEngine.PlayOneShot(jinglePath!, AudioBus.MenuSound);
            Console.WriteLine(
                $"Menu jingle: {Path.GetFileName(jinglePath)} via {audioDevice!.Driver}, " +
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
            new CatalogHostFactory(flow, songCatalog, titleTextures));
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

                    reportDiagnostics(active);
                    coordinator.ApplyPendingTransitionAsync().AsTask().GetAwaiter().GetResult();
                    active = requireLumenScene(coordinator);
                    textureIds = uploadTextures(application, active);
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
        ISongBoardTextureService textures) : ILumenLayerHostFactory
    {
        private readonly GameFlowSession _flow = flow;
        private readonly SongSelectCatalogView _catalog = catalog;
        private readonly ISongBoardTextureService _textures = textures;

        public LumenLayerHost Create(SceneLayerDefinition layer) => layer.HostId switch
        {
            "player-entry" => new LumenLayerHost(
                new LumenFrontendHostBinding(ViewerFrontendServices.Instance, _flow),
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
            var binding = new SongSelectHostBinding(new SongSelectSession(
                _catalog,
                _textures,
                ConsolePreviewController.Instance));
            return new LumenLayerHost(binding, binding.Attach);
        }
    }

    private sealed class ViewerFrontendServices : ILumenFrontendServices
    {
        public static ViewerFrontendServices Instance { get; } = new();

        public bool IsReady => true;

        public bool IsStartLumen => true;

        public bool IsFreePlay => false;

        public void Initialize()
        {
        }

        public bool TryEnterPlayer() => true;

        public void StopVoice()
        {
        }

        public LumenHostValue CallExternalInterface(LumenHostCall hostCall)
        {
            Console.WriteLine($"ExternalInterface.call({hostCall.Arguments.Length} arguments)");
            return LumenHostValue.Undefined;
        }
    }

    private sealed class ConsolePreviewController : ISongPreviewController
    {
        public static ConsolePreviewController Instance { get; } = new();

        public void SetPreview(SongPreviewRequest? request)
        {
            if (request is null)
                Console.WriteLine("Song preview stopped.");
            else
                Console.WriteLine($"Song preview requested at {request.Start.TotalSeconds:0.###}s for {request.Song}.");
        }
    }
}
