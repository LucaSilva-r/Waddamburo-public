using System.Buffers.Binary;
using System.IO.Compression;

namespace Waddamburo.App.Cli;

/// <summary>
/// The fonts shipped inside the executable (SIL OFL 1.1, notices in THIRD_PARTY_NOTICES.md): Mochiy Pop
/// P One, the title font when the user supplies none, and Noto Sans CJK JP Black cut down to the
/// characters Mochiy Pop lacks (rare symbols, Chinese, Hangul), the fallback behind every font.
/// They are written to a folder (FreeType reads files) and rewritten when the bundled ones change.
/// </summary>
internal static class BundledFonts
{
    private const string Title = "MochiyPopPOne-Regular.ttf", Fallback = "NotoSansCJKjp-Black-fallback.otf";

    /// <summary>Writes the fonts into <paramref name="directory"/> unless already there (removing others); returns their paths.</summary>
    public static (string Title, string Fallback) Extract(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var stale in Directory.EnumerateFiles(directory).Where(static path => Path.GetFileName(path) is not (Title or Fallback)))
            File.Delete(stale);
        return (extract(directory, Title), extract(directory, Fallback));
    }

    private static string extract(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        using var resource = typeof(BundledFonts).Assembly.GetManifestResourceStream($"Waddamburo.Fonts.{name}.gz")
            ?? throw new InvalidOperationException($"The bundled font {name} is missing from the build.");
        // A gzip stream ends with its uncompressed size: an extracted file of that size is this font.
        // ponytail: a changed font of the same size is not noticed; hash it if that ever happens.
        Span<byte> size = stackalloc byte[4];
        resource.Seek(-4, SeekOrigin.End);
        resource.ReadExactly(size);
        resource.Position = 0;
        if (File.Exists(path) && new FileInfo(path).Length == BinaryPrimitives.ReadUInt32LittleEndian(size))
            return path;
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        var partial = path + ".partial";
        using (var file = File.Create(partial))
            gzip.CopyTo(file);
        File.Move(partial, path, overwrite: true);
        return path;
    }
}
