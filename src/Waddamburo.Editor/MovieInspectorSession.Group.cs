using System.Numerics;
using System.Text.Json.Nodes;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private sealed record GroupMember(JsonObject Layer, int Frame, Matrix3x2 Parent, Matrix3x2 Local);
    private bool _groupEdited;
    private readonly List<GroupMember> _groupDrag = [];
    private bool selectionBounds(LumenPlayer player, out LumenNativeSurfacePlacement bounds)
    {
        var min = new Vector2(float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity);
        foreach (var path in _selection)
            if (player.TryInspectBounds(path, out var box))
            {
                min = Vector2.Min(min, new(box.X, box.Y));
                max = Vector2.Max(max, new(box.X + box.Width, box.Y + box.Height));
            }
        bounds = new(min.X, min.Y, max.X - min.X, max.Y - min.Y);
        return float.IsFinite(min.X);
    }

    private void captureGroup(LumenPlayer player, IReadOnlyList<LumenInstanceInfo> clips)
    {
        _groupDrag.Clear();
        _groupEdited = false;
        foreach (var clip in clips.Where(clip => _selection.Contains(clip.Path)))
        {
            if (_selection.Any(path => path != clip.Path && (path.Length == 0 || clip.Path.StartsWith(path + "/", StringComparison.Ordinal)))) continue;
            if (layerOf(clips, clip) is not { } source || !player.TryInspectParentTransform(clip.Path, out var parent)) continue;
            var key = MovieDocument.KeyAt((JsonObject)source.Layer.DeepClone(), source.Frame);
            float value(string name) => (float)MovieDocument.Value(key, name)!;
            var local = Matrix3x2.CreateScale(value("scaleX"), value("scaleY"))
                * Matrix3x2.CreateRotation(value("rotation") * MathF.PI / 180)
                * Matrix3x2.CreateTranslation(value("x"), value("y"));
            _groupDrag.Add(new(source.Layer, source.Frame, new(parent.M11, parent.M12, parent.M21, parent.M22, parent.X, parent.Y), local));
        }
    }

    private void transformGroup(StageMove move, Vector2 point, Vector2 delta, Vector2 anchor, bool continues)
    {
        var change = Matrix3x2.Identity;
        if (_stageTool == 0)
            change = Matrix3x2.CreateTranslation(delta);
        else if (_stageTool == 1)
        {
            var angle = (float)EffectGizmo.Rotation(move.Start - move.Pivot, point - move.Pivot);
            if (ImGuiNET.ImGui.GetIO().KeyShift) angle = MathF.Round(angle / 15) * 15;
            change = Matrix3x2.CreateRotation(angle * MathF.PI / 180, anchor);
        }
        else
        {
            var start = move.Start - anchor;
            var now = point - anchor;
            var sx = Math.Abs(start.X) > .001f ? now.X / start.X : 1;
            var sy = Math.Abs(start.Y) > .001f ? now.Y / start.Y : 1;
            if (move.Handle is 4 or 8) sx = 1;
            if (move.Handle is 6 or 10) sy = 1;
            if (move.Handle == 0) sx = sy = MathF.Exp(delta.X / 150);
            else if (ImGuiNET.ImGui.GetIO().KeyShift) sx = sy = Math.Abs(sx - 1) > Math.Abs(sy - 1) ? sx : sy;
            change = Matrix3x2.CreateScale(sx, sy, anchor);
        }
        var updates = new List<(GroupMember Member, Matrix3x2 Matrix)>();
        foreach (var member in _groupDrag)
        {
            if (!Matrix3x2.Invert(member.Parent, out var inverse)) return;
            var matrix = member.Local * member.Parent * change * inverse;
            // The package currently stores rotation and scale, not skew. Never silently distort a selection.
            var dot = matrix.M11 * matrix.M21 + matrix.M12 * matrix.M22;
            var lengths = MathF.Sqrt((matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12) * (matrix.M21 * matrix.M21 + matrix.M22 * matrix.M22));
            if (Math.Abs(dot) > Math.Max(.0001f, lengths * .0001f))
            {
                _notice = "This group transform needs skew. Use uniform scaling (Shift), or edit the rotated objects separately.";
                return;
            }
            updates.Add((member, matrix));
        }
        edited(() => _document!.Edit(_ =>
        {
            foreach (var (member, matrix) in updates)
            {
                var key = MovieDocument.KeyAt(member.Layer, member.Frame);
                var sx = MathF.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12);
                MovieDocument.Set(key, "x", matrix.M31);
                MovieDocument.Set(key, "y", matrix.M32);
                MovieDocument.Set(key, "rotation", MathF.Atan2(matrix.M12, matrix.M11) * 180 / MathF.PI);
                MovieDocument.Set(key, "scaleX", sx);
                MovieDocument.Set(key, "scaleY", sx > 1e-6f ? matrix.GetDeterminant() / sx : 0);
            }
        }, _groupEdited));
        _groupEdited = true;
        _notice = null;
    }
}
