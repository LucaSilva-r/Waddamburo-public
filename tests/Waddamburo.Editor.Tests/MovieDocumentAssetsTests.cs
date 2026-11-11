using Waddamburo.Formats.Package;

namespace Waddamburo.Editor.Tests;

public sealed class MovieDocumentAssetsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("waddamburo-assets-test").FullName;

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
        return new MovieDocument(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private MoviePackage.Assembled assemble(List<ActionScriptException>? errors = null) =>
        MoviePackage.Assemble(File.ReadAllText(Path.Combine(_directory, "movie.json")), file => File.ReadAllText(Path.Combine(_directory, file)), errors);

    [Fact]
    public void TheFileStaysCompactAndStillAssembles()
    {
        var document = open();
        document.Edit(root => root["frameRate"] = 30);
        var text = File.ReadAllText(document.Path);
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
        Assert.True(File.Exists(Path.Combine(_directory, "textures", "My_Star_2.svg")));
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
        File.WriteAllBytes(Path.Combine(_directory, "textures", "ball.png"), []);
        Assert.Equal(3, assemble().TextureFiles.Length);
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
        Assert.StartsWith("//", File.ReadAllText(document.FilePath("scripts/go.as")));

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
