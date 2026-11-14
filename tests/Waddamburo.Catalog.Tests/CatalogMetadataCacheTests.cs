using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Waddamburo.Catalog.Tests;

public sealed partial class CatalogMetadataCacheTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("catalog-cache-test-").FullName;
    private string Root => Path.Combine(_directory, "library");
    private string Database => Path.Combine(_directory, "metadata.db");
    private string FilePath => Path.Combine(Root, "chart.txt");
    private CatalogMetadataScope Open(string version = "v1", CancellationToken token = default) =>
        new CatalogMetadataCache(Database).Open(Root, version, cancellationToken: token);

    public CatalogMetadataCacheTests()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(FilePath, "first");
    }

    [Fact]
    public void WarmScanSurvivesNewCacheInstancesAndOnlyReparsesChanges()
    {
        using (var scan = Open()) Assert.Equal("first", scan.Read(FilePath, "text", () => "first", TestJson.Default.String));
        using (var scan = Open())
        {
            Assert.Equal("first", scan.Read(FilePath, "text", () => throw new InvalidOperationException("Unchanged file parsed"), TestJson.Default.String));
            Assert.Equal(1, scan.Hits);
        }
        File.WriteAllText(FilePath, "other"); // same size; timestamp still invalidates
        File.SetLastWriteTimeUtc(FilePath, DateTime.UtcNow.AddMinutes(1));
        using (var scan = Open())
        {
            Assert.Equal("other", scan.Read(FilePath, "text", () => "other", TestJson.Default.String));
            Assert.Equal(1, scan.Misses);
        }
        using var versioned = Open("v2");
        Assert.Equal("new parser", versioned.Read(FilePath, "text", () => "new parser", TestJson.Default.String));
        Assert.Equal(1, versioned.Misses);
    }

    [Fact]
    public void InventoryTracksAdditionsAndDeletionsAndPrunesRemovedRows()
    {
        using (var scan = Open()) scan.Read(FilePath, "text", () => "first", TestJson.Default.String);
        File.Delete(FilePath);
        var added = Path.Combine(Root, "new.txt");
        File.WriteAllText(added, "new");
        using (var scan = Open())
        {
            Assert.False(scan.Exists(FilePath));
            Assert.True(scan.Exists(added));
            Assert.Equal(3, scan.Size(added));
            Assert.Equal([added], scan.Paths);
            scan.Read(added, "text", () => "new", TestJson.Default.String);
        }
        using var connection = new SqliteConnection($"Data Source={Database}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM file_metadata_v1";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void SourceTreesFollowLinksAndUserLibrariesDoNot()
    {
        if (OperatingSystem.IsWindows()) return; // links need a privilege there
        var elsewhere = Path.Combine(_directory, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "song.bin"), "x");
        Directory.CreateSymbolicLink(Path.Combine(Root, "linked"), elsewhere);
        File.CreateSymbolicLink(Path.Combine(Root, "file.bin"), Path.Combine(elsewhere, "song.bin"));
        using (var library = Open())
        {
            Assert.False(library.Exists(Path.Combine(Root, "linked", "song.bin")));
            Assert.False(library.Exists(Path.Combine(Root, "file.bin")));
        }
        using var install = new CatalogMetadataCache(Database).Open(Root, "v1", sourceTree: true);
        Assert.True(install.Exists(Path.Combine(Root, "linked", "song.bin")));
        Assert.True(install.Exists(Path.Combine(Root, "file.bin")));
        Assert.Equal(1, install.Size(Path.Combine(Root, "file.bin")));
    }

    [Fact]
    public void InventoryCanBeKeptToTheFoldersAndFilesAScanNeeds()
    {
        Directory.CreateDirectory(Path.Combine(Root, "charts", "song"));
        Directory.CreateDirectory(Path.Combine(Root, "models"));
        File.WriteAllText(Path.Combine(Root, "charts", "song", "a.bin"), "a");
        File.WriteAllText(Path.Combine(Root, "models", "big.bin"), "b");
        using var scan = new CatalogMetadataCache(Database).Open(Root, "v1", within: ["charts", "chart.txt", "absent"]);
        Assert.Equal([FilePath, Path.Combine(Root, "charts", "song", "a.bin")], scan.Paths.Order());
    }

    [Fact]
    public void CancelledScanDoesNotReplaceThePreviousCache()
    {
        using (var scan = Open()) scan.Read(FilePath, "text", () => "first", TestJson.Default.String);
        using var cancellation = new CancellationTokenSource();
        using (var scan = Open(token: cancellation.Token)) cancellation.Cancel();
        using var warm = Open();
        Assert.Equal("first", warm.Read(FilePath, "text", () => throw new InvalidOperationException("Cache lost"), TestJson.Default.String));
    }

    [Fact]
    public void FilesChangedDuringParsingAreNeverPersisted()
    {
        using (var scan = Open()) scan.Read(FilePath, "text", () => { File.WriteAllText(FilePath, "changed"); return "stale"; }, TestJson.Default.String);
        using var next = Open();
        Assert.Equal("fresh", next.Read(FilePath, "text", () => "fresh", TestJson.Default.String));
        Assert.Equal(1, next.Misses);
    }

    [Fact]
    public void CorruptDatabaseFallsBackToSourceMetadata()
    {
        File.WriteAllText(Database, "not a database");
        using (var scan = Open())
            Assert.Equal("source", scan.Read(FilePath, "text", () => "source", TestJson.Default.String));
        using var recovered = Open();
        Assert.Equal("source", recovered.Read(FilePath, "text", () => throw new InvalidOperationException("Corrupt cache not rebuilt"), TestJson.Default.String));
    }

    [Fact]
    public void InvalidPayloadIsReparsedWithoutLosingTheSource()
    {
        using (var scan = Open()) scan.Read(FilePath, "text", () => "first", TestJson.Default.String);
        using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE file_metadata_v1 SET json='invalid json'";
            command.ExecuteNonQuery();
        }
        using var next = Open();
        Assert.Equal("reparsed", next.Read(FilePath, "text", () => "reparsed", TestJson.Default.String));
        Assert.Equal(1, next.Misses);
    }

    [Fact]
    public void DifferentLibrariesAndMetadataKindsCannotShareEntries()
    {
        using (var scan = Open()) scan.Read(FilePath, "text", () => "first", TestJson.Default.String);
        var other = Directory.CreateDirectory(Path.Combine(_directory, "other")).FullName;
        var sameName = Path.Combine(other, "chart.txt");
        File.Copy(FilePath, sameName);
        File.SetLastWriteTimeUtc(sameName, File.GetLastWriteTimeUtc(FilePath));
        using (var scan = new CatalogMetadataCache(Database).Open(other, "v1"))
        {
            Assert.Equal("other root", scan.Read(sameName, "text", () => "other root", TestJson.Default.String));
            Assert.Equal(0, scan.Hits);
        }
        using var original = Open();
        Assert.Equal("other kind", original.Read(FilePath, "different", () => "other kind", TestJson.Default.String));
        Assert.Equal(0, original.Hits);
    }

    [Fact]
    public void UnavailableCacheStorageDoesNotPreventScanning()
    {
        using var scan = new CatalogMetadataCache(_directory).Open(Root, "v1"); // directory cannot be opened as a database
        Assert.Equal("source", scan.Read(FilePath, "text", () => "source", TestJson.Default.String));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    [JsonSerializable(typeof(string))]
    private sealed partial class TestJson : JsonSerializerContext;
}
