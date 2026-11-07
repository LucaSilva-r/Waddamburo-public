using System.Text.Json;
using Waddamburo.Catalog;

namespace Waddamburo.Editor.Tests;

public sealed class EffectEditorFilesTests
{
    [Fact]
    public void AdjacentTjaFilesArePrimaryAndSavingPreservesOtherCourses()
    {
        var folder = Directory.CreateTempSubdirectory("waddamburo-editor-");
        try
        {
            var song = Path.Combine(folder.FullName, "Song.wddmod");
            var player = Path.Combine(folder.FullName, "player", "synthetic.wddmod");
            File.WriteAllText(song, """{"wddmod":1,"author":"Synthetic","tracks":{"camera.x":[{"beat":0,"value":4}]},"courses":{"*":{"tracks":{"notes.drunk":[{"beat":0,"value":1}]}},"easy":{"tracks":{"don.yaw":[{"beat":0,"value":90}]}},"oni":{"tracks":{"don.yaw":[{"beat":0,"value":180}]}}}}""");
            var files = new EffectEditorFiles(player, TaikoCourse.Oni, song);
            Assert.Equal(180, files.Load()!.Tracks["don.yaw"][0].Value);
            var document = EffectDocument.Empty.AddTrack("don.yaw").SetKey("don.yaw", new(8, 360, Curve: new(.3, 0, .2, 1)));
            files.SavePlayer(document);
            Assert.Equal(180, files.Load()!.Tracks["don.yaw"][0].Value); // A legacy player override cannot shadow the adjacent file.
            files.Save(document);
            Assert.Equal(song, files.PrimaryPath);
            using var written = JsonDocument.Parse(File.ReadAllBytes(song));
            Assert.Equal("Synthetic", written.RootElement.GetProperty("author").GetString());
            Assert.Equal(90, ChartModFile.ReadTracks(File.ReadAllBytes(song), TaikoCourse.Easy)["don.yaw"][0].Value);
            Assert.Equal(1, ChartModFile.ReadTracks(File.ReadAllBytes(song), null)["notes.drunk"][0].Value);
            Assert.Empty(ChartModFile.ReadTracks(File.ReadAllBytes(song), TaikoCourse.Oni)["camera.x"]);
            Assert.Empty(ChartModFile.ReadTracks(File.ReadAllBytes(song), TaikoCourse.Oni)["notes.drunk"]);
            Assert.Equal(4, ChartModFile.ReadTracks(File.ReadAllBytes(song), TaikoCourse.Easy)["camera.x"][0].Value);
            Assert.Equal(document.Tracks["don.yaw"].AsEnumerable(), ChartModFile.ReadTracks(File.ReadAllBytes(song), TaikoCourse.Oni)["don.yaw"].AsEnumerable());
            Assert.Empty(Directory.EnumerateFiles(folder.FullName, "*.tmp", SearchOption.AllDirectories));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public void StockEffectsCanBeExportedSharedImportedAndSavedForTheCurrentChart()
    {
        var folder = Directory.CreateTempSubdirectory("waddamburo-sharing-");
        try
        {
            var shared = Path.Combine(folder.FullName, "Shared.wddmod");
            var document = EffectDocument.Empty.SetKey("don.yaw", new(8, 0)).SetKey("don.yaw", new(16, 360, Curve: new(.3, -.2, .7, 1.2)));
            EffectEditorFiles.Export(shared, document);
            var recipient = new EffectEditorFiles(Path.Combine(folder.FullName, "recipient.wddmod"), TaikoCourse.Oni);
            var imported = recipient.Import(shared);
            Assert.True(imported.ContentEquals(document));
            recipient.Save(imported);
            Assert.True(recipient.Load()!.ContentEquals(document));
            Assert.Throws<InvalidOperationException>(() => EffectEditorFiles.Export(shared, EffectDocument.EmbeddedPreview));
            Assert.True(recipient.Import(shared).ContentEquals(document));
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public void TjaSaveCreatesOnlyTheAdjacentFileAndIsIndependentOfPlayerHashChanges()
    {
        var folder = Directory.CreateTempSubdirectory("waddamburo-adjacent-");
        try
        {
            var song = Path.Combine(folder.FullName, "Authoring.wddmod");
            var player = Path.Combine(folder.FullName, "player.wddmod");
            var document = EffectDocument.Empty.SetKey("camera.x", new(4, 80));
            new EffectEditorFiles(player, TaikoCourse.Oni, song).Save(document);
            Assert.True(File.Exists(song)); Assert.False(File.Exists(player));
            var afterNoteEdit = new EffectEditorFiles(Path.Combine(folder.FullName, "other-hash.wddmod"), TaikoCourse.Oni, song);
            Assert.True(afterNoteEdit.Load()!.ContentEquals(document));
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public void BrokenSongFilesAreNotOverwrittenAndANewSongFileCanBeCreated()
    {
        var folder = Directory.CreateTempSubdirectory("waddamburo-editor-");
        try
        {
            var song = Path.Combine(folder.FullName, "Song.wddmod");
            var files = new EffectEditorFiles(Path.Combine(folder.FullName, "player.wddmod"), TaikoCourse.Oni, song);
            Assert.Null(files.Load());
            files.SaveSong(EffectDocument.Empty.AddTrack("don.yaw"));
            Assert.Empty(files.Load()!.Tracks["don.yaw"]);
            File.WriteAllText(song, "{ broken");
            Assert.Throws<InvalidDataException>(() => files.SaveSong(EffectDocument.Empty));
            Assert.Equal("{ broken", File.ReadAllText(song));
            Assert.Throws<InvalidOperationException>(() => files.SavePlayer(EffectDocument.EmbeddedPreview));
        }
        finally { folder.Delete(recursive: true); }
    }
}
