using System.Numerics;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

/// <summary>Stage/screen mapping uses logical window units, independent of the backing framebuffer scale.</summary>
public readonly record struct EffectViewport(EditorPanel Rectangle)
{
    public static EffectViewport Fit(EditorPanel region)
    {
        var width = Math.Min(region.Width, region.Height * 16 / 9);
        var height = width * 9 / 16;
        return new(new(region.X + (region.Width - width) / 2, region.Y + (region.Height - height) / 2, width, height));
    }
    public Vector2 ToStage(Vector2 screen) => (screen - Rectangle.Position) * new Vector2(1280 / Rectangle.Width, 720 / Rectangle.Height);
    public Vector2 ToScreen(Vector2 stage) => Rectangle.Position + stage * new Vector2(Rectangle.Width / 1280, Rectangle.Height / 720);
    public bool Contains(Vector2 point) => point.X >= Rectangle.X && point.Y >= Rectangle.Y
        && point.X <= Rectangle.X + Rectangle.Width && point.Y <= Rectangle.Y + Rectangle.Height;
}

public static class EffectGizmo
{
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

    public static double Rotation(Vector2 from, Vector2 to)
    {
        var degrees = (Math.Atan2(to.Y, to.X) - Math.Atan2(from.Y, from.X)) * 180 / Math.PI;
        return degrees > 180 ? degrees - 360 : degrees < -180 ? degrees + 360 : degrees;
    }
    public static double Scale(Vector2 from, Vector2 to) => from.Length() < 1e-5f ? 1 : Math.Clamp(to.Length() / from.Length(), .001, 1000);
}
