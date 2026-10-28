namespace Waddamburo.Lumen.Rendering;

/// <summary>Fits native bevels with square texels around a separate, four-pixel black rim.</summary>
public static class LumenNativeFrame
{
    public static LumenNativeSurfacePlacement Bounds(LumenRenderQuad quad)
    {
        var left = Math.Min(Math.Min(quad.TopLeft.X, quad.TopRight.X), Math.Min(quad.BottomLeft.X, quad.BottomRight.X));
        var top = Math.Min(Math.Min(quad.TopLeft.Y, quad.TopRight.Y), Math.Min(quad.BottomLeft.Y, quad.BottomRight.Y));
        var right = Math.Max(Math.Max(quad.TopLeft.X, quad.TopRight.X), Math.Max(quad.BottomLeft.X, quad.BottomRight.X));
        var bottom = Math.Max(Math.Max(quad.TopLeft.Y, quad.TopRight.Y), Math.Max(quad.BottomLeft.Y, quad.BottomRight.Y));
        return new(left, top, right - left, bottom - top);
    }

    public static LumenNativeSurfacePlacement Bounds(IReadOnlyList<LumenRenderQuad> quads)
    {
        if (quads.Count == 0) throw new InvalidOperationException("Sequence contains no elements");
        var result = Bounds(quads[0]);
        for (var i = 1; i < quads.Count; i++) result = union(result, Bounds(quads[i]));
        return result;
    }

    public static LumenNativeSurfacePlacement Bounds(IEnumerable<LumenRenderQuad> quads)
    {
        if (quads is IReadOnlyList<LumenRenderQuad> list) return Bounds(list);
        using var enumerator = quads.GetEnumerator();
        if (!enumerator.MoveNext()) throw new InvalidOperationException("Sequence contains no elements");
        var result = Bounds(enumerator.Current);
        while (enumerator.MoveNext()) result = union(result, Bounds(enumerator.Current));
        return result;
    }

    private static LumenNativeSurfacePlacement union(LumenNativeSurfacePlacement first, LumenNativeSurfacePlacement second)
    {
        var left = Math.Min(first.X, second.X); var top = Math.Min(first.Y, second.Y);
        return new(left, top, Math.Max(first.X + first.Width, second.X + second.Width) - left,
            Math.Max(first.Y + first.Height, second.Y + second.Height) - top);
    }

    public static IEnumerable<LumenRenderQuad> Compose(IReadOnlyList<LumenRenderQuad> quads, float rowScale = 1, bool normalizeAspect = true)
    {
        if (quads.Count == 0) yield break;
        var source = Bounds(quads);
        const int padding = 4;
        var x0 = source.X - padding; var y0 = source.Y - padding;
        var x1 = source.X + source.Width + padding; var y1 = source.Y + source.Height + padding;
        var sample = quads[0]; var largest = -1f;
        foreach (var quad in quads)
        {
            var box = Bounds(quad); var area = box.Width * box.Height;
            if (area > largest) { largest = area; sample = quad; }
        }
        var u = (sample.TopLeft.U + sample.BottomRight.U) / 2;
        var v = (sample.TopLeft.V + sample.BottomRight.V) / 2;
        yield return sample with { TopLeft = new(x0, y0, u, v), TopRight = new(x1, y0, u, v),
            BottomLeft = new(x0, y1, u, v), BottomRight = new(x1, y1, u, v),
            MultiplyColor = new(0, 0, 0, sample.MultiplyColor.Alpha), AddColor = LumenRenderColor.Transparent,
            Blend = LumenRenderBlend.Normal, UseNearestSampling = true };
        // Native bevel pieces contain transparent fringe. Their interior rests
        // on the native body colour, rather than exposing the black rim through it.
        yield return sample with { TopLeft = new(x0 + 4, y0 + 4, u, v), TopRight = new(x1 - 4, y0 + 4, u, v),
            BottomLeft = new(x0 + 4, y1 - 4, u, v), BottomRight = new(x1 - 4, y1 - 4, u, v), UseNearestSampling = true };
        if (!normalizeAspect)
        {
            foreach (var quad in quads)
                if (LumenQuadGeometry.Clip(quad, new(x0 + 4, y0 + 4, x1 - x0 - 8, y1 - y0 - 8)) is { } interior)
                    yield return interior with { UseNearestSampling = true };
            yield break;
        }
        const float blackX = 0;
        const float blackY = 0;
        const float cornerX = 35.2f;
        var cornerY = 20.8f * rowScale;
        float[] sx = [source.X + blackX, source.X + cornerX, source.X + source.Width - cornerX, source.X + source.Width - blackX];
        float[] sy = [source.Y + blackY, source.Y + cornerY, source.Y + source.Height - cornerY, source.Y + source.Height - blackY];
        // Undo the browser's unequal .65/1.1 factors for the native corner pixels.
        var bevelX = Math.Min((cornerX - blackX) * .8f / 1.1f, (x1 - x0 - 8) / 2);
        var bevelY = Math.Min((cornerY - blackY) * .8f / (.65f * rowScale), (y1 - y0 - 8) / 2);
        float[] dx = [x0 + 4, x0 + 4 + bevelX, x1 - 4 - bevelX, x1 - 4];
        float[] dy = [y0 + 4, y0 + 4 + bevelY, y1 - 4 - bevelY, y1 - 4];
        foreach (var quad in quads)
            for (var row = 0; row < 3; row++)
                for (var column = 0; column < 3; column++)
                {
                    if (LumenQuadGeometry.Clip(quad, new(sx[column], sy[row], sx[column + 1] - sx[column], sy[row + 1] - sy[row])) is not { } slice) continue;
                    LumenRenderVertex fit(LumenRenderVertex p) => p with
                    {
                        X = dx[column] + (p.X - sx[column]) / (sx[column + 1] - sx[column]) * (dx[column + 1] - dx[column]),
                        Y = dy[row] + (p.Y - sy[row]) / (sy[row + 1] - sy[row]) * (dy[row + 1] - dy[row]),
                    };
                    yield return slice with { TopLeft = fit(slice.TopLeft), TopRight = fit(slice.TopRight),
                        BottomLeft = fit(slice.BottomLeft), BottomRight = fit(slice.BottomRight), UseNearestSampling = true };
                }
    }
}
