using Waddamburo.Catalog;

namespace Waddamburo.Editor.Tests;

public sealed class EffectDocumentTests
{
    private static EffectDocument Document() => EffectDocument.Empty.AddTrack("don.yaw")
        .SetKey("don.yaw", new(0, 0)).SetKey("don.yaw", new(8, 360, ChartEase.OutBack));

    [Fact]
    public void EditsAreImmutableAndUnknownPropertiesOrKeyCollisionsLeaveTheOriginalIntact()
    {
        var document = Document();
        var moved = document.SetKey("don.yaw", new(12, 180), 8);
        Assert.Equal(8, document.Tracks["don.yaw"][1].Beat);
        Assert.Equal(12, moved.Tracks["don.yaw"][1].Beat);
        Assert.Throws<InvalidDataException>(() => document.SetKey("don.yaw", new(0, 180), 8));
        Assert.Throws<InvalidDataException>(() => document.AddTrack("unknown.x"));
        Assert.Throws<InvalidDataException>(() => document.SetKey("don.yaw", new(1, double.NaN)));
        Assert.Throws<InvalidDataException>(() => document.SetKey("don.yaw", new(1, 1, Curve: new(2, 0, 0, 1))));
        Assert.Equal(360, document.Tracks["don.yaw"][1].Value);
    }

    [Fact]
    public void MultiKeyMovementIsAtomicAndKeepsRelativeSpacingAcrossTracks()
    {
        var document = Document().AddTrack("notes.drunk").SetKey("notes.drunk", new(3, .5));
        HashSet<EffectKey> keys = [new("don.yaw", 8), new("notes.drunk", 3)];
        var moved = document.MoveKeys(keys, 2);
        Assert.True(moved.Contains(new("don.yaw", 10)));
        Assert.True(moved.Contains(new("notes.drunk", 5)));
        Assert.Throws<InvalidDataException>(() => document.MoveKeys(keys, -8)); // collides with don.yaw's first key
        Assert.True(document.Contains(new("notes.drunk", 3)));
        var deleted = moved.DeleteKeys(new HashSet<EffectKey> { new("don.yaw", 10), new("notes.drunk", 5) });
        Assert.Single(deleted.Tracks["don.yaw"]);
        Assert.Empty(deleted.Tracks["notes.drunk"]);
    }

    [Fact]
    public void ADragIsOneUndoStepAndCancelledDragsDoNotEnterHistory()
    {
        var before = Document();
        var history = new EffectHistory(before);
        HashSet<EffectKey> selected = [new("don.yaw", 8)];
        history.BeginGesture();
        history.Preview(before.MoveKeys(selected, 1));
        history.Preview(before.MoveKeys(selected, 2));
        history.EndGesture();
        Assert.True(history.Dirty);
        Assert.True(history.Undo());
        Assert.True(before.ContentEquals(history.Current));
        Assert.False(history.CanUndo);
        Assert.False(history.Dirty);
        Assert.True(history.Redo());
        Assert.True(history.Current.Contains(new("don.yaw", 10)));
        history.MarkSaved();
        history.BeginGesture(); history.Preview(before); history.CancelGesture();
        Assert.False(history.Dirty);
        Assert.True(history.Undo());
        Assert.True(history.Dirty);
        history.Edit(before.SetKey("don.yaw", new(4, 90)), "New branch");
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void StartingAFileOverEmbeddedEffectsCanBeUndoneWithoutLosingTheirPreviewState()
    {
        var history = new EffectHistory(EffectDocument.EmbeddedPreview);
        history.Edit(EffectDocument.Empty, "Start modchart");
        Assert.True(history.Dirty);
        Assert.False(history.Current.UsesEmbeddedEffects);
        history.Undo();
        Assert.True(history.Current.UsesEmbeddedEffects);
        Assert.False(history.Dirty);
    }

    [Theory]
    [InlineData(2.12, .25, 2)]
    [InlineData(2.13, .25, 2.25)]
    [InlineData(2.125, .25, 2.25)]
    [InlineData(2.13, 0, 2.13)]
    public void SnappingUsesTheSelectedBeatGrid(double beat, double interval, double expected) =>
        Assert.Equal(expected, EffectDocument.Snap(beat, interval));

    [Fact]
    public void CurvesHaveOneCanonicalRepresentationAcrossSaveAndReload()
    {
        var document = Document().SetKey("don.yaw", new(8, 360, ChartEase.OutBack, new(.3, 0, .2, 1)), 8);
        var read = EffectDocument.FromTracks(ChartModFile.ReadTracks(ChartModFile.Write(document.Tracks), null));
        Assert.True(document.ContentEquals(read));
        Assert.Equal(ChartEase.Linear, document.Tracks["don.yaw"][1].Ease);
    }
}
