using System.IO.Compression;

namespace Waddamburo.Editor.Tests;

public sealed class SkinLintTests
{
    [Fact]
    public void ASkinsProblemsAreFoundWhereTheyAre()
    {
        var root = Directory.CreateTempSubdirectory("waddamburo-lint-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "skin.json"), """{ "format": 1, "name": "Test", "version": "1.0" }""");
            File.WriteAllText(Path.Combine(root, "scenes.json"), """{ "format": 1, "scenes": { "entry": { "layers": [ { "movie": "entry/entry", "role": "decoration" } ] } } }""");
            var package = Path.Combine(root, "entry", "entry");
            Directory.CreateDirectory(Path.Combine(package, "scripts"));
            File.WriteAllText(Path.Combine(package, "movie.json"), """
                { "format": 2, "root": "main", "textures": { "gone": "textures/gone.png" },
                  "sprites": { "main": { "frames": 2, "scripts": { "0": "scripts/main.as", "1": "scripts/broken.as" }, "layers": [] },
                               "made": { "frames": 1, "export": "made", "layers": [] } } }
                """);
            File.WriteAllText(Path.Combine(package, "scripts", "main.as"), "Wd.on(\"made.up\", function(v) {});\nstop();\n");
            File.WriteAllText(Path.Combine(package, "scripts", "broken.as"), "var = ;\n");
            Directory.CreateDirectory(Path.Combine(root, "previews"));
            File.WriteAllText(Path.Combine(root, "previews", "nowhere.json"), """{ "format": 1, "scenarios": [] }""");
            Directory.CreateDirectory(Path.Combine(root, ".editor"));
            File.WriteAllText(Path.Combine(root, ".editor", "cache"), "x");

            var problems = SkinLint.Check(root);
            string[] texts = [.. problems.Select(static problem => $"{problem.Level} {problem.File}:{problem.Line} {problem.Text}")];
            Assert.Contains(texts, static text => text.StartsWith("Warning skin.json:0", StringComparison.Ordinal) && text.Contains("author"));
            Assert.Contains(texts, static text => text.StartsWith("Error scenes.json", StringComparison.Ordinal) && text.Contains("player-entry"));
            Assert.True(texts.Any(static text => text.StartsWith("Error entry/entry/scripts/broken.as:1", StringComparison.Ordinal)), string.Join("\n", texts));
            Assert.Contains(texts, static text => text.StartsWith("Error entry/entry/scripts/broken.as:1", StringComparison.Ordinal));
            Assert.Contains(texts, static text => text.StartsWith("Error entry/entry/scripts/main.as:1", StringComparison.Ordinal) && text.Contains("made.up"));
            Assert.Contains(texts, static text => text.Contains("gone.png is missing", StringComparison.Ordinal));
            Assert.Contains(texts, static text => text.Contains("nowhere", StringComparison.Ordinal));
            // An exported sprite is the scripts' to make: not unused.
            Assert.DoesNotContain(texts, static text => text.Contains("\"made\"", StringComparison.Ordinal));

            var zip = SkinLint.Export(root);
            using (var archive = ZipFile.OpenRead(zip))
            {
                Assert.Contains(archive.Entries, static entry => entry.FullName == "entry/entry/movie.json");
                Assert.DoesNotContain(archive.Entries, static entry => entry.FullName.StartsWith(".editor", StringComparison.Ordinal));
            }
            Assert.EndsWith("Test-1.0.zip", zip, StringComparison.Ordinal);
            File.Delete(zip);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // The skin the engine ships lints clean of errors (its own CI runs --lint-skin too).
    [Fact]
    public void TheShippedSkinHasNoErrors()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "movies"));
        var errors = SkinLint.Check(root).Where(static problem => problem.Level == SkinLint.Level.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(static problem => $"{problem.File}:{problem.Line} {problem.Text}")));
    }
}

public sealed class WdDumpTests
{
    // The VS Code extension's copy of the engine's names is the engine's (regenerate: Waddamburo.App --dump-wd=...).
    [Fact]
    public void TheExtensionsNamesAreCurrent()
    {
        var file = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "vscode-waddamburo", "wd-names.json"));
        Assert.True(File.ReadAllText(file) == WdDump.Json(),
            "tools/vscode-waddamburo/wd-names.json is behind WdNames: run Waddamburo.App --dump-wd=tools/vscode-waddamburo/wd-names.json");
    }
}
