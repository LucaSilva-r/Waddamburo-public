using Waddamburo.App.Cli;

namespace Waddamburo.App.Tests;

public sealed class GameDataLayoutTests
{
    [Fact]
    public void MissingDataReportsAllRequiredPathsWithoutCreatingUserState()
    {
        var root = Path.Combine(Path.GetTempPath(), "waddamburo-missing-" + Guid.NewGuid().ToString("N"));
        var error = Assert.Throws<MissingGameDataException>(() => GameDataLayout.Resolve(root));
        Assert.Equal(root, error.Root);
        Assert.Contains(Path.Combine(root, "data", "sound"), error.Missing);
        Assert.Contains(Path.Combine(root, "data", "lumendata", "packed", "song_select", "packeddata.ddp"), error.Missing);
        Assert.Contains("USRDIR", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void EmptyDataDirectoriesStillReportMissingArchives()
    {
        using var fixture = new LayoutFixture();
        File.Delete(Path.Combine(fixture.Root, "data", "lumendata", "packed", "indicator", "packeddata.ddp"));
        var error = Assert.Throws<MissingGameDataException>(() => GameDataLayout.Resolve(fixture.Root));
        Assert.Single(error.Missing);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "waddamburo")));
    }

    [Fact]
    public void SuppliedAndOverriddenFontsDoNotProduceMissingFontWarnings()
    {
        using var fixture = new LayoutFixture();
        var local = Path.Combine(fixture.Root, "data", "font", "font.ttf");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllBytes(local, []);
        var found = GameDataLayout.Resolve(fixture.Root);
        Assert.Equal(local, found.FontPath);
        Assert.Null(found.MissingFontPath);
        var supplied = Path.Combine(fixture.Root, "other.otf");
        File.WriteAllBytes(supplied, []);
        var overridden = GameDataLayout.Resolve(fixture.Root, supplied);
        Assert.Equal(supplied, overridden.FontPath);
        Assert.Null(overridden.MissingFontPath);
        Assert.Throws<FileNotFoundException>(() => GameDataLayout.Resolve(fixture.Root, supplied + ".missing"));
    }

    [Fact]
    public void SystemFallbackRecordsTheExactOriginalFontInstallPath()
    {
        using var fixture = new LayoutFixture();
        var layout = GameDataLayout.Resolve(fixture.Root);
        Assert.True(File.Exists(layout.FontPath));
        Assert.Equal(Path.Combine(fixture.Root, "data", "font", "font.ttf"), layout.MissingFontPath);
    }

    private sealed class LayoutFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "waddamburo-layout-" + Guid.NewGuid().ToString("N"));
        public LayoutFixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "data", "don3d"));
            Directory.CreateDirectory(Path.Combine(Root, "data", "sound"));
            foreach (var archive in new[] { "attract/kidou", "song_select", "indicator" })
            {
                var path = Path.Combine(Root, "data", "lumendata", "packed", archive.Replace('/', Path.DirectorySeparatorChar), "packeddata.ddp");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, []);
            }
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
