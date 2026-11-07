using Waddamburo.Catalog;

namespace Waddamburo.Editor.Tests;

public sealed class EffectKeyPatternTests
{
    private static EffectDocument Source() => EffectDocument.Empty
        .SetKey("don.yaw", new(4, 0)).SetKey("don.yaw", new(8, 360, ChartEase.OutBack))
        .SetKey("camera.x", new(6, 10, Curve: new(.2, -.4, .8, 1.3)));

    [Fact]
    public void PastePreservesRelativeBeatsChannelsAndInterpolationInOneUndoStep()
    {
        var source = Source();
        HashSet<EffectKey> selected = [new("don.yaw", 4), new("don.yaw", 8), new("camera.x", 6)];
        var pattern = EffectKeyPattern.Capture(source, selected);
        var edit = pattern.Paste(source, 20);
        Assert.Equal(3, edit.Keys.Count);
        Assert.Equal(360, edit.Document.Tracks["don.yaw"].First(k => k.Beat == 24).Value);
        Assert.Equal(ChartEase.OutBack, edit.Document.Tracks["don.yaw"].First(k => k.Beat == 24).Ease);
        Assert.Equal(source.Tracks["camera.x"][0].Curve, edit.Document.Tracks["camera.x"].First(k => k.Beat == 22).Curve);
        Assert.True(source.Contains(new("don.yaw", 4)));
        var history = new EffectHistory(source);
        history.Edit(edit.Document, "Paste"); history.Undo();
        Assert.True(history.Current.ContentEquals(source)); Assert.False(history.CanUndo);
        history.Redo(); Assert.True(history.Current.Contains(new("camera.x", 22)));
    }

    [Fact]
    public void OneTrackCanBeRemappedAndPasteReplacesKeysOnlyAtMatchingBeats()
    {
        var source = Source();
        var pattern = EffectKeyPattern.Capture(source, new HashSet<EffectKey> { new("don.yaw", 4), new("don.yaw", 8) });
        var result = pattern.Paste(source, 6, "camera.x").Document;
        Assert.Equal(0, result.Tracks["camera.x"].First(k => k.Beat == 6).Value);
        Assert.Equal(360, result.Tracks["camera.x"].First(k => k.Beat == 10).Value);
        Assert.Equal(2, source.Tracks["don.yaw"].Length);
    }

    [Fact]
    public void ClosedPatternsShareEndpointsWithoutLosingClosingCurves()
    {
        var source = EffectDocument.Empty.SetKey("don.yaw", new(0, 0)).SetKey("don.yaw", new(2, 90))
            .SetKey("don.yaw", new(4, 0, ChartEase.InOutBack));
        var pattern = EffectKeyPattern.Capture(source, new HashSet<EffectKey> { new("don.yaw", 0), new("don.yaw", 2), new("don.yaw", 4) });
        var repeated = pattern.Repeat(source, 3, 4);
        Assert.Equal(9, repeated.Document.Tracks["don.yaw"].Length);
        Assert.Equal(16, repeated.Document.Tracks["don.yaw"][^1].Beat);
        Assert.All(repeated.Document.Tracks["don.yaw"].Where(k => k.Beat > 0 && k.Beat % 4 == 0),
            key => Assert.Equal(ChartEase.InOutBack, key.Ease));
    }

    [Fact]
    public void RepeatRejectsConflictingSeamsAndCollisionsAtomicallyUnlessReplacementIsChosen()
    {
        var source = Source();
        var pattern = EffectKeyPattern.Capture(source, new HashSet<EffectKey> { new("don.yaw", 4), new("don.yaw", 8) });
        Assert.Throws<InvalidDataException>(() => pattern.Repeat(source, 2, 4));
        Assert.Equal(360, source.Tracks["don.yaw"][1].Value);
        var repeated = pattern.Repeat(source, 2, 5).Document;
        Assert.Equal(new[] { 4d, 8, 9, 13, 14, 18 }, repeated.Tracks["don.yaw"].Select(k => k.Beat));
        Assert.Equal(0, pattern.Repeat(source, 1, 4, true).Document.Tracks["don.yaw"].First(k => k.Beat == 8).Value);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(1001, 4)]
    [InlineData(1, 0)]
    [InlineData(1, double.NaN)]
    public void RepeatLimitsInvalidRequests(int copies, double period)
    {
        var source = Source();
        var pattern = EffectKeyPattern.Capture(source, new HashSet<EffectKey> { new("don.yaw", 4) });
        Assert.Throws<InvalidDataException>(() => pattern.Repeat(source, copies, period));
    }
}
