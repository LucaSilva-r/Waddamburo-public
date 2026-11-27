using Waddamburo.Editor;
using Waddamburo.Game.Scenes;

namespace Waddamburo.Editor.Tests;

public sealed class SceneDocumentTests
{
    [Fact]
    public void EditsAreCheckedUndoneAsOneDragAndSaved()
    {
        var root = Directory.CreateTempSubdirectory("waddamburo-scenes-test").FullName;
        try
        {
            var scenes = new SceneDocument(root);
            Assert.Empty(scenes.Layers("entry"));
            Assert.False(scenes.Composes("entry"));
            // A screen without its required role is refused, and nothing changes.
            Assert.Throws<InvalidOperationException>(() => scenes.Edit("entry", list => list.Add(new("entry/decor", "decoration", 0, 0, null))));
            Assert.False(scenes.IsDirty);
            scenes.Edit("entry", list => list.Add(new("entry/entry", "player-entry", 0, 0, null)));
            scenes.Edit("entry", list => list.Add(new("indicator/player_name", "indicator-part", 60, 638, 0)));
            // A drag: many edits, one undo step.
            scenes.Edit("entry", list => list[1] = list[1] with { X = 70 });
            scenes.Edit("entry", list => list[1] = list[1] with { X = 80 }, continues: true);
            Assert.Equal(80, scenes.Layers("entry")[1].X);
            scenes.Undo();
            Assert.Equal(60, scenes.Layers("entry")[1].X);
            scenes.Redo();
            scenes.Edit("entry", list => list[0] = list[0] with { ScaleX = 1.5f, ScaleY = .5f, Rotation = -15 });
            scenes.Save();
            var saved = SkinScenes.Find([root])!;
            Assert.Equal(["entry/entry", "indicator/player_name"], saved.Layers("entry")!.Select(static layer => layer.Movie));
            Assert.Equal(0, saved.Layers("entry")![1].Side);
            Assert.Equal(80, saved.Layers("entry")![1].X);
            Assert.Equal((1.5f, .5f, -15f), (saved.Layers("entry")![0].ScaleX, saved.Layers("entry")![0].ScaleY, saved.Layers("entry")![0].Rotation));
            // Turned 90°: x goes down the screen.
            var turned = new SkinScenes.Layer("a/b", "decoration", 100, 0, null, Rotation: 90).Transform.Transform(10, 0);
            Assert.Equal(100, turned.X, 3);
            Assert.Equal(10, turned.Y, 3);
            Assert.False(scenes.IsDirty);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class SceneScaleTests
{
    // A 100x100 movie with its origin at its top left. Dragging the bottom left corner 100 px further left doubles the
    // width about the opposite (top right) corner, which stays put: the layer moves left by 100.
    [Fact]
    public void ACornerScalesAboutTheOppositeOne()
    {
        var layer = new SkinScenes.Layer("a/b", "decoration", 0, 0, null);
        var scaled = MovieInspectorSession.scaled(layer, new(0, 100), new(-100, 100), handle: 3, anchor: new(100, 0), proportional: false, snapping: false);
        Assert.Equal((2f, 1f, -100f, 0f), (scaled.ScaleX, scaled.ScaleY, scaled.X, scaled.Y));
        // Turned 90°, the same corner pulled the same way along the layer's own x: the anchor still stays.
        var turned = layer with { X = 200, Rotation = 90 };
        var anchorBefore = turned.Transform.Transform(100, 0);
        var grown = MovieInspectorSession.scaled(turned, new(100, 0), new(100, -100), handle: 3, anchor: new(100, 0), proportional: false, snapping: false);
        var anchorAfter = grown.Transform.Transform(100, 0);
        Assert.Equal(2, grown.ScaleX, 3);
        Assert.Equal(anchorBefore.X, anchorAfter.X, 1);
        Assert.Equal(anchorBefore.Y, anchorAfter.Y, 1);
    }
}

public sealed class SceneLayoutTests
{
    [Fact]
    public void LayoutPointsAreKeptByLayerEditsAndGoBackToStandard()
    {
        var root = Directory.CreateTempSubdirectory("waddamburo-layout-test").FullName;
        try
        {
            var scenes = new SceneDocument(root);
            scenes.EditLayout(14, new(300, 280));
            scenes.EditLayout(13, new(910, 40));
            scenes.Edit("gameplay", list => list.Add(new("deco/stars", "decoration", 0, 0, null)));
            Assert.Equal(new System.Numerics.Vector2(300, 280), scenes.Layout[14]);
            Assert.Single(scenes.Layers("gameplay"));
            Assert.Contains("\"layout\": { \"13\": [910, 40], \"14\": [300, 280] }", scenes.Text);
            scenes.EditLayout(14, null);
            Assert.False(scenes.Layout.ContainsKey(14));
            // A layout anywhere but gameplay, or a point past the layout's, is refused.
            Assert.Throws<InvalidDataException>(() => SkinScenes.Parse("""{ "format": 1, "scenes": { "boot": { "layers": [ { "movie": "a/b", "role": "boot" } ], "layout": { "1": [0, 0] } } } }"""));
            Assert.Throws<InvalidDataException>(() => SkinScenes.Parse("""{ "format": 1, "scenes": { "gameplay": { "layers": [], "layout": { "99": [0, 0] } } } }"""));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
