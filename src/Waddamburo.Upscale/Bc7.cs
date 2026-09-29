using System.Runtime.InteropServices;

namespace Waddamburo.Upscale;

/// <summary>BC7 texture blocks through the native waddamburo_texture library (bc7enc).</summary>
public static partial class Bc7
{
    private const uint AbiVersion = 0x00010000;

    /// <summary>Whether the native library is present and speaks this ABI.</summary>
    public static bool Available { get; } = probe();

    /// <summary>
    /// RGBA8 (sizes multiples of 4) to BC7 blocks, one byte per pixel. <paramref name="partitions"/>:
    /// 0 fastest, 16 (default) close to the best quality measured at a third of the speed.
    /// </summary>
    public static unsafe byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, int partitions = 16)
    {
        check(rgba.Length, width, height, 4);
        var blocks = new byte[width * height];
        fixed (byte* source = rgba)
        fixed (byte* target = blocks)
            if (encode(source, (uint)width, (uint)height, (uint)partitions, target) != 0)
                throw new ArgumentException("BC7 encoding rejected the texture.");
        return blocks;
    }

    public static unsafe byte[] Decode(ReadOnlySpan<byte> blocks, int width, int height)
    {
        check(blocks.Length, width, height, 1);
        var rgba = new byte[width * height * 4];
        fixed (byte* source = blocks)
        fixed (byte* target = rgba)
            if (decode(source, (uint)width, (uint)height, target) != 0)
                throw new ArgumentException("BC7 decoding rejected the texture.");
        return rgba;
    }

    private static void check(int length, int width, int height, int bytesPerPixel)
    {
        if (width <= 0 || height <= 0 || width % 4 != 0 || height % 4 != 0 || length != width * height * bytesPerPixel)
            throw new ArgumentException($"BC7 needs sizes that are multiples of 4 and matching data ({width}x{height}).");
    }

    private static bool probe()
    {
        try
        {
            return version() == AbiVersion;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("waddamburo_texture", EntryPoint = "waddamburo_texture_get_abi_version")]
    private static partial uint version();

    [LibraryImport("waddamburo_texture", EntryPoint = "waddamburo_texture_encode_bc7")]
    private static unsafe partial int encode(byte* rgba, uint width, uint height, uint partitions, byte* blocks);

    [LibraryImport("waddamburo_texture", EntryPoint = "waddamburo_texture_decode_bc7")]
    private static unsafe partial int decode(byte* blocks, uint width, uint height, byte* rgba);
}
