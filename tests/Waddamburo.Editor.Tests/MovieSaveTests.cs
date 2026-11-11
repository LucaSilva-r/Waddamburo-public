using Waddamburo.Game;

namespace Waddamburo.Editor.Tests;

public sealed class MovieSaveTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("waddamburo-save-test").FullName;
    private MovieDocument open()
    {
        File.WriteAllText(Path.Combine(_directory, "movie.json"), """
            { "format": 1, "root": "main", "sprites": { "main": { "frames": 4, "layers": [] } } }
            """);
        return new(_directory);
    }
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void UnsavedArtworkPreviewsWithoutWritingAnyPackageFiles()
    {
        var document = open();
        var original = File.ReadAllText(document.Path);
        var source = Path.Combine(_directory, "source.svg");
        File.WriteAllText(source, "synthetic artwork bytes");
        var name = document.ImportTexture(source);
        document.AddShape("face", name, 24, 24);
        var relative = (string)document.Root["textures"]![name]!;
        var preview = LumenMovieContent.LoadPackage(_directory, bytes =>
        {
            Assert.Equal("synthetic artwork bytes", System.Text.Encoding.UTF8.GetString(bytes));
            return (1, 1, new byte[] { 255, 0, 0, 255 });
        }, movieJson: document.Text, readFile: document.ReadFile);
        Assert.Single(preview.Textures);
        Assert.True(document.IsDirty);
        Assert.Equal(original, File.ReadAllText(document.Path));
        Assert.False(Directory.Exists(Path.Combine(_directory, "textures")));
        Assert.False(File.Exists(Path.Combine(_directory, MovieDocument.MetadataFileName)));
        document.Save();
        Assert.False(document.IsDirty);
        Assert.Equal("synthetic artwork bytes", File.ReadAllText(document.FilePath(relative)));
        Assert.NotEqual(original, File.ReadAllText(document.Path));
        Assert.True(File.Exists(Path.Combine(_directory, MovieDocument.MetadataFileName)));
        document.Undo();
        Assert.True(document.IsDirty);
        document.Redo();
        Assert.False(document.IsDirty);
    }

    [Fact]
    public void UndoingAnUnsavedImportReturnsToCleanWithoutLeavingFiles()
    {
        var document = open();
        var source = Path.Combine(_directory, "source.png");
        File.WriteAllBytes(source, [1, 2, 3]);
        document.ImportTexture(source);
        Assert.True(document.IsDirty);
        document.Undo();
        Assert.False(document.IsDirty);
        Assert.False(Directory.Exists(Path.Combine(_directory, "textures")));
        document.Redo();
        Assert.True(document.IsDirty);
        document.Save();
        Assert.False(document.IsDirty);
    }

    [Fact]
    public void SavedScriptsCanBeEditedOutsideWithoutBeingOverwrittenByLaterSaves()
    {
        var document = open();
        document.AttachScript("main", "0", "scripts/start.as");
        Assert.False(File.Exists(document.FilePath("scripts/start.as")));
        document.Save();
        File.WriteAllText(document.FilePath("scripts/start.as"), "stop();");
        Assert.Equal("stop();", System.Text.Encoding.UTF8.GetString(document.ReadFile("scripts/start.as")));
        document.Edit(root => root["frameRate"] = 30);
        document.Save();
        Assert.Equal("stop();", File.ReadAllText(document.FilePath("scripts/start.as")));
        Assert.False(document.IsDirty);
    }

    [Fact]
    public void LabelNavigationSkipsDuplicatesAndDoesNotWrap()
    {
        int[] labels = [30, 0, 10, 10];
        Assert.Equal(10, MovieTransport.AdjacentLabel(15, -1, labels));
        Assert.Equal(30, MovieTransport.AdjacentLabel(10, 1, labels));
        Assert.Equal(0, MovieTransport.AdjacentLabel(10, -1, labels));
        Assert.Null(MovieTransport.AdjacentLabel(0, -1, labels));
        Assert.Null(MovieTransport.AdjacentLabel(30, 1, labels));
        Assert.Null(MovieTransport.AdjacentLabel(0, 1, []));
    }
}
