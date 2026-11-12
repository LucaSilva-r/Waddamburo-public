namespace Waddamburo.Catalog.Tests;

public sealed class ChartUploadPolicyTests
{
    [Fact]
    public void MarkersCoverTheirFolderAndDescendantsWithoutAffectingSiblingsOrOtherLibraries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wdb-privacy-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "library");
        var privateFolder = Path.Combine(library, "private");
        var nested = Path.Combine(privateFolder, "nested", "song.tja");
        var publicChart = Path.Combine(library, "public", "song.tja");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
            Directory.CreateDirectory(Path.GetDirectoryName(publicChart)!);
            File.WriteAllText(Path.Combine(root, "donotupload"), "");
            Assert.False(ChartUploadPolicy.HasMarker(nested, library)); // outside the library doesn't apply
            File.WriteAllText(Path.Combine(privateFolder, "donotupload.txt"), "");
            Assert.False(ChartUploadPolicy.HasMarker(nested, library)); // exact extensionless name
            File.WriteAllText(Path.Combine(privateFolder, "donotupload"), "");
            Assert.True(ChartUploadPolicy.HasMarker(nested, library));
            Assert.True(ChartUploadPolicy.HasMarker(Path.Combine(privateFolder, "other.osu"), library));
            Assert.False(ChartUploadPolicy.HasMarker(publicChart, library));
            File.Delete(Path.Combine(privateFolder, "donotupload"));
            Assert.False(ChartUploadPolicy.HasMarker(nested, library)); // read fresh, no rescan
            File.WriteAllText(Path.Combine(library, "donotupload"), "");
            Assert.True(ChartUploadPolicy.HasMarker(publicChart, library));
            Assert.True(ChartUploadPolicy.HasMarker(nested, library));
            Assert.True(ChartUploadPolicy.HasMarker(Path.Combine(root, "library-other", "song.tja"), library));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
