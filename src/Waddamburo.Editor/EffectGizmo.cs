using System.Numerics;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Editor;

/// <summary>Stage/screen mapping uses logical window units, independent of the backing framebuffer scale.</summary>
public readonly record struct EffectViewport(EditorPanel Rectangle)
{
    /// <param name="aspect">The stage's shape on screen: 16:9, or 4:3 for a 4:3 layout (the stage squeezed).</param>
    public static EffectViewport Fit(EditorPanel region, float aspect = 16f / 9)
    {
        var width = Math.Min(region.Width, region.Height * aspect);
        var height = width / aspect;
        return new(new(region.X + (region.Width - width) / 2, region.Y + (region.Height - height) / 2, width, height));
    }
    public Vector2 ToStage(Vector2 screen) => (screen - Rectangle.Position) * new Vector2(1280 / Rectangle.Width, 720 / Rectangle.Height);
    public Vector2 ToScreen(Vector2 stage) => Rectangle.Position + stage * new Vector2(Rectangle.Width / 1280, Rectangle.Height / 720);
    public bool Contains(Vector2 point) => point.X >= Rectangle.X && point.Y >= Rectangle.Y
        && point.X <= Rectangle.X + Rectangle.Width && point.Y <= Rectangle.Y + Rectangle.Height;
}

public static class EffectGizmo
{
    public static StageEffectElement? Pick(IEnumerable<StageEffectElement> elements, Vector2 point, string? selected)
    {
        // Large backdrops should not hide smaller selectable scene elements.
        return elements.Reverse().Where(e => e.Contains(point))
            .OrderBy(e => (double)(e.Maximum.X - e.Minimum.X) * (e.Maximum.Y - e.Minimum.Y))
            .ThenBy(e => e.Target == selected ? 0 : 1).FirstOrDefault();
    }

    /// <summary>
    /// The element after <paramref name="current"/> among those under <paramref name="point"/>, in picking order (smaller
    /// first), wrapping round: clicking again on one spot reaches the ones behind.
    /// </summary>
    public static StageEffectElement? Next(IEnumerable<StageEffectElement> elements, Vector2 point, string? current)
    {
        var under = elements.Reverse().Where(e => e.Contains(point))
            .OrderBy(e => (double)(e.Maximum.X - e.Minimum.X) * (e.Maximum.Y - e.Minimum.Y)).ToArray();
        return under.Length == 0 ? null : under[(Array.FindIndex(under, e => e.Target == current) + 1) % under.Length];
    }
    public static Vector2 ParentVector(LumenMatrix parent, Vector2 vector)
    {
        var determinant = (double)parent.M11 * parent.M22 - (double)parent.M12 * parent.M21;
        if (Math.Abs(determinant) < 1e-10 || !double.IsFinite(determinant))
            throw new InvalidDataException("The camera scale is zero; restore it before dragging this element.");
        var x = (parent.M22 * (double)vector.X - parent.M21 * (double)vector.Y) / determinant;
        var y = (-parent.M12 * (double)vector.X + parent.M11 * (double)vector.Y) / determinant;
        if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > float.MaxValue || Math.Abs(y) > float.MaxValue)
            throw new InvalidDataException("The element's transform is too large to drag.");
        return new((float)x, (float)y);
    }

    /// <summary><paramref name="offset"/> turned by <paramref name="degrees"/> (the stage's y points down) and scaled.</summary>
    public static Vector2 Orbit(Vector2 offset, double degrees, float scale)
    {
        var (sin, cos) = Math.SinCos(degrees * Math.PI / 180);
        return new Vector2((float)(offset.X * cos - offset.Y * sin), (float)(offset.X * sin + offset.Y * cos)) * scale;
    }

    public static double Rotation(Vector2 from, Vector2 to)
    {
        var degrees = (Math.Atan2(to.Y, to.X) - Math.Atan2(from.Y, from.X)) * 180 / Math.PI;
        return degrees > 180 ? degrees - 360 : degrees < -180 ? degrees + 360 : degrees;
    }
    /// <summary>
    /// The shift that snaps the nearest of <paramref name="edges"/> onto one of <paramref name="targets"/> within
    /// <paramref name="threshold"/>, and the target it snapped to (null: none near, no shift).
    /// </summary>
    public static (float Shift, float? Guide) Snap(IEnumerable<float> edges, IEnumerable<float> targets, float threshold)
    {
        var best = (Shift: 0f, Guide: (float?)null, Distance: threshold);
        foreach (var edge in edges)
            foreach (var target in targets)
                if (Math.Abs(target - edge) is var distance && distance < best.Distance)
                    best = (target - edge, target, distance);
        return (best.Shift, best.Guide);
    }

    public static double Scale(Vector2 from, Vector2 to) => from.Length() < 1e-5f ? 1 : Math.Clamp(to.Length() / from.Length(), .001, 1000);
}
