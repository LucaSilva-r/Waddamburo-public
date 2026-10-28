namespace Waddamburo.Lumen.Rendering;

/// <summary>Clips screen-aligned quads while preserving rotated or reversed texture coordinates.</summary>
public static class LumenQuadGeometry
{
    public static LumenRenderQuad? Clip(LumenRenderQuad quad, LumenNativeSurfacePlacement clip)
    {
        var bounds = LumenNativeFrame.Bounds(quad);
        var left = Math.Max(clip.X, bounds.X);
        var right = Math.Min(clip.X + clip.Width, bounds.X + bounds.Width);
        var top = Math.Max(clip.Y, bounds.Y);
        var bottom = Math.Min(clip.Y + clip.Height, bounds.Y + bounds.Height);
        if (right <= left || bottom <= top) return null;
        var a = quad.TopLeft; var b = quad.TopRight; var c = quad.BottomLeft;
        var determinant = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (Math.Abs(determinant) < .00001f) return null;
        LumenRenderVertex at(float x, float y)
        {
            var dx = x - a.X; var dy = y - a.Y;
            var u = (dx * (c.Y - a.Y) - dy * (c.X - a.X)) / determinant;
            var v = ((b.X - a.X) * dy - (b.Y - a.Y) * dx) / determinant;
            return new(x, y, a.U + u * (b.U - a.U) + v * (c.U - a.U), a.V + u * (b.V - a.V) + v * (c.V - a.V));
        }
        return quad with { TopLeft = at(left, top), TopRight = at(right, top),
            BottomLeft = at(left, bottom), BottomRight = at(right, bottom) };
    }

    public static LumenRenderQuad Move(LumenRenderQuad quad, float x, float y)
    {
        LumenRenderVertex move(LumenRenderVertex v) => v with { X = v.X + x, Y = v.Y + y };
        return quad with { TopLeft = move(quad.TopLeft), TopRight = move(quad.TopRight),
            BottomLeft = move(quad.BottomLeft), BottomRight = move(quad.BottomRight) };
    }

    public static IEnumerable<LumenRenderQuad> FadeLeft(LumenRenderQuad quad, float start, float end)
    {
        LumenRenderQuad mirror(LumenRenderQuad source)
        {
            LumenRenderVertex flip(LumenRenderVertex vertex) => vertex with { X = start + end - vertex.X };
            return source with { TopLeft = flip(source.TopRight), TopRight = flip(source.TopLeft),
                BottomLeft = flip(source.BottomRight), BottomRight = flip(source.BottomLeft) };
        }
        return FadeRight(mirror(quad), start, end).Select(mirror);
    }

    public static IEnumerable<LumenRenderQuad> FadeRight(LumenRenderQuad quad, float start, float end)
    {
        var bounds = LumenNativeFrame.Bounds(quad);
        if (bounds.X + bounds.Width <= start) { yield return quad; yield break; }
        if (bounds.X >= end) yield break;
        if (Clip(quad, new(-10000, -10000, start + 10000, 20000)) is { } opaque) yield return opaque;
        var first = start + Math.Max(0, MathF.Floor(bounds.X - start));
        var last = Math.Min(end, bounds.X + bounds.Width);
        for (var x = first; x < last; x += 1)
        {
            var width = Math.Min(1, end - x);
            if (Clip(quad, new(x, -10000, width, 20000)) is not { } strip) continue;
            var fraction = Math.Clamp(((x + width / 2 - start) / (end - start) - .25f) / .75f, 0, 1);
            var alpha = strip.MaskOperation == LumenRenderMaskOperation.Draw ? MathF.Cos(fraction * MathF.PI / 2) : 1;
            yield return strip with { MultiplyColor = strip.MultiplyColor with { Alpha = strip.MultiplyColor.Alpha * alpha },
                AddColor = strip.AddColor with { Alpha = strip.AddColor.Alpha * alpha } };
        }
    }

    public static IEnumerable<LumenRenderQuad> TileClockwise(LumenRenderQuad quad, float width, float height, bool mirrorAlternateColumns = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var bounds = LumenNativeFrame.Bounds(quad);
        var left = bounds.X; var top = bounds.Y;
        var right = bounds.X + bounds.Width; var bottom = bounds.Y + bounds.Height;
        var original = Clip(quad, new(left, top, right - left, bottom - top));
        if (original is not { } upright) yield break;
        // Rotate the existing on-screen orientation, including the compact
        // category's own rotation; replacing UVs with absolute values cancels it.
        if (Math.Abs(upright.TopRight.U - upright.TopLeft.U) < Math.Abs(upright.TopRight.V - upright.TopLeft.V))
            (width, height) = (height, width);
        for (var y = top; y < bottom; y += height)
        {
            var column = 0;
            for (var x = left; x < right; x += width, column++)
            {
                var tile = quad with { TopLeft = new(x, y, upright.BottomLeft.U, upright.BottomLeft.V),
                    TopRight = new(x + width, y, upright.TopLeft.U, upright.TopLeft.V),
                    BottomLeft = new(x, y + height, upright.BottomRight.U, upright.BottomRight.V),
                    BottomRight = new(x + width, y + height, upright.TopRight.U, upright.TopRight.V) };
                // Mirrored repeat joins equal source edges, so a non-periodic
                // motif remains continuous even at the clipped final column.
                if (mirrorAlternateColumns && (column & 1) != 0)
                    tile = tile with
                    {
                        TopLeft = tile.TopRight with { X = tile.TopLeft.X },
                        TopRight = tile.TopLeft with { X = tile.TopRight.X },
                        BottomLeft = tile.BottomRight with { X = tile.BottomLeft.X },
                        BottomRight = tile.BottomLeft with { X = tile.BottomRight.X },
                    };
                if (Clip(tile, new(left, top, right - left, bottom - top)) is { } clipped) yield return clipped;
            }
        }
    }
}

public sealed record LumenTextLayout(float Width, float Height, IReadOnlyList<LumenTextLine> Lines);
public readonly record struct LumenTextLine(float Width, float Top, float Height);

/// <summary>Each line repeats at its own period inside the same rectangle, without reversing direction.</summary>
public static class LumenTextMarquee
{
    public static IEnumerable<LumenRenderQuad> Compose(LumenRenderQuad quad, LumenTextLayout layout,
        float left, float right, float ticks, float? subtitleTicks = null, float scale = 1)
    {
        var vertices = new[] { quad.TopLeft, quad.TopRight, quad.BottomLeft, quad.BottomRight };
        var x = vertices.Min(v => v.X); var y = vertices.Min(v => v.Y);
        var width = vertices.Max(v => v.X) - x; var height = vertices.Max(v => v.Y) - y;
        for (var lineIndex = 0; lineIndex < layout.Lines.Count; lineIndex++)
        {
            var line = layout.Lines[lineIndex];
            var lineWidth = Math.Min(width, (line.Width + 6) / layout.Width * width);
            var lineTop = y + line.Top / layout.Height * height;
            var lineHeight = line.Height / layout.Height * height;
            var sourceLeft = x + (width - lineWidth) / 2;
            if (LumenQuadGeometry.Clip(quad, new(sourceLeft, lineTop, lineWidth, lineHeight)) is not { } source) continue;
            if (lineIndex > 0)
            {
                // Keep a stable raster size without snapping a moving parent to pixels.
                var snappedHeight = Math.Max(1, MathF.Round(lineHeight / scale)) * scale;
                var factor = snappedHeight / lineHeight;
                var middle = sourceLeft + lineWidth / 2;
                LumenRenderVertex fit(LumenRenderVertex vertex) => vertex with
                { X = middle + (vertex.X - middle) * factor, Y = lineTop + (vertex.Y - lineTop) * factor };
                source = source with { TopLeft = fit(source.TopLeft), TopRight = fit(source.TopRight),
                    BottomLeft = fit(source.BottomLeft), BottomRight = fit(source.BottomRight) };
                lineWidth *= factor;
                sourceLeft = middle - lineWidth / 2;
                lineHeight = snappedHeight;
            }
            var viewport = right - left;
            var scrolling = lineWidth > viewport;
            // Keep the marquee's clock in card coordinates. Recomputing its
            // period in projected pixels jumps to another phase as a D-wheel
            // row approaches/recedes, even when its title age has not changed.
            var period = lineWidth + 48 * scale;
            var speed = .75f * scale;
            var age = Math.Max(0, lineIndex == 0 ? ticks : subtitleTicks ?? ticks);
            var phase = age % (60 + period / speed);
            var offset = scrolling ? Math.Max(0, phase - 60) * speed : 0;
            var target = scrolling ? left - offset : left + (viewport - lineWidth) / 2;
            for (var copy = 0; copy < (scrolling ? 2 : 1); copy++)
                if (LumenQuadGeometry.Clip(LumenQuadGeometry.Move(source, target + copy * period - sourceLeft, 0),
                    new(left, lineTop, viewport, lineHeight)) is { } clipped) yield return clipped;
        }
    }
}
