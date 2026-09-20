using Waddamburo.Game.Flow;
using Waddamburo.Game.Lumen;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

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
        SdlKeyboardTimeline inputTimeline)
    {
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
            new ProbeHostFactory(flow));
        var coordinator = new GameFlowCoordinator(catalog, loader, flow);
        coordinator.StartAsync(entryId).AsTask().GetAwaiter().GetResult();

        try
        {
            using var application = new SdlApplication(
                "Waddamburo — Entry → Song Select",
                windowWidth,
                windowHeight,
                debugGpu: false,
                resizable: screenshotPath is null,
                highPixelDensity: screenshotPath is null);
            Console.WriteLine($"SDL_GPU driver: {application.GpuDriver}");
            var active = requireLumenScene(coordinator);
            var textureIds = uploadTextures(application, active);
            var simulationTick = 0;

            RenderFrame createFrame(double interpolationFraction) => LumenRenderFrameAdapter.Compose(
                active.Player.CreateRenderSnapshot((float)interpolationFraction),
                RenderColor.WaddamburoBlue,
                index => index < textureIds.Length
                    ? textureIds[index]
                    : throw new InvalidDataException($"Scene snapshot references missing texture {index}."));

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

    private sealed class ProbeHostFactory(GameFlowSession flow) : ILumenLayerHostFactory
    {
        private readonly GameFlowSession _flow = flow;

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

        private static LumenLayerHost createSongSelectHost()
        {
            var binding = new SongSelectProbeHostBinding();
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

    private sealed class SongSelectProbeHostBinding : ILumenHostBinding
    {
        private static readonly string[] NotificationMethods =
        [
            "SetMotion", "RequestSE", "RequestSystemSE", "NotifyGenreFolder",
            "NotifyOpenFolder", "NotifyCloseFolder", "NotifyStopBGM",
        ];

        private LumenPlayer? _player;
        private bool _assigned;

        public void Attach(LumenPlayer player) => _player = player;

        public void Install(LumenHostContext context)
        {
            context.RegisterExternalInterfaceCall(_ => LumenHostValue.Undefined);
            context.RegisterObject("Lumen", lumen =>
            {
                lumen.RegisterMethod("GetMusicData", _ => LumenHostValue.FromBoolean(true));
                lumen.RegisterMethod("GetPlayerData", _ => LumenHostValue.FromBoolean(true));
                lumen.RegisterMethod("IsStart", _ => LumenHostValue.FromBoolean(true));
                lumen.RegisterMethod("IsInitWait", _ => LumenHostValue.FromBoolean(assignInitialData()));
                lumen.RegisterMethod("GetMusicInfo_Basic", _ =>
                {
                    invoke("SetMusicData", LumenHostValue.FromNumber(0), LumenHostValue.FromNumber(0));
                    invoke("SetPlayerBits", LumenHostValue.FromNumber(0), LumenHostValue.FromNumber(0));
                    return LumenHostValue.Undefined;
                });
                lumen.RegisterMethod("RequestSongBoardTexture_Short", updateBoard);
                lumen.RegisterMethod("RequestSongBoardTexture_Long", updateBoard);
                foreach (var name in NotificationMethods)
                {
                    lumen.RegisterMethod(name, _ => LumenHostValue.Undefined);
                }
            });
        }

        private bool assignInitialData()
        {
            if (_assigned)
                return true;
            if (_player is null)
                return false;
            _assigned = true;
            invoke(
                "AssignMusic",
                LumenHostValue.FromString("J-POP"),
                LumenHostValue.FromNumber(1),
                LumenHostValue.FromNumber(0),
                LumenHostValue.FromNumber(-1));
            invoke("SetSelectedMusic", LumenHostValue.FromNumber(0), LumenHostValue.FromNumber(-1));
            invoke(
                "SetPlayer",
                LumenHostValue.FromNumber(0),
                LumenHostValue.FromBoolean(true),
                LumenHostValue.FromBoolean(true),
                LumenHostValue.FromNumber(30));
            return true;
        }

        private LumenHostValue updateBoard(LumenHostCall hostCall)
        {
            if (!hostCall.Arguments.IsEmpty)
                invoke("UpdateMusicBoard", hostCall.Arguments[0]);
            return LumenHostValue.Undefined;
        }

        private void invoke(string name, params LumenHostValue[] arguments)
        {
            if (_player is null || !_player.TryInvokeCallback(name, arguments))
                throw new InvalidOperationException($"Song Select did not accept callback '{name}'.");
        }
    }
}
