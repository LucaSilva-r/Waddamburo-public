using Waddamburo.Game.Don;
using Waddamburo.Formats.Lmb;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Game.Tests;

public sealed class TaikoDonPresentationTests
{
    [Fact]
    public void CharacterDrawsAboveSceneAndHandsPlacementToBalloonUntilItCloses()
    {
        var lane = new LumenRenderQuad(7, new(0, 184, 0, 0), new(1280, 184, 1, 0),
            new(1280, 360, 1, 1), new(0, 360, 0, 1),
            LumenRenderColor.White, LumenRenderColor.Transparent);
        var scene = new LumenRenderSnapshot(1280, 720, [lane]);
        var controller = new Controller();
        var presentation = new TaikoDonPresentation(controller, marker());
        Assert.Equal("don_normal", controller.Motions[^1].Loop);
        var normal = presentation.Compose(scene);
        Assert.Equal(lane, normal.Quads[0]);
        var character = Assert.Single(normal.Quads.Where(quad => quad.NativeSurface is not null));
        Assert.Equal(new LumenNativeSurfaceKey("synthetic-player:0"), character.NativeSurface);
        Assert.Equal(character, normal.Quads[^1]);
        Assert.Equal(448, character.BottomRight.X - character.TopLeft.X);
        Assert.Equal(256, character.BottomRight.Y - character.TopLeft.Y);
        Assert.Equal(-24, character.TopLeft.X);
        Assert.Equal(-36, character.TopLeft.Y);
        Assert.Equal(DonPresentationLayout.Gameplay, Assert.Single(controller.Layouts));
        presentation.SetBalloonVisible(true);
        Assert.Same(scene, presentation.Compose(scene));
        presentation.SetGoGo(true);
        Assert.Single(controller.Motions);
        presentation.SetBalloonVisible(false);
        Assert.Equal(2, controller.Layouts.Count);
        Assert.Equal(DonPresentationLayout.Gameplay, controller.Layouts[^1]);
        Assert.Equal("don_swing02", controller.Motions[^1].Loop);
        presentation.SetGoGo(false);
        Assert.Equal("don_normal", controller.Motions[^1].Loop);
        Assert.Equal(normal.Quads.ToArray(), presentation.Compose(scene).Quads.ToArray());
        Assert.Single(scene.Quads);
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
    }
}
