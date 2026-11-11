using System.Numerics;

namespace Waddamburo.Editor;

/// <summary>Stage-space gestures shared by single and multiple selections, independent of UI input.</summary>
public static class MovieTransform
{
    public static Vector2 ScaleAnchor(int handle, Vector2 min, Vector2 max, Vector2 pivot, bool alt) => alt ? pivot : handle switch
    {
        3 => max, 4 => new(pivot.X, max.Y), 5 => new(min.X, max.Y), 6 => new(min.X, pivot.Y),
        7 => min, 8 => new(pivot.X, min.Y), 9 => new(max.X, min.Y), 10 => new(max.X, pivot.Y), _ => pivot,
    };

    public static Vector2 ScaleFactors(int handle, Vector2 start, Vector2 point, Vector2 anchor, bool uniform)
    {
        var from = start - anchor;
        var to = point - anchor;
        var x = Math.Abs(from.X) > .001f ? to.X / from.X : 1;
        var y = Math.Abs(from.Y) > .001f ? to.Y / from.Y : 1;
        if (handle is 4 or 8) x = 1;
        if (handle is 6 or 10) y = 1;
        if (handle == 0) x = y = MathF.Exp((point.X - start.X) / 150);
        else if (uniform) x = y = Math.Abs(x - 1) > Math.Abs(y - 1) ? x : y;
        return new(x, y);
    }

    public static Vector2 ConstrainMove(Vector2 delta, int handle, bool shift, ref int lockedAxis)
    {
        if (!shift) lockedAxis = 0;
        else if (lockedAxis == 0 && delta.LengthSquared() > .000001f)
            lockedAxis = Math.Abs(delta.X) >= Math.Abs(delta.Y) ? 1 : 2;
        var axis = shift ? lockedAxis : handle;
        return axis switch { 1 => new(delta.X, 0), 2 => new(0, delta.Y), _ => delta };
    }

    public static bool HasSelectedAncestor(string path, IEnumerable<string> selected) =>
        selected.Any(parent => parent != path && (parent.Length == 0 || path.StartsWith(parent + "/", StringComparison.Ordinal)));

    /// <summary>Converts a world-space operation back to local TRS; refuses singular parents, skew and collapsed axes.</summary>
    public static bool TryApply(Matrix3x2 local, Matrix3x2 parent, Matrix3x2 change, out Vector2 position, out float angle, out Vector2 scale)
    {
        position = scale = default;
        angle = 0;
        if (!Matrix3x2.Invert(parent, out var inverse)) return false;
        var matrix = local * parent * change * inverse;
        var x = new Vector2(matrix.M11, matrix.M12);
        var y = new Vector2(matrix.M21, matrix.M22);
        var sx = x.Length();
        var sy = y.Length();
        if (!float.IsFinite(sx + sy + matrix.M31 + matrix.M32) || sx < 1e-6f || sy < 1e-6f
            || Math.Abs(Vector2.Dot(x, y)) > sx * sy * .0001f) return false;
        position = matrix.Translation;
        angle = MathF.Atan2(x.Y, x.X) * 180 / MathF.PI;
        scale = new(sx, matrix.GetDeterminant() / sx);
        return true;
    }
}
