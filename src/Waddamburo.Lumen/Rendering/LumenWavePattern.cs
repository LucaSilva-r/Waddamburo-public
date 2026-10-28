namespace Waddamburo.Lumen.Rendering;

/// <summary>A continuous seigaiha field constructed from semicircles and antialiased arc bands.</summary>
public static class LumenWavePattern
{
    public static readonly LumenNativeSurfaceKey Stroke = new("vector-ui:stroke");
    private const float Radius = 37.5f;
    private static readonly (float X, float Y)[] Arc = Enumerable.Range(0, 13)
        .Select(i => (MathF.Cos(MathF.PI + i * MathF.PI / 12), MathF.Sin(MathF.PI + i * MathF.PI / 12))).ToArray();

    public static IEnumerable<LumenRenderQuad> Draw(LumenNativeSurfacePlacement aperture, float alpha, byte maskDepth,
        LumenNativeSurfacePlacement? visibleBounds = null)
    {
        var blue = new LumenRenderColor(.16f, .63f, .84f, alpha);
        var light = new LumenRenderColor(.66f, .86f, .94f, alpha);
        LumenRenderQuad masked(LumenRenderQuad quad) => quad with { MaskDepth = maskDepth };
        yield return masked(LumenVectorGeometry.Rectangle(aperture, blue));
        // All rows share one coordinate system for the entire aperture, rather
        // than restarting at PNG tile boundaries. No mirrored edge pixels.
        for (var row = 0; row <= MathF.Ceiling(aperture.Height / (Radius / 2)) + 2; row++)
        {
            var y = aperture.Y + row * Radius / 2;
            // Cull against the reveal without moving the pattern's origin. A narrow
            // folder otherwise spends the shared vertex buffer on invisible waves.
            if (visibleBounds is { } vertical && (y + 2 < vertical.Y || y - Radius - 2 > vertical.Y + vertical.Height)) continue;
            var phase = (row & 1) * Radius;
            for (var column = 0; column <= MathF.Ceiling(aperture.Width / (Radius * 2)); column++)
            {
                var x = aperture.X + column * Radius * 2 + phase;
                if (visibleBounds is { } horizontal && (x + Radius + 2 < horizontal.X || x - Radius - 2 > horizontal.X + horizontal.Width)) continue;
                var contour = Arc.Select(p => new LumenVectorGeometry.Point(x + p.X * Radius, y + p.Y * Radius)).ToArray();
                foreach (var quad in LumenVectorGeometry.Polygon(contour, blue, antialias: false)) yield return masked(quad);
                foreach (var radius in new[] { 35.5f, 27.5f, 19.5f, 11.5f })
                    for (var segment = 0; segment < Arc.Length - 1; segment++)
                    {
                        var a = Arc[segment]; var b = Arc[segment + 1];
                        const float halfBand = 1.8f;
                        var quad = LumenVectorGeometry.Quad(
                            new(x + a.X * (radius - halfBand), y + a.Y * (radius - halfBand)),
                            new(x + a.X * (radius + halfBand), y + a.Y * (radius + halfBand)),
                            new(x + b.X * (radius + halfBand), y + b.Y * (radius + halfBand)),
                            new(x + b.X * (radius - halfBand), y + b.Y * (radius - halfBand)), light, Stroke);
                        yield return masked(quad);
                    }
            }
        }
    }
}
