using System.Collections.Immutable;
using Waddamburo.Game.Don;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Tests;

public sealed class GameFlowSessionTests
{
    [Fact]
    public void RainbowTransitionWaitsForAuthoredCoverAndRevealAnimations()
    {
        var transition = new RainbowTransitionSequence(leadInTicks: 2, coveredTicks: 3);

        transition.Begin(10);
        Assert.Equal(RainbowTransitionState.WaitingToCover, transition.State);
        Assert.False(transition.ShouldStartCover(11));
        Assert.True(transition.ShouldStartCover(12));

        transition.StartCover();
        Assert.False(transition.FinishCoverWhenStopped(animationIsPlaying: true, 20));
        Assert.True(transition.FinishCoverWhenStopped(animationIsPlaying: false, 21));
        Assert.Equal(RainbowTransitionState.Covered, transition.State);
        Assert.False(transition.ShouldStartReveal(23));
        Assert.True(transition.ShouldStartReveal(24));

        transition.StartReveal();
        Assert.False(transition.FinishRevealWhenStopped(animationIsPlaying: true));
        Assert.True(transition.FinishRevealWhenStopped(animationIsPlaying: false));
        Assert.Equal(RainbowTransitionState.Complete, transition.State);
    }

    [Fact]
    public void RainbowTransitionRejectsOverlappingSequences()
    {
        var transition = new RainbowTransitionSequence();

        transition.Begin(0);

        Assert.Throws<InvalidOperationException>(() => transition.Begin(1));
        Assert.Throws<InvalidOperationException>(transition.StartReveal);
    }

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
    public void HostRestartCanReplaceAPendingAuthoredTransition()
    {
        var flow = activeSession("result");
        Assert.True(flow.TryRequestTransition(new LumenSceneRequest(1, 0, 0)));

        flow.CancelPendingTransition();
        flow.LoadScene(new SceneId("gameplay"));

        Assert.Null(flow.PendingTransition);
        Assert.Equal(GameFlowState.Loading, flow.State);
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
    public void AttachingDonToANewMovieResetsPreviousSceneState()
    {
        var don = new SyntheticDonPresentation();
        var player = new LumenPlayer(TestMovies.TransitionMovie(), 1280, 720);

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

        Assert.True(coordinator.Flow.TryRequestTransition(new LumenSceneRequest(1, 0, 0)));
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

    [Theory]
    [InlineData("entry")]
    [InlineData("song-select")]
    public async Task HostSelectedSceneReplacesPendingAuthoredRequest(string targetName)
    {
        var loader = new SyntheticSceneLoader();
        await using var coordinator = new GameFlowCoordinator(createCatalog(includeRoute: true), loader);
        await coordinator.StartAsync(new SceneId("entry"));
        var previous = Assert.IsType<SyntheticSceneInstance>(coordinator.ActiveScene);
        Assert.True(coordinator.Flow.TryRequestTransition(new LumenSceneRequest(1, 0, 0)));
        var target = new SceneId(targetName);

        await coordinator.TransitionToAsync(target);

        Assert.Equal(GameFlowState.Active, coordinator.Flow.State);
        Assert.Equal(target, coordinator.Flow.CurrentScene);
        Assert.Equal(target, coordinator.ActiveScene!.Id);
        Assert.Null(coordinator.Flow.PendingTransition);
        Assert.NotSame(previous, coordinator.ActiveScene);
        Assert.Equal(1, previous.DisposeCount);
        Assert.Equal([new SceneId("entry"), target], loader.LoadedIds);
    }

    [Fact]
    public async Task UnknownHostSceneKeepsThePendingAuthoredRequest()
    {
        var loader = new SyntheticSceneLoader();
        await using var coordinator = new GameFlowCoordinator(createCatalog(includeRoute: true), loader);
        await coordinator.StartAsync(new SceneId("entry"));
        var previous = Assert.IsType<SyntheticSceneInstance>(coordinator.ActiveScene);
        var request = new LumenSceneRequest(1, 0, 0);
        Assert.True(coordinator.Flow.TryRequestTransition(request));

        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await coordinator.TransitionToAsync(new SceneId("unknown")));

        Assert.Equal(GameFlowState.TransitionPending, coordinator.Flow.State);
        Assert.Equal(request, coordinator.Flow.PendingTransition);
        Assert.Same(previous, coordinator.ActiveScene);
        Assert.Equal(0, previous.DisposeCount);
        Assert.Equal([new SceneId("entry")], loader.LoadedIds);
    }

    [Fact]
    public async Task CancelledHostTransitionDoesNotReviveTheSupersededMovieRequest()
    {
        var loader = new SyntheticSceneLoader();
        await using var coordinator = new GameFlowCoordinator(createCatalog(includeRoute: true), loader);
        await coordinator.StartAsync(new SceneId("entry"));
        var previous = Assert.IsType<SyntheticSceneInstance>(coordinator.ActiveScene);
        Assert.True(coordinator.Flow.TryRequestTransition(new LumenSceneRequest(1, 0, 0)));
        loader.CancelNextLoad = true;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await coordinator.TransitionToAsync(new SceneId("entry"), cancellation.Token));

        Assert.Equal(GameFlowState.Active, coordinator.Flow.State);
        Assert.Null(coordinator.Flow.PendingTransition);
        Assert.Same(previous, coordinator.ActiveScene);
        Assert.Equal(0, previous.DisposeCount);
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
