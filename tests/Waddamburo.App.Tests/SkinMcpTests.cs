using System.Text.Json.Nodes;
using Waddamburo.App.Tools;

namespace Waddamburo.App.Tests;

public sealed class SkinMcpTests
{
    // An assistant reads and writes the skin's files only: nothing above or beside its folder.
    [Theory]
    [InlineData("../README.md")]
    [InlineData("entry/../../x")]
    [InlineData("/etc/passwd")]
    public void PathsOutsideTheSkinAreRefused(string path)
    {
        var root = Path.Combine(Path.GetTempPath(), "skin");
        Assert.Throws<ArgumentException>(() => SkinMcp.inside(root, path));
    }

    [Fact]
    public void WritesAndReadsInsideTheSkin()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            JsonNode call(string name, JsonObject arguments) => SkinMcp.Answer(root, "tools/call", new() { ["name"] = name, ["arguments"] = arguments });
            call("write_file", new() { ["path"] = "a/b.as", ["text"] = "trace(1);" });
            Assert.Equal("trace(1);", call("read_file", new() { ["path"] = "a/b.as" })["content"]![0]!["text"]!.GetValue<string>());
            Assert.Equal("a/b.as", call("list_files", [])["content"]![0]!["text"]!.GetValue<string>());
            Assert.True(call("read_file", new() { ["path"] = "../x" })["isError"]!.GetValue<bool>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
