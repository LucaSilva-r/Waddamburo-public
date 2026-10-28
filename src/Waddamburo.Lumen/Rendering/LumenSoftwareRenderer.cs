namespace Waddamburo.Lumen.Rendering;

public sealed record LumenSoftwareTexture(int Width, int Height, ReadOnlyMemory<byte> Rgba);

/// <summary>A bounded CPU preview of textured Lumen snapshots, including stencil masks.</summary>
public static class LumenSoftwareRenderer
{
    public static byte[] Render(LumenRenderSnapshot snapshot, IReadOnlyList<LumenSoftwareTexture> textures, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(textures);
        if (width <= 0 || height <= 0 || (long)width * height > 16_777_216)
            throw new ArgumentOutOfRangeException(nameof(width));
        foreach (var texture in textures)
            if (texture.Width <= 0 || texture.Height <= 0 || (long)texture.Width * texture.Height * 4 != texture.Rgba.Length)
                throw new ArgumentException("Invalid RGBA texture.", nameof(textures));
        var pixels = new byte[checked(width * height * 4)];
        var stencil = new byte[checked(width * height)];
        foreach (var quad in snapshot.Quads)
        {
            if (quad.NativeSurface is not null || quad.TextureIndex >= textures.Count) continue;
            var texture = textures[(int)quad.TextureIndex];
            LumenRenderVertex scale(LumenRenderVertex p) => p with { X = p.X * width / snapshot.StageWidth, Y = p.Y * height / snapshot.StageHeight };
            var a = scale(quad.TopLeft);
            var b = scale(quad.TopRight);
            var c = scale(quad.BottomRight);
            var d = scale(quad.BottomLeft);
            triangle(a, b, c, quad, texture, pixels, stencil, width, height);
            triangle(a, c, d, quad, texture, pixels, stencil, width, height);
        }
        return pixels;
    }

    private static void triangle(LumenRenderVertex a, LumenRenderVertex b, LumenRenderVertex c,
        LumenRenderQuad quad, LumenSoftwareTexture texture, byte[] pixels, byte[] stencil, int width, int height)
    {
        var area = edge(a, b, c.X, c.Y);
        if (!float.IsFinite(area) || Math.Abs(area) < 1e-8f) return;
        if (area < 0) { (b, c) = (c, b); area = -area; }
        var left = (int)Math.Clamp(MathF.Floor(Math.Min(a.X, Math.Min(b.X, c.X))), 0, width);
        var right = (int)Math.Clamp(MathF.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))), 0, width);
        var top = (int)Math.Clamp(MathF.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))), 0, height);
        var bottom = (int)Math.Clamp(MathF.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))), 0, height);
        var source = texture.Rgba.Span;
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            var wa = edge(b, c, x + 0.5f, y + 0.5f);
            var wb = edge(c, a, x + 0.5f, y + 0.5f);
            var wc = edge(a, b, x + 0.5f, y + 0.5f);
            if (!inside(wa, b, c) || !inside(wb, c, a) || !inside(wc, a, b)) continue;
            wa /= area; wb /= area; wc /= area;
            var u = wa * a.U + wb * b.U + wc * c.U;
            var v = wa * a.V + wb * b.V + wc * c.V;
            if (!float.IsFinite(u) || !float.IsFinite(v)) continue;
            var tx = (int)Math.Clamp(u * texture.Width, 0, texture.Width - 1);
            var ty = (int)Math.Clamp(v * texture.Height, 0, texture.Height - 1);
            var offset = (ty * texture.Width + tx) * 4;
            var alpha = Math.Clamp(source[offset + 3] / 255f * quad.MultiplyColor.Alpha + quad.AddColor.Alpha, 0, 1);
            if (alpha <= 0) continue;
            var pixel = y * width + x;
            if (stencil[pixel] != quad.MaskDepth) continue;
            if (quad.MaskOperation == LumenRenderMaskOperation.Push) { if (stencil[pixel] < byte.MaxValue) stencil[pixel]++; continue; }
            if (quad.MaskOperation == LumenRenderMaskOperation.Pop) { if (stencil[pixel] > 0) stencil[pixel]--; continue; }
            var target = pixel * 4;
            var destinationAlpha = pixels[target + 3] / 255f;
            var outputAlpha = alpha + destinationAlpha * (1 - alpha);
            for (var channel = 0; channel < 3; channel++)
            {
                var multiply = channel == 0 ? quad.MultiplyColor.Red : channel == 1 ? quad.MultiplyColor.Green : quad.MultiplyColor.Blue;
                var add = channel == 0 ? quad.AddColor.Red : channel == 1 ? quad.AddColor.Green : quad.AddColor.Blue;
                var value = Math.Clamp(source[offset + channel] / 255f * multiply + add, 0, 1);
                var destination = pixels[target + channel] / 255f;
                var blended = quad.Blend switch
                {
                    LumenRenderBlend.Add => Math.Min(1, destination + value),
                    LumenRenderBlend.Screen => 1 - (1 - destination) * (1 - value),
                    LumenRenderBlend.Multiply => destination * value,
                    _ => value,
                };
                // Straight-alpha output. Include the source contribution outside destination coverage.
                var output = ((1 - alpha) * destination * destinationAlpha + (1 - destinationAlpha) * value * alpha
                    + destinationAlpha * alpha * blended) / outputAlpha;
                pixels[target + channel] = (byte)Math.Clamp((int)MathF.Round(output * 255), 0, 255);
            }
            pixels[target + 3] = (byte)MathF.Round(outputAlpha * 255);
        }
    }

    private static float edge(LumenRenderVertex a, LumenRenderVertex b, float x, float y) =>
        (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);

    private static bool inside(float value, LumenRenderVertex a, LumenRenderVertex b) =>
        value > 0 || (value == 0 && (b.Y < a.Y || (b.Y == a.Y && b.X > a.X)));
}
