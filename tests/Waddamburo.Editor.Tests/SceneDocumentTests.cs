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
    // The scene view's drags go through the component editor's transform (MovieTransform): a 100x100 layer, its
    // bottom left square (9) pulled 100 px left, doubles in width about its top right corner, which stays put.
    [Fact]
    public void ACornerScalesAboutTheOppositeOneAndAGroupTurnsTogether()
    {
        var layer = new SkinScenes.Layer("a/b", "decoration", 0, 0, null);
        var (min, max) = (new System.Numerics.Vector2(0, 0), new System.Numerics.Vector2(100, 100));
        var anchor = MovieTransform.ScaleAnchor(9, min, max, (min + max) / 2, alt: false);
        var change = MovieTransform.Change(2, 9, new(0, 100), new(-100, 100), new(-100, 0), (min + max) / 2, anchor, shift: false);
        var scaled = MovieInspectorSession.Transformed(layer, change, 2)!;
        Assert.Equal((2f, 1f, -100f, 0f), (scaled.ScaleX, scaled.ScaleY, scaled.X, scaled.Y));
        // Two layers turned 90 degrees about the middle between them: they swap sides and turn too.
        var turn = MovieTransform.Change(1, StageHandles.Rotate, new(100, 0), new(0, 100), default, new(0, 0), new(0, 0), shift: false);
        var a = MovieInspectorSession.Transformed(layer with { X = 100 }, turn, 1)!;
        Assert.Equal(0, a.X, 2);
        Assert.Equal(100, a.Y, 2);
        Assert.Equal(90, a.Rotation, 2);
    }
}

public sealed class ScenePlaceTests
{
    [Fact]
    public void PlacesAreKeptByLayerEditsAndGoBackToTheLayout()
    {
        var root = Directory.CreateTempSubdirectory("waddamburo-place-test").FullName;
        try
        {
            var scenes = new SceneDocument(root);
            scenes.EditPlace("lane", new("lane", "place", 300, 280, null, Rotation: 10));
            scenes.EditPlace("gage_don_1p", new("x", "place", 400, 100, null));
            scenes.Edit("gameplay", list => list.Add(new("deco/stars", "decoration", 0, 0, null)));
            Assert.Equal((300f, 280f, 10f), (scenes.Places["lane"].X, scenes.Places["lane"].Y, scenes.Places["lane"].Rotation));
            Assert.Single(scenes.Layers("gameplay"));
            Assert.Contains("\"lane\": { \"x\": 300, \"y\": 280, \"rotation\": 10 }", scenes.Text);
            scenes.EditPlace("lane", null);
            Assert.False(scenes.Places.ContainsKey("lane"));
            Assert.Throws<InvalidDataException>(() => SkinScenes.Parse("""{ "format": 1, "scenes": { "boot": { "layers": [ { "movie": "a/b", "role": "boot" } ], "places": { "lane": { "x": 0 } } } } }"""));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AMatrixReadsBackAsPlaceScaleAndTurn()
    {
        var layer = new SkinScenes.Layer("a/b", "place", 10, 20, null, 2, .5f, 30);
        var back = SkinScenes.Layer.FromMatrix("a/b", "place", layer.Transform);
        Assert.Equal(2, back.ScaleX, 3);
        Assert.Equal(.5f, back.ScaleY, 3);
        Assert.Equal(30, back.Rotation, 2);
        Assert.Equal((10f, 20f), (back.X, back.Y));
    }
}
