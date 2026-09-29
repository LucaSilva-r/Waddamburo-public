using System.Buffers.Binary;
using System.IO.Compression;

namespace Waddamburo.Upscale;

/// <summary>8-bit RGBA PNG files: screenshots, and the texture upscaler's input and output.</summary>
public static class PngFile
{
    /// <summary>Writes RGBA pixels; <paramref name="dropAlpha"/> writes them as RGB.</summary>
    public static void Write(string path, uint width, uint height, ReadOnlySpan<byte> rgba, bool dropAlpha = false) =>
        File.WriteAllBytes(path, Encode(width, height, rgba, dropAlpha));

    public static byte[] Encode(uint width, uint height, ReadOnlySpan<byte> rgba, bool dropAlpha = false)
    {
        if ((ulong)rgba.Length != checked((ulong)width * height * 4))
            throw new ArgumentException("RGBA8 data must contain exactly width * height * 4 bytes.", nameof(rgba));
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var rowBytes = checked((int)width * 4);
            var rgb = dropAlpha ? new byte[checked((int)width * 3)] : null;
            for (var row = 0; row < height; row++)
            {
                zlib.WriteByte(0);
                var line = rgba.Slice(checked(row * rowBytes), rowBytes);
                if (rgb is null)
                {
                    zlib.Write(line);
                    continue;
                }
                for (var x = 0; x < width; x++)
                    line.Slice(x * 4, 3).CopyTo(rgb.AsSpan(x * 3));
                zlib.Write(rgb);
            }
        }

        using var stream = new MemoryStream();
        stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = (byte)(dropAlpha ? 2 : 6);
        writeChunk(stream, "IHDR"u8, header);
        writeChunk(stream, "IDAT"u8, compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
        writeChunk(stream, "IEND"u8, []);
        return stream.ToArray();
    }

    /// <summary>
    /// Reads an 8-bit RGB or RGBA, non-interlaced PNG as RGBA.
    /// ponytail: only the layouts the upscaler writes; palette, 16-bit and interlaced files throw.
    /// </summary>
    public static (uint Width, uint Height, byte[] Rgba) Read(string path) => Decode(File.ReadAllBytes(path), path);

    public static (uint Width, uint Height, byte[] Rgba) Decode(byte[] bytes, string path = "PNG data")
    {
        var file = bytes.AsSpan();
        if (file.Length < 8 || !file[..8].SequenceEqual((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10]))
            throw new InvalidDataException($"{path} is not a PNG file.");
        uint width = 0, height = 0;
        var channels = 0;
        using var idat = new MemoryStream();
        for (var offset = 8; offset + 12 <= file.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(file[offset..]));
            var type = file.Slice(offset + 4, 4);
            var data = file.Slice(offset + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                width = BinaryPrimitives.ReadUInt32BigEndian(data);
                height = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                channels = (data[8], data[9], data[12]) switch
                {
                    (8, 6, 0) => 4,
                    (8, 2, 0) => 3,
                    _ => throw new InvalidDataException($"{path}: unsupported PNG layout."),
                };
            }
            else if (type.SequenceEqual("IDAT"u8))
                idat.Write(data);
            else if (type.SequenceEqual("IEND"u8))
                break;
            offset += 12 + length;
        }
        if (channels == 0)
            throw new InvalidDataException($"{path}: missing PNG header.");

        var stride = checked((int)width * channels);
        var raw = new byte[checked((stride + 1) * (int)height)];
        idat.Position = 0;
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
            zlib.ReadExactly(raw);
        var rgba = new byte[checked((int)width * (int)height * 4)];
        var previous = new byte[stride];
        var current = new byte[stride];
        for (var row = 0; row < height; row++)
        {
            var line = raw.AsSpan(row * (stride + 1), stride + 1);
            var filter = line[0];
            line[1..].CopyTo(current);
            for (var i = 0; i < stride; i++)
            {
                int left = i >= channels ? current[i - channels] : 0, up = previous[i];
                int upLeft = i >= channels ? previous[i - channels] : 0;
                current[i] = (byte)(current[i] + filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => paeth(left, up, upLeft),
                    _ => throw new InvalidDataException($"{path}: bad PNG filter {filter}."),
                });
            }
            for (var x = 0; x < width; x++)
            {
                var target = (row * (int)width + x) * 4;
                current.AsSpan(x * channels, 3).CopyTo(rgba.AsSpan(target));
                rgba[target + 3] = channels == 4 ? current[x * channels + 3] : (byte)255;
            }
            (previous, current) = (current, previous);
        }
        return (width, height, rgba);
    }

    private static int paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void writeChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
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
