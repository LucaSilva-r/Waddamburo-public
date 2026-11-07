using System.Numerics;
using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor.Tests;

public sealed class EffectGizmoTests
{
    [Theory]
    [InlineData(20, 40, 800, 600)]
    [InlineData(100, 80, 1000, 300)]
    public void ViewportMappingFitsTheStageAndRoundTripsLogicalCoordinates(float x, float y, float width, float height)
    {
        var view = EffectViewport.Fit(new(x, y, width, height));
        Assert.Equal(16f / 9, view.Rectangle.Width / view.Rectangle.Height, 5);
        var point = new Vector2(740, 250);
        var restored = view.ToStage(view.ToScreen(point));
        Assert.Equal(point.X, restored.X, 3); Assert.Equal(point.Y, restored.Y, 3);
        Assert.True(view.Contains(view.ToScreen(point)));
        Assert.False(view.Contains(new(x - 1, y - 1)));
    }

    [Fact]
    public void MovementUsesStageAxesBeforeTheRotatedScaledCamera()
    {
        var parent = StageEffects.Transform(40, 20, 90, 2, 3, 1, (640, 360))!.Value.Matrix;
        var delta = EffectGizmo.ParentVector(parent, new(-60, 20));
        Assert.Equal(10, delta.X, 4); Assert.Equal(20, delta.Y, 4);
        Assert.Equal(90, EffectGizmo.Rotation(new(10, 0), new(0, 10)), 5);
        Assert.Equal(-90, EffectGizmo.Rotation(new(0, 10), new(10, 0)), 5);
        Assert.Equal(2, EffectGizmo.Scale(new(3, 4), new(6, 8)), 5);
        Assert.Throws<InvalidDataException>(() => EffectGizmo.ParentVector(new LumenMatrix(), new(1, 1)));
    }

    [Fact]
    public void AutoKeyWritesChannelsAtomicallyAndPreservesExistingIncomingEasing()
    {
        var document = EffectDocument.Empty.SetKey("don.yaw", new(8, 90, ChartEase.OutBack));
        var changed = document.SetValuesAt(8, new Dictionary<string, double> { ["don.yaw"] = 180 }, false);
        Assert.Equal(ChartEase.OutBack, changed.Tracks["don.yaw"][0].Ease);
        Assert.Equal(180, changed.Tracks["don.yaw"][0].Value);
        var values = new Dictionary<string, double> { ["don.x"] = 10, ["don.y"] = 20 };
        Assert.Throws<InvalidDataException>(() => document.SetValuesAt(8, values, false));
        Assert.Single(document.Tracks);
        var keyed = document.SetValuesAt(8, values, true);
        Assert.Equal(10, keyed.Tracks["don.x"][0].Value);
        Assert.Equal(20, keyed.Tracks["don.y"][0].Value);
        values["don.y"] = double.NaN;
        Assert.Throws<InvalidDataException>(() => document.SetValuesAt(8, values, true));
        Assert.Single(document.Tracks);
    }
}
