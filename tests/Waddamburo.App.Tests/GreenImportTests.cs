using System.IO.Compression;
using Waddamburo.App.Cli;

namespace Waddamburo.App.Tests;

public sealed class GreenImportTests
{
    [Fact]
    public void MatchFindsTheDataFolderAnywhere()
    {
        Assert.Equal("data/config/common/config.xml", GreenImport.Match(@"[ARCADE] Green\USRDIR\DATA\config\common\config.xml"));
        Assert.Equal("data/config/common/config.xml", GreenImport.Match("USRDIR/data/config/common/config.xml"));
        Assert.Null(GreenImport.Match("ARCDIR/data/config/common/nope.xml"));
        Assert.Null(GreenImport.Match("PARAM.SFO"));
    }

    [Fact]
    public void ZipImportTakesOnlyGreensFilesAndResumes()
    {
        var root = Directory.CreateTempSubdirectory("green-import").FullName;
        try
        {
            using var zip = new MemoryStream();
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var name in (string[])["Dump/USRDIR/data/config/common/config.xml", "Dump/README.txt"])
                {
                    using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write("x");
                }
            }
            zip.Position = 0;
            var import = new GreenImport(root);
            import.Import(zip);
            Assert.Equal(1, import.Files);
            Assert.Equal("x", File.ReadAllText(Path.Combine(root, "data/config/common/config.xml")));
            Assert.Single(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
