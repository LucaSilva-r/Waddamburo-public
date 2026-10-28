namespace Waddamburo.Lumen.Rendering;

/// <summary>Moves native panel corners intact; only the flat middle spans resize.</summary>
public static class LumenPanelMorph
{
    public static LumenRenderVertex Warp(LumenRenderVertex vertex, LumenNativeSurfacePlacement source,
        LumenNativeSurfacePlacement destination) => vertex with
    {
        X = map(vertex.X, source.X, source.Width, destination.X, destination.Width),
        Y = map(vertex.Y, source.Y, source.Height, destination.Y, destination.Height),
    };

    /// <summary>Collapses a frame to zero without leaving corners or shadow outside its aperture.</summary>
    public static LumenRenderQuad? WarpHorizontal(LumenRenderQuad quad, LumenNativeSurfacePlacement source,
        LumenNativeSurfacePlacement destination)
    {
        var warped = quad with { TopLeft = Warp(quad.TopLeft, source, destination), TopRight = Warp(quad.TopRight, source, destination),
            BottomLeft = Warp(quad.BottomLeft, source, destination), BottomRight = Warp(quad.BottomRight, source, destination) };
        // Below two4px rims, retain only pixels inside the shrinking aperture.
        return destination.Width < 8 ? LumenQuadGeometry.Clip(warped, destination with { Y = -10000, Height = 20000 }) : warped;
    }

    /// <summary>Splits a native rectangular button into nine UV-preserving pieces before resizing it.</summary>
    public static IEnumerable<LumenRenderQuad> ResizeSlices(LumenRenderQuad quad,
        LumenNativeSurfacePlacement destination, float border = 12, float inset = 0)
    {
        var vertices = new[] { quad.TopLeft, quad.TopRight, quad.BottomLeft, quad.BottomRight };
        var x = vertices.Min(v => v.X); var y = vertices.Min(v => v.Y);
        var width = vertices.Max(v => v.X) - x; var height = vertices.Max(v => v.Y) - y;
        x += inset; y += inset; width -= 2 * inset; height -= 2 * inset;
        var bx = Math.Min(border, Math.Min(width, destination.Width) / 2);
        var by = Math.Min(border, Math.Min(height, destination.Height) / 2);
        float[] sx = [x, x + bx, x + width - bx, x + width];
        float[] sy = [y, y + by, y + height - by, y + height];
        float[] dx = [destination.X, destination.X + bx, destination.X + destination.Width - bx, destination.X + destination.Width];
        float[] dy = [destination.Y, destination.Y + by, destination.Y + destination.Height - by, destination.Y + destination.Height];
        var a = quad.TopLeft; var b = quad.TopRight; var c = quad.BottomLeft;
        var det = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (Math.Abs(det) < .0001f) yield break;
        LumenRenderVertex point(int column, int row)
        {
            var px = sx[column] - a.X; var py = sy[row] - a.Y;
            var u = (px * (c.Y - a.Y) - py * (c.X - a.X)) / det;
            var v = ((b.X - a.X) * py - (b.Y - a.Y) * px) / det;
            return new(dx[column], dy[row], a.U + u * (b.U - a.U) + v * (c.U - a.U),
                a.V + u * (b.V - a.V) + v * (c.V - a.V));
        }
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                yield return quad with { TopLeft = point(column, row), TopRight = point(column + 1, row),
                    BottomLeft = point(column, row + 1), BottomRight = point(column + 1, row + 1), UseNearestSampling = true };
    }

    private static float map(float value, float start, float length, float destination, float size)
    {
        // The rotated native 32px corner is 35.2px wide in the browser.
        var border = Math.Min(36, Math.Min(length, size) / 2);
        var local = value - start;
        if (local <= border) return destination + local;
        if (local >= length - border) return destination + size - (length - local);
        return destination + border + (local - border) * (size - 2 * border) / (length - 2 * border);
    }
}
