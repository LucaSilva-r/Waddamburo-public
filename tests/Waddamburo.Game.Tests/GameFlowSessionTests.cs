using System.Buffers.Binary;
using System.Collections.Immutable;
using Waddamburo.Formats.Lmb;
using Waddamburo.Game.Don;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Lumen;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Tests;

public sealed class GameFlowSessionTests
{
    [Fact]
    public void AuthoredRequestWaitsForResolvedSceneToLoadBeforeActivation()
    {
        var flow = new GameFlowSession();
        var entry = new SceneId("entry");
        var songSelect = new SceneId("song-select");

        flow.Start(entry);
        Assert.Equal(GameFlowState.Loading, flow.State);
        Assert.Equal(entry, flow.LoadingScene);

        flow.ActivateLoadedScene();
        Assert.Equal(GameFlowState.Active, flow.State);
        Assert.Equal(entry, flow.CurrentScene);

        var request = new LumenSceneRequest(1, 0, 0);
        Assert.True(flow.TryRequestTransition(request));
        Assert.Equal(GameFlowState.TransitionPending, flow.State);
        Assert.Equal(request, flow.PendingTransition);
        Assert.False(flow.TryRequestTransition(new LumenSceneRequest(2, 0, 0)));

        flow.LoadTransitionTarget(songSelect);
        Assert.Equal(GameFlowState.Loading, flow.State);
        Assert.Equal(entry, flow.CurrentScene);
        Assert.Equal(songSelect, flow.LoadingScene);
        Assert.Null(flow.PendingTransition);

        flow.ActivateLoadedScene();
        Assert.Equal(GameFlowState.Active, flow.State);
        Assert.Equal(songSelect, flow.CurrentScene);
        Assert.Null(flow.LoadingScene);
    }

    [Fact]
    public void RestartLoadsAFreshInstanceOfTheCurrentScene()
    {
        var flow = activeSession("gameplay");

        flow.RestartCurrentScene();

        Assert.Equal(GameFlowState.Loading, flow.State);
        Assert.Equal(new SceneId("gameplay"), flow.CurrentScene);
        Assert.Equal(new SceneId("gameplay"), flow.LoadingScene);
    }

    [Fact]
    public void FailureAndStopClearTransientState()
    {
        var flow = activeSession("entry");
        Assert.True(flow.TryRequestTransition(new LumenSceneRequest(1, 0, 0)));
        var failure = new InvalidDataException("synthetic load failure");

        flow.Fail(failure);

        Assert.Equal(GameFlowState.Failed, flow.State);
        Assert.Same(failure, flow.Failure);
        Assert.Null(flow.PendingTransition);
        Assert.Null(flow.LoadingScene);

        flow.Stop();
        Assert.Equal(GameFlowState.Stopped, flow.State);
        Assert.Null(flow.CurrentScene);
        Assert.Null(flow.Failure);
    }

    [Fact]
    public void LumenSceneRequestRequiresThreeFiniteIntegers()
    {
        var request = LumenSceneRequest.FromHostCall(call(1, 0, -1));

        Assert.Equal(new LumenSceneRequest(1, 0, -1), request);
        Assert.Throws<ArgumentException>(() => LumenSceneRequest.FromHostCall(call(1, 0)));
        Assert.Throws<ArgumentException>(() => LumenSceneRequest.FromHostCall(new LumenHostCall(
            [LumenHostValue.FromString("1"), LumenHostValue.FromNumber(0), LumenHostValue.FromNumber(0)])));
        Assert.Throws<ArgumentOutOfRangeException>(() => LumenSceneRequest.FromHostCall(call(1.5, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => LumenSceneRequest.FromHostCall(call(double.NaN, 0, 0)));
    }

    [Fact]
    public void SyntheticLumenSetNextSceneEntersTheGameFlowMachine()
    {
        var flow = activeSession("entry");
        var services = new SyntheticFrontendServices();
        var player = new LumenPlayer(
            createTransitionMovie(),
            1280,
            720,
            hostBinding: new LumenFrontendHostBinding(services, flow));

        player.Advance();

        Assert.Equal(GameFlowState.TransitionPending, flow.State);
        Assert.Equal(new LumenSceneRequest(1, 0, 0), flow.PendingTransition);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_HOST_CALL_FAILED");
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_AVM_METHOD_UNRESOLVED");
    }

    [Fact]
    public void FrontendBindingForwardsEntryDonMotionCalls()
    {
        var don = new SyntheticDonPresentation();
        var player = new LumenPlayer(
            createDonMotionMovie(),
            1280,
            720,
            hostBinding: new LumenFrontendHostBinding(
                new SyntheticFrontendServices(),
                new GameFlowSession(),
                don));

        player.Advance();

        Assert.Equal(
            new DonMotionRequest(0, "don_entry1P_out", "don_entry_loop"),
            Assert.Single(don.Requests));
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_AVM_METHOD_UNRESOLVED");
    }

    [Fact]
    public void AttachingDonToANewMovieResetsPreviousSceneState()
    {
        var don = new SyntheticDonPresentation();
        var player = new LumenPlayer(createTransitionMovie(), 1280, 720);

        DonLumenBinding.Attach(player, don);

        Assert.Equal(DonPresentationLayout.Standard, Assert.Single(don.ResetLayouts));
    }

    [Fact]
    public async Task AuthoredRequestLoadsAndSwapsItsCatalogMappedScene()
    {
        var loader = new SyntheticSceneLoader();
        await using var coordinator = new GameFlowCoordinator(createCatalog(includeRoute: true), loader);
        await coordinator.StartAsync(new SceneId("entry"));
        var entry = Assert.IsType<SyntheticSceneInstance>(coordinator.ActiveScene);
        var player = new LumenPlayer(
            createTransitionMovie(),
            1280,
            720,
            hostBinding: new LumenFrontendHostBinding(new SyntheticFrontendServices(), coordinator.Flow));

        player.Advance();
        await coordinator.ApplyPendingTransitionAsync();

        Assert.Equal(GameFlowState.Active, coordinator.Flow.State);
        Assert.Equal(new SceneId("song-select"), coordinator.Flow.CurrentScene);
        Assert.Equal(new SceneId("song-select"), coordinator.ActiveScene!.Id);
        Assert.Equal([new SceneId("entry"), new SceneId("song-select")], loader.LoadedIds);
        Assert.Equal(1, entry.DisposeCount);
    }

    [Fact]
    public async Task CancelledTransitionKeepsThePreviousSceneActive()
    {
        var loader = new SyntheticSceneLoader();
        await using var coordinator = new GameFlowCoordinator(createCatalog(includeRoute: true), loader);
        await coordinator.StartAsync(new SceneId("entry"));
        var entry = coordinator.ActiveScene;
        Assert.True(coordinator.Flow.TryRequestTransition(new LumenSceneRequest(1, 0, 0)));
        loader.CancelNextLoad = true;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await coordinator.ApplyPendingTransitionAsync(cancellation.Token));

        Assert.Equal(GameFlowState.Active, coordinator.Flow.State);
        Assert.Equal(new SceneId("entry"), coordinator.Flow.CurrentScene);
        Assert.Same(entry, coordinator.ActiveScene);
    }

    [Fact]
    public async Task MissingRouteFailsExplicitlyWithoutReplacingTheCurrentScene()
    {
        var loader = new SyntheticSceneLoader();
        await using var coordinator = new GameFlowCoordinator(createCatalog(includeRoute: false), loader);
        await coordinator.StartAsync(new SceneId("entry"));
        var entry = coordinator.ActiveScene;
        Assert.True(coordinator.Flow.TryRequestTransition(new LumenSceneRequest(99, 0, 0)));

        var failure = await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await coordinator.ApplyPendingTransitionAsync());

        Assert.Contains("no route", failure.Message, StringComparison.Ordinal);
        Assert.Equal(GameFlowState.Failed, coordinator.Flow.State);
        Assert.Same(entry, coordinator.ActiveScene);
        Assert.Equal([new SceneId("entry")], loader.LoadedIds);
    }

    [Fact]
    public async Task CoordinatorRestartBuildsAFreshSceneAndDisposesTheOldOne()
    {
        var loader = new SyntheticSceneLoader();
        await using var coordinator = new GameFlowCoordinator(createCatalog(includeRoute: true), loader);
        await coordinator.StartAsync(new SceneId("entry"));
        var first = Assert.IsType<SyntheticSceneInstance>(coordinator.ActiveScene);

        await coordinator.RestartAsync();

        Assert.Equal(GameFlowState.Active, coordinator.Flow.State);
        Assert.Equal(new SceneId("entry"), coordinator.ActiveScene!.Id);
        Assert.NotSame(first, coordinator.ActiveScene);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal([new SceneId("entry"), new SceneId("entry")], loader.LoadedIds);
    }

    [Fact]
    public async Task ProductSelectedTransitionLoadsNativeOnlyGameplayScene()
    {
        var entry = new SceneId("entry");
        var gameplay = new SceneId("gameplay");
        var catalog = new SceneCatalog(
            [
                new SceneDefinition(SceneDefinition.CurrentVersion, entry, [layer("entry-host")]),
                new SceneDefinition(SceneDefinition.CurrentVersion, gameplay, []),
            ],
            []);
        var loader = new SyntheticSceneLoader();
        await using var coordinator = new GameFlowCoordinator(catalog, loader);
        await coordinator.StartAsync(entry);
        var previous = Assert.IsType<SyntheticSceneInstance>(coordinator.ActiveScene);

        await coordinator.TransitionToAsync(gameplay);

        Assert.Equal(GameFlowState.Active, coordinator.Flow.State);
        Assert.Equal(gameplay, coordinator.Flow.CurrentScene);
        Assert.Equal(gameplay, coordinator.ActiveScene!.Id);
        Assert.Equal([entry, gameplay], loader.LoadedIds);
        Assert.Equal(1, previous.DisposeCount);
    }

    [Fact]
    public async Task LumenLoaderCreatesAnEmptyStageForNativeOnlyScenes()
    {
        var gameplay = new SceneId("gameplay");
        var loader = new LumenGameSceneLoader(new UnusedContentSource(), new UnusedHostFactory());

        var loaded = await loader.LoadAsync(
            new SceneDefinition(SceneDefinition.CurrentVersion, gameplay, []),
            CancellationToken.None);
        await using var scene = Assert.IsType<LumenGameSceneInstance>(loaded);

        Assert.Equal(gameplay, scene.Id);
        Assert.Empty(scene.Layers);
        Assert.Empty(scene.Textures);
        Assert.Empty(scene.Player.CreateRenderSnapshot().Quads);
    }

    [Fact]
    public void SceneDefinitionsRejectUnknownVersionsAndInvalidTransforms()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneDefinition(
            SceneDefinition.CurrentVersion + 1,
            new SceneId("future"),
            [layer("host")]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneLayerDefinition(
            "archive",
            "movie",
            LumenMatrix.Identity with { X = float.NaN },
            "host"));
    }

    [Fact]
    public async Task DirectoryMovieSourceRejectsRelativeRootsAndEscapingArchiveIds()
    {
        Assert.Throws<ArgumentException>(() => new DirectoryLumenMovieContentSource("relative-assets"));
        var directory = Directory.CreateTempSubdirectory("waddamburo-assets-");
        try
        {
            var source = new DirectoryLumenMovieContentSource(directory.FullName);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await source.LoadAsync("../outside.ddp", "movie.lm", CancellationToken.None));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static GameFlowSession activeSession(string sceneName)
    {
        var flow = new GameFlowSession();
        flow.Start(new SceneId(sceneName));
        flow.ActivateLoadedScene();
        return flow;
    }

    private static LumenHostCall call(params double[] values) => new(
        values.Select(LumenHostValue.FromNumber).ToImmutableArray());

    private static SceneCatalog createCatalog(bool includeRoute)
    {
        var entry = new SceneId("entry");
        var songSelect = new SceneId("song-select");
        return new SceneCatalog(
            [
                new SceneDefinition(SceneDefinition.CurrentVersion, entry, [layer("entry-host")]),
                new SceneDefinition(SceneDefinition.CurrentVersion, songSelect, [layer("song-select-host")]),
            ],
            includeRoute
                ? [new SceneTransitionRoute(entry, new LumenSceneRequest(1, 0, 0), songSelect)]
                : []);
    }

    private static SceneLayerDefinition layer(string hostId) => new(
        "green-ui",
        "synthetic-movie",
        LumenMatrix.Identity,
        hostId);

    private static LmbMovieDefinition createTransitionMovie()
    {
        var action = new byte[]
        {
            0x96, 0x14, 0x00,
                0x07, 0x00, 0x00, 0x00, 0x00,
                0x07, 0x00, 0x00, 0x00, 0x00,
                0x07, 0x01, 0x00, 0x00, 0x00,
                0x07, 0x03, 0x00, 0x00, 0x00,
            0x96, 0x03, 0x00, 0x09, 0x00, 0x00,
            0x1C,
            0x96, 0x03, 0x00, 0x09, 0x01, 0x00,
            0x52,
            0x17,
            0x00,
        };
        var file = createLmb(
            words(LmbTags.MovieProperties, 0, 0, 0, 7, 0, 0, 0, BitConverter.SingleToUInt32Bits(60)),
            record(LmbTags.StringPool, stringPool("Lumen", "SetNextScene")),
            record(LmbTags.ActionPool, actionPool(action)),
            words(LmbTags.DefineSprite, 7, 0, 0, 2, 3, 2, 0),
            words(LmbTags.ShowFrame, 0, 1),
            words(LmbTags.ShowFrame, 1, 2),
            words(LmbTags.DoAction, 0, 0));
        return LmbSemanticReader.Read(file).Value;
    }

    private static LmbMovieDefinition createDonMotionMovie()
    {
        var action = new byte[]
        {
            0x96, 0x08, 0x00,
                0x09, 0x02, 0x00,
                0x07, 0x01, 0x00, 0x00, 0x00,
            0x3C,
            0x96, 0x08, 0x00,
                0x09, 0x03, 0x00,
                0x07, 0x02, 0x00, 0x00, 0x00,
            0x3C,
            0x96, 0x14, 0x00,
                0x07, 0x02, 0x00, 0x00, 0x00,
                0x07, 0x01, 0x00, 0x00, 0x00,
                0x07, 0x00, 0x00, 0x00, 0x00,
                0x07, 0x03, 0x00, 0x00, 0x00,
            0x96, 0x03, 0x00, 0x09, 0x00, 0x00,
            0x1C,
            0x96, 0x03, 0x00, 0x09, 0x01, 0x00,
            0x52,
            0x17,
            0x00,
        };
        var file = createLmb(
            words(LmbTags.MovieProperties, 0, 0, 0, 7, 0, 0, 0, BitConverter.SingleToUInt32Bits(60)),
            record(LmbTags.StringPool, stringPool("Lumen", "SetMotion", "DON_ENTRY1P_OUT", "DON_ENTRY_LOOP")),
            record(LmbTags.ActionPool, actionPool(action)),
            words(LmbTags.DefineSprite, 7, 0, 0, 2, 3, 2, 0),
            words(LmbTags.ShowFrame, 0, 1),
            words(LmbTags.ShowFrame, 1, 2),
            words(LmbTags.DoAction, 0, 0));
        return LmbSemanticReader.Read(file).Value;
    }

    private static LmbFile createLmb(params (uint Tag, byte[] Payload)[] records)
    {
        using var stream = new MemoryStream();
        stream.Write("LMB\0"u8);
        stream.Write(new byte[LmbFile.HeaderLength - 4]);
        foreach (var (tag, payload) in records)
        {
            writeUInt32(stream, tag);
            writeUInt32(stream, checked((uint)payload.Length / 4));
            stream.Write(payload);
        }
        return LmbFile.Parse(stream.ToArray());
    }

    private static (uint Tag, byte[] Payload) record(uint tag, byte[] payload) => (tag, payload);

    private static (uint Tag, byte[] Payload) words(uint tag, params uint[] values)
    {
        var payload = new byte[values.Length * 4];
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(index * 4, 4), values[index]);
        return (tag, payload);
    }

    private static byte[] actionPool(byte[] action)
    {
        using var stream = new MemoryStream();
        writeUInt32(stream, 1);
        writeUInt32(stream, checked((uint)action.Length));
        stream.Write(action);
        while (stream.Position % 4 != 0)
            stream.WriteByte(0);
        return stream.ToArray();
    }

    private static byte[] stringPool(params string[] values)
    {
        using var stream = new MemoryStream();
        writeUInt32(stream, checked((uint)values.Length));
        foreach (var value in values)
        {
            var encoded = System.Text.Encoding.UTF8.GetBytes(value);
            writeUInt32(stream, checked((uint)encoded.Length));
            stream.Write(encoded);
            stream.WriteByte(0);
            while (stream.Position % 4 != 0)
                stream.WriteByte(0);
        }
        return stream.ToArray();
    }

    private static void writeUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private sealed class SyntheticFrontendServices : ILumenFrontendServices
    {
        public bool IsReady => true;

        public bool IsStartLumen => true;

        public bool IsFreePlay => false;

        public void Initialize()
        {
        }

        public bool TryEnterPlayer() => true;

        public void RequestSound(LumenFrontendSoundRequest request)
        {
        }

        public void StopVoice()
        {
        }

        public LumenHostValue CallExternalInterface(LumenHostCall hostCall) => LumenHostValue.Undefined;
    }

    private sealed class SyntheticDonPresentation : IDonPresentationController
    {
        public List<DonMotionRequest> Requests { get; } = [];

        public List<DonPresentationLayout> ResetLayouts { get; } = [];

        public void Reset(DonPresentationLayout layout) => ResetLayouts.Add(layout);

        public LumenNativeSurfaceKey GetSurface(int playerIndex) => new($"don:{playerIndex}");

        public void SetMotion(DonMotionRequest request) => Requests.Add(request);
    }

    private sealed class SyntheticSceneLoader : IGameSceneLoader
    {
        public List<SceneId> LoadedIds { get; } = [];

        public bool CancelNextLoad { get; set; }

        public ValueTask<IGameSceneInstance> LoadAsync(
            SceneDefinition definition,
            CancellationToken cancellationToken)
        {
            if (CancelNextLoad)
            {
                CancelNextLoad = false;
                cancellationToken.ThrowIfCancellationRequested();
            }
            LoadedIds.Add(definition.Id);
            return ValueTask.FromResult<IGameSceneInstance>(new SyntheticSceneInstance(definition.Id));
        }
    }

    private sealed class UnusedContentSource : ILumenMovieContentSource
    {
        public ValueTask<LumenMovieContent> LoadAsync(
            string archiveId,
            string movieId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A native-only scene must not load authored content.");
    }

    private sealed class UnusedHostFactory : ILumenLayerHostFactory
    {
        public LumenLayerHost Create(SceneLayerDefinition layer) =>
            throw new InvalidOperationException("A native-only scene must not create a Lumen host.");
    }

    private sealed class SyntheticSceneInstance(SceneId id) : IGameSceneInstance
    {
        public SceneId Id { get; } = id;

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
