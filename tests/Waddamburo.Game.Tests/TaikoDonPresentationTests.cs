using Waddamburo.Catalog;
using Waddamburo.Game.Don;
using Waddamburo.Formats.Lmb;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Game.Tests;

public sealed class TaikoDonPresentationTests
{
    [Fact]
    public void CharacterMarkerHandsPlacementToBalloonUntilItCloses()
    {
        var controller = new Controller();
        var presentation = new TaikoDonPresentation(controller, marker());
        Assert.Equal("don_normal", controller.Motions[^1].Loop);
        var layer = Assert.IsType<LumenSceneLayer>(presentation.Layer);
        var character = Assert.Single(new LumenScenePlayer(1280, 720, [layer]).CreateRenderSnapshot().Quads);
        Assert.Equal(new LumenNativeSurfaceKey("synthetic-player:0"), character.NativeSurface);
        Assert.Equal(448, character.BottomRight.X - character.TopLeft.X);
        Assert.Equal(256, character.BottomRight.Y - character.TopLeft.Y);
        Assert.Equal(-24, character.TopLeft.X);
        Assert.Equal(-36, character.TopLeft.Y);
        Assert.Equal(DonPresentationLayout.Gameplay, Assert.Single(controller.Layouts));
        presentation.SetBalloonVisible(true);
        Assert.Null(presentation.Layer);
        presentation.SetGoGo(true);
        Assert.Single(controller.Motions);
        presentation.SetBalloonVisible(false);
        Assert.Equal(2, controller.Layouts.Count);
        Assert.Equal(DonPresentationLayout.Gameplay, controller.Layouts[^1]);
        Assert.Equal("don_sabi", controller.Motions[^1].Loop);
        presentation.SetGoGo(false);
        Assert.Equal("don_normal", controller.Idles[^1]);
        Assert.Same(layer.Player, presentation.Layer?.Player);
    }

    [Fact]
    public void PlayStateSelectsIdleAndReactions()
    {
        var controller = new Controller();
        var don = new TaikoDonPresentation(controller, marker());
        void judge(TaikoHitResult result, int count = 1)
        {
            for (var i = 0; i < count; i++)
                don.OnJudged(new(0, new(TimeSpan.Zero, PlayableNoteKind.Don), result, TimeSpan.Zero, false));
        }

        judge(TaikoHitResult.Great, 9);
        Assert.Equal(new(0, null, "don_normal"), controller.Motions[^1]);
        judge(TaikoHitResult.Good);
        Assert.Equal(new(0, "don_combo", "don_normal"), controller.Motions[^1]);
        don.OnJudged(new(0, new(TimeSpan.Zero, PlayableNoteKind.Don), TaikoHitResult.Great, TimeSpan.Zero, true));
        Assert.Equal(new(0, "don_combo", "don_normal"), controller.Motions[^1]);

        judge(TaikoHitResult.Miss);
        Assert.Equal("don_miss", controller.Idles[^1]);
        judge(TaikoHitResult.Miss, 4);
        Assert.Single(controller.Idles);
        var frame = new LumenScenePlayer(1280, 720, [Assert.IsType<LumenSceneLayer>(don.Layer)]).CreateRenderSnapshot();
        Assert.Same(frame, don.Tint(frame));
        judge(TaikoHitResult.Miss);
        Assert.Equal("don_miss6", controller.Idles[^1]);
        Assert.Equal(new LumenRenderColor(0.5f, 0.5f, 0.5f, 1), Assert.Single(don.Tint(frame).Quads).MultiplyColor);
        judge(TaikoHitResult.Great);
        Assert.Equal(new(0, "don_miss_normal", "don_normal"), controller.Motions[^1]);

        don.SetGauge(TaikoGaugeState.Cleared);
        Assert.Equal(new(0, "don_norm_up", "don_norm_loop"), controller.Motions[^1]);
        var layer = Assert.IsType<LumenSceneLayer>(don.Layer);
        var plain = new LumenScenePlayer(1280, 720, [layer]).CreateRenderSnapshot();
        Assert.Same(plain, don.Tint(plain));
        don.SetGauge(TaikoGaugeState.Full);
        Assert.Equal(new(0, "don_full_gage", "don_norm_loop"), controller.Motions[^1]);
        var gold = Assert.Single(don.Tint(plain).Quads).AddColor;
        Assert.Equal(new LumenRenderColor(0.4906f, 0.4312f, 0.0203f, 0), gold);
        judge(TaikoHitResult.Great, 9);
        Assert.Equal(new(0, "don_full_combo", "don_norm_loop"), controller.Motions[^1]);
        don.SetGauge(TaikoGaugeState.Cleared);
        Assert.Equal("don_norm_loop", controller.Idles[^1]);
        don.SetGauge(TaikoGaugeState.BelowClear);
        Assert.Equal(new(0, "don_norm_down", "don_normal"), controller.Motions[^1]);

        don.SetGoGo(true);
        Assert.Equal(new(0, "don_sabi_start", "don_sabi"), controller.Motions[^1]);
        var reactions = controller.Motions.Count;
        judge(TaikoHitResult.Miss);
        judge(TaikoHitResult.Great, 10);
        Assert.Equal(reactions, controller.Motions.Count);
        Assert.Equal("don_sabi", controller.Idles[^1]);
        don.SetGoGo(false);
        Assert.Equal("don_normal", controller.Idles[^1]);
    }

    private static LumenSceneLayer marker()
    {
        var shape = new LmbShapeDefinition(2, 1, [], [new LmbShapeGeometry(
            [new(-4, -4, 0, 0), new(4, -4, 1, 0), new(4, 4, 1, 1), new(-4, 4, 0, 1)],
            0, 0, [], null!)], null!);
        var sprite = new LmbSpriteDefinition(1, 1, 1, 1, 0, [],
            [new LmbFrameLabelCommand(1, 0, 0, [], null!), new LmbShowFrameCommand(0, 1, [], null!),
             new LmbPlaceObjectCommand(2, 1, 1, 1, 0, 1, 0, 0, 0, uint.MaxValue, uint.MaxValue, [], null!)], null!);
        var movie = new LmbMovieDefinition(null!, null, [new(0, "", 0), new(1, "don1p", 0)],
            [], [new(1, 0, 0, 1, 0, 0)], [], [], [], [], [shape], [sprite], []);
        return new(new LumenPlayer(movie, 1280, 720, rootCharacterId: 1),
            LumenMatrix.Identity with { X = 200, Y = 92 }, 0, 0);
    }

    private sealed class Controller : IDonPresentationController
    {
        public List<DonPresentationLayout> Layouts { get; } = [];
        public void Reset(DonPresentationLayout layout) => Layouts.Add(layout);
        public LumenNativeSurfaceKey GetSurface(int playerIndex) => new($"synthetic-player:{playerIndex}");
        public List<DonMotionRequest> Motions { get; } = [];
        public void SetMotion(DonMotionRequest request) => Motions.Add(request);
        public List<string> Idles { get; } = [];
        public void SetIdle(int playerIndex, string loop) => Idles.Add(loop);
    }
}
