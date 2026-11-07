using System.Numerics;
using ImGuiNET;
using Waddamburo.Game.Gameplay;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

public sealed partial class EffectsEditorSession
{
    private readonly Func<TimeSpan, IReadOnlyList<StageEffectElement>>? _getElements;
    private IReadOnlyList<StageEffectElement> _elements = [];
    private string? _viewportTarget;
    private bool _autoKey = true;
    private enum GizmoMode { Move, Rotate, Scale, Slider }
    private GizmoMode _gizmoMode;
    private sealed record ViewportDrag(GizmoMode Mode, StageEffectElement Element, EffectViewport View,
        Vector2 Start, double Beat, Dictionary<string, double> Values, bool AutoKey);
    private ViewportDrag? _viewportDrag;

    private StageEffectElement CameraElement()
    {
        double Value(string property) => _preview.ValueAt("camera." + property, _transport.Clock.Position);
        var matrix = StageEffects.Transform(Value("x"), Value("y"), Value("rotation"), Value("scale") * Value("scalex"),
            Value("scale") * Value("scaley"), 1, (640, 360))?.Matrix ?? LumenMatrix.Identity;
        var points = new[] { matrix.Transform(0, 0), matrix.Transform(1280, 0), matrix.Transform(1280, 720), matrix.Transform(0, 720) };
        var pivot = matrix.Transform(640, 360);
        return new("camera", new(points.Min(p => p.X), points.Min(p => p.Y)), new(points.Max(p => p.X), points.Max(p => p.Y)),
            new(pivot.X, pivot.Y), LumenMatrix.Identity);
    }

    private StageEffectElement? SelectedElement() => _viewportTarget == "camera" ? CameraElement()
        : _elements.FirstOrDefault(e => e.Target == _viewportTarget);

    private void SelectElement(string target)
    {
        CommitInspector(); EndDrag();
        var track = _trackNames.FirstOrDefault(k => k.StartsWith(target + ".", StringComparison.Ordinal));
        if (track is not null) Select(track, false);
        else { _primary = null; _key = null; _inspectorKey = null; _selected.Clear(); _selectedKeys.Clear(); }
        _viewportTarget = target;
        _addTarget = Math.Max(0, Array.IndexOf(TargetsList, target));
    }

    private void Viewport(EditorPanel region)
    {
        if (_getElements is null) return;
        var view = EffectViewport.Fit(EditorLayout.Preview(region));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (Panel("##Effects viewport", region, false, ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBringToFrontOnFocus))
        {
            ImGui.SetCursorScreenPos(region.Position + new Vector2(8));
            ImGui.BeginDisabled(_history.Current.UsesEmbeddedEffects);
            foreach (var mode in new[] { GizmoMode.Move, GizmoMode.Rotate, GizmoMode.Scale })
            {
                var active = _gizmoMode == mode;
                if (active) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.35f, .44f, .54f, 1));
                if (ImGui.Button(mode.ToString(), new(67, 24))) { EndDrag(); _gizmoMode = mode; }
                if (active) ImGui.PopStyleColor();
                ImGui.SameLine();
            }
            ImGui.Checkbox("Auto-key", ref _autoKey);
            ImGui.EndDisabled();
            ImGui.SetCursorScreenPos(view.Rectangle.Position);
            ImGui.InvisibleButton("Stage canvas", view.Rectangle.Size);
            var hovered = ImGui.IsItemHovered();
            var mouse = ImGui.GetIO().MousePos;
            var draw = ImGui.GetWindowDrawList();
            draw.PushClipRect(view.Rectangle.Position, view.Rectangle.Position + view.Rectangle.Size, true);
            var selected = SelectedElement();
            var hitGizmo = false;
            if (selected is not null && !_history.Current.UsesEmbeddedEffects)
                hitGizmo = DrawGizmo(draw, view, selected, hovered, mouse);
            if (hovered && !hitGizmo && _pending == PendingAction.None && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                var point = view.ToStage(mouse);
                var hit = selected?.Contains(point) == true && selected.Target == "camera" ? selected
                    : EffectGizmo.Pick(_elements, point, _viewportTarget);
                if (hit is not null)
                {
                    SelectElement(hit.Target);
                    BeginViewportDrag(_gizmoMode, hit, view, mouse);
                }
            }
            var help = _gizmoMode == GizmoMode.Move ? "Drag the selection to move" : _gizmoMode == GizmoMode.Rotate
                ? "Drag the ring or selection to rotate" : "Drag a corner or selection to scale";
            draw.AddText(view.Rectangle.Position + new Vector2(8, view.Rectangle.Height - 22), Text,
                selected is null ? "Click an element to select it" : $"{selected.Target}  |  {help}");
            draw.PopClipRect();
        }
        ImGui.End(); ImGui.PopStyleVar();
    }

    private bool DrawGizmo(ImDrawListPtr draw, EffectViewport view, StageEffectElement element, bool hovered, Vector2 mouse)
    {
        var min = view.ToScreen(element.Minimum); var max = view.ToScreen(element.Maximum);
        var pivot = view.ToScreen(element.Pivot);
        if (!float.IsFinite(pivot.X) || !float.IsFinite(pivot.Y)) return false;
        draw.AddRect(min, max, Accent, 0, ImDrawFlags.None, 1.5f);
        var hit = false;
        if (_gizmoMode == GizmoMode.Move)
        {
            draw.AddLine(pivot - new Vector2(9, 0), pivot + new Vector2(32, 0), Playhead, 2);
            draw.AddLine(pivot - new Vector2(0, 9), pivot + new Vector2(0, 32), Accent, 2);
            draw.AddTriangleFilled(pivot + new Vector2(32, 0), pivot + new Vector2(24, -4), pivot + new Vector2(24, 4), Playhead);
            draw.AddTriangleFilled(pivot + new Vector2(0, 32), pivot + new Vector2(-4, 24), pivot + new Vector2(4, 24), Accent);
            hit = hovered && (Vector2.Distance(mouse, pivot) < 12
                || mouse.X >= pivot.X && mouse.X <= pivot.X + 32 && Math.Abs(mouse.Y - pivot.Y) < 6
                || mouse.Y >= pivot.Y && mouse.Y <= pivot.Y + 32 && Math.Abs(mouse.X - pivot.X) < 6);
        }
        else if (_gizmoMode == GizmoMode.Rotate)
        {
            draw.AddCircle(pivot, 48, Playhead, 48, 2.5f);
            draw.AddCircleFilled(pivot + new Vector2(48, 0), 5, Playhead);
            hit = hovered && Math.Abs(Vector2.Distance(mouse, pivot) - 48) < 10;
        }
        else
        {
            foreach (var corner in new[] { min, new Vector2(max.X, min.Y), max, new Vector2(min.X, max.Y) })
            {
                draw.AddRectFilled(corner - new Vector2(5), corner + new Vector2(5), Playhead, 2);
                hit |= hovered && Vector2.Distance(mouse, corner) < 11;
            }
        }
        draw.AddCircleFilled(pivot, 3, Playhead);
        draw.AddText(min + new Vector2(4, 4), Accent, element.Target);
        if (hit && _pending == PendingAction.None && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            BeginViewportDrag(_gizmoMode, element, view, mouse);
        return hit;
    }

    private void BeginViewportDrag(GizmoMode mode, StageEffectElement element, EffectViewport view, Vector2 pointer, string? property = null)
    {
        if (_history.Current.UsesEmbeddedEffects) return;
        if (!CommitInspector()) return;
        EndDrag();
        var beat = SnapBeat(CurrentBeat);
        string[] properties = property is not null ? [property] : mode == GizmoMode.Move ? ["x", "y"]
            : mode == GizmoMode.Rotate ? ["rotation"] : ["scale"];
        var values = properties.ToDictionary(p => element.Target + "." + p, p => _preview.ValueAt(element.Target + "." + p, beat));
        _viewportDrag = new(mode, element, view, view.ToStage(pointer), beat, values, _autoKey);
        _dragBefore = _history.Current; _history.BeginGesture();
    }

    private void MoveViewport()
    {
        if (_viewportDrag is not { } drag || drag.Mode == GizmoMode.Slider || !ImGui.IsMouseDragging(ImGuiMouseButton.Left)) return;
        var point = drag.View.ToStage(ImGui.GetIO().MousePos);
        try
        {
            var values = new Dictionary<string, double>(drag.Values);
            if (drag.Mode == GizmoMode.Move)
            {
                var delta = EffectGizmo.ParentVector(drag.Element.Parent, point - drag.Start);
                values[drag.Element.Target + ".x"] += delta.X; values[drag.Element.Target + ".y"] += delta.Y;
            }
            else
            {
                var from = EffectGizmo.ParentVector(drag.Element.Parent, drag.Start - drag.Element.Pivot);
                var to = EffectGizmo.ParentVector(drag.Element.Parent, point - drag.Element.Pivot);
                var screenDelta = ImGui.GetIO().MousePos.X - drag.View.ToScreen(drag.Start).X;
                var nearPivot = Vector2.Distance(drag.View.ToScreen(drag.Start), drag.View.ToScreen(drag.Element.Pivot)) < 12;
                if (drag.Mode == GizmoMode.Rotate)
                    values[drag.Element.Target + ".rotation"] += nearPivot ? screenDelta : EffectGizmo.Rotation(from, to);
                else values[drag.Element.Target + ".scale"] *= nearPivot ? Math.Exp(Math.Clamp(screenDelta / 120, -7, 7)) : EffectGizmo.Scale(from, to);
            }
            PreviewElementValues(values);
        }
        catch (InvalidDataException e) { _message = e.Message; _messageError = true; }
    }

    private void PreviewElementValues(Dictionary<string, double> values)
    {
        if (_viewportDrag is not { } drag || _dragBefore is null) return;
        try
        {
            var document = _dragBefore.SetValuesAt(drag.Beat, values, drag.AutoKey);
            if (_history.Preview(document)) { _transport.Clock.SetPaused(true); ApplyCurrent(); }
            _selectedKeys.Clear(); _selectedKeys.UnionWith(values.Keys.Select(t => new EffectKey(t, drag.Beat)));
            var first = values.Keys.First();
            if (_expandedGraphs.Add(first)) _revealTrack = first;
            FocusKey(new(first, drag.Beat));
        }
        catch (InvalidDataException e) { _message = e.Message; _messageError = true; }
    }

    private void ElementInspector()
    {
        if (_getElements is null || _viewportTarget is not { } target) return;
        ImGui.TextUnformatted(target + " element");
        double Value(string property) => _preview.ValueAt(target + "." + property, _transport.Clock.Position);
        ImGui.TextUnformatted($"Position: {F(Value("x"))}, {F(Value("y"))}");
        ImGui.TextUnformatted($"Rotation: {F(Value("rotation"))} | Scale: {F(Value("scale"))}");
        if (target == "don" && !_history.Current.UsesEmbeddedEffects)
        {
            foreach (var property in new[] { "yaw", "pitch", "roll" })
            {
                var value = (float)Math.Clamp(Value(property), -360, 360);
                ImGui.SetNextItemWidth(-52);
                var changed = ImGui.SliderFloat(property, ref value, -360, 360, "%.1f");
                if (ImGui.IsItemActivated() && SelectedElement() is { } element)
                    BeginViewportDrag(GizmoMode.Slider, element, EffectViewport.Fit(new(0, 0, 1280, 720)), default, property);
                if (changed && _viewportDrag is { Mode: GizmoMode.Slider }) PreviewElementValues(new() { [target + "." + property] = value });
                if (ImGui.IsItemDeactivated()) EndDrag();
            }
        }
        ImGui.Separator();
    }
}
