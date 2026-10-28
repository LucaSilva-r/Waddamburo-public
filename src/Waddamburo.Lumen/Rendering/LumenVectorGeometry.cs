namespace Waddamburo.Lumen.Rendering;

/// <summary>Resolution-independent UI geometry. Only colour/coverage fields are sampled from textures.</summary>
public static class LumenVectorGeometry
{
    public static readonly LumenNativeSurfaceKey White = new("vector-ui:white");
    public static readonly LumenNativeSurfaceKey Coverage = new("vector-ui:coverage");
    public static readonly LumenNativeSurfaceKey Shadow = new("vector-ui:shadow");
    public const float FrameBorder = 4;
    public const string FrameAnchor = "vector-frame:anchor";
    public const string PegAnchor = "vector-frame:peg";
    public const string FrameEnd = "vector-frame:end";
    public readonly record struct Point(float X, float Y);

    public static LumenRenderQuad Quad(Point a, Point b, Point c, Point d, LumenRenderColor colour,
        LumenNativeSurfaceKey? surface = null) => new(0,
            new(a.X, a.Y, 0, 0), new(b.X, b.Y, 1, 0), new(c.X, c.Y, 1, 1), new(d.X, d.Y, 0, 1),
            colour, LumenRenderColor.Transparent, NativeSurface: surface ?? White);

    public static LumenRenderQuad Rectangle(LumenNativeSurfacePlacement box, LumenRenderColor colour,
        LumenNativeSurfaceKey? surface = null) => Quad(new(box.X, box.Y), new(box.X + box.Width, box.Y),
            new(box.X + box.Width, box.Y + box.Height), new(box.X, box.Y + box.Height), colour, surface);

    public static Point[] RectanglePath(LumenNativeSurfacePlacement box) =>
        [new(box.X, box.Y), new(box.X + box.Width, box.Y), new(box.X + box.Width, box.Y + box.Height), new(box.X, box.Y + box.Height)];

    /// <summary>Insets a clockwise convex contour with exact intersections of its offset edges.</summary>
    public static Point[] Offset(IReadOnlyList<Point> path, float amount)
    {
        var result = new Point[path.Count];
        for (var i = 0; i < path.Count; i++)
        {
            var a = path[(i + path.Count - 1) % path.Count]; var b = path[i]; var c = path[(i + 1) % path.Count];
            var ax = b.X - a.X; var ay = b.Y - a.Y; var bx = c.X - b.X; var by = c.Y - b.Y;
            var al = MathF.Sqrt(ax * ax + ay * ay); var bl = MathF.Sqrt(bx * bx + by * by);
            var nx = -ay / al - by / bl; var ny = ax / al + bx / bl;
            var denominator = 1 + (ax * bx + ay * by) / (al * bl);
            var dx = amount * nx / Math.Max(.01f, denominator); var dy = amount * ny / Math.Max(.01f, denominator);
            // A nearly collapsed tab can form an acute join. Limit its miter
            // rather than extruding an antialias spike outside the contour.
            var length = MathF.Sqrt(dx * dx + dy * dy);
            var limit = Math.Abs(amount) * 4;
            var scale = length > limit ? limit / length : 1;
            result[i] = new(b.X + dx * scale, b.Y + dy * scale);
        }
        return result;
    }

    public static IEnumerable<LumenRenderQuad> Polygon(IReadOnlyList<Point> path, LumenRenderColor colour, bool antialias = true, bool openBottom = false)
    {
        if (path.Count < 3 || colour.Alpha <= 0) yield break;
        var centre = new Point(path.Average(p => p.X), path.Average(p => p.Y));
        // A quad carries two adjacent fan triangles, keeping vector fields
        // within the renderer's transient vertex budget without losing detail.
        for (var i = 0; i < path.Count; i += 2)
            yield return Quad(centre, path[i], path[(i + 1) % path.Count],
                i + 2 <= path.Count ? path[(i + 2) % path.Count] : path[(i + 1) % path.Count], colour);
        if (!antialias) yield break;
        var outside = Offset(path, -.65f);
        var bottom = path.Max(p => p.Y);
        if (openBottom)
            for (var i = 0; i < outside.Length; i++) outside[i] = outside[i] with { Y = Math.Min(outside[i].Y, bottom) };
        for (var i = 0; i < path.Count; i++)
        {
            var next = (i + 1) % path.Count;
            if (openBottom && Math.Abs(path[i].Y - bottom) < .0001f && Math.Abs(path[next].Y - bottom) < .0001f) continue;
            var edge = Quad(outside[i], outside[next], path[next], path[i], colour, Coverage);
            yield return edge with
            {
                TopLeft = edge.TopLeft with { U = 0, V = .5f }, TopRight = edge.TopRight with { U = 0, V = .5f },
                BottomRight = edge.BottomRight with { U = 1, V = .5f }, BottomLeft = edge.BottomLeft with { U = 1, V = .5f },
            };
        }
    }

    /// <summary>Rounded upper corners and sloping sides, with the folder's original flat base.</summary>
    public static Point[] Tab(LumenNativeSurfacePlacement box, bool peg = false)
    {
        var points = new List<Point>();
        void at(float x, float y)
        {
            var p = new Point(box.X + x, box.Y + y);
            if (points.Count == 0 || Math.Abs(points[^1].X - p.X) + Math.Abs(points[^1].Y - p.Y) > .00001f)
                points.Add(p);
        }
        void curve(float ax, float ay, float bx, float by, float cx, float cy)
        {
            for (var i = 1; i <= 12; i++)
            {
                var t = i / 12f; var u = 1 - t;
                at(u * u * ax + 2 * u * t * bx + t * t * cx, u * u * ay + 2 * u * t * by + t * t * cy);
            }
        }
        if (peg)
        {
            // Original cropped 44x20 cap: asymmetric round shoulders meet
            // upright sides. Keep the native outline instead of a trapezoid.
            var sx = box.Width / 44; var sy = box.Height / 20;
            at(0, 9.8f * sy);
            curve(0, 9.8f * sy, 0, 0, 12.44f * sx, 0);
            at(32.23f * sx, 0);
            curve(32.23f * sx, 0, 41.115f * sx, 0, box.Width, 8 * sy);
            at(box.Width, box.Height); at(0, box.Height);
        }
        else
        {
            // The original 64px end caps have broad shoulders and a shallow
            // straight lower side, rather than a generic rounded trapezoid.
            var cap = Math.Min(box.Height, box.Width / 2);
            var scale = cap / 64;
            const float top = 28.25f, control = 52.875f, end = 56.875f, bendY = 24.375f;
            at((64 - end) * scale, bendY * scale);
            curve((64 - end) * scale, bendY * scale, (64 - control) * scale, 0, (64 - top) * scale, 0);
            at(box.Width - (64 - top) * scale, 0);
            curve(box.Width - (64 - top) * scale, 0, box.Width - (64 - control) * scale, 0,
                box.Width - (64 - end) * scale, bendY * scale);
            at(box.Width - .6f * scale, box.Height); at(.6f * scale, box.Height);
        }
        return points.ToArray();
    }

    public static IEnumerable<LumenRenderQuad> OutlinedTab(LumenNativeSurfacePlacement box, LumenRenderColor colour, bool peg = false)
    {
        var path = Tab(box, peg);
        foreach (var quad in Polygon(path, new(0, 0, 0, colour.Alpha))) yield return quad;
        // The base rests behind the front frame, so there is no extra horizontal outline at the join.
        var inside = Offset(path, Math.Min(4, Math.Min(box.Width, box.Height) / 4));
        inside[^2] = inside[^2] with { Y = box.Y + box.Height + .1f };
        inside[^1] = inside[^1] with { Y = box.Y + box.Height + .1f };
        foreach (var quad in Polygon(inside, colour)) yield return quad;
    }

    /// <summary>The label's open base meets the same pixel edge as the card's top bevel.</summary>
    public static LumenRenderQuad[] CategoryTab(LumenNativeSurfacePlacement box, LumenRenderColor colour,
        LumenNativeSurfacePlacement card) => joinedTab(box, colour, card, peg: false);

    /// <summary>The cap covers the top rim; neither its sides nor its base can hang below that rim.</summary>
    public static LumenRenderQuad[] FolderPeg(LumenNativeSurfacePlacement box, LumenRenderColor colour,
        LumenNativeSurfacePlacement card) => joinedTab(box, colour, card, peg: true);

    private static LumenRenderQuad[] joinedTab(LumenNativeSurfacePlacement box, LumenRenderColor colour,
        LumenNativeSurfacePlacement card, bool peg)
    {
        if (colour.Alpha <= 0) return [];
        var join = card.Y + FrameBorder;
        var path = Tab(box, peg);
        var inside = Offset(path, Math.Min(FrameBorder, Math.Min(box.Width, box.Height) / 4));
        if (!peg)
        {
            // The original tab bounds can end a fractional pixel before the
            // bevel. Extend only its open base, keeping the shoulders/text fixed.
            path[^2] = path[^2] with { Y = join };
            path[^1] = path[^1] with { Y = join };
        }
        inside[^2] = inside[^2] with { Y = peg ? box.Y + box.Height + .1f : join };
        inside[^1] = inside[^1] with { Y = peg ? box.Y + box.Height + .1f : join };
        var outer = above(path); var fill = above(inside);
        if (outer.Length < 3 || fill.Length < 3) return [];
        var drawing = new List<LumenRenderQuad>
        {
            Rectangle(card, LumenRenderColor.Transparent) with { Source = PegAnchor },
        };
        drawing.AddRange(Polygon(outer, new(0, 0, 0, colour.Alpha), openBottom: true));
        drawing.AddRange(Polygon(fill, colour, openBottom: true));
        drawing[^1] = drawing[^1] with { Source = FrameEnd };
        return drawing.ToArray();

        Point[] above(IReadOnlyList<Point> contour)
        {
            var clipped = new List<Point>();
            void at(Point p)
            {
                if (clipped.Count == 0 || clipped[^1] != p) clipped.Add(p);
            }
            for (var i = 0; i < contour.Count; i++)
            {
                var a = contour[(i + contour.Count - 1) % contour.Count]; var b = contour[i];
                if ((a.Y <= join) != (b.Y <= join))
                    at(new(a.X + (b.X - a.X) * (join - a.Y) / (b.Y - a.Y), join));
                if (b.Y <= join) at(b);
            }
            if (clipped.Count > 1 && clipped[0] == clipped[^1]) clipped.RemoveAt(clipped.Count - 1);
            return clipped.ToArray();
        }
    }

    /// <summary>Compact nine-slice shadow with a crisp edge and a fixed offset at every panel size.</summary>
    public static LumenRenderQuad[] SoftShadow(LumenNativeSurfacePlacement box, float alpha)
    {
        const float blur = 4;
        float[] x = [box.X + 4 - blur, box.X + 4 + blur, box.X + box.Width + 4 - blur, box.X + box.Width + 4 + blur];
        float[] y = [box.Y + 5 - blur, box.Y + 5 + blur, box.Y + box.Height + 5 - blur, box.Y + box.Height + 5 + blur];
        float[] uv = [0, .375f, .625f, 1];
        var quads = new List<LumenRenderQuad>(9);
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
            quads.Add(new(0, new(x[column], y[row], uv[column], uv[row]), new(x[column + 1], y[row], uv[column + 1], uv[row]),
                new(x[column + 1], y[row + 1], uv[column + 1], uv[row + 1]), new(x[column], y[row + 1], uv[column], uv[row + 1]),
                new(0, 0, 0, alpha), LumenRenderColor.Transparent, NativeSurface: Shadow));
        return quads.ToArray();
    }

    /// <summary>Index 1 remains the rim anchor used by the panel morphs; all visible edges are vector polygons.</summary>
    public static LumenRenderQuad[] Frame(LumenNativeSurfacePlacement box, LumenNativeSurfaceKey paint,
        float alpha = 1, bool verticalGradient = false, float border = FrameBorder, float bevelSize = 6)
    {
        var shadow = SoftShadow(box, alpha);
        var result = new List<LumenRenderQuad> { shadow[0], Rectangle(box, LumenRenderColor.Transparent) with { Source = FrameAnchor } };
        result.AddRange(shadow.Skip(1));
        result.Add(Rectangle(box, new(0, 0, 0, alpha)));
        var left = box.X + border; var top = box.Y + border; var right = box.X + box.Width - border; var bottom = box.Y + box.Height - border;
        var bevel = Math.Min(bevelSize, Math.Min(right - left, bottom - top) / 2);
        if (bevel <= 0)
        {
            result[^1] = result[^1] with { Source = FrameEnd };
            return result.ToArray();
        }
        LumenRenderQuad painted(Point a, Point b, Point c, Point d, float multiplier = 1, float add = 0)
        {
            LumenRenderVertex vertex(Point p) => new(p.X, p.Y,
                verticalGradient ? .5f : .5f / 256 + (p.X - left) / (right - left) * (255f / 256),
                verticalGradient ? .5f / 256 + (p.Y - top) / (bottom - top) * (255f / 256) : .5f);
            return new(0, vertex(a), vertex(b), vertex(c), vertex(d), new(multiplier, multiplier, multiplier, alpha),
                new(add, add, add, 0), NativeSurface: paint);
        }
        result.Add(painted(new(left, top), new(right, top), new(right, bottom), new(left, bottom)));
        result.Add(painted(new(left, top), new(right, top), new(right - bevel, top + bevel), new(left + bevel, top + bevel), .65f, .35f));
        result.Add(painted(new(left, bottom), new(left + bevel, bottom - bevel), new(right - bevel, bottom - bevel), new(right, bottom), .55f));
        result.Add(painted(new(left, top), new(left + bevel, top + bevel), new(left + bevel, bottom - bevel), new(left, bottom), .65f, .35f));
        result.Add(painted(new(right - bevel, top + bevel), new(right, top), new(right, bottom), new(right - bevel, bottom - bevel), .55f));
        result[^1] = result[^1] with { Source = FrameEnd };
        return result.ToArray();
    }
}
