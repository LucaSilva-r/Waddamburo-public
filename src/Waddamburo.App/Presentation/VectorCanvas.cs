using Waddamburo.Platform.Sdl.Text;

namespace Waddamburo.App.Presentation;

/// <summary>
/// An RGBA buffer the host's own UI (pairing pill, account picker) is drawn into at the window's
/// scale: anti-aliased shapes from signed distances, the game's outlined title font, and images.
/// Coordinates are stage units (1280x720); <see cref="Scale"/> maps them to pixels.
/// </summary>
internal sealed class VectorCanvas(int width, int height, int scale, string fontPath)
{
    public int Scale { get; } = scale;
    public int PixelWidth { get; } = width * scale;
    public int PixelHeight { get; } = height * scale;
    public byte[] Pixels { get; } = new byte[width * scale * height * scale * 4];

    /// <summary>A rounded rectangle (stage units) filled with <paramref name="fill"/> inside an outline.</summary>
    public void RoundedRect(float x, float y, float w, float h, float radius, (byte R, byte G, byte B) fill,
        float outline = 3, (byte R, byte G, byte B)? outlineColour = null)
    {
        float s = Scale;
        int x0 = Math.Max(0, (int)(x * s) - 1), x1 = Math.Min(PixelWidth, (int)((x + w) * s) + 1);
        int y0 = Math.Max(0, (int)(y * s) - 1), y1 = Math.Min(PixelHeight, (int)((y + h) * s) + 1);
        float cx = (x + w / 2) * s, cy = (y + h / 2) * s, hx = w / 2 * s - radius * s, hy = h / 2 * s - radius * s;
        for (var py = y0; py < y1; py++)
            for (var px = x0; px < x1; px++)
            {
                // Distance to a rounded box: the inner box grown by the radius.
                float dx = Math.Max(Math.Abs(px + 0.5f - cx) - hx, 0), dy = Math.Max(Math.Abs(py + 0.5f - cy) - hy, 0);
                var distance = MathF.Sqrt(dx * dx + dy * dy) - radius * s;
                if (outline > 0)
                    Blend(px, py, outlineColour ?? (0, 0, 0), Coverage(distance));
                Blend(px, py, fill, Coverage(distance + outline * s));
            }
    }

    /// <summary>A filled triangle (stage units), e.g. a choice arrow.</summary>
    public void Triangle((float X, float Y) a, (float X, float Y) b, (float X, float Y) c, (byte R, byte G, byte B) colour)
    {
        float s = Scale;
        (float X, float Y) p0 = (a.X * s, a.Y * s), p1 = (b.X * s, b.Y * s), p2 = (c.X * s, c.Y * s);
        int x0 = Math.Max(0, (int)Math.Min(p0.X, Math.Min(p1.X, p2.X)) - 1), x1 = Math.Min(PixelWidth, (int)Math.Max(p0.X, Math.Max(p1.X, p2.X)) + 2);
        int y0 = Math.Max(0, (int)Math.Min(p0.Y, Math.Min(p1.Y, p2.Y)) - 1), y1 = Math.Min(PixelHeight, (int)Math.Max(p0.Y, Math.Max(p1.Y, p2.Y)) + 2);
        // Signed distance to each edge (inside negative, whichever winding), then the largest.
        float edge((float X, float Y) from, (float X, float Y) to, float px, float py)
        {
            float dx = to.X - from.X, dy = to.Y - from.Y, length = MathF.Sqrt(dx * dx + dy * dy);
            return ((px - from.X) * dy - (py - from.Y) * dx) / length;
        }
        var winding = MathF.Sign(edge(p0, p1, p2.X, p2.Y));
        for (var py = y0; py < y1; py++)
            for (var px = x0; px < x1; px++)
            {
                float fx = px + 0.5f, fy = py + 0.5f;
                // Inside, every edge has the winding's sign: negate so inside is negative.
                var distance = Math.Max(Math.Max(-winding * edge(p0, p1, fx, fy), -winding * edge(p1, p2, fx, fy)),
                    -winding * edge(p2, p0, fx, fy));
                Blend(px, py, colour, Coverage(distance));
            }
    }

    /// <summary>A ring (an outline only), e.g. the picker's selection.</summary>
    public void Ring(float x, float y, float w, float h, float radius, float thickness, (byte R, byte G, byte B) colour)
    {
        float s = Scale;
        float cx = (x + w / 2) * s, cy = (y + h / 2) * s, hx = w / 2 * s - radius * s, hy = h / 2 * s - radius * s;
        for (var py = 0; py < PixelHeight; py++)
            for (var px = 0; px < PixelWidth; px++)
            {
                float dx = Math.Max(Math.Abs(px + 0.5f - cx) - hx, 0), dy = Math.Max(Math.Abs(py + 0.5f - cy) - hy, 0);
                var distance = MathF.Abs(MathF.Sqrt(dx * dx + dy * dy) - radius * s + thickness * s / 2) - thickness * s / 2;
                Blend(px, py, colour, Coverage(distance));
            }
    }

    /// <summary>
    /// The game's outlined title text, cropped to its ink and centred at (<paramref name="centreX"/>,
    /// <paramref name="centreY"/>), shrunk to fit <paramref name="maxWidth"/> x <paramref name="maxHeight"/>.
    /// </summary>
    public void Text(string text, float centreX, float centreY, float maxWidth, float maxHeight, uint outlineRgb = 0x000000)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        var surface = NativeVerticalTextRasterizer.RenderSongTitle(fontPath, text, null,
            SongTitleTextProfile.GameplayTitle, outlineRgb, (uint)Scale);
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
        Image(surface, (left, top, right - left + 1, bottom - top + 1), centreX, centreY, maxWidth, maxHeight, upscale: false);
    }

    /// <summary>An image (or part of one) centred at a point and fitted into a box, keeping its aspect.</summary>
    public void Image(RgbaTextSurface image, (int X, int Y, int W, int H)? part, float centreX, float centreY,
        float maxWidth, float maxHeight, bool upscale = true)
    {
        var (sx, sy, sw, sh) = part ?? (0, 0, (int)image.Width, (int)image.Height);
        float s = Scale;
        var factor = Math.Min(maxWidth * s / sw, maxHeight * s / sh);
        if (!upscale)
            factor = Math.Min(1f, factor);
        int outWidth = (int)(sw * factor), outHeight = (int)(sh * factor);
        int originX = (int)(centreX * s - outWidth / 2f), originY = (int)(centreY * s - outHeight / 2f);
        var stride = (int)image.Width;
        for (var y = 0; y < outHeight; y++)
            for (var x = 0; x < outWidth; x++)
            {
                var source = ((sy + Math.Min(sh - 1, (int)(y / factor))) * stride + sx + Math.Min(sw - 1, (int)(x / factor))) * 4;
                var alpha = image.Pixels[source + 3] / 255f;
                if (alpha > 0)
                    Blend(originX + x, originY + y,
                        (image.Pixels[source], image.Pixels[source + 1], image.Pixels[source + 2]), alpha);
            }
    }

    public static float Coverage(float distance) => Math.Clamp(0.5f - distance, 0f, 1f);

    /// <summary>Straight-alpha "over" at a pixel; outside the buffer is ignored.</summary>
    public void Blend(int x, int y, (byte R, byte G, byte B) colour, float alpha)
    {
        if (alpha <= 0 || x < 0 || y < 0 || x >= PixelWidth || y >= PixelHeight)
            return;
        var index = (y * PixelWidth + x) * 4;
        var below = Pixels[index + 3] / 255f;
        var result = alpha + below * (1 - alpha);
        byte mix(byte top, byte bottom) => (byte)Math.Round((top * alpha + bottom * below * (1 - alpha)) / result);
        Pixels[index] = mix(colour.R, Pixels[index]);
        Pixels[index + 1] = mix(colour.G, Pixels[index + 1]);
        Pixels[index + 2] = mix(colour.B, Pixels[index + 2]);
        Pixels[index + 3] = (byte)Math.Round(result * 255);
    }
}
