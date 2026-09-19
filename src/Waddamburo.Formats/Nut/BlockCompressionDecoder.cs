using System.Buffers.Binary;
using Waddamburo.Formats.IO;

namespace Waddamburo.Formats.Nut;

/// <summary>Portable BC1/BC3 reference decoding for tests and diagnostic images.</summary>
public static class BlockCompressionDecoder
{
    public static byte[] DecodeBc1(
        ReadOnlySpan<byte> blocks,
        int width,
        int height,
        ParserLimits? limits = null) =>
        decode(blocks, width, height, 8, false, limits ?? ParserLimits.Default);

    public static byte[] DecodeBc3(
        ReadOnlySpan<byte> blocks,
        int width,
        int height,
        ParserLimits? limits = null) =>
        decode(blocks, width, height, 16, true, limits ?? ParserLimits.Default);

    private static byte[] decode(
        ReadOnlySpan<byte> blocks,
        int width,
        int height,
        int blockLength,
        bool hasExplicitAlpha,
        ParserLimits limits)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (width > limits.MaxTextureDimension)
        {
            throw new FormatLimitException(
                nameof(ParserLimits.MaxTextureDimension),
                width,
                limits.MaxTextureDimension,
                0);
        }
        if (height > limits.MaxTextureDimension)
        {
            throw new FormatLimitException(
                nameof(ParserLimits.MaxTextureDimension),
                height,
                limits.MaxTextureDimension,
                0);
        }

        var blockWidth = (width + 3) / 4;
        var blockHeight = (height + 3) / 4;
        var requiredInput = checked(blockWidth * blockHeight * blockLength);
        if (blocks.Length < requiredInput)
            throw new FormatReadException("Compressed texture blocks are truncated", 0, requiredInput, blocks.Length);

        var outputLength = checked((long)width * height * 4);
        if (outputLength > limits.MaxTextureBytes)
        {
            throw new FormatLimitException(
                nameof(ParserLimits.MaxTextureBytes),
                outputLength,
                limits.MaxTextureBytes,
                0);
        }
        if (outputLength > limits.MaxAllocationBytes)
        {
            throw new FormatLimitException(
                nameof(ParserLimits.MaxAllocationBytes),
                outputLength,
                limits.MaxAllocationBytes,
                0);
        }

        var rgba = new byte[checked((int)outputLength)];
        Span<Rgba> colours = stackalloc Rgba[4];
        Span<byte> alphas = stackalloc byte[8];
        for (var blockY = 0; blockY < blockHeight; blockY++)
        {
            for (var blockX = 0; blockX < blockWidth; blockX++)
            {
                var blockIndex = blockY * blockWidth + blockX;
                var block = blocks.Slice(blockIndex * blockLength, blockLength);
                var colourOffset = hasExplicitAlpha ? 8 : 0;
                var colour0 = BinaryPrimitives.ReadUInt16LittleEndian(block[colourOffset..]);
                var colour1 = BinaryPrimitives.ReadUInt16LittleEndian(block[(colourOffset + 2)..]);
                colours[0] = expandRgb565(colour0);
                colours[1] = expandRgb565(colour1);
                if (hasExplicitAlpha || colour0 > colour1)
                {
                    colours[2] = interpolate(colours[0], colours[1], 2, 1, 3);
                    colours[3] = interpolate(colours[0], colours[1], 1, 2, 3);
                }
                else
                {
                    colours[2] = interpolate(colours[0], colours[1], 1, 1, 2);
                    colours[3] = default;
                }

                ulong alphaBits = 0;
                if (hasExplicitAlpha)
                {
                    buildAlphaPalette(block, alphas);
                    for (var byteIndex = 0; byteIndex < 6; byteIndex++)
                        alphaBits |= (ulong)block[2 + byteIndex] << (8 * byteIndex);
                }

                var colourBits = BinaryPrimitives.ReadUInt32LittleEndian(block[(colourOffset + 4)..]);
                for (var pixel = 0; pixel < 16; pixel++)
                {
                    var x = blockX * 4 + pixel % 4;
                    var y = blockY * 4 + pixel / 4;
                    if (x >= width || y >= height)
                        continue;

                    var colour = colours[(int)(colourBits >> (2 * pixel)) & 3];
                    var alpha = hasExplicitAlpha
                        ? alphas[(int)(alphaBits >> (3 * pixel)) & 7]
                        : colour.A;
                    var destination = (y * width + x) * 4;
                    rgba[destination] = colour.R;
                    rgba[destination + 1] = colour.G;
                    rgba[destination + 2] = colour.B;
                    rgba[destination + 3] = alpha;
                }
            }
        }

        return rgba;
    }

    private static void buildAlphaPalette(ReadOnlySpan<byte> block, Span<byte> alphas)
    {
        var alpha0 = block[0];
        var alpha1 = block[1];
        alphas[0] = alpha0;
        alphas[1] = alpha1;
        if (alpha0 > alpha1)
        {
            for (var index = 1; index < 7; index++)
                alphas[index + 1] = (byte)(((7 - index) * alpha0 + index * alpha1) / 7);
        }
        else
        {
            for (var index = 1; index < 5; index++)
                alphas[index + 1] = (byte)(((5 - index) * alpha0 + index * alpha1) / 5);
            alphas[6] = 0;
            alphas[7] = 255;
        }
    }

    private static Rgba expandRgb565(ushort value)
    {
        var red = (value >> 11) & 0x1F;
        var green = (value >> 5) & 0x3F;
        var blue = value & 0x1F;
        return new Rgba(
            (byte)((red << 3) | (red >> 2)),
            (byte)((green << 2) | (green >> 4)),
            (byte)((blue << 3) | (blue >> 2)),
            255);
    }

    private static Rgba interpolate(Rgba first, Rgba second, int firstWeight, int secondWeight, int divisor) =>
        new(
            (byte)((first.R * firstWeight + second.R * secondWeight) / divisor),
            (byte)((first.G * firstWeight + second.G * secondWeight) / divisor),
            (byte)((first.B * firstWeight + second.B * secondWeight) / divisor),
            255);

    private readonly record struct Rgba(byte R, byte G, byte B, byte A);
}
