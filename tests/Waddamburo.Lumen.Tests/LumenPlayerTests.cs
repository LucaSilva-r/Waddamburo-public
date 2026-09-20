using System.Buffers.Binary;
using System.Collections.Immutable;
using Waddamburo.Formats.Lmb;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Lumen.Tests;

public sealed class LumenPlayerTests
{
    [Fact]
    public void FramePlacementsProduceImmutableTransformedRenderSnapshots()
    {
        var player = new LumenPlayer(createMovie(), 1280, 720);

        var first = Assert.Single(player.CreateRenderSnapshot().Quads);
        Assert.Equal(4U, first.TextureIndex);
        Assert.Equal(new LumenRenderVertex(10, 20, 0, 0), first.TopLeft);
        Assert.Equal(new LumenRenderVertex(110, 70, 1, 1), first.BottomRight);
        Assert.Equal(new LumenRenderColor(0.5f, 1, 1, 0.5f), first.MultiplyColor);

        player.Advance();

        var second = Assert.Single(player.CreateRenderSnapshot().Quads);
        Assert.Equal(new LumenRenderVertex(30, 40, 0, 0), second.TopLeft);
        Assert.Equal(new LumenRenderVertex(130, 90, 1, 1), second.BottomRight);
        Assert.Equal(first.MultiplyColor, second.MultiplyColor);
        Assert.Contains(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");

        player.Advance();
        Assert.Empty(player.CreateRenderSnapshot().Quads);

        player.Advance();
        Assert.Single(player.CreateRenderSnapshot().Quads);
        Assert.Equal(0, player.CurrentFrame);
    }

    [Fact]
    public void AddBlendModeFlowsIntoRenderSnapshot()
    {
        var player = new LumenPlayer(createMovie(firstBlendMode: 8), 1280, 720);

        var quad = Assert.Single(player.CreateRenderSnapshot().Quads);

        Assert.Equal(LumenRenderBlend.Add, quad.Blend);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_BLEND_MODE_DEFERRED");
    }

    [Fact]
    public void MatrixCompositionAppliesChildBeforeParent()
    {
        var local = new LumenMatrix(2, 0, 0, 3, 5, 7);
        var parent = new LumenMatrix(0, 1, -1, 0, 20, 30);

        var sequential = parent.Transform(local.Transform(4, 6).X, local.Transform(4, 6).Y);
        var composed = local.Then(parent).Transform(4, 6);

        Assert.Equal(sequential, composed);
    }

    [Fact]
    public void RenderSnapshotInterpolatesBetweenPreviousAndCurrentTickState()
    {
        var player = new LumenPlayer(createMovie(), 1280, 720);

        player.Advance();
        var previous = Assert.Single(player.CreateRenderSnapshot(0).Quads);
        var halfway = Assert.Single(player.CreateRenderSnapshot(0.5f).Quads);
        var current = Assert.Single(player.CreateRenderSnapshot(1).Quads);

        Assert.Equal(new LumenRenderVertex(10, 20, 0, 0), previous.TopLeft);
        Assert.Equal(new LumenRenderVertex(20, 30, 0, 0), halfway.TopLeft);
        Assert.Equal(new LumenRenderVertex(30, 40, 0, 0), current.TopLeft);
    }

    [Fact]
    public void RenderSnapshotTreatsLargeTranslationAsCut()
    {
        var player = new LumenPlayer(createMovie(secondX: 250), 1280, 720);

        player.Advance();
        var atStartOfPresentationInterval = Assert.Single(player.CreateRenderSnapshot(0).Quads);

        Assert.Equal(new LumenRenderVertex(250, 40, 0, 0), atStartOfPresentationInterval.TopLeft);
    }

    [Fact]
    public void RenderSnapshotTreatsAbruptMultiplyColorChangeAsCut()
    {
        var player = new LumenPlayer(createMovie(secondColorIndex: 1), 1280, 720);

        player.Advance();
        var atStartOfPresentationInterval = Assert.Single(player.CreateRenderSnapshot(0).Quads);

        Assert.Equal(LumenRenderColor.Transparent, atStartOfPresentationInterval.MultiplyColor);
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    public void RenderSnapshotRejectsInvalidInterpolationFraction(float fraction)
    {
        var player = new LumenPlayer(createMovie(), 1280, 720);

        Assert.Throws<ArgumentOutOfRangeException>(() => player.CreateRenderSnapshot(fraction));
    }

    [Fact]
    public void SeekRestoresExactF105SnapshotAndResetsInterpolation()
    {
        var player = new LumenPlayer(createMovie(includeSeekKey: true), 1280, 720);

        player.Seek(2);

        Assert.Equal(2, player.CurrentFrame);
        var previous = Assert.Single(player.CreateRenderSnapshot(0).Quads);
        var current = Assert.Single(player.CreateRenderSnapshot(1).Quads);
        Assert.Equal(new LumenRenderVertex(30, 40, 0, 0), current.TopLeft);
        Assert.Equal(current, previous);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_KEYFRAMES_DEFERRED");
    }

    [Fact]
    public void SeekWithoutSnapshotReplaysOrdinaryFramesFromStart()
    {
        var player = new LumenPlayer(createMovie(), 1280, 720);

        player.Seek(1);

        Assert.Equal(1, player.CurrentFrame);
        var quad = Assert.Single(player.CreateRenderSnapshot().Quads);
        Assert.Equal(new LumenRenderVertex(30, 40, 0, 0), quad.TopLeft);
        Assert.Contains(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void SeekRejectsFramesOutsideRootTimeline(int frame)
    {
        var player = new LumenPlayer(createMovie(), 1280, 720);

        Assert.Throws<ArgumentOutOfRangeException>(() => player.Seek(frame));
    }

    [Fact]
    public void StopAndPlayControlRootTimelineAdvancement()
    {
        var player = new LumenPlayer(createMovie(), 1280, 720);

        player.Stop();
        player.Advance();
        Assert.False(player.IsPlaying);
        Assert.Equal(0, player.CurrentFrame);

        player.Play();
        player.Advance();
        Assert.True(player.IsPlaying);
        Assert.Equal(1, player.CurrentFrame);
    }

    [Fact]
    public void GotoLabelRestoresFrameAndAppliesRequestedPlaybackState()
    {
        var player = new LumenPlayer(createMovie(), 1280, 720);

        player.GotoLabel("middle", play: false);
        Assert.False(player.IsPlaying);
        Assert.Equal(1, player.CurrentFrame);
        Assert.Single(player.CreateRenderSnapshot().Quads);
        player.Advance();
        Assert.Equal(1, player.CurrentFrame);

        player.GotoLabel("end", play: true);
        Assert.True(player.IsPlaying);
        Assert.Equal(2, player.CurrentFrame);
        Assert.Empty(player.CreateRenderSnapshot().Quads);
    }

    [Fact]
    public void GotoLabelRejectsUnknownLabelWithoutChangingState()
    {
        var player = new LumenPlayer(createMovie(), 1280, 720);

        Assert.Throws<KeyNotFoundException>(() => player.GotoLabel("missing", play: false));
        Assert.True(player.IsPlaying);
        Assert.Equal(0, player.CurrentFrame);
    }

    [Fact]
    public void SimpleStopFrameActionRunsAfterEnteringFrame()
    {
        var player = new LumenPlayer(createMovie(actionBytecode: [0x07, 0x00]), 1280, 720);

        player.Advance();

        Assert.Equal(1, player.CurrentFrame);
        Assert.False(player.IsPlaying);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
        player.Advance();
        Assert.Equal(1, player.CurrentFrame);
    }

    [Fact]
    public void TimelineGoToLabelActionJumpsCurrentClipAndContinuesPlaying()
    {
        var player = new LumenPlayer(createMovie(actionBytecode: [
            0x8C, 0x02, 0x00, 0x01, 0x00,
            0x06,
            0x00,
        ]), 1280, 720);

        player.Advance();

        Assert.Equal(2, player.CurrentFrame);
        Assert.True(player.IsPlaying);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
    }

    [Fact]
    public void TimelineJumpExecutesTheDestinationFramesAction()
    {
        var player = new LumenPlayer(createMovie(
            actionBytecode: [
                0x8C, 0x02, 0x00, 0x01, 0x00,
                0x06,
                0x00,
            ],
            targetActionBytecode: [0x07, 0x00]), 1280, 720);

        player.Advance();

        Assert.Equal(2, player.CurrentFrame);
        Assert.False(player.IsPlaying);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
    }

    [Fact]
    public void TargetFrameActionTakesPrecedenceOverRequestedPlaybackState()
    {
        var player = new LumenPlayer(createMovie(actionBytecode: [0x06, 0x00]), 1280, 720);

        player.GotoFrame(1, play: false);

        Assert.Equal(1, player.CurrentFrame);
        Assert.True(player.IsPlaying);
    }

    [Fact]
    public void UnsupportedFrameActionDoesNotPartiallyExecuteSimplePrefix()
    {
        var player = new LumenPlayer(createMovie(actionBytecode: [0x07, 0x04, 0x00]), 1280, 720);

        player.Advance();

        Assert.True(player.IsPlaying);
        Assert.Contains(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
    }

    [Fact]
    public void ConditionalFrameActionUsesTypedPushAndBranchOperands()
    {
        var player = new LumenPlayer(createMovie(actionBytecode: [
            0x96, 0x02, 0x00, 0x05, 0x01,
            0x9D, 0x02, 0x00, 0x06, 0x00,
            0x06,
            0x99, 0x02, 0x00, 0x01, 0x00,
            0x07,
            0x00,
        ]), 1280, 720);

        player.Advance();

        Assert.False(player.IsPlaying);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
    }

    [Fact]
    public void FailedPrimitiveActionDoesNotCommitPlaybackChanges()
    {
        var player = new LumenPlayer(createMovie(actionBytecode: [0x07, 0x12, 0x00]), 1280, 720);

        player.Advance();

        Assert.True(player.IsPlaying);
        Assert.Contains(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
    }

    [Fact]
    public void PrimitiveActionInstructionBudgetStopsInfiniteBranchWithoutSideEffects()
    {
        var player = new LumenPlayer(createMovie(actionBytecode: [
            0x07,
            0x99, 0x02, 0x00, 0xFB, 0xFF,
            0x00,
        ]), 1280, 720);

        player.Advance();

        Assert.True(player.IsPlaying);
        Assert.Contains(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_LIMIT");
    }

    [Fact]
    public void RuntimeLimitsRejectInvalidValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LumenRuntimeLimits(maxInstructionsPerAction: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LumenRuntimeLimits(maxStackValues: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LumenRuntimeLimits(maxPendingActions: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LumenRuntimeLimits(maxRegisters: 0));
    }

    [Fact]
    public void ConfiguredInstructionLimitRollsBackPlaybackMutation()
    {
        var player = new LumenPlayer(
            createMovie(actionBytecode: [0x07, 0x00]),
            1280,
            720,
            limits: new LumenRuntimeLimits(maxInstructionsPerAction: 1));

        player.Advance();

        Assert.True(player.IsPlaying);
        Assert.Contains(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_LIMIT");
    }

    [Fact]
    public void ConfiguredStackLimitRollsBackPlaybackMutation()
    {
        var player = new LumenPlayer(
            createMovie(actionBytecode: [0x07, 0x96, 0x02, 0x00, 0x02, 0x02, 0x00]),
            1280,
            720,
            limits: new LumenRuntimeLimits(maxStackValues: 1));

        player.Advance();

        Assert.True(player.IsPlaying);
        Assert.Contains(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_LIMIT");
    }

    [Fact]
    public void PendingActionLimitDropsExcessWorkWithStableDiagnostic()
    {
        var player = new LumenPlayer(
            createMovie(actionBytecode: [0x07, 0x00], duplicateAction: true),
            1280,
            720,
            limits: new LumenRuntimeLimits(maxPendingActions: 1));

        player.Advance();

        Assert.False(player.IsPlaying);
        Assert.Contains(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_QUEUE_LIMIT");
    }

    [Fact]
    public void PrimitiveArithmeticAndComparisonCanDrivePlaybackBranch()
    {
        var player = new LumenPlayer(createMovie(actionBytecode: [
            0x96, 0x0A, 0x00,
            0x07, 0x02, 0x00, 0x00, 0x00,
            0x07, 0x03, 0x00, 0x00, 0x00,
            0x47,
            0x96, 0x05, 0x00, 0x07, 0x05, 0x00, 0x00, 0x00,
            0x49,
            0x9D, 0x02, 0x00, 0x06, 0x00,
            0x06,
            0x99, 0x02, 0x00, 0x01, 0x00,
            0x07,
            0x00,
        ]), 1280, 720);

        player.Advance();

        Assert.False(player.IsPlaying);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
    }

    [Fact]
    public void NamedClipMemberActionCanHidePlacedContent()
    {
        var player = new LumenPlayer(createMovie(placementNameStringIndex: 31, actionBytecode: [
            0x96, 0x03, 0x00, 0x09, 0x1F, 0x00,
            0x1C,
            0x96, 0x05, 0x00, 0x09, 0x02, 0x00, 0x05, 0x00,
            0x4F,
            0x00,
        ]), 1280, 720);

        player.Advance();

        Assert.Empty(player.CreateRenderSnapshot().Quads);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
    }

    [Fact]
    public void FailedMemberActionDoesNotCommitStagedVisibility()
    {
        var player = new LumenPlayer(createMovie(placementNameStringIndex: 31, actionBytecode: [
            0x96, 0x03, 0x00, 0x09, 0x1F, 0x00,
            0x1C,
            0x96, 0x05, 0x00, 0x09, 0x02, 0x00, 0x05, 0x00,
            0x4F,
            0x12,
            0x00,
        ]), 1280, 720);

        player.Advance();

        Assert.Single(player.CreateRenderSnapshot().Quads);
        Assert.Contains(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
    }

    [Fact]
    public void RegisterValuesCanFeedNamedClipMembers()
    {
        var player = new LumenPlayer(createMovie(placementNameStringIndex: 31, actionBytecode: [
            0x96, 0x02, 0x00, 0x05, 0x00,
            0x87, 0x01, 0x00, 0x00,
            0x17,
            0x96, 0x03, 0x00, 0x09, 0x1F, 0x00,
            0x1C,
            0x96, 0x05, 0x00, 0x09, 0x02, 0x00, 0x04, 0x00,
            0x4F,
            0x00,
        ]), 1280, 720);

        player.Advance();

        Assert.Empty(player.CreateRenderSnapshot().Quads);
    }

    [Fact]
    public void UnresolvedFunctionReturnsUndefinedAndAllowsInitializerToFinish()
    {
        var player = new LumenPlayer(createMovie(placementNameStringIndex: 31, actionBytecode: [
            0x96, 0x0D, 0x00,
            0x07, 0x2A, 0x00, 0x00, 0x00,
            0x07, 0x01, 0x00, 0x00, 0x00,
            0x09, 0x01, 0x00,
            0x3D,
            0x17,
            0x96, 0x03, 0x00, 0x09, 0x1F, 0x00,
            0x1C,
            0x96, 0x05, 0x00, 0x09, 0x02, 0x00, 0x05, 0x00,
            0x4F,
            0x00,
        ]), 1280, 720);

        player.Advance();

        Assert.Empty(player.CreateRenderSnapshot().Quads);
        Assert.Contains(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_AVM_CALL_UNRESOLVED");
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
    }

    [Fact]
    public void ClassBootstrapValuesCompleteAndReachAuthoredStop()
    {
        var player = new LumenPlayer(createMovie(actionBytecode: [
            0x9B, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00,
                0x00,
            0x96, 0x03, 0x00, 0x09, 0x00, 0x00,
            0x1C,
            0x87, 0x01, 0x00, 0x00,
            0x17,
            0x96, 0x05, 0x00, 0x04, 0x00, 0x09, 0x04, 0x00,
            0x1C,
            0x69,
            0x96, 0x0F, 0x00,
                0x07, 0x01, 0x00, 0x00, 0x00,
                0x07, 0x02, 0x00, 0x00, 0x00,
                0x07, 0x02, 0x00, 0x00, 0x00,
            0x42,
            0x17,
            0x96, 0x08, 0x00,
                0x07, 0x00, 0x00, 0x00, 0x00,
                0x09, 0x04, 0x00,
            0x40,
            0x17,
            0x96, 0x0A, 0x00,
                0x07, 0x00, 0x00, 0x00, 0x00,
                0x04, 0x00,
                0x09, 0x01, 0x00,
            0x52,
            0x17,
            0x96, 0x0A, 0x00,
                0x07, 0x01, 0x00, 0x00, 0x00,
                0x07, 0x03, 0x00, 0x00, 0x00,
            0x63,
            0x96, 0x05, 0x00, 0x07, 0x02, 0x00, 0x00, 0x00,
            0x61,
            0x17,
            0x07,
            0x00,
        ]), 1280, 720);

        player.Advance();
        player.Advance();

        Assert.Equal(1, player.CurrentFrame);
        Assert.False(player.IsPlaying);
        Assert.Contains(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_AVM_METHOD_UNRESOLVED");
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
    }

    [Fact]
    public void RegisterClassBindsAnExistingExportAndRunsItsConstructor()
    {
        var player = new LumenPlayer(createMovie(
            rootExportStringIndex: 5,
            actionBytecode: [
                0x8E, 0x09, 0x00,
                    0x00, 0x00,
                    0x00, 0x00,
                    0x02,
                    0x01, 0x00,
                    0x0F, 0x00,
                    0x96, 0x02, 0x00, 0x04, 0x01,
                    0x96, 0x05, 0x00, 0x09, 0x02, 0x00, 0x05, 0x00,
                    0x4F,
                    0x00,
                0x96, 0x03, 0x00, 0x09, 0x00, 0x00,
                0x1C,
                0x96, 0x0B, 0x00,
                    0x09, 0x05, 0x00,
                    0x07, 0x02, 0x00, 0x00, 0x00,
                    0x09, 0x04, 0x00,
                0x1C,
                0x96, 0x03, 0x00, 0x09, 0x06, 0x00,
                0x52,
                0x17,
                0x07,
                0x00,
            ]), 1280, 720);

        player.Advance();

        Assert.Empty(player.CreateRenderSnapshot().Quads);
        Assert.False(player.IsPlaying);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_AVM_METHOD_UNRESOLVED");
    }

    [Fact]
    public void HostObjectCallsAreSynchronousAndScopedToOnePlayer()
    {
        var firstCalls = 0;
        var secondCalls = 0;
        var firstArgument = double.NaN;
        var action = new byte[]
        {
            0x96, 0x0A, 0x00,
                0x07, 0x2A, 0x00, 0x00, 0x00,
                0x07, 0x01, 0x00, 0x00, 0x00,
            0x96, 0x03, 0x00, 0x09, 0x07, 0x00,
            0x1C,
            0x96, 0x03, 0x00, 0x09, 0x08, 0x00,
            0x52,
            0x87, 0x01, 0x00, 0x00,
            0x17,
            0x96, 0x03, 0x00, 0x09, 0x09, 0x00,
            0x1C,
            0x96, 0x05, 0x00, 0x09, 0x02, 0x00, 0x04, 0x00,
            0x4F,
            0x00,
        };
        var first = new LumenPlayer(
            createMovie(actionBytecode: action),
            1280,
            720,
            hostBinding: new DelegateHostBinding(context => context.RegisterObject("resource", resource =>
                resource.RegisterMethod("ResolveVisible", call =>
                {
                    firstCalls++;
                    firstArgument = call.Arguments[0].AsNumber();
                    return LumenHostValue.FromBoolean(false);
                }))));
        var second = new LumenPlayer(
            createMovie(actionBytecode: action),
            1280,
            720,
            hostBinding: new DelegateHostBinding(context => context.RegisterObject("resource", resource =>
                resource.RegisterMethod("ResolveVisible", _ =>
                {
                    secondCalls++;
                    return LumenHostValue.FromBoolean(true);
                }))));

        first.Advance();
        second.Advance();

        Assert.Equal(1, firstCalls);
        Assert.Equal(1, secondCalls);
        Assert.Equal(42, firstArgument);
        Assert.Empty(first.CreateRenderSnapshot().Quads);
        Assert.Single(second.CreateRenderSnapshot().Quads);
        Assert.DoesNotContain(first.Diagnostics, diagnostic => diagnostic.Code == "LUM_AVM_METHOD_UNRESOLVED");
        Assert.DoesNotContain(second.Diagnostics, diagnostic => diagnostic.Code == "LUM_AVM_METHOD_UNRESOLVED");
    }

    [Fact]
    public void PendingGlobalMemberWriteIsVisibleThroughBareVariableLookup()
    {
        var calls = 0;
        var player = new LumenPlayer(
            createMovie(actionBytecode: [
                0x96, 0x03, 0x00, 0x09, 0x1E, 0x00,
                0x1C,
                0x96, 0x03, 0x00, 0x09, 0x1C, 0x00,
                0x96, 0x03, 0x00, 0x09, 0x07, 0x00,
                0x1C,
                0x4F,
                0x96, 0x08, 0x00,
                    0x07, 0x00, 0x00, 0x00, 0x00,
                    0x09, 0x1C, 0x00,
                0x1C,
                0x96, 0x03, 0x00, 0x09, 0x1D, 0x00,
                0x52,
                0x17,
                0x00,
            ]),
            1280,
            720,
            hostBinding: new DelegateHostBinding(context =>
            {
                context.RegisterObject("alias", alias => alias.RegisterMethod("Ping", _ =>
                {
                    calls += 100;
                    return LumenHostValue.Undefined;
                }));
                context.RegisterObject("resource", resource => resource.RegisterMethod("Ping", _ =>
                {
                    calls++;
                    return LumenHostValue.Undefined;
                }));
            }));

        player.Advance();

        Assert.Equal(1, calls);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_AVM_METHOD_UNRESOLVED");
    }

    [Fact]
    public void KeyIsDownReadsOnlyTheCurrentPlayersImmutableInputSnapshot()
    {
        var action = new byte[]
        {
            0x96, 0x03, 0x00, 0x09, 0x1F, 0x00,
            0x1C,
            0x96, 0x03, 0x00, 0x09, 0x02, 0x00,
            0x96, 0x0A, 0x00,
                0x07, 0x00, 0x00, 0x00, 0x00,
                0x07, 0x01, 0x00, 0x00, 0x00,
            0x96, 0x03, 0x00, 0x09, 0x22, 0x00,
            0x96, 0x03, 0x00, 0x09, 0x23, 0x00,
            0x52,
            0x96, 0x05, 0x00, 0x07, 0x01, 0x00, 0x00, 0x00,
            0x96, 0x03, 0x00, 0x09, 0x20, 0x00,
            0x1C,
            0x96, 0x03, 0x00, 0x09, 0x21, 0x00,
            0x52,
            0x4F,
            0x00,
        };
        var released = new LumenPlayer(
            createMovie(placementNameStringIndex: 31, actionBytecode: action),
            1280,
            720);
        var pressed = new LumenPlayer(
            createMovie(placementNameStringIndex: 31, actionBytecode: action),
            1280,
            720);

        released.Advance(LumenInputSnapshot.Empty);
        pressed.Advance(new LumenInputSnapshot([68]));

        Assert.Empty(released.CreateRenderSnapshot().Quads);
        Assert.Single(pressed.CreateRenderSnapshot().Quads);
        Assert.DoesNotContain(pressed.Diagnostics, diagnostic => diagnostic.Code == "LUM_AVM_METHOD_UNRESOLVED");
    }

    [Fact]
    public void ConstructedArrayExposesItsRequestedLength()
    {
        var player = new LumenPlayer(
            createMovie(placementNameStringIndex: 31, actionBytecode: [
                0x96, 0x0D, 0x00,
                    0x07, 0x02, 0x00, 0x00, 0x00,
                    0x07, 0x01, 0x00, 0x00, 0x00,
                    0x09, 0x24, 0x00,
                0x40,
                0x87, 0x01, 0x00, 0x00,
                0x17,
                0x96, 0x03, 0x00, 0x09, 0x1F, 0x00,
                0x1C,
                0x96, 0x05, 0x00, 0x09, 0x02, 0x00, 0x04, 0x00,
                0x96, 0x03, 0x00, 0x09, 0x19, 0x00,
                0x4E,
                0x4F,
                0x00,
            ]),
            1280,
            720);

        player.Advance();

        Assert.Single(player.CreateRenderSnapshot().Quads);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
    }

    [Fact]
    public void AuthoredFunctionReturnsPropagateToCallers()
    {
        var player = new LumenPlayer(
            createMovie(actionBytecode: [
                0x9B, 0x06, 0x00,
                    0x14, 0x00,
                    0x00, 0x00,
                    0x15, 0x00,
                    0x96, 0x08, 0x00,
                        0x07, 0x00, 0x00, 0x00, 0x00,
                        0x09, 0x07, 0x00,
                    0x1C,
                    0x96, 0x03, 0x00, 0x09, 0x08, 0x00,
                    0x52,
                    0x3E,
                    0x00,
                0x96, 0x03, 0x00, 0x09, 0x09, 0x00,
                0x1C,
                0x96, 0x03, 0x00, 0x09, 0x02, 0x00,
                0x96, 0x08, 0x00,
                    0x07, 0x00, 0x00, 0x00, 0x00,
                    0x09, 0x14, 0x00,
                0x3D,
                0x4F,
                0x00,
            ]),
            1280,
            720,
            hostBinding: new DelegateHostBinding(context => context.RegisterObject("resource", resource =>
                resource.RegisterMethod("ResolveVisible", _ => LumenHostValue.FromBoolean(true)))));

        player.Advance();

        Assert.Single(player.CreateRenderSnapshot().Quads);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
    }

    [Fact]
    public void AuthoredGotoAndStopUsesOneBasedClipFrames()
    {
        var player = new LumenPlayer(createMovie(removeOnThirdFrame: false, placementNameStringIndex: 31, actionBytecode: [
            0x96, 0x03, 0x00, 0x09, 0x1F, 0x00,
            0x1C,
            0x96, 0x05, 0x00, 0x09, 0x02, 0x00, 0x05, 0x00,
            0x4F,
            0x96, 0x0A, 0x00,
                0x07, 0x03, 0x00, 0x00, 0x00,
                0x07, 0x01, 0x00, 0x00, 0x00,
            0x96, 0x03, 0x00, 0x09, 0x09, 0x00,
            0x1C,
            0x96, 0x03, 0x00, 0x09, 0x15, 0x00,
            0x52,
            0x17,
            0x00,
        ]), 1280, 720);

        player.Advance();

        Assert.Equal(2, player.CurrentFrame);
        Assert.False(player.IsPlaying);
        Assert.Empty(player.CreateRenderSnapshot().Quads);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_AVM_METHOD_UNRESOLVED");
    }

    [Fact]
    public void PrimitiveToStringReturnsAnAvmString()
    {
        var player = new LumenPlayer(createMovie(actionBytecode: [
            0x96, 0x03, 0x00, 0x09, 0x09, 0x00,
            0x1C,
            0x96, 0x03, 0x00, 0x09, 0x02, 0x00,
            0x96, 0x0A, 0x00,
                0x07, 0x00, 0x00, 0x00, 0x00,
                0x07, 0x03, 0x00, 0x00, 0x00,
            0x96, 0x03, 0x00, 0x09, 0x16, 0x00,
            0x52,
            0x4F,
            0x00,
        ]), 1280, 720);

        player.Advance();

        Assert.Single(player.CreateRenderSnapshot().Quads);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_AVM_METHOD_UNRESOLVED");
    }

    [Fact]
    public void ArrayIndexWritesOverrideInitialValues()
    {
        var player = new LumenPlayer(createMovie(actionBytecode: [
            0x96, 0x07, 0x00,
                0x05, 0x00,
                0x07, 0x01, 0x00, 0x00, 0x00,
            0x42,
            0x87, 0x01, 0x00, 0x00,
            0x17,
            0x96, 0x07, 0x00,
                0x04, 0x00,
                0x09, 0x17, 0x00,
                0x05, 0x01,
            0x4F,
            0x96, 0x03, 0x00, 0x09, 0x09, 0x00,
            0x1C,
            0x96, 0x03, 0x00, 0x09, 0x02, 0x00,
            0x96, 0x05, 0x00, 0x04, 0x00, 0x09, 0x17, 0x00,
            0x4E,
            0x4F,
            0x00,
        ]), 1280, 720);

        player.Advance();

        Assert.Single(player.CreateRenderSnapshot().Quads);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
    }

    [Fact]
    public void ArrayPushAppendsValuesAndUpdatesLengthTransactionally()
    {
        var player = new LumenPlayer(createMovie(actionBytecode: [
            0x96, 0x05, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00,
            0x42,
            0x87, 0x01, 0x00, 0x00,
            0x17,
            0x96, 0x07, 0x00,
                0x05, 0x01,
                0x07, 0x01, 0x00, 0x00, 0x00,
            0x96, 0x02, 0x00, 0x04, 0x00,
            0x96, 0x03, 0x00, 0x09, 0x28, 0x00,
            0x52,
            0x17,
            0x96, 0x03, 0x00, 0x09, 0x1F, 0x00,
            0x1C,
            0x96, 0x03, 0x00, 0x09, 0x02, 0x00,
            0x96, 0x05, 0x00, 0x04, 0x00, 0x09, 0x17, 0x00,
            0x4E,
            0x4F,
            0x00,
        ]), 1280, 720);

        player.Advance();

        Assert.Single(player.CreateRenderSnapshot().Quads);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_AVM_METHOD_UNRESOLVED");
    }

    [Fact]
    public void MathFloorReturnsTheGreatestIntegerBelowItsArgument()
    {
        var player = new LumenPlayer(createMovie(actionBytecode: [
            0x96, 0x0A, 0x00,
                0x07, 0x03, 0x00, 0x00, 0x00,
                0x07, 0x02, 0x00, 0x00, 0x00,
            0x0D,
            0x96, 0x05, 0x00, 0x07, 0x01, 0x00, 0x00, 0x00,
            0x96, 0x03, 0x00, 0x09, 0x29, 0x00,
            0x1C,
            0x96, 0x03, 0x00, 0x09, 0x2A, 0x00,
            0x52,
            0x96, 0x05, 0x00, 0x07, 0x01, 0x00, 0x00, 0x00,
            0x49,
            0x87, 0x01, 0x00, 0x00,
            0x17,
            0x96, 0x03, 0x00, 0x09, 0x1F, 0x00,
            0x1C,
            0x96, 0x05, 0x00, 0x09, 0x02, 0x00, 0x04, 0x00,
            0x4F,
            0x00,
        ]), 1280, 720);

        player.Advance();

        Assert.Single(player.CreateRenderSnapshot().Quads);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_AVM_METHOD_UNRESOLVED");
    }

    [Fact]
    public void StringLengthAndCharAtReturnAvmValues()
    {
        var player = new LumenPlayer(createMovie(actionBytecode: [
            0x96, 0x03, 0x00, 0x09, 0x09, 0x00,
            0x1C,
            0x96, 0x03, 0x00, 0x09, 0x02, 0x00,
            0x96, 0x06, 0x00, 0x09, 0x18, 0x00, 0x09, 0x19, 0x00,
            0x4E,
            0x96, 0x05, 0x00, 0x07, 0x01, 0x00, 0x00, 0x00,
            0x49,
            0x60,
            0x4F,
            0x96, 0x0D, 0x00,
                0x07, 0x00, 0x00, 0x00, 0x00,
                0x07, 0x01, 0x00, 0x00, 0x00,
                0x09, 0x18, 0x00,
            0x96, 0x03, 0x00, 0x09, 0x1A, 0x00,
            0x52,
            0x96, 0x03, 0x00, 0x09, 0x1B, 0x00,
            0x49,
            0x60,
            0x4F,
            0x00,
        ]), 1280, 720);

        player.Advance();

        Assert.Single(player.CreateRenderSnapshot().Quads);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_AVM_METHOD_UNRESOLVED");
    }

    [Fact]
    public void HostFailuresReturnUndefinedWithStableDiagnostic()
    {
        var player = new LumenPlayer(
            createMovie(actionBytecode: [
                0x96, 0x0D, 0x00,
                    0x07, 0x2A, 0x00, 0x00, 0x00,
                    0x07, 0x01, 0x00, 0x00, 0x00,
                    0x09, 0x0A, 0x00,
                0x3D,
                0x17,
                0x00,
            ]),
            1280,
            720,
            hostBinding: new DelegateHostBinding(context => context.RegisterFunction(
                "FailingHost",
                _ => throw new InvalidOperationException("test"))));

        player.Advance();

        Assert.Contains(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_HOST_CALL_FAILED");
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
    }

    [Fact]
    public void ExternalInterfaceCallbacksAreRegisteredAndInvokedSynchronously()
    {
        var player = new LumenPlayer(createMovie(actionBytecode: [
            0x8E, 0x0C, 0x00,
                0x10, 0x00,
                0x01, 0x00,
                0x03,
                0x01, 0x00,
                0x02, 0x00, 0x00,
                0x0F, 0x00,
                0x96, 0x02, 0x00, 0x04, 0x01,
                0x96, 0x05, 0x00, 0x09, 0x02, 0x00, 0x04, 0x02,
                0x4F,
                0x00,
            0x96, 0x03, 0x00, 0x09, 0x09, 0x00,
            0x1C,
            0x96, 0x08, 0x00,
                0x09, 0x0F, 0x00,
                0x07, 0x03, 0x00, 0x00, 0x00,
            0x96, 0x03, 0x00, 0x09, 0x0B, 0x00,
            0x1C,
            0x96, 0x03, 0x00, 0x09, 0x0C, 0x00,
            0x4E,
            0x96, 0x03, 0x00, 0x09, 0x0D, 0x00,
            0x4E,
            0x96, 0x03, 0x00, 0x09, 0x0E, 0x00,
            0x52,
            0x17,
            0x00,
        ]), 1280, 720);

        Assert.Empty(player.CallbackNames);
        player.Advance();

        Assert.True(
            player.CallbackNames.SequenceEqual(["SetVisible"]),
            string.Join(" | ", player.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        var callback = Assert.Single(player.Callbacks);
        Assert.Equal("SetVisible", callback.Name);
        Assert.Equal("middle", Assert.Single(callback.Parameters));
        Assert.True(player.TryInvokeCallback(
            "SetVisible",
            [LumenHostValue.FromBoolean(false)]));
        Assert.Empty(player.CreateRenderSnapshot().Quads);
        Assert.False(player.TryInvokeCallback("Missing", []));
    }

    [Fact]
    public void ExternalInterfaceCallsReachThePlayersHostBindingSynchronously()
    {
        ImmutableArray<LumenHostValue> received = [];
        var player = new LumenPlayer(
            createMovie(actionBytecode: [
                0x96, 0x0D, 0x00,
                    0x07, 0x07, 0x00, 0x00, 0x00,
                    0x09, 0x26, 0x00,
                    0x07, 0x02, 0x00, 0x00, 0x00,
                0x96, 0x03, 0x00, 0x09, 0x0B, 0x00,
                0x1C,
                0x96, 0x03, 0x00, 0x09, 0x0C, 0x00,
                0x4E,
                0x96, 0x03, 0x00, 0x09, 0x0D, 0x00,
                0x4E,
                0x96, 0x03, 0x00, 0x09, 0x25, 0x00,
                0x52,
                0x17,
                0x00,
            ]),
            1280,
            720,
            hostBinding: new DelegateHostBinding(context =>
                context.RegisterExternalInterfaceCall(call =>
                {
                    received = call.Arguments;
                    return LumenHostValue.Undefined;
                })));

        player.Advance();

        Assert.Equal(2, received.Length);
        Assert.Equal("Confirm", received[0].AsString());
        Assert.Equal(7, received[1].AsNumber());
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_AVM_METHOD_UNRESOLVED");
    }

    [Fact]
    public void CloneAndRemoveSpriteCommitAsOneDisplayListTransaction()
    {
        var player = new LumenPlayer(createMovie(
            placementNameStringIndex: 31,
            actionBytecode: [
                0x96, 0x03, 0x00, 0x09, 0x1F, 0x00,
                0x1C,
                0x96, 0x08, 0x00,
                    0x09, 0x27, 0x00,
                    0x07, 0xE8, 0x03, 0x00, 0x00,
                0x24,
                0x96, 0x03, 0x00, 0x09, 0x1F, 0x00,
                0x1C,
                0x25,
                0x00,
            ]), 1280, 720);

        player.Advance();

        var quad = Assert.Single(player.CreateRenderSnapshot().Quads);
        Assert.Equal(new LumenRenderVertex(30, 40, 0, 0), quad.TopLeft);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
    }

    [Fact]
    public void NewObjectInvokesAuthoredConstructorWithPrototype()
    {
        var player = new LumenPlayer(createMovie(actionBytecode: [
            0x8E, 0x09, 0x00,
                0x11, 0x00,
                0x00, 0x00,
                0x02,
                0x01, 0x00,
                0x0F, 0x00,
                0x96, 0x02, 0x00, 0x04, 0x01,
                0x96, 0x05, 0x00, 0x09, 0x12, 0x00, 0x05, 0x00,
                0x4F,
                0x00,
            0x96, 0x08, 0x00,
                0x07, 0x00, 0x00, 0x00, 0x00,
                0x09, 0x11, 0x00,
            0x40,
            0x87, 0x01, 0x00, 0x00,
            0x17,
            0x96, 0x05, 0x00, 0x04, 0x00, 0x09, 0x12, 0x00,
            0x4E,
            0x87, 0x01, 0x00, 0x01,
            0x17,
            0x96, 0x03, 0x00, 0x09, 0x09, 0x00,
            0x1C,
            0x96, 0x05, 0x00, 0x09, 0x02, 0x00, 0x04, 0x01,
            0x4F,
            0x00,
        ]), 1280, 720);

        player.Advance();

        Assert.Empty(player.CreateRenderSnapshot().Quads);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
    }

    [Fact]
    public void FrameScriptsInstallHandlersBeforeTheSameTicksEnterFramePhase()
    {
        var player = new LumenPlayer(createMovie(actionBytecode: [
            0x9B, 0x06, 0x00,
                0x10, 0x00,
                0x00, 0x00,
                0x11, 0x00,
                0x96, 0x03, 0x00, 0x09, 0x09, 0x00,
                0x1C,
                0x96, 0x05, 0x00, 0x09, 0x02, 0x00, 0x05, 0x00,
                0x4F,
                0x00,
            0x87, 0x01, 0x00, 0x00,
            0x17,
            0x96, 0x03, 0x00, 0x09, 0x09, 0x00,
            0x1C,
            0x96, 0x05, 0x00, 0x09, 0x13, 0x00, 0x04, 0x00,
            0x4F,
            0x00,
        ]), 1280, 720);

        player.Advance();

        Assert.Empty(player.CreateRenderSnapshot().Quads);
        Assert.DoesNotContain(player.Diagnostics, diagnostic => diagnostic.Code == "LUM_ACTION_DEFERRED");
    }

    [Fact]
    public void SceneComposesChildTransformsTextureNamespacesAndLayerOrder()
    {
        var first = new LumenPlayer(createMovie(), 1280, 720);
        var second = new LumenPlayer(createMovie(), 1280, 720);
        var scene = new LumenScenePlayer(
            1280,
            720,
            [
                new LumenSceneLayer(first, LumenMatrix.Identity, 0, 5),
                new LumenSceneLayer(second, new LumenMatrix(2, 0, 0, 2, 200, 100), 5, 5),
            ]);

        var snapshot = scene.CreateRenderSnapshot();

        Assert.Equal([4u, 9u], snapshot.Quads.Select(quad => quad.TextureIndex));
        Assert.Equal(new LumenRenderVertex(10, 20, 0, 0), snapshot.Quads[0].TopLeft);
        Assert.Equal(new LumenRenderVertex(220, 140, 0, 0), snapshot.Quads[1].TopLeft);
        Assert.Equal(new LumenRenderVertex(420, 240, 1, 1), snapshot.Quads[1].BottomRight);

        scene.Advance();
        Assert.Equal(1, first.CurrentFrame);
        Assert.Equal(1, second.CurrentFrame);
    }

    [Fact]
    public void EmptySceneStillRejectsInvalidInterpolationFraction()
    {
        var scene = new LumenScenePlayer(1280, 720, []);

        Assert.Throws<ArgumentOutOfRangeException>(() => scene.CreateRenderSnapshot(float.PositiveInfinity));
    }

    private static LmbMovieDefinition createMovie(
        float secondX = 30,
        uint secondColorIndex = uint.MaxValue,
        bool includeSeekKey = false,
        byte[]? actionBytecode = null,
        bool duplicateAction = false,
        uint rootExportStringIndex = 0,
        ushort firstBlendMode = 0,
        bool removeOnThirdFrame = true,
        uint placementNameStringIndex = 0,
        byte[]? targetActionBytecode = null)
    {
        var geometry = new uint[]
        {
            bits(0), bits(0), bits(0), bits(0),
            bits(100), bits(0), bits(1), bits(0),
            bits(100), bits(50), bits(1), bits(1),
            bits(0), bits(50), bits(0), bits(1),
            4, 0x00410000,
        };
        var records = new List<(uint Tag, byte[] Payload)>
        {
            words(LmbTags.MovieProperties, 0, 0, 0, 7, 0, 0, 0, bits(60)),
            record(LmbTags.StringPool, stringPool(
                "middle", "end", "_visible", "prototype", "Object", "RootExport", "registerClass",
                "resource", "ResolveVisible", "this", "FailingHost", "flash", "external",
                "ExternalInterface", "addCallback", "SetVisible", "", "Ctor", "value", "onEnterFrame",
                "HostVisible", "gotoAndStop", "toString", "0", "7", "length", "charAt", "7",
                "alias", "Ping", "_global", "placed", "Key", "isDown", "D", "charCodeAt", "Array",
                "call", "Confirm", "copy", "push", "Math", "floor")),
            words(LmbTags.ColorTransformPool, 2, 0x00800100, 0x01000080, 0, 0),
            words(LmbTags.MatrixPool, 1, bits(1), bits(0), bits(0), bits(1), bits(secondX), bits(40)),
            words(LmbTags.TranslationPool, 1, bits(10), bits(20)),
            record(
                LmbTags.ActionPool,
                targetActionBytecode is null
                    ? actionPool(actionBytecode ?? [0x04, 0x00])
                    : actionPool(actionBytecode ?? [0x04, 0x00], targetActionBytecode)),
            words(LmbTags.DefineShape, 42, 0, 0, 1),
            words(LmbTags.ShapeGeometry, geometry),
            words(LmbTags.DefineSprite, 7, 0, rootExportStringIndex, 2, 3, 2, 0),
            words(LmbTags.FrameLabel, 0, 1, 0),
            words(LmbTags.FrameLabel, 1, 2, 0),
            words(LmbTags.ShowFrame, 0, 1),
            words(LmbTags.PlaceObject, 42, 1, 0, placementNameStringIndex, 0x00010000U | firstBlendMode, 0x00030000, 0, 0x80000000, 0, uint.MaxValue, 0, 0),
            words(LmbTags.ShowFrame, 1, duplicateAction ? 3U : 2U),
            words(LmbTags.PlaceObject, 42, 1, 0, 0, 0x00020000, 0x00030000, 0, 0, secondColorIndex, uint.MaxValue, 0, 0),
            words(LmbTags.DoAction, 0, 0),
            words(LmbTags.ShowFrame, 2, targetActionBytecode is null ? 1U : 2U),
            words(LmbTags.RemoveObject, 42, 0x00030000),
        };
        if (targetActionBytecode is not null)
            records.Insert(records.Count - 1, words(LmbTags.DoAction, 1, 0));
        if (!removeOnThirdFrame)
            records.RemoveAt(records.Count - 1);
        if (duplicateAction)
            records.Insert(records.Count - 2, words(LmbTags.DoAction, 0, 0));
        if (includeSeekKey)
        {
            records.Add(words(LmbTags.FrameKey, 2, 1));
            records.Add(words(
                LmbTags.PlaceObject,
                42, 2, 0, uint.MaxValue, 0x00010000, 0x00020000,
                0x00020000, 0, uint.MaxValue, uint.MaxValue, 0, 0));
        }
        var file = createLmb([.. records]);
        return LmbSemanticReader.Read(file, validationContext: new LmbSemanticValidationContext(textureCount: 5)).Value;
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

    private static byte[] actionPool(params byte[][] values)
    {
        using var stream = new MemoryStream();
        writeUInt32(stream, checked((uint)values.Length));
        foreach (var value in values)
        {
            writeUInt32(stream, checked((uint)value.Length));
            stream.Write(value);
            while (stream.Position % 4 != 0)
                stream.WriteByte(0);
        }
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

    private static uint bits(float value) => BitConverter.SingleToUInt32Bits(value);

    private static void writeUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private sealed class DelegateHostBinding(Action<LumenHostContext> install) : ILumenHostBinding
    {
        public void Install(LumenHostContext context) => install(context);
    }
}
