namespace Waddamburo.Platform.Sdl.Rendering;

/// <summary>A frame's final normalized bounds, or a cap joined to its top border.</summary>
public readonly record struct RenderPixelFrame(RenderRectangle Bounds, float JoinThickness = 0);

/// <summary>Rasterizes opposite frame edges from the same pixel distances, after all layout/morph transforms.</summary>
public static class RenderPixelGrid
{
    public static RenderQuad Snap(RenderQuad quad, int width, int height)
    {
        if (quad.PixelFrame is not { } frame || width <= 0 || height <= 0) return quad;
        var box = frame.Bounds;
        if (box.Width <= 0 || box.Height <= 0) return quad;
        RenderVertex snap(RenderVertex v)
        {
            if (frame.JoinThickness > 0)
            {
                // Keep curved cap contours smooth; snap only their clipped join.
                return Math.Abs(v.Y - box.Y - frame.JoinThickness) < .000001f
                    ? v with { Y = (pixel(box.Y * height) + pixel(frame.JoinThickness * height)) / height } : v;
            }
            return v with { X = axis(v.X, box.X, box.Width, width), Y = axis(v.Y, box.Y, box.Height, height) };
        }
        return quad with { TopLeft = snap(quad.TopLeft), TopRight = snap(quad.TopRight),
            BottomLeft = snap(quad.BottomLeft), BottomRight = snap(quad.BottomRight) };
    }

    private static float axis(float value, float start, float size, int pixels) =>
        (value - start <= size / 2
            ? pixel(start * pixels) + pixel((value - start) * pixels)
            : pixel((start + size) * pixels) - pixel((start + size - value) * pixels)) / pixels;

    // Normalized float coordinates can land either side of an exact half pixel.
    // Quantize that sub-pixel numerical noise before rounding the shared distance.
    private static float pixel(float value) => MathF.Floor(value + .5005f);
}
