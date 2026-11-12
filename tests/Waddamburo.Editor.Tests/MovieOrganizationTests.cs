using System.Text.Json.Nodes;
using Waddamburo.Formats.Package;

namespace Waddamburo.Editor.Tests;

public sealed class MovieOrganizationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("waddamburo-organization-test").FullName;
    private MovieDocument open()
    {
        File.WriteAllText(Path.Combine(_directory, "movie.json"), """
            { "format": 1, "root": "main", "textures": { "art": "textures/art.svg" },
              "shapes": { "face": { "texture": "art", "rect": [0, 0, 20, 20] } },
              "sprites": { "main": { "frames": 1, "export": "ContractSymbol", "layers": [
                { "character": "face", "name": "contractChild", "keys": [{ "frame": 0 }] } ] } } }
            """);
        return new MovieDocument(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void RenamePreservesIdentityReferencesAndContractNamesAcrossReloadAndHistory()
    {
        var document = open();
        var ids = document.Assets().ToDictionary(asset => asset.Group, asset => asset.Id);
        Assert.All(ids.Values, id => Assert.True(Guid.TryParse(id, out _)));
        Assert.False(File.Exists(Path.Combine(_directory, MovieDocument.MetadataFileName))); // Opening a legacy package writes nothing.
        Assert.Contains("\"format\": 1", File.ReadAllText(document.Path)); // ... and the file stays format 1 until saved,
        var original = document.Text; // while the document is format 2 from the start: undo goes back to this.
        document.Edit(_ => { });
        Assert.False(document.CanUndo);
        Assert.False(File.Exists(Path.Combine(_directory, MovieDocument.MetadataFileName)));
        document.Rename("textures", "art", "new_art");
        document.Rename("shapes", "face", "new_face");
        document.Rename("sprites", "main", "new_main");
        Assert.Equal(ids["textures"], document.AssetId("textures", "new_art"));
        Assert.Equal(ids["shapes"], document.AssetId("shapes", "new_face"));
        Assert.Equal(ids["sprites"], document.AssetId("sprites", "new_main"));
        Assert.Equal("new_art", (string?)document.Root["shapes"]!["new_face"]!["texture"]);
        Assert.Equal("new_face", (string?)document.Root["sprites"]!["new_main"]!["layers"]![0]!["character"]);
        Assert.Equal("contractChild", (string?)document.Root["sprites"]!["new_main"]!["layers"]![0]!["name"]);
        Assert.Equal("ContractSymbol", (string?)document.Root["sprites"]!["new_main"]!["export"]);
        Assert.Equal("new_main", (string?)document.Root["root"]);
        document.Save();
        var reopened = new MovieDocument(_directory);
        Assert.Equal(ids["sprites"], reopened.AssetId("sprites", "new_main"));
        MoviePackage.Assemble(document.Text);
        document.Undo(); document.Undo(); document.Undo();
        Assert.Equal(original.TrimEnd(), document.Text.TrimEnd());
        Assert.Equal(ids["sprites"], document.AssetId("sprites", "main"));
        document.Redo(); document.Redo(); document.Redo();
        document.Save();
        Assert.Equal(ids["sprites"], new MovieDocument(_directory).AssetId("sprites", "new_main"));
    }

    [Fact]
    public void FoldersKeepStableMembershipAndRejectCyclesAndDeletionOfContents()
    {
        var document = open();
        var a = document.CreateFolder("Artwork");
        var b = document.CreateFolder("Notes", a);
        var sprite = document.AddSprite("note", folder: b);
        var id = document.AssetId("sprites", sprite);
        document.RenameFolder(a, "Art");
        Assert.Equal("Art / Notes", document.FolderPath(b));
        document.Save();
        Assert.Equal(b, new MovieDocument(_directory).Assets().Single(asset => asset.Id == id).Folder);
        Assert.Throws<InvalidOperationException>(() => document.MoveFolder(a, b));
        Assert.Throws<InvalidOperationException>(() => document.DeleteFolder(a));
        Assert.Throws<InvalidOperationException>(() => document.DeleteFolder(b));
        Assert.Throws<InvalidOperationException>(() => document.CreateFolder("art"));
        Assert.Throws<InvalidOperationException>(() => document.CreateFolder("../bad"));
        document.MoveAsset("sprites", sprite, "");
        document.Undo();
        Assert.Equal(b, document.Assets().Single(asset => asset.Id == id).Folder);
        document.Redo();
        document.MoveFolder(b, "");
        Assert.Equal("Notes", document.FolderPath(b));
        document.DeleteFolder(b);
        document.Undo();
        Assert.Equal("Notes", document.FolderPath(b));
        Assert.Equal(id, document.AssetId("sprites", sprite));
    }

    [Fact]
    public void DirectoryImportsPreserveStructureAndTakeOneUndoStep()
    {
        var document = open();
        var input = Path.Combine(_directory, "source");
        Directory.CreateDirectory(Path.Combine(input, "Nested"));
        File.WriteAllText(Path.Combine(input, "note.svg"), "<svg/>");
        File.WriteAllText(Path.Combine(input, "Nested", "note.svg"), "<svg/>");
        File.WriteAllText(Path.Combine(input, "ignored.txt"), "not an image");
        var names = document.ImportDirectory(input);
        Assert.Equal(2, names.Count);
        Assert.Equal(2, names.Distinct().Count());
        var imported = document.Assets().Where(asset => names.Contains(asset.Name)).ToArray();
        Assert.Equal(["source", "source / Nested"], imported.Select(asset => document.FolderPath(asset.Folder)).Order());
        var identities = imported.Select(asset => asset.Id).ToArray();
        document.Undo();
        Assert.Empty(document.Folders());
        Assert.DoesNotContain(document.Assets(), asset => identities.Contains(asset.Id));
        document.Redo();
        document.Save();
        var reopened = new MovieDocument(_directory);
        Assert.Equal(identities, reopened.Assets().Where(asset => identities.Contains(asset.Id)).Select(asset => asset.Id));
        Assert.All(names, name => Assert.True(File.Exists(document.FilePath((string)document.Root["textures"]![name]!))));
    }

    [Fact]
    public void RefusedEditsLeaveBothDocumentsAndHistoryIntact()
    {
        var document = open();
        var id = document.AssetId("shapes", "face");
        Assert.Throws<InvalidOperationException>(() => document.Edit(root =>
        {
            root["root"] = "broken";
            throw new InvalidOperationException("cancelled");
        }));
        Assert.Equal("main", (string?)document.Root["root"]);
        Assert.Equal(id, document.AssetId("shapes", "face"));
        Assert.False(document.CanUndo);
        var readOnly = new MovieDocument((JsonObject)document.Root.DeepClone(), new Dictionary<uint, string>());
        Assert.Throws<InvalidOperationException>(() => readOnly.ImportDirectory(_directory));
    }

    [Fact]
    public void LucideResourcesAreEmbeddedAndPreparedForTheExistingRenderer()
    {
        foreach (var name in EditorIcons.Names)
        {
            var svg = System.Text.Encoding.UTF8.GetString(EditorIcons.Svg(name));
            Assert.Contains("viewBox=\"0 0 24 24\"", svg);
            Assert.Contains("stroke=\"#ffffff\"", svg);
            Assert.DoesNotContain("currentColor", svg);
        }
    }
}
