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
            if (MovieTransform.HasSelectedAncestor(clip.Path, _selection)) continue;
            if (layerOf(clips, clip) is not { } source || !player.TryInspectParentTransform(clip.Path, out var parent)) continue;
            var key = MovieDocument.KeyAt((JsonObject)source.Layer.DeepClone(), source.Frame);
            float value(string name) => (float)MovieDocument.Value(key, name)!;
            var local = Matrix3x2.CreateScale(value("scaleX"), value("scaleY"))
                * Matrix3x2.CreateRotation(value("rotation") * MathF.PI / 180)
                * Matrix3x2.CreateTranslation(value("x"), value("y"));
            _groupDrag.Add(new(source.Layer, source.Frame, new(parent.M11, parent.M12, parent.M21, parent.M22, parent.X, parent.Y), local));
        }
    }

    private void transformGroup(StageMove move, Vector2 point, Vector2 delta, Vector2 anchor)
    {
        var change = Matrix3x2.Identity;
        if (move.Op == 0)
            change = Matrix3x2.CreateTranslation(delta);
        else if (move.Op == 1)
        {
            var angle = (float)EffectGizmo.Rotation(move.Start - move.Pivot, point - move.Pivot);
            if (ImGuiNET.ImGui.GetIO().KeyShift) angle = MathF.Round(angle / 15) * 15;
            change = Matrix3x2.CreateRotation(angle * MathF.PI / 180, anchor);
        }
        else
        {
            var factors = MovieTransform.ScaleFactors(move.Handle, move.Start, point, anchor, ImGuiNET.ImGui.GetIO().KeyShift);
            change = Matrix3x2.CreateScale(factors, anchor);
        }
        var updates = new List<(GroupMember Member, Vector2 Position, float Angle, Vector2 Scale)>();
        foreach (var member in _groupDrag)
        {
            if (!MovieTransform.TryApply(member.Local, member.Parent, change, out var position, out var angle, out var scale))
            {
                _notice = "This transform requires skew or a collapsed axis. Use uniform scaling (Shift), or edit the objects separately.";
                return;
            }
            updates.Add((member, position, angle, scale));
        }
        if (updates.Count == 0) return;
        var revision = _document!.Revision;
        edited(() => _document.Edit(_ =>
        {
            foreach (var (member, position, angle, scale) in updates)
            {
                // Only what this kind of drag changes: a move leaves rotation and scale alone.
                if (move.Op == 0)
                    setValues(member.Layer, member.Frame, ("x", position.X), ("y", position.Y));
                else
                    setValues(member.Layer, member.Frame, ("x", position.X), ("y", position.Y), ("rotation", angle),
                        ("scaleX", scale.X), ("scaleY", scale.Y));
            }
        }, _groupEdited));
        _groupEdited |= _document.Revision != revision;
        _notice = null;
    }
}
