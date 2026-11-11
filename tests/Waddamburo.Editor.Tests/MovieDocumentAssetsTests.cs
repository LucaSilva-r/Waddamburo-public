using Waddamburo.Formats.Package;

namespace Waddamburo.Editor.Tests;

public sealed class MovieDocumentAssetsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("waddamburo-assets-test").FullName;

    private MovieDocument? _working;

    private MovieDocument open()
    {
        File.WriteAllText(Path.Combine(_directory, "movie.json"), """
            {
              "format": 1, "root": "main",
              "textures": { "ball": "textures/ball.png" },
              "shapes": { "ball": { "texture": "ball", "rect": [-8, -8, 16, 16] }, "spare": { "rect": [0, 0, 4, 4] } },
              "sprites": {
                "main": { "frames": 4, "labels": { "go": 2 }, "layers": [
                  { "character": "ball", "name": "b", "keys": [ { "frame": 0, "x": 10 }, { "frame": 3, "x": 20 } ] } ] }
              }
            }
            """);
        return _working = new MovieDocument(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private MoviePackage.Assembled assemble(List<ActionScriptException>? errors = null) =>
        MoviePackage.Assemble(_working!.Text, file => System.Text.Encoding.UTF8.GetString(_working.ReadFile(file)), errors);

    [Fact]
    public void TheFileStaysCompactAndStillAssembles()
    {
        var document = open();
        document.Edit(root => root["frameRate"] = 30);
        var text = document.Text;
        Assert.Contains("""{ "frame": 0, "x": 10 }""", text);
        Assert.Contains("\"ball\": { \"texture\": \"ball\", \"rect\": [-8, -8, 16, 16] }", text);
        Assert.True(text.Split('\n').Length < 30, text);
        assemble();
    }

    [Fact]
    public void AssetsAreImportedRenamedAndOnlyDeletedWhenUnused()
    {
        var document = open();
        var source = Path.Combine(_directory, "My Star!.svg");
        File.WriteAllText(source, "<svg/>");
        Assert.Equal("My_Star", document.ImportTexture(source));
        Assert.Equal("My_Star_2", document.ImportTexture(source));
        Assert.False(File.Exists(Path.Combine(_directory, "textures", "My_Star_2.svg")));
        Assert.Equal("<svg/>", System.Text.Encoding.UTF8.GetString(document.ReadFile("textures/My_Star_2.svg")));
        Assert.Throws<InvalidOperationException>(() => document.ImportTexture(Path.Combine(_directory, "movie.json")));

        Assert.Equal("star", document.AddShape("star", "My_Star", 64, 32, [0, 0, .5, 1]));
        Assert.Equal("ball_2", document.AddShape("ball", null, 8, 8)); // Characters share one space of names.
        Assert.Equal(["shape star"], document.Uses("textures", "My_Star"));
        Assert.Equal(["main: b"], document.Uses("shapes", "ball"));
        Assert.Equal(["the movie's root"], document.Uses("sprites", "main"));
        Assert.Equal([("textures", "My_Star_2"), ("shapes", "spare"), ("shapes", "star"), ("shapes", "ball_2")], document.Unused());

        document.Rename("textures", "ball", "orb");
        document.Rename("shapes", "ball", "orb");
        document.Rename("sprites", "main", "stage");
        Assert.Equal(("orb", "orb", "stage"), ((string?)document.Root["shapes"]!["orb"]!["texture"],
            (string?)document.Root["sprites"]!["stage"]!["layers"]![0]!["character"], (string?)document.Root["root"]));
        Assert.Equal(["orb", "spare", "star", "ball_2"], document.Root["shapes"]!.AsObject().Select(static each => each.Key)); // In place.
        Assert.Throws<InvalidOperationException>(() => document.Rename("shapes", "spare", "stage"));
        Assert.Throws<InvalidOperationException>(() => document.Rename("shapes", "spare", "no good"));
        Assert.Throws<InvalidOperationException>(() => document.Delete("shapes", "orb"));
        document.Delete("shapes", "spare");
        Assert.False(document.Exists("shapes", "spare"));
        document.Undo();
        Assert.True(document.Exists("shapes", "spare"));
        Assert.Throws<InvalidOperationException>(() => document.FilePath("../outside.as"));
        Directory.CreateDirectory(Path.Combine(_directory, "textures"));
        File.WriteAllBytes(Path.Combine(_directory, "textures", "ball.png"), []);
        Assert.Equal(3, assemble().TextureFiles.Length);
    }

    [Fact]
    public void ReplacingArtworkPreservesNamesAndRestoresFilesThroughHistory()
    {
        var document = open();
        Directory.CreateDirectory(Path.Combine(_directory, "textures"));
        File.WriteAllText(document.FilePath("textures/ball.png"), "original");
        var replacement = Path.Combine(_directory, "new.png");
        File.WriteAllText(replacement, "replacement");
        var original = File.ReadAllText(document.Path);
        document.ReplaceTexture("ball", replacement);
        var replacedPath = (string)document.Root["textures"]!["ball"]!;
        Assert.NotEqual("textures/ball.png", replacedPath);
        Assert.Equal("replacement", System.Text.Encoding.UTF8.GetString(document.ReadFile(replacedPath)));
        Assert.Equal("original", File.ReadAllText(document.FilePath("textures/ball.png")));
        Assert.Equal("ball", (string?)document.Root["shapes"]!["ball"]!["texture"]);
        Assert.Equal("b", (string?)document.Root["sprites"]!["main"]!["layers"]![0]!["name"]);
        document.Undo();
        Assert.Equal(original.TrimEnd(), File.ReadAllText(document.Path).TrimEnd());
        Assert.Equal("original", System.Text.Encoding.UTF8.GetString(document.ReadFile((string)document.Root["textures"]!["ball"]!)));
        document.Redo();
        Assert.Equal("replacement", System.Text.Encoding.UTF8.GetString(document.ReadFile((string)document.Root["textures"]!["ball"]!)));

        document.Save();
        // Replacing from the current artwork itself is safe, too (no overwrite of history).
        document.ReplaceTexture("ball", document.FilePath(replacedPath));
        Assert.NotEqual(replacedPath, (string?)document.Root["textures"]!["ball"]);
        document.Undo();
        Assert.Equal(replacedPath, (string?)document.Root["textures"]!["ball"]);
        assemble();
    }

    [Fact]
    public void UndoneImportsNeverOverwriteFilesStillNeededByHistory()
    {
        var document = open();
        var source = Path.Combine(_directory, "star.svg");
        File.WriteAllText(source, "first artwork");
        var name = document.ImportTexture(source);
        var first = (string)document.Root["textures"]![name]!;
        document.Save();
        document.Undo();
        File.WriteAllText(source, "second artwork");
        Assert.Equal(name, document.ImportTexture(source));
        var second = (string)document.Root["textures"]![name]!;
        Assert.NotEqual(first, second);
        Assert.Equal("first artwork", File.ReadAllText(document.FilePath(first)));
        Assert.Equal("second artwork", System.Text.Encoding.UTF8.GetString(document.ReadFile(second)));
        document.Undo();
        document.Redo();
        Assert.Equal(second, (string?)document.Root["textures"]![name]);
    }

    [Fact]
    public void ShapeArtworkEditsKeepTheSkeletonAndAssembleAfterUndoRedo()
    {
        var document = open();
        var skeleton = document.Root["sprites"]!.ToJsonString();
        document.SetShapeRect("ball", -16, -12, 32, 24);
        document.SetShapeUv("ball", [.25, 0, .75, 1]);
        document.SetShapeTexture("ball", null);
        Assert.Empty(document.Uses("textures", "ball"));
        Assert.Equal(skeleton, document.Root["sprites"]!.ToJsonString());
        assemble();
        document.Undo();
        Assert.Equal("ball", (string?)document.Root["shapes"]!["ball"]!["texture"]);
        document.Redo();
        Assert.Null(document.Root["shapes"]!["ball"]!["texture"]);
        document.SetShapeUv("ball", null);
        Assert.Null(document.Root["shapes"]!["ball"]!["uv"]);
        var before = File.ReadAllText(document.Path);
        Assert.Throws<InvalidOperationException>(() => document.SetShapeTexture("ball", "missing"));
        Assert.Throws<InvalidOperationException>(() => document.SetShapeRect("ball", 0, 0, double.PositiveInfinity, 2));
        Assert.Throws<InvalidOperationException>(() => document.SetShapeRect("ball", 0, 0, -1, 2));
        Assert.Throws<InvalidOperationException>(() => document.SetShapeUv("ball", [0, 0, 0, 1]));
        Assert.Throws<InvalidOperationException>(() => document.SetShapeUv("ball", [0, 0, double.NaN, 1]));
        Assert.Throws<InvalidOperationException>(() => document.SetShapeUv("ball", [-1, 0, 1, 1]));
        Assert.Equal(before, File.ReadAllText(document.Path));
        Assert.Equal(skeleton, document.Root["sprites"]!.ToJsonString());
        assemble();
    }

    [Fact]
    public void ALayerMovesToAnotherSpriteButNeverInsideItself()
    {
        var document = open();
        var box = document.AddSprite("box");
        var inner = document.AddSprite("inner");
        var layers = document.Root["sprites"]!["main"]!["layers"]!.AsArray();
        var count = layers.Count;
        document.Edit(root =>
        {
            layers.Add((System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonNode.Parse($$"""{ "character": "{{box}}", "name": "box", "keys": [ { "frame": 0 } ] }""")!);
            root["sprites"]![box]!["layers"] = System.Text.Json.Nodes.JsonNode.Parse($$"""[ { "character": "{{inner}}", "name": "inner", "keys": [ { "frame": 0, "x": 5 } ] } ]""");
        });
        // A value set as a float by an edit reads back like any other number.
        document.Edit(root => root["sprites"]![box]!["layers"]![0]!["keys"]![0]!["x"] = 7f);
        Assert.Equal(7, MovieDocument.Value((System.Text.Json.Nodes.JsonObject)document.Root["sprites"]![box]!["layers"]![0]!["keys"]![0]!, "x"));
        document.MoveLayerTo(box, 0, "main", 0);
        Assert.Equal("inner", (string?)layers[0]!["name"]);
        Assert.Empty(document.Root["sprites"]![box]!["layers"]!.AsArray());
        Assert.Throws<InvalidOperationException>(() => document.MoveLayerTo("main", count + 1, box, 0)); // box into box
        document.MoveLayerTo("main", 0, box, 0);
        Assert.Throws<InvalidOperationException>(() => document.MoveLayerTo("main", count, inner, 0)); // box into what it holds
        assemble();
    }

    [Fact]
    public void LayersAndKeysTakeTheirSettings()
    {
        var document = open();
        Assert.Equal(1, document.DuplicateLayer("main", 0));
        document.Edit(root => root["sprites"]!["main"]!["layers"]![1]!["name"] = "copy");
        document.MoveLayer("main", 1, 0);
        Assert.Equal(["copy", "b"], document.Root["sprites"]!["main"]!["layers"]!.AsArray().Select(static each => (string?)each!["name"]));
        document.SetBlend("main", 1, "add");
        document.SetMask("main", 0, 1);
        Assert.Throws<InvalidOperationException>(() => document.SetMask("main", 1, 1)); // Nothing above the top layer.
        Assert.Throws<InvalidOperationException>(() => document.SetBlend("main", 0, "glow"));
        Assert.Throws<InvalidOperationException>(() => document.SetUntil("main", 0, 3)); // On its last key.
        document.Edit(root => root["sprites"]!["main"]!["frames"] = 8);
        document.SetUntil("main", 0, 5);

        var layer = (System.Text.Json.Nodes.JsonObject)document.Root["sprites"]!["main"]!["layers"]![1]!;
        var first = MovieDocument.KeyAt(layer, 0);
        MovieDocument.SetTint(first, 1, 0, 0);
        MovieDocument.SetEase(first, "out");
        Assert.Equal("out", MovieDocument.Ease(first));
        var middle = MovieDocument.KeyAt(layer, 1); // A third of the way in time, 5/9 of the way eased out.
        Assert.Equal((15.556, (1d, .556, .556)), (MovieDocument.Value(middle, "x"), MovieDocument.Tint(middle)));
        MovieDocument.SetEase(first, "hold");
        Assert.Equal(("hold", null), (MovieDocument.Ease(first), (string?)first["ease"]));
        document.Edit(root => root["frameRate"] = 60); // Written with the key changes made on the tree above.
        assemble();
    }

    [Fact]
    public void ScriptsAreAttachedListedAndTheirErrorsCollected()
    {
        var document = open();
        document.AttachScript("main", "go", "scripts/go.as");
        document.AttachScript("main", "0", "scripts/start.as");
        document.AddClass("scripts/classes.as");
        document.AddClass("scripts/classes.as");
        Assert.Throws<InvalidOperationException>(() => document.AttachScript("main", "9", "scripts/late.as"));
        Assert.Throws<InvalidOperationException>(() => document.AttachScript("main", "0", "scripts/start.txt"));
        Assert.Equal([new("scripts/classes.as", null, null), new("scripts/go.as", "main", "go"), new("scripts/start.as", "main", "0")],
            document.Scripts());
        Assert.StartsWith("//", System.Text.Encoding.UTF8.GetString(document.ReadFile("scripts/go.as")));
        document.Save();

        File.WriteAllText(document.FilePath("scripts/go.as"), "var a = ;");
        File.WriteAllText(document.FilePath("scripts/start.as"), "var fine = 1;\nstop(;");
        Assert.Throws<ActionScriptException>(() => assemble());
        var errors = new List<ActionScriptException>();
        assemble(errors); // The movie still assembles, without the two scripts.
        Assert.Equal([("scripts/go.as", 1, "Unexpected ';'."), ("scripts/start.as", 2, "Unexpected ';'.")],
            errors.OrderBy(static each => each.File).Select(static each => (each.File, each.Line, each.Problem)));

        document.DetachScript("main", "go");
        document.DetachScript("main", "0");
        document.RemoveClass("scripts/classes.as");
        Assert.Empty(document.Scripts());
        Assert.Null(document.Root["classes"]);
        Assert.Null(document.Root["sprites"]!["main"]!["scripts"]);
    }
}
