using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.Platform.Sdl.Text;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The cabinet's pairing pill, drawn over everything at the top of the screen: a yellow capsule
/// with a red disc (the drum's colours, after TaikoRecomp's pairing pill), the six-digit code in the
/// game's outlined title font and its countdown in the disc. After a pairing it shows the player's
/// name instead. Rebuilt only when its text changes.
/// </summary>
internal sealed class PairingPill(SdlApplication application, string fontPath) : IDisposable
{
    private const int Width = 272, Height = 64, Disc = 64, TopMargin = 12;
    private static readonly (byte R, byte G, byte B) Yellow = (254, 205, 1), Red = (249, 71, 40);
    private (string Text, string? Badge)? _shown;
    private (string Text, string? Badge, uint Scale)? _drawn;
    private RenderTextureId? _texture;

    /// <summary><paramref name="badge"/> goes in the red disc (the countdown); null leaves it empty.</summary>
    public void Show(string text, string? badge) => _shown = (text, badge);

    public void Hide() => _shown = null;

    public IEnumerable<RenderQuad> Quads()
    {
        if (_shown is not { } shown)
            return [];
        var scale = (uint)Math.Clamp((application.GetPixelSize().Height + 719) / 720, 1, 4);
        if (_drawn != (shown.Text, shown.Badge, scale))
        {
            if (_texture is { } old)
                application.ReleaseTexture(old);
            _texture = application.UploadRgba8((uint)(Width * scale), (uint)(Height * scale), draw(shown.Text, shown.Badge, (int)scale));
            _drawn = (shown.Text, shown.Badge, scale);
        }
        return [RenderQuad.FromRectangles(_texture!.Value,
            new RenderRectangle((1280f - Width) / 2 / 1280f, TopMargin / 720f, Width / 1280f, Height / 720f),
            RenderRectangle.Full, RenderColor.White, RenderColor.Transparent)];
    }

    public void Dispose()
    {
        if (_texture is { } texture)
            application.ReleaseTexture(texture);
    }

    private byte[] draw(string text, string? badge, int scale)
    {
        int width = Width * scale, height = Height * scale;
        var pixels = new byte[width * height * 4];
        float s = scale, radius = 30.5f * s, outline = 3f * s, middle = 32f * s;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                // Signed distances (negative inside): the capsule, then the disc over its left end.
                var capsule = MathF.Sqrt(MathF.Pow(px - Math.Clamp(px, 32f * s, (Width - 34f) * s), 2) + MathF.Pow(py - middle, 2)) - radius;
                var disc = MathF.Sqrt(MathF.Pow(px - middle, 2) + MathF.Pow(py - middle, 2)) - radius;
                blend(pixels, width, x, y, (0, 0, 0), coverage(capsule));
                blend(pixels, width, x, y, Yellow, coverage(capsule + outline));
                blend(pixels, width, x, y, (0, 0, 0), coverage(disc));
                blend(pixels, width, x, y, Red, coverage(disc + outline));
            }
        stamp(pixels, width, height, text, (Disc + (Width - Disc) / 2f) * s, (Width - Disc - 24) * s, scale);
        if (badge is not null)
            stamp(pixels, width, height, badge, middle, (Disc - 14) * s, scale);
        return pixels;
    }

    private static float coverage(float distance) => Math.Clamp(0.5f - distance, 0f, 1f);

    // The game's outlined title text, cropped to its ink and centred at (centreX, middle), shrunk to maxWidth.
    private void stamp(byte[] pixels, int width, int height, string text, float centreX, float maxWidth, int scale)
    {
        var surface = NativeVerticalTextRasterizer.RenderSongTitle(fontPath, text, null,
            SongTitleTextProfile.GameplayTitle, 0x000000, (uint)scale);
        int sw = (int)surface.Width, sh = (int)surface.Height;
        int left = sw, right = -1, top = sh, bottom = -1;
        for (var y = 0; y < sh; y++)
            for (var x = 0; x < sw; x++)
                if (surface.Pixels[(y * sw + x) * 4 + 3] != 0)
                {
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
        if (right < 0)
            return;
        int inkWidth = right - left + 1, inkHeight = bottom - top + 1;
        var factor = Math.Min(1f, Math.Min(maxWidth / inkWidth, (height - 12f * scale) / inkHeight));
        int outWidth = (int)(inkWidth * factor), outHeight = (int)(inkHeight * factor);
        int originX = (int)(centreX - outWidth / 2f), originY = (height - outHeight) / 2;
        for (var y = 0; y < outHeight; y++)
            for (var x = 0; x < outWidth; x++)
            {
                // Nearest-neighbour sampling into the ink box: the text only ever shrinks a little.
                var source = ((top + (int)(y / factor)) * sw + left + (int)(x / factor)) * 4;
                var alpha = surface.Pixels[source + 3] / 255f;
                if (alpha > 0 && originX + x is >= 0 and var tx && tx < width)
                    blend(pixels, width, tx, originY + y,
                        (surface.Pixels[source], surface.Pixels[source + 1], surface.Pixels[source + 2]), alpha);
            }
    }

    // Straight-alpha "over" into the RGBA buffer.
    private static void blend(byte[] pixels, int width, int x, int y, (byte R, byte G, byte B) colour, float alpha)
    {
        if (alpha <= 0)
            return;
        var index = (y * width + x) * 4;
        var below = pixels[index + 3] / 255f;
        var result = alpha + below * (1 - alpha);
        byte mix(byte top, byte bottom) => (byte)Math.Round((top * alpha + bottom * below * (1 - alpha)) / result);
        pixels[index] = mix(colour.R, pixels[index]);
        pixels[index + 1] = mix(colour.G, pixels[index + 1]);
        pixels[index + 2] = mix(colour.B, pixels[index + 2]);
        pixels[index + 3] = (byte)Math.Round(result * 255);
    }
}
