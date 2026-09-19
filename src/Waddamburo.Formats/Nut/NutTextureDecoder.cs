using Waddamburo.Formats.IO;

namespace Waddamburo.Formats.Nut;

/// <summary>Decodes one NTP3 base mip to renderer-neutral R,G,B,A bytes.</summary>
public static class NutTextureDecoder
{
    public static byte[] DecodeRgba8(NutTexture texture, ParserLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var parserLimits = limits ?? ParserLimits.Default;
        return texture.Format switch
        {
            NutPixelFormat.Bc1 => BlockCompressionDecoder.DecodeBc1(
                texture.Data.Span,
                texture.Width,
                texture.Height,
                parserLimits),
            NutPixelFormat.Bc3 => BlockCompressionDecoder.DecodeBc3(
                texture.Data.Span,
                texture.Width,
                texture.Height,
                parserLimits),
            NutPixelFormat.Argb or NutPixelFormat.ArgbAlternate => decodeArgb(texture, parserLimits),
            _ => throw new FormatReadException($"Unsupported NTP3 texture format {texture.Format}", texture.HeaderOffset + 0x13),
        };
    }

    private static byte[] decodeArgb(NutTexture texture, ParserLimits limits)
    {
        var outputLength = checked((long)texture.Width * texture.Height * 4);
        if (outputLength > limits.MaxTextureBytes)
        {
            throw new FormatLimitException(
                nameof(ParserLimits.MaxTextureBytes),
                outputLength,
                limits.MaxTextureBytes,
                texture.DataOffset);
        }
        if (outputLength > limits.MaxAllocationBytes)
        {
            throw new FormatLimitException(
                nameof(ParserLimits.MaxAllocationBytes),
                outputLength,
                limits.MaxAllocationBytes,
                texture.DataOffset);
        }
        if (texture.Data.Length < outputLength)
        {
            throw new FormatReadException(
                "Raw NTP3 texture payload is truncated",
                texture.DataOffset,
                outputLength,
                texture.Data.Length);
        }

        var rgba = new byte[checked((int)outputLength)];
        var argb = texture.Data.Span;
        for (var offset = 0; offset < rgba.Length; offset += 4)
        {
            rgba[offset] = argb[offset + 1];
            rgba[offset + 1] = argb[offset + 2];
            rgba[offset + 2] = argb[offset + 3];
            rgba[offset + 3] = argb[offset];
        }
        return rgba;
    }
}
