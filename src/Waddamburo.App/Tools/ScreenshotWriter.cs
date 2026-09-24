using System.Buffers.Binary;
using System.IO.Compression;
using Waddamburo.Platform.Sdl.Rendering;

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
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var rgba = capture.Rgba8.AsSpan();
            var rowBytes = checked((int)capture.Width * 4);
            for (var row = 0; row < capture.Height; row++)
            {
                zlib.WriteByte(0);
                zlib.Write(rgba.Slice(checked((int)row * rowBytes), rowBytes));
            }
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, capture.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], capture.Height);
        header[8] = 8;
        header[9] = 6;
        writePngChunk(stream, "IHDR"u8, header);
        writePngChunk(stream, "IDAT"u8, compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
        writePngChunk(stream, "IEND"u8, []);
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

    private static void writePngChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, checked((uint)data.Length));
        stream.Write(word);
        stream.Write(type);
        stream.Write(data);
        var crc = uint.MaxValue;
        updateCrc(ref crc, type);
        updateCrc(ref crc, data);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc);
        stream.Write(word);
    }

    private static void updateCrc(ref uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
    }
}
