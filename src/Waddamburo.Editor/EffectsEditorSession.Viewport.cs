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
    // Corner: the scale handle dragged (corners 0 top left, 1 top right, 2 bottom right, 3 bottom left; sides 4 top,
    // 5 right, 6 bottom, 7 left), -1 for none.
    // Group: every element the drag moves, rotates or scales (the selection; the primary Element first).
    private sealed record ViewportDrag(GizmoMode Mode, StageEffectElement Element, EffectViewport View,
        Vector2 Start, double Beat, Dictionary<string, double> Values, bool AutoKey, int Corner = -1, string[]? Group = null,
        (Vector2 Min, Vector2 Max)? Box = null, Dictionary<string, Vector2>? Pivots = null, int Axis = 0);
    // The move arrow grabbed: 0 none (free), 1 x only, 2 y only.
    private int _axis;
    // Elements selected with the primary one (Ctrl+click): dragged, turned and scaled together.
    private readonly HashSet<string> _also = new(StringComparer.Ordinal);
    private IEnumerable<string> SelectedTargets => _viewportTarget is { } primary ? _also.Prepend(primary) : [];
    private IEnumerable<StageEffectElement> SelectedGroup => SelectedTargets
        .Select(target => _elements.FirstOrDefault(e => e.Target == target)).OfType<StageEffectElement>();
    private int _corner = -1;
    // A press inside the selection: if it ends without a drag (a click), the next element behind there is selected.
    private (Vector2 Point, bool Moved)? _clickThrough;
    // Snapping guides shown while dragging (stage units): vertical lines at X, horizontal at Y.
    private readonly List<float> _guideX = [], _guideY = [];
    private bool FourThree => _preview.ValueAt("camera.aspect", _transport.Clock.Position) < 1;
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

    // What the gizmo works on: the selected element, or several as one object (their joint box, its centre the pivot;
    // the lead element's name, so the drag takes the whole selection).
    private StageEffectElement? GizmoElement()
    {
        var group = SelectedGroup.ToArray();
        if (group.Length < 2) return SelectedElement();
        var (min, max) = (group.Select(e => e.Minimum).Aggregate(Vector2.Min), group.Select(e => e.Maximum).Aggregate(Vector2.Max));
        return group[0] with { Minimum = min, Maximum = max, Pivot = (min + max) / 2 };
    }

    private void SelectElement(string target)
    {
        _also.Clear();
        SelectPrimary(target);
    }

    // Ctrl+click: an element joins the selection (and leads it), or leaves it. The camera is selected alone.
    private void ToggleElement(string target)
    {
        if (_viewportTarget is null || target == "camera" || _viewportTarget == "camera") { SelectElement(target); return; }
        if (target == _viewportTarget)
        {
            if (_also.FirstOrDefault() is not { } next) return;
            _also.Remove(next);
            SelectPrimary(next);
        }
        else if (!_also.Remove(target))
        {
            _also.Add(_viewportTarget);
            SelectPrimary(target);
        }
    }

    private void SelectPrimary(string target)
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
        var view = EffectViewport.Fit(EditorLayout.Preview(region), FourThree ? 4f / 3 : 16f / 9);
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
            if (!_layout) { ImGui.Checkbox("Auto-key", ref _autoKey); ImGui.SameLine(); }
            var freeze = _transport.FreezeAnimations;
            if (ImGui.Checkbox("Freeze animations", ref freeze)) _transport.FreezeAnimations = freeze;
            ImGui.EndDisabled();
            ImGui.SetCursorScreenPos(view.Rectangle.Position);
            ImGui.InvisibleButton("Stage canvas", view.Rectangle.Size);
            var hovered = ImGui.IsItemHovered();
            var mouse = ImGui.GetIO().MousePos;
            var draw = ImGui.GetWindowDrawList();
            draw.PushClipRect(view.Rectangle.Position, view.Rectangle.Position + view.Rectangle.Size, true);
            var selected = GizmoElement();
            foreach (var member in _also.Count > 0 ? SelectedGroup : []) // each member's own box, inside the selection's
                draw.AddRect(view.ToScreen(member.Minimum), view.ToScreen(member.Maximum), Muted, 0, ImDrawFlags.None, 1);
            var hitGizmo = false;
            if (selected is not null && !_history.Current.UsesEmbeddedEffects)
                hitGizmo = DrawGizmo(draw, view, selected, hovered, mouse);
            if (hovered && !hitGizmo && _pending == PendingAction.None && ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                && ImGui.GetIO().KeyCtrl)
            {
                if (EffectGizmo.Pick(_elements, view.ToStage(mouse), null) is { } hit) ToggleElement(hit.Target);
            }
            else if (hovered && !hitGizmo && _pending == PendingAction.None && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                var point = view.ToStage(mouse);
                // The selection keeps the press (even under elements in front): it drags, or a click goes deeper.
                if (selected?.Contains(point) == true || SelectedGroup.Any(e => e.Contains(point)))
                {
                    _clickThrough = (point, false);
                    BeginViewportDrag(_gizmoMode, selected ?? SelectedGroup.First(), view, mouse);
                }
                else if (EffectGizmo.Pick(_elements, point, _viewportTarget) is { } hit)
                {
                    SelectElement(hit.Target);
                    BeginViewportDrag(_gizmoMode, hit, view, mouse);
                }
            }
            if (_clickThrough is { } press)
            {
                if (ImGui.IsMouseDragging(ImGuiMouseButton.Left, 3)) _clickThrough = press with { Moved = true };
                else if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    _clickThrough = null;
                    if (!press.Moved && EffectGizmo.Next(_elements, press.Point, _viewportTarget) is { } behind)
                        SelectElement(behind.Target);
                }
                if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _clickThrough = null;
            }
            if (_viewportDrag is null) { _guideX.Clear(); _guideY.Clear(); }
            foreach (var x in _guideX)
                draw.AddLine(view.ToScreen(new(x, 0)), view.ToScreen(new(x, 720)), Guide, 1);
            foreach (var y in _guideY)
                draw.AddLine(view.ToScreen(new(0, y)), view.ToScreen(new(1280, y)), Guide, 1);
            var help = _gizmoMode == GizmoMode.Move ? "Drag the selection to move (Alt: no snapping)" : _gizmoMode == GizmoMode.Rotate
                ? "Drag the ring or selection to rotate" : "Corners stretch (Shift: keep shape), sides one axis, the selection scales";
            draw.AddText(view.Rectangle.Position + new Vector2(8, view.Rectangle.Height - 22), Text,
                selected is null ? "Click an element to select it (Ctrl+click: several)"
                : $"{string.Join(", ", SelectedTargets)}  |  {help}");
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
            // The centre moves freely; each arrow moves along its axis only.
            var onX = hovered && mouse.X >= pivot.X + 9 && mouse.X <= pivot.X + 34 && Math.Abs(mouse.Y - pivot.Y) < 7;
            var onY = hovered && mouse.Y >= pivot.Y + 9 && mouse.Y <= pivot.Y + 34 && Math.Abs(mouse.X - pivot.X) < 7;
            _axis = onX ? 1 : onY ? 2 : 0;
            if (onX) draw.AddLine(pivot, pivot + new Vector2(32, 0), Text, 3);
            if (onY) draw.AddLine(pivot, pivot + new Vector2(0, 32), Text, 3);
            hit = hovered && Vector2.Distance(mouse, pivot) < 9 || onX || onY;
        }
        else if (_gizmoMode == GizmoMode.Rotate)
        {
            draw.AddCircle(pivot, 48, Playhead, 48, 2.5f);
            draw.AddCircleFilled(pivot + new Vector2(48, 0), 5, Playhead);
            hit = hovered && Math.Abs(Vector2.Distance(mouse, pivot) - 48) < 10;
        }
        else
        {
            _corner = -1;
            var handles = Handles(min, max);
            for (var index = 0; index < handles.Length; index++)
            {
                var size = index < 4 ? 5 : 4;
                draw.AddRectFilled(handles[index] - new Vector2(size), handles[index] + new Vector2(size), index < 4 ? Playhead : Accent, 2);
                if (hovered && Vector2.Distance(mouse, handles[index]) < 11) { hit = true; _corner = index; }
            }
        }
        draw.AddCircleFilled(pivot, 3, Playhead);
        draw.AddText(min + new Vector2(4, 4), Accent, element.Target);
        if (hit && _pending == PendingAction.None && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            BeginViewportDrag(_gizmoMode, element, view, mouse, corner: _gizmoMode == GizmoMode.Scale ? _corner : -1,
                axis: _gizmoMode == GizmoMode.Move ? _axis : 0);
        return hit;
    }

    private void BeginViewportDrag(GizmoMode mode, StageEffectElement element, EffectViewport view, Vector2 pointer, string? property = null,
        int corner = -1, int axis = 0)
    {
        if (_history.Current.UsesEmbeddedEffects) return;
        if (!CommitInspector()) return;
        EndDrag();
        var beat = _layout ? 0 : SnapBeat(CurrentBeat);
        // Turning and scaling move the elements too: as one object, each orbits the selection's centre.
        string[] properties = property is not null ? [property] : mode == GizmoMode.Move ? ["x", "y"]
            : mode == GizmoMode.Rotate ? ["rotation", "x", "y"] : corner >= 0 ? ["x", "y", "scalex", "scaley"] : ["scale", "x", "y"];
        // The gizmo's drags take the whole selection; a single property (the inspector), the element alone.
        string[] group = property is null && element.Target != "camera"
            ? [element.Target, .. SelectedTargets.Where(t => t != element.Target)] : [element.Target];
        var values = group.SelectMany(target => properties.Select(p => target + "." + p))
            .ToDictionary(key => key, key => _preview.ValueAt(key, beat));
        // The selection's joint box as the drag begins (what snaps; the live bounds move with the drag).
        var members = _elements.Where(e => group.Contains(e.Target)).DefaultIfEmpty(element).ToArray();
        var box = (members.Select(e => e.Minimum).Aggregate(Vector2.Min), members.Select(e => e.Maximum).Aggregate(Vector2.Max));
        var pivots = members.ToDictionary(static e => e.Target, static e => e.Pivot);
        _viewportDrag = new(mode, element, view, view.ToStage(pointer), beat, values, _layout || _autoKey, corner, group, box, pivots, axis);
        _dragBefore = _history.Current; _history.BeginGesture();
    }

    private void MoveViewport()
    {
        if (_viewportDrag is not { } drag || drag.Mode == GizmoMode.Slider || !ImGui.IsMouseDragging(ImGuiMouseButton.Left)) return;
        var point = drag.View.ToStage(ImGui.GetIO().MousePos);
        try
        {
            var values = new Dictionary<string, double>(drag.Values);
            _guideX.Clear(); _guideY.Clear();
            var group = drag.Group ?? [drag.Element.Target];
            if (drag.Mode == GizmoMode.Move)
            {
                // The selection's joint box snaps; every element moves by the same amount.
                var moved = point - drag.Start;
                if (drag.Axis == 1) moved.Y = 0; else if (drag.Axis == 2) moved.X = 0; // an arrow: its axis only
                var (boxMin, boxMax) = drag.Box ?? (drag.Element.Minimum, drag.Element.Maximum);
                var (min, max) = (boxMin + moved, boxMax + moved);
                moved += SnapShift(drag.Axis == 2 ? [] : [min.X, (min.X + max.X) / 2, max.X],
                    drag.Axis == 1 ? [] : [min.Y, (min.Y + max.Y) / 2, max.Y], drag);
                var delta = EffectGizmo.ParentVector(drag.Element.Parent, moved);
                foreach (var target in group)
                {
                    values[target + ".x"] += delta.X; values[target + ".y"] += delta.Y;
                }
            }
            else if (drag.Corner >= 0)
                StretchCorner(drag, point, values);
            else
            {
                var from = EffectGizmo.ParentVector(drag.Element.Parent, drag.Start - drag.Element.Pivot);
                var to = EffectGizmo.ParentVector(drag.Element.Parent, point - drag.Element.Pivot);
                var screenDelta = ImGui.GetIO().MousePos.X - drag.View.ToScreen(drag.Start).X;
                var nearPivot = Vector2.Distance(drag.View.ToScreen(drag.Start), drag.View.ToScreen(drag.Element.Pivot)) < 12;
                var (turn, ratio) = drag.Mode == GizmoMode.Rotate
                    ? (nearPivot ? screenDelta : EffectGizmo.Rotation(from, to), 1d)
                    : (0d, nearPivot ? Math.Exp(Math.Clamp(screenDelta / 120, -7, 7)) : EffectGizmo.Scale(from, to));
                foreach (var target in group)
                {
                    if (drag.Mode == GizmoMode.Rotate) values[target + ".rotation"] += turn; else values[target + ".scale"] *= ratio;
                    // Its centre turns and scales about the selection's: one rigid object (no move for a single element).
                    var pivot = drag.Pivots?.GetValueOrDefault(target) ?? drag.Element.Pivot;
                    var shift = EffectGizmo.ParentVector(drag.Element.Parent,
                        EffectGizmo.Orbit(pivot - drag.Element.Pivot, turn, (float)ratio) - (pivot - drag.Element.Pivot));
                    values[target + ".x"] += shift.X; values[target + ".y"] += shift.Y;
                }
            }
            PreviewElementValues(values);
        }
        catch (InvalidDataException e) { _message = e.Message; _messageError = true; }
    }

    // The scale handles: corners (0-3, clockwise from top left), then side midpoints (4 top, 5 right, 6 bottom, 7 left).
    private static Vector2[] Handles(Vector2 min, Vector2 max)
    {
        var mid = (min + max) / 2;
        return [min, new(max.X, min.Y), max, new(min.X, max.Y), new(mid.X, min.Y), new(max.X, mid.Y), new(mid.X, max.Y), new(min.X, mid.Y)];
    }

    // A corner or side dragged to point (snapped to nearby edges); the opposite one stays where it is (unrotated elements).
    // A side scales its axis alone.
    private void StretchCorner(ViewportDrag drag, Vector2 point, Dictionary<string, double> values)
    {
        var handles = Handles(drag.Element.Minimum, drag.Element.Maximum);
        var side = drag.Corner >= 4;
        var (dragged, anchor) = (handles[drag.Corner], handles[side ? 4 + (drag.Corner - 2) % 4 : (drag.Corner + 2) % 4]);
        var (axisX, axisY) = (!side || drag.Corner is 5 or 7, !side || drag.Corner is 4 or 6);
        point += SnapShift(axisX ? [point.X] : [], axisY ? [point.Y] : [], drag);
        var size = dragged - anchor;
        var ratio = new Vector2(!axisX || Math.Abs(size.X) < 1 ? 1 : (point.X - anchor.X) / size.X,
            !axisY || Math.Abs(size.Y) < 1 ? 1 : (point.Y - anchor.Y) / size.Y);
        if (ImGui.GetIO().KeyShift && !side) ratio = new(Math.Max(ratio.X, ratio.Y));
        ratio = Vector2.Clamp(ratio, new(.01f), new(100));
        // Each element scales about its own centre, which moves away from the anchor by the same ratio (the anchor stays).
        foreach (var target in drag.Group ?? [drag.Element.Target])
        {
            var pivot = drag.Pivots?.GetValueOrDefault(target) ?? drag.Element.Pivot;
            var shift = EffectGizmo.ParentVector(drag.Element.Parent, (anchor - pivot) * (Vector2.One - ratio));
            values[target + ".scalex"] *= ratio.X; values[target + ".scaley"] *= ratio.Y;
            values[target + ".x"] += shift.X; values[target + ".y"] += shift.Y;
        }
    }

    // Edges and centres snap to other elements' and the stage's within 6 screen pixels (Alt: off); the guides show where.
    private Vector2 SnapShift(float[] xs, float[] ys, ViewportDrag drag)
    {
        if (ImGui.GetIO().KeyAlt) return Vector2.Zero;
        var group = drag.Group ?? [drag.Element.Target];
        var others = _elements.Where(e => !group.Contains(e.Target) && e.Target != "camera").ToArray();
        var threshold = drag.View.ToStage(drag.View.Rectangle.Position + new Vector2(6)) - drag.View.ToStage(drag.View.Rectangle.Position);
        var (shiftX, guideX) = EffectGizmo.Snap(xs, [0, 640, 1280, .. others.SelectMany(e => (float[])[e.Minimum.X, (e.Minimum.X + e.Maximum.X) / 2, e.Maximum.X])], threshold.X);
        var (shiftY, guideY) = EffectGizmo.Snap(ys, [0, 360, 720, .. others.SelectMany(e => (float[])[e.Minimum.Y, (e.Minimum.Y + e.Maximum.Y) / 2, e.Maximum.Y])], threshold.Y);
        if (guideX is { } x) _guideX.Add(x);
        if (guideY is { } y) _guideY.Add(y);
        return new(shiftX, shiftY);
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
        if (_layout) { LayoutValues(target); return; }
        if (_also.Count > 0) { ImGui.TextWrapped($"Also selected: {string.Join(", ", _also)}"); ImGui.Separator(); return; }
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
