using System.Text.Json.Nodes;

namespace Waddamburo.Editor.Tests;

public sealed class MovieDocumentTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("waddamburo-document-test").FullName;

    private MovieDocument open(string? json = null)
    {
        File.WriteAllText(Path.Combine(_directory, "movie.json"), json ?? """
            {
              // A comment the editor may drop.
              "format": 1, "root": "main",
              "shapes": { "box": { "rect": [0, 0, 10, 10] } },
              "texts": { "label": { "rect": [0, 0, 50, 20] } },
              "sprites": {
                "main": { "frames": 11, "layers": [
                  { "character": "box", "name": "a", "keys": [ { "frame": 0, "x": 100, "scale": 2 }, { "frame": 10, "x": 200, "alpha": 0 } ] },
                  { "character": "label", "depth": 7, "keys": [ { "frame": 0 } ] }
                ] }
              }
            }
            """);
        return new MovieDocument(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ArchiveReferenceClipsResolveTheirIntegerDepthsWithoutSerializingTheDescription()
    {
        const string movie = """
            { "format": 1, "root": "main",
              "shapes": { "body": { "rect": [0, 0, 20, 20] } },
              "sprites": { "main": { "frames": 3, "layers": [
                { "character": "body", "name": "left", "keys": [{ "frame": 0, "x": 20 }] },
                { "character": "body", "name": "right", "depth": 7, "keys": [{ "frame": 0, "x": 40 }] }
              ] } } }
            """;
        var assembled = Waddamburo.Formats.Package.MoviePackage.Assemble(movie);
        var definition = Waddamburo.Formats.Lmb.LmbSemanticReader.Read(Waddamburo.Formats.Lmb.LmbFile.Parse(assembled.Lmb)).Value;
        var (description, names) = Waddamburo.Formats.Package.MovieReference.Describe(definition, []);
        var document = new MovieDocument(description, names);
        var root = (string)description["root"]!;
        var layers = description["sprites"]![root]!["layers"]!.AsArray();
        foreach (var node in layers)
        {
            var layer = node!.AsObject();
            var depth = layer["depth"]!.GetValue<int>(); // The in-memory archive description really stores Int32.
            var resolved = document.Layer(root, depth, (string)layer["character"]!);
            Assert.Same(layer, resolved);
            var sampled = MovieDocument.KeyAt((JsonObject)resolved!.DeepClone(), 1);
            Assert.NotNull(MovieDocument.Value(sampled, "x"));
            Assert.Equal((1d, 1d, 1d), MovieDocument.Tint(sampled));
        }
        Assert.True(document.ReadOnly);
        Assert.False(document.IsDirty);
        Assert.False(document.CanUndo);
        Assert.Null(document.Layer(root, 8, (string)layers[0]!["character"]!));
    }

    [Fact]
    public void ClipsAreFoundByCharacterAndDepth()
    {
        var document = open();
        Assert.Equal(["box", "label", "main", null], new uint[] { 1, 2, 3, 4 }.Select(document.CharacterName));
        Assert.Equal("a", (string?)document.Layer("main", 1, "box")?["name"]);
        Assert.NotNull(document.Layer("main", 7, "label"));
        Assert.Null(document.Layer("main", 2, "label")); // Its depth is given, not its place in the list.
        Assert.Null(document.Layer("main", 1, "label"));
    }

    [Fact]
    public void ANewKeyStartsWithTheValuesTheLayerHasThere()
    {
        var document = open();
        var layer = document.Layer("main", 1, "box")!;
        var key = MovieDocument.KeyAt(layer, 5);
        Assert.Equal((150d, 1.5, 1.5, .5), (MovieDocument.Value(key, "x"), MovieDocument.Value(key, "scaleX"), MovieDocument.Value(key, "scaleY"), MovieDocument.Value(key, "alpha")));
        Assert.Null(key["y"]); // Left out while it is the default.
        Assert.Equal([0, 5, 10], layer["keys"]!.AsArray().Select(static each => (int)each!["frame"]!));
        Assert.Same(key, MovieDocument.KeyAt(layer, 5));

        var first = MovieDocument.KeyAt(layer, 0);
        MovieDocument.Set(first, "scaleX", 3);
        Assert.Equal((null, 3d, 2d), ((double?)first["scale"], (double)first["scaleX"]!, (double)first["scaleY"]!));
    }

    [Fact]
    public void EditsPreviewInMemoryAndSaveOnlyOnRequest()
    {
        var document = open();
        var changes = 0;
        document.Changed += () => changes++;
        double x() => (double)JsonNode.Parse(document.Text,
            documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!["sprites"]!["main"]!["layers"]![0]!["keys"]![0]!["x"]!;

        var original = File.ReadAllText(document.Path);
        document.Edit(_ => { }); // Nothing changed: nothing written, nothing to undo.
        Assert.False(document.CanUndo);
        Assert.Equal(0, changes);

        document.Edit(root => root["sprites"]!["main"]!["layers"]![0]!["keys"]![0]!["x"] = 110);
        document.Edit(root => root["sprites"]!["main"]!["layers"]![0]!["keys"]![0]!["x"] = 120, continues: true);
        document.Edit(root => root["sprites"]!["main"]!["layers"]![0]!["keys"]![0]!["x"] = 300);
        Assert.Equal((300d, 3), (x(), changes));
        Assert.Equal(original, File.ReadAllText(document.Path));
        Assert.True(document.IsDirty);

        document.Undo();
        Assert.Equal(120, x()); // The first two were one gesture.
        document.Undo();
        Assert.Equal(100, x());
        Assert.Contains("// A comment", File.ReadAllText(document.Path)); // Back to the very text it was opened with.
        Assert.False(document.CanUndo);
        Assert.False(document.IsDirty);
        document.Redo();
        document.Redo();
        Assert.Equal(300, x());
        Assert.False(document.CanRedo);
        document.Save();
        Assert.False(document.IsDirty);
        Assert.Contains("300", File.ReadAllText(document.Path));
        Assert.Equal(300d, (double)document.Layer("main", 1, "box")!["keys"]![0]!["x"]!); // The tree follows the file.
    }
}
