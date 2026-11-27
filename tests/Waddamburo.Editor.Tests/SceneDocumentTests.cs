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
