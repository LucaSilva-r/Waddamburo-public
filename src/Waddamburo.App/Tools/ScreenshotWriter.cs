using Waddamburo.Platform.Sdl.Rendering;

using Waddamburo.Upscale;

namespace Waddamburo.App.Tools;

internal static class ScreenshotWriter
{
    public static void Write(string path, RenderCapture capture)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".png":
                WritePng(path, capture);
                break;
            case ".bmp":
                WriteBmp(path, capture);
                break;
            default:
                throw new ArgumentException("--screenshot must name a .png or .bmp file.", nameof(path));
        }
        Console.WriteLine($"Screenshot: {Path.GetFullPath(path)}");
    }

    public static void WritePng(string path, RenderCapture capture)
    {
        validate(path, capture);
        PngFile.Write(path, capture.Width, capture.Height, capture.Rgba8.AsSpan());
    }

    public static void WriteBmp(string path, RenderCapture capture)
    {
        var pixelBytes = validate(path, capture);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(checked(54u + pixelBytes));
        writer.Write(0u);
        writer.Write(54u);
        writer.Write(40u);
        writer.Write(checked((int)capture.Width));
        writer.Write(checked((int)capture.Height));
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0u);
        writer.Write(pixelBytes);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0u);
        writer.Write(0u);

        var rgba = capture.Rgba8.AsSpan();
        var rowBytes = checked((int)capture.Width * 4);
        for (var row = checked((int)capture.Height) - 1; row >= 0; row--)
        {
            var rowOffset = row * rowBytes;
            for (var columnOffset = 0; columnOffset < rowBytes; columnOffset += 4)
            {
                var offset = rowOffset + columnOffset;
                writer.Write(rgba[offset + 2]);
                writer.Write(rgba[offset + 1]);
                writer.Write(rgba[offset]);
                writer.Write(rgba[offset + 3]);
            }
        }
    }

    private static uint validate(string path, RenderCapture capture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(capture);
        var pixelBytes = checked(capture.Width * capture.Height * 4);
        if (capture.Rgba8.Length != pixelBytes)
            throw new ArgumentException("Capture data does not match its dimensions.", nameof(capture));
        return pixelBytes;
    }
}
