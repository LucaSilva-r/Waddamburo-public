using System.Xml.Linq;

namespace Waddamburo.Architecture.Tests;

public sealed class DependencyRulesTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void ProjectReferencesFollowTheAllowedGraph()
    {
        var expected = new Dictionary<string, string[]>
        {
            ["Waddamburo.Catalog"] = [],
            ["Waddamburo.Formats"] = [],
            ["Waddamburo.Lumen"] = ["Waddamburo.Formats"],
            ["Waddamburo.Game"] = ["Waddamburo.Catalog", "Waddamburo.Formats", "Waddamburo.Lumen"],
            ["Waddamburo.Platform.Sdl"] = ["Waddamburo.Formats", "Waddamburo.Lumen"],
            ["Waddamburo.Providers.OsuLazer"] = ["Waddamburo.Catalog"],
            ["Waddamburo.Providers.Stock"] = ["Waddamburo.Catalog"],
            ["Waddamburo.Providers.Tja"] = ["Waddamburo.Catalog"],
            ["Waddamburo.Services"] = ["Waddamburo.Game"],
            // Shop, dan, AI battle, credit, events: state, rules and online calls any frontend drives; never a frontend.
            ["Waddamburo.Features"] = ["Waddamburo.Game", "Waddamburo.Services"],
            // The game's own movies (Bandai's): only the App, as the composition root, knows they exist.
            // It draws with the engine's platform layer (user, 2026-11-15).
            ["Waddamburo.Skins.Green"] = ["Waddamburo.Features", "Waddamburo.Game", "Waddamburo.Platform.Sdl"],
            ["Waddamburo.Upscale"] = [],
            ["Waddamburo.Editor"] = ["Waddamburo.Catalog", "Waddamburo.Game", "Waddamburo.Lumen", "Waddamburo.Platform.Sdl"],
            ["Waddamburo.App"] = ["Waddamburo.Catalog", "Waddamburo.Editor", "Waddamburo.Features", "Waddamburo.Game", "Waddamburo.Platform.Sdl", "Waddamburo.Providers.OsuLazer", "Waddamburo.Providers.Stock", "Waddamburo.Providers.Tja", "Waddamburo.Services", "Waddamburo.Skins.Green", "Waddamburo.Upscale"],
        };

        foreach (var (project, allowed) in expected)
        {
            var path = Path.Combine(Root, "src", project, $"{project}.csproj");
            var actual = XDocument.Load(path)
                .Descendants("ProjectReference")
                .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value))
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(allowed.Order(StringComparer.Ordinal), actual);
        }
    }

    [Fact]
    public void NativeImportsAreConfinedToPlatformAdapters()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Waddamburo.Platform.Sdl{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("DllImport", StringComparison.Ordinal)
                        || File.ReadAllText(path).Contains("LibraryImport", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Root, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Waddamburo.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Waddamburo repository root not found.");
    }
}
