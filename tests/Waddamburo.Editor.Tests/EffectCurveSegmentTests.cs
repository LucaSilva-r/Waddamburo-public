using Waddamburo.Catalog;

namespace Waddamburo.Editor.Tests;

public sealed class EffectCurveSegmentTests
{
    [Theory]
    [InlineData(10, 110)]
    [InlineData(110, 10)]
    [InlineData(20, 20)]
    public void HandlesRoundTripForRisingFallingAndFlatSegments(double start, double end)
    {
        var curve = new ChartModCurve(.3, -.2, .8, 1.4);
        var segment = new EffectCurveSegment(new(4, start), new(12, end, Curve: curve));
        for (var i = 0; i < 2; i++)
        {
            var (beat, value) = segment.Handle(i);
            var moved = segment.MoveHandle(i, beat, value);
            Assert.Equal(curve.X1, moved.X1, 12); Assert.Equal(curve.X2, moved.X2, 12);
            Assert.Equal(curve.Y1, moved.Y1, 12); Assert.Equal(curve.Y2, moved.Y2, 12);
        }
    }

    [Fact]
    public void DraggingClampsHorizontalHandlesAndAllowsOvershootWithoutChangingTheOtherHandle()
    {
        var segment = new EffectCurveSegment(new(4, 100), new(12, 0, Curve: new(.3, 0, .7, 1)));
        Assert.Equal(new ChartModCurve(0, -.5, .7, 1), segment.MoveHandle(0, -10, 150));
        Assert.Equal(new ChartModCurve(.3, 0, 1, 1.5), segment.MoveHandle(1, 20, -50));
        var history = new EffectHistory(EffectDocument.Empty.SetKey("don.yaw", segment.Start).SetKey("don.yaw", segment.End));
        history.BeginGesture();
        history.Preview(history.Current.SetKey("don.yaw", segment.End with { Curve = segment.MoveHandle(0, 8, 130) }, 12));
        history.EndGesture();
        Assert.True(history.Undo());
        Assert.Equal(segment.End, history.Current.Tracks["don.yaw"][1]);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void TwoDimensionalKeyMovementPreservesGroupOffsetsAndRejectsCollisions()
    {
        var document = EffectDocument.Empty.SetKey("don.yaw", new(0, 0)).SetKey("don.yaw", new(8, 360))
            .SetKey("camera.rotation", new(4, 30));
        HashSet<EffectKey> keys = [new("don.yaw", 8), new("camera.rotation", 4)];
        var moved = document.MoveKeys(keys, 2, -15);
        Assert.Equal(345, moved.Tracks["don.yaw"][1].Value);
        Assert.Equal(15, moved.Tracks["camera.rotation"][0].Value);
        Assert.Equal(6, moved.Tracks["camera.rotation"][0].Beat);
        Assert.Throws<InvalidDataException>(() => document.MoveKeys(keys, -8, -15));
        Assert.Equal(360, document.Tracks["don.yaw"][1].Value);
    }
}
