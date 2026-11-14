using Waddamburo.Game.Don;
using Waddamburo.Game.Flow;
using Waddamburo.Game.Tests;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Skins.Green.Tests;

/// <summary>The game's frontend and result bindings: scene requests and Don-chan motions from the movie.</summary>
public sealed class FrontendBindingTests
{
    [Fact]
    public void SyntheticLumenSetNextSceneEntersTheGameFlowMachine()
    {
        var flow = activeSession("entry");
        var services = new SyntheticFrontendServices();
        var player = new LumenPlayer(
            TestMovies.TransitionMovie(),
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
            TestMovies.DonMotionMovie(),
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
    public void FrontendBindingWithDonAndEntryRegistersEachHostMethodOnce()
    {
        // The game's entry wiring: Don calls (ChangeAccessory...) and the entry host's own must not collide.
        var player = new LumenPlayer(TestMovies.DonMotionMovie(), 1280, 720,
            hostBinding: new LumenFrontendHostBinding(new SyntheticFrontendServices(), new GameFlowSession(),
                new SyntheticDonPresentation(), new EntrySceneHost(new IndicatorParts(IndicatorPartsScene.Entry, countdown: false))));

        player.Advance();
    }

    [Theory]
    [InlineData(0, 30, 31, "don_result_in", "don_result_loop")]
    [InlineData(0, 32, 33, "don_result_clear", "don_result_clear_loop")]
    [InlineData(0, 35, 36, "don_result_failure", "don_result_failure_loop")]
    [InlineData(0, 38, 39, "don_result_full", "don_result_full_loop")]
    [InlineData(0, 41, 42, "don_result_percious", "don_result_percious_loop")]
    [InlineData(1, 38, 39, "don_result_full", "don_result_full_loop")]
    [InlineData(1, 40, -1, "don_result_full_koshibai", null)]
    public void ResultBindingResolvesNumericDonMotionsWithoutMovieConstants(
        int side, int oneShot, int loop, string expectedOneShot, string? expectedLoop)
    {
        var don = new SyntheticDonPresentation();
        var player = new LumenPlayer(TestMovies.ResultDonMotionMovie(side, oneShot, loop), 1280, 720,
            hostBinding: new ResultHostBinding(static () => [], 1, 0, don));

        player.Advance();

        Assert.True(don.Requests.Count == 1,
            string.Join(" | ", player.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Equal(new DonMotionRequest(side, expectedOneShot, expectedLoop), don.Requests[0]);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_AVM_METHOD_UNRESOLVED");
    }

    private static GameFlowSession activeSession(string sceneName)
    {
        var flow = new GameFlowSession();
        flow.Start(new SceneId(sceneName));
        flow.ActivateLoadedScene();
        return flow;
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

        public void StopVoice(int? cue = null)
        {
        }

        public LumenHostValue CallExternalInterface(LumenHostCall hostCall) => LumenHostValue.Undefined;
    }
}
