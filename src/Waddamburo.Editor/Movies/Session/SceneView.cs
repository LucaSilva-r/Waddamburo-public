using System.Numerics;
using ImGuiNET;
using Waddamburo.Game.Scenes;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

/// <summary>
/// The scene view: a screen's layers listed, the screen playing in the middle, the selected layer's place, scale,
/// turn, role and drum on the right. Gameplay's own movies (the lane, gauge, song info...) are listed first and placed
/// the same way, written as the skin's places (scenes.json gameplay places). On the stage: Move (W), Rotate (E),
/// Scale (R) and Transform (T) tools, snapping to the stage's edges and middle, the other layers' edges and a grid
/// (Ctrl held: no snapping). One gesture is one undo step.
/// </summary>
public sealed partial class MovieInspectorSession
{
    private enum SceneTool { Move, Rotate, Scale, Transform }

    private SceneTool _sceneTool = SceneTool.Move;
    private bool _snap = true;
    private int _grid = 4; // an index of GridSteps: 8 px
    // The gesture under way on the stage: what it does, where it started, the layer as it was then.
    private (SceneTool Tool, Vector2 From, SkinScenes.Layer Start, int Handle, Vector2 Anchor)? _gesture2;
    private bool _gestureEdited, _fieldEdited;
    private readonly List<(Vector2 A, Vector2 B)> _sceneGuides = [];
    private float _sceneZoom = 1;   // 1: the screen fitted to the panel with a margin
    private Vector2 _scenePan;
    private bool _scenePanning;
    // The selection: a layer of the skin's (_sceneLayer) or one of the screen's own (_layoutLayer, gameplay's), not both.
    private int _layoutLayer = -1;

    // The name a screen's own movie is placed by (null: placed by the engine, not by the skin).
    private static string? placeName(SkinScenes.Layer layer) => GameplaySceneComposition.PlaceName(layer.Movie[(layer.Movie.LastIndexOf('/') + 1)..]);

    // The selection as an index of the whole screen (the screen's own layers first, then the skin's), and back.
    private int selectedIndex(int ownCount, int skinCount) => _layoutLayer >= 0 && _layoutLayer < ownCount ? _layoutLayer
        : _sceneLayer >= 0 && _sceneLayer < skinCount ? ownCount + _sceneLayer : -1;

    private void select(int index, int ownCount) => (_layoutLayer, _sceneLayer) = index < 0 ? (-1, -1) : index < ownCount ? (index, -1) : (-1, index - ownCount);

    // A layer changed: the skin's in its list, the screen's own as a place of the skin's.
    private void commit(int index, IReadOnlyList<SkinScenes.Layer> own, SkinScenes.Layer next, bool continues)
    {
        if (index >= own.Count)
        {
            var at = index - own.Count;
            editScene(each => each[at] = next, continues);
            return;
        }
        if (placeName(own[index]) is not { } name)
            return;
        try
        {
            Scenes!.EditPlace(name, next, continues);
            _notice = null;
        }
        catch (InvalidOperationException refused)
        {
            _notice = refused.Message;
        }
    }

    private void scenePage(float top, Vector2 size)
    {
        var key = _sceneKey!;
        var layers = Scenes!.Layers(key);
        var own = Skin!.Fixed?.Invoke(key) ?? [];
        if (_sceneLayer >= layers.Count) _sceneLayer = layers.Count - 1;
        if (_layoutLayer >= own.Count) _layoutLayer = -1;
        const float side = 290;
        var height = size.Y - top;
        if (panel("##scene layers", new(0, top, side, height), ImGuiWindowFlags.NoTitleBar))
        {
            if (ImGui.Button("< Home")) _page = Page.Home;
            ImGui.SameLine();
            ImGui.TextUnformatted(key);
            if (!Scenes.Composes(key) && layers.Count > 0)
                ImGui.TextDisabled("The engine's built-in screen: an edit writes it into scenes.json.");
            foreach (var role in SkinScenes.Screens[key].Required)
                ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(layers.Any(layer => layer.Role == role) ? Good : Bad),
                    (layers.Any(layer => layer.Role == role) ? "has " : "needs ") + $"a layer with role \"{role}\"");
            ImGui.SeparatorText("Layers, back to front");
            if (own.Count > 0)
            {
                ImGui.TextDisabled("The screen's own, each placed freely (the");
                ImGui.TextDisabled("notes follow lane_hit, the hit target):");
                for (var index = 0; index < own.Count; index++)
                {
                    var layer = own[index];
                    var name = placeName(layer);
                    ImGui.PushID(1000 + index);
                    if (ImGui.Selectable($"  {layer.Movie[(layer.Movie.LastIndexOf('/') + 1)..]}{(name is not null && Scenes.Places.ContainsKey(name) ? "  · placed" : "")}",
                        _layoutLayer == index, ImGuiSelectableFlags.AllowDoubleClick))
                    {
                        select(index, own.Count);
                        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && Skin.Exists(layer.Movie)) openPackage(layer.Movie);
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{layer.Movie} ({layer.Role})\nDouble-click to edit it.");
                    ImGui.PopID();
                }
                ImGui.Spacing();
            }
            for (var index = 0; index < layers.Count; index++)
            {
                var layer = layers[index];
                ImGui.PushID(index);
                if (ImGui.Selectable($"{layer.Movie[(layer.Movie.LastIndexOf('/') + 1)..]}  ({layer.Role}){(layer.Side is { } drum ? drum == 0 ? " left" : " right" : "")}",
                    _sceneLayer == index, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    select(own.Count + index, own.Count);
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) openPackage(layer.Movie);
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{layer.Movie}\nDouble-click to edit it.");
                ImGui.PopID();
            }
            if (layers.Count == 0)
                ImGui.TextDisabled("None of the skin's: drag a component onto the stage.");
            var chosen = _sceneLayer;
            ImGui.BeginDisabled(chosen < 0);
            if (ImGui.Button("Up") && chosen > 0) { editScene(list => (list[chosen - 1], list[chosen]) = (list[chosen], list[chosen - 1])); _sceneLayer--; }
            ImGui.SameLine();
            if (ImGui.Button("Down") && chosen >= 0 && chosen < layers.Count - 1) { editScene(list => (list[chosen + 1], list[chosen]) = (list[chosen], list[chosen + 1])); _sceneLayer++; }
            ImGui.SameLine();
            if (ImGui.Button("Remove") && chosen >= 0) { editScene(list => list.RemoveAt(chosen)); _sceneLayer = -1; }
            ImGui.SameLine();
            if (ImGui.Button("Edit") && chosen >= 0) openPackage(layers[chosen].Movie);
            ImGui.EndDisabled();
            ImGui.SeparatorText("Components (drag onto the stage)");
            components(open: openPackage, columns: 2);
        }
        ImGui.End();
        if (panel("##scene layer", new(size.X - side, top, side, height), ImGuiWindowFlags.NoTitleBar))
        {
            if (_sceneLayer >= 0 && _sceneLayer < layers.Count)
                layerInspector(key, layers[_sceneLayer], own);
            else if (_layoutLayer >= 0 && _layoutLayer < own.Count)
                layerInspector(key, own[_layoutLayer], own);
            else
                ImGui.TextDisabled("Select a layer: in the list, or on the stage.");
            ImGui.Separator();
            if (ImGui.Button(Scenes.IsDirty ? "Save scenes.json *" : "Saved")) saveScenes();
            ImGui.SameLine();
            ImGui.TextDisabled("Ctrl+S");
        }
        ImGui.End();
        if (panel("##scene stage", new(side, top, size.X - side * 2, height), ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoTitleBar))
        {
            stageTools();
            scenarioPicker(key);
            sceneStage(key, [.. own, .. layers], own);
        }
        ImGui.End();
    }

    private void stageTools()
    {
        void tool(string name, SceneTool which, string keyName)
        {
            var on = _sceneTool == which;
            if (on) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
            if (ImGui.Button(name)) _sceneTool = which;
            if (on) ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{name} ({keyName})");
            ImGui.SameLine();
        }
        tool("Move", SceneTool.Move, "W");
        tool("Rotate", SceneTool.Rotate, "E");
        tool("Scale", SceneTool.Scale, "R");
        tool("Transform", SceneTool.Transform, "T: drag inside to move, a handle to scale, just outside a corner to turn");
        ImGui.SameLine(0, 24);
        ImGui.Checkbox("Snap", ref _snap);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("To the stage's edges and middle, the other layers' edges, the grid; 15° turns, 0.05 scale steps.\nHold Ctrl to turn it off for a moment.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        ImGui.Combo("##grid", ref _grid, GridNames, GridNames.Length);
        ImGui.SameLine(0, 24);
        ImGui.TextDisabled($"zoom {_sceneZoom * 100:0}%%");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Wheel zooms, middle drag moves, Home fits the screen, F frames the selection.\nArrows nudge (Shift: 10). Delete removes.");
    }

    private static readonly int[] GridSteps = [0, 1, 2, 4, 8, 16, 32, 64];
    private static readonly string[] GridNames = [.. GridSteps.Select(static step => step == 0 ? "no grid" : $"grid {step}")];

    private void layerInspector(string key, SkinScenes.Layer layer, IReadOnlyList<SkinScenes.Layer> own)
    {
        var mine = _layoutLayer < 0; // the skin's layer (else one of the screen's own)
        var index = mine ? own.Count + _sceneLayer : _layoutLayer;
        ImGui.TextUnformatted(layer.Movie);
        if (!Skin!.Exists(layer.Movie)) ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(Bad), "No package yet (shown as a label).");
        if (ImGui.Button("Edit this component")) openPackage(layer.Movie);
        ImGui.Spacing();
        if (mine)
        {
            var role = layer.Role;
            var at = _sceneLayer;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##role", $"Role: {role}"))
            {
                foreach (var choice in SkinScenes.Screens[key].Required.Append(LayerRoles.IndicatorPart).Append(Decoration).Append(role).Distinct())
                    if (ImGui.Selectable(choice, choice == role))
                        editScene(list => list[at] = list[at] with { Role = choice });
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The engine part that drives this movie. Decoration: nothing drives it, it plays on its own.");
        }
        else if (placeName(layer) is { } name)
        {
            ImGui.TextWrapped(name == "lane_hit" ? "The hit target: the notes, bar lines and rolls come to it along its turn and size. Place the lane's band (lane) and hit effects with it."
                : name == "gage_don_1p" ? "The soul gauge (every course's)." : $"Placed as \"{name}\".");
            ImGui.BeginDisabled(!Scenes!.Places.ContainsKey(name));
            if (ImGui.Button("Back to the layout's place"))
                try { Scenes.EditPlace(name, null); _notice = null; }
                catch (InvalidOperationException refused) { _notice = refused.Message; }
            ImGui.EndDisabled();
        }
        // A field's drag is one undo step: its first change starts one, the rest join it.
        void field(string label, Func<bool> draw, Func<SkinScenes.Layer, SkinScenes.Layer> change)
        {
            ImGui.TextDisabled(label);
            ImGui.SameLine(90);
            ImGui.SetNextItemWidth(-1);
            var changed = draw();
            if (ImGui.IsItemActivated()) _fieldEdited = false;
            if (changed)
            {
                commit(index, own, change(layer), _fieldEdited);
                _fieldEdited = true;
            }
        }
        var place = new Vector2(layer.X, layer.Y);
        field("Place", () => ImGui.DragFloat2("##place", ref place, 1, -4000, 4000, "%.0f"),
            each => each with { X = MathF.Round(place.X), Y = MathF.Round(place.Y) });
        var scale = new Vector2(layer.ScaleX, layer.ScaleY);
        field("Scale", () => ImGui.DragFloat2("##scale", ref scale, .005f, -20, 20, "%.3f"),
            each => each with { ScaleX = scale.X == 0 ? .001f : scale.X, ScaleY = scale.Y == 0 ? .001f : scale.Y });
        var turn = layer.Rotation;
        field("Rotation", () => ImGui.DragFloat("##rotation", ref turn, .5f, -360, 360, "%.1f°"), each => each with { Rotation = turn });
        if (mine)
        {
            var drum = layer.Side is null ? 0 : layer.Side.Value + 1;
            field("Drum", () => ImGui.Combo("##side", ref drum, "Every drum\0Left drum only\0Right drum only\0"),
                each => each with { Side = drum == 0 ? null : drum - 1 });
        }
        if (ImGui.Button("Reset scale and rotation"))
            commit(index, own, layer with { ScaleX = 1, ScaleY = 1, Rotation = 0 }, false);
    }

    // Stage space: the 1280x720 screen. A layer's box: its movie's bounds (layer = its index in the whole screen)
    // through its transform (four corners).
    private Vector2[]? layerCorners(int index, SkinScenes.Layer layer)
    {
        if (_liveScene is null || !_liveScene.Bounds(index, out var bounds)) return null;
        var matrix = layer.Transform;
        Vector2 at(float x, float y) { var (tx, ty) = matrix.Transform(x, y); return new(tx, ty); }
        return [at(bounds.X, bounds.Y), at(bounds.X + bounds.Width, bounds.Y), at(bounds.X + bounds.Width, bounds.Y + bounds.Height), at(bounds.X, bounds.Y + bounds.Height)];
    }

    private static (Vector2 Min, Vector2 Max) extent(Vector2[] corners) =>
        (Vector2.Min(Vector2.Min(corners[0], corners[1]), Vector2.Min(corners[2], corners[3])),
         Vector2.Max(Vector2.Max(corners[0], corners[1]), Vector2.Max(corners[2], corners[3])));

    private static bool inside(Vector2[] quad, Vector2 point)
    {
        var sign = 0;
        for (var index = 0; index < 4; index++)
        {
            var (a, b) = (quad[index], quad[(index + 1) % 4]);
            var cross = (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);
            if (cross == 0) continue;
            if (sign == 0) sign = Math.Sign(cross);
            else if (sign != Math.Sign(cross)) return false;
        }
        return true;
    }

    // A point in the layer's own space as its transform was at the gesture's start.
    private static Vector2 local(SkinScenes.Layer layer, Vector2 stage)
    {
        var m = layer.Transform;
        var determinant = m.M11 * m.M22 - m.M21 * m.M12;
        if (Math.Abs(determinant) < 1e-6f) return Vector2.Zero;
        var (x, y) = (stage.X - m.X, stage.Y - m.Y);
        return new((m.M22 * x - m.M21 * y) / determinant, (-m.M12 * x + m.M11 * y) / determinant);
    }

    // all: the screen's own layers, then the skin's (an index of it is the live scene's layer index).
    private void sceneStage(string key, IReadOnlyList<SkinScenes.Layer> all, IReadOnlyList<SkinScenes.Layer> own)
    {
        // The canvas: the whole panel, the screen in it fitted with a margin (so handles on its edges can be taken),
        // then zoomed about the pointer (wheel) and moved (middle drag). Home: fitted again; F: the selection framed.
        var area = ImGui.GetContentRegionAvail();
        var origin = ImGui.GetCursorScreenPos();
        var fit = Math.Min(area.X / 1280, area.Y / 720) * .85f;
        var scale = fit * _sceneZoom;
        var size = new Vector2(1280, 720) * scale;
        var corner = origin + area / 2 + _scenePan - size / 2;
        var list = ImGui.GetWindowDrawList();
        ImGui.InvisibleButton("##stage", Vector2.Max(area, Vector2.One));
        // Outlines and handles stay on the canvas, off the tool bar above it.
        list.PushClipRect(origin, origin + Vector2.Max(area, Vector2.One), true);
        var io = ImGui.GetIO();
        var mouse = ImGui.GetMousePos();
        var over = ImGui.IsItemHovered();
        var selection = selectedIndex(own.Count, all.Count - own.Count);
        if (over && io.MouseWheel != 0 && _gesture2 is null)
        {
            var held = (mouse - corner) / scale;
            _sceneZoom = Math.Clamp(_sceneZoom * MathF.Pow(1.15f, io.MouseWheel), .1f, 16);
            scale = fit * _sceneZoom;
            size = new Vector2(1280, 720) * scale;
            // The point under the pointer stays under it.
            _scenePan = mouse - held * scale + size / 2 - origin - area / 2;
            corner = origin + area / 2 + _scenePan - size / 2;
        }
        if (over && ImGui.IsMouseClicked(ImGuiMouseButton.Middle)) _scenePanning = true;
        if (_scenePanning)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Middle)) _scenePanning = false;
            else { _scenePan += io.MouseDelta; corner += io.MouseDelta; ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll); }
        }
        if (over && !io.WantTextInput && !io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Home, false))
            (_sceneZoom, _scenePan) = (1, Vector2.Zero);
        if (over && !io.WantTextInput && !io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.F, false)
            && selection >= 0 && layerCorners(selection, all[selection]) is { } framed)
        {
            var (low, high) = extent(framed);
            var extentSize = Vector2.Max(high - low, new(8));
            _sceneZoom = Math.Clamp(Math.Min(area.X * .6f / (extentSize.X * fit), area.Y * .6f / (extentSize.Y * fit)), .1f, 16);
            _scenePan = (new Vector2(640, 360) - (low + high) / 2) * fit * _sceneZoom;
            list.PopClipRect();
            return; // drawn framed from the next frame
        }
        // The whole canvas is drawn (a layer past the screen's edges shows), darkened outside the screen.
        var panelSize = Vector2.Max(area, Vector2.One);
        if (liveScene(key, ((origin.X - corner.X) / scale, (origin.Y - corner.Y) / scale, panelSize.X / scale, panelSize.Y / scale),
            panelSize * io.DisplayFramebufferScale) is { } texture)
            list.AddImage(texture, origin, origin + panelSize);
        var shade = color(0, 0, 0, .55f);
        var (left, right) = (Math.Clamp(corner.X, origin.X, origin.X + panelSize.X), Math.Clamp(corner.X + size.X, origin.X, origin.X + panelSize.X));
        var (topEdge, bottomEdge) = (Math.Clamp(corner.Y, origin.Y, origin.Y + panelSize.Y), Math.Clamp(corner.Y + size.Y, origin.Y, origin.Y + panelSize.Y));
        list.AddRectFilled(origin, new(origin.X + panelSize.X, topEdge), shade);
        list.AddRectFilled(new(origin.X, bottomEdge), origin + panelSize, shade);
        list.AddRectFilled(new(origin.X, topEdge), new(left, bottomEdge), shade);
        list.AddRectFilled(new(right, topEdge), new(origin.X + panelSize.X, bottomEdge), shade);
        list.AddRect(corner - Vector2.One, corner + size + Vector2.One, color(.55f, .6f, .66f), 0, ImDrawFlags.None, 1);
        var stagePoint = (mouse - corner) / scale;
        var snapping = _snap && !io.KeyCtrl;
        Vector2 screen(Vector2 point) => corner + point * scale;
        if (over && !io.WantTextInput && !io.KeyCtrl)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.W, false)) _sceneTool = SceneTool.Move;
            if (ImGui.IsKeyPressed(ImGuiKey.E, false)) _sceneTool = SceneTool.Rotate;
            if (ImGui.IsKeyPressed(ImGuiKey.R, false)) _sceneTool = SceneTool.Scale;
            if (ImGui.IsKeyPressed(ImGuiKey.T, false)) _sceneTool = SceneTool.Transform;
        }
        // The topmost layer under the pointer that can be placed (the engine's notes cannot).
        int hit()
        {
            for (var index = all.Count - 1; index >= 0; index--)
                if ((index >= own.Count || placeName(all[index]) is not null) && layerCorners(index, all[index]) is { } quad && inside(quad, stagePoint))
                    return index;
            return -1;
        }
        var selected = selection >= 0 ? layerCorners(selection, all[selection]) : null;
        // Scale handles: the box's corners (0-3) and the middles of its sides (4-7), on the screen.
        Vector2[] handles = selected is null ? [] :
            [.. selected, .. Enumerable.Range(0, 4).Select(index => (selected[index] + selected[(index + 1) % 4]) / 2)];
        var scaling = _sceneTool is SceneTool.Scale or SceneTool.Transform;
        int handleUnder() => !scaling ? -1 : Array.FindIndex(handles, handle => Vector2.Distance(screen(handle), mouse) <= 7);
        // Free transform: just outside a corner turns the layer.
        bool turnZone() => _sceneTool == SceneTool.Transform && selected is not null && handleUnder() < 0 && !inside(selected, stagePoint)
            && selected.Any(at => Vector2.Distance(screen(at), mouse) <= 28);
        var under = over ? hit() : -1;
        // A component dropped on the stage: a new layer of the skin's there, on top.
        if (_dragPackage is { } dropped && over && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var at = new Vector2(MathF.Round(stagePoint.X), MathF.Round(stagePoint.Y));
            editScene(each => each.Add(new SkinScenes.Layer(dropped, Decoration, at.X, at.Y, null)));
            select(own.Count + Scenes!.Layers(key).Count - 1, own.Count);
            _dragPackage = null;
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            var handle = handleUnder();
            var turn = turnZone();
            var keep = handle >= 0 || turn || (_sceneTool == SceneTool.Rotate && selected is not null && (under == selection || under < 0));
            if (!keep)
            {
                select(under, own.Count);
                selection = under;
            }
            // A handle scales about the opposite one (Alt: about the layer's origin), which stays where it is.
            var anchor = handle < 0 || io.KeyAlt || localHandles(selection) is not { } corners ? Vector2.Zero
                : corners[handle < 4 ? (handle + 2) % 4 : 4 + (handle - 2) % 4];
            _gesture2 = selection < 0 ? null : (handle >= 0 ? SceneTool.Scale : turn || _sceneTool == SceneTool.Rotate && keep ? SceneTool.Rotate : SceneTool.Move,
                stagePoint, all[selection], handle, anchor);
            _gestureEdited = false;
        }
        _sceneGuides.Clear();
        if (_gesture2 is { } gesture && selection >= 0)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _gesture2 = null;
            else if (stagePoint != gesture.From)
            {
                var next = gesture.Tool switch
                {
                    SceneTool.Rotate => rotated(gesture.Start, gesture.From, stagePoint, snapping),
                    SceneTool.Scale => scaled(gesture.Start, gesture.From, stagePoint, gesture.Handle, gesture.Anchor, io.KeyShift, snapping),
                    _ => moved(gesture.Start, stagePoint - gesture.From, all, selection, snapping, scale),
                };
                if (next != all[selection])
                {
                    commit(selection, own, next, _gestureEdited);
                    _gestureEdited = true;
                }
            }
        }
        if (over && !io.WantTextInput && selection >= 0)
        {
            var step = io.KeyShift ? 10 : 1;
            var nudge = new Vector2(ImGui.IsKeyPressed(ImGuiKey.RightArrow) ? step : ImGui.IsKeyPressed(ImGuiKey.LeftArrow) ? -step : 0,
                ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? step : ImGui.IsKeyPressed(ImGuiKey.UpArrow) ? -step : 0);
            if (nudge != Vector2.Zero)
                commit(selection, own, all[selection] with { X = all[selection].X + nudge.X, Y = all[selection].Y + nudge.Y }, false);
            if (selection >= own.Count && (ImGui.IsKeyPressed(ImGuiKey.Delete, false) || ImGui.IsKeyPressed(ImGuiKey.Backspace, false)))
            {
                var at = selection - own.Count;
                editScene(each => each.RemoveAt(at));
                select(-1, own.Count);
            }
        }
        // What the pointer is over, the selection with its tool's handles, the snapping guides.
        if (under >= 0 && under != selection && layerCorners(under, all[under]) is { } hovered)
            list.AddPolyline(ref hovered.Select(screen).ToArray()[0], 4, color(1, 1, 1, .5f), ImDrawFlags.Closed, 1);
        if (selection >= 0 && layerCorners(selection, all[selection]) is { } box)
        {
            var points = box.Select(screen).ToArray();
            list.AddPolyline(ref points[0], 4, Accent, ImDrawFlags.Closed, 2);
            list.AddText(points[0] - new Vector2(0, 17), Accent, all[selection].Movie);
            var pivot = screen(new(all[selection].X, all[selection].Y));
            list.AddCircleFilled(pivot, 4, Accent);
            if (scaling)
                foreach (var handle in handles)
                    list.AddRectFilled(screen(handle) - new Vector2(4), screen(handle) + new Vector2(4), Accent);
            if (_sceneTool == SceneTool.Rotate)
            {
                list.AddCircle(pivot, 48, Accent, 48, 1.5f);
                var (sin, cos) = MathF.SinCos(all[selection].Rotation * MathF.PI / 180);
                list.AddLine(pivot, pivot + new Vector2(cos, sin) * 48, Accent, 2);
            }
        }
        foreach (var (a, b) in _sceneGuides)
            list.AddLine(screen(a), screen(b), color(.3f, .85f, 1, .9f), 1);
        if (_dragPackage is not null && over)
            list.AddRect(corner, corner + size, Accent, 0, ImDrawFlags.None, 3);
        list.PopClipRect();
        if (handleUnder() >= 0) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        else if (turnZone()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
    }

    private SkinScenes.Layer rotated(SkinScenes.Layer start, Vector2 from, Vector2 to, bool snapping)
    {
        var pivot = new Vector2(start.X, start.Y);
        var turn = (MathF.Atan2(to.Y - pivot.Y, to.X - pivot.X) - MathF.Atan2(from.Y - pivot.Y, from.X - pivot.X)) * 180 / MathF.PI;
        var angle = start.Rotation + turn;
        angle = snapping ? MathF.Round(angle / 15) * 15 : MathF.Round(angle, 1);
        angle = ((angle + 180) % 360 + 360) % 360 - 180;
        return start with { Rotation = angle };
    }

    // The selected layer's handles in its own movie's space: corners 0-3 (top left, clockwise), side middles 4-7.
    private Vector2[]? localHandles(int index)
    {
        if (index < 0 || _liveScene is null || !_liveScene.Bounds(index, out var bounds)) return null;
        Vector2[] corners = [new(bounds.X, bounds.Y), new(bounds.X + bounds.Width, bounds.Y),
            new(bounds.X + bounds.Width, bounds.Y + bounds.Height), new(bounds.X, bounds.Y + bounds.Height)];
        return [.. corners, .. Enumerable.Range(0, 4).Select(at => (corners[at] + corners[(at + 1) % 4]) / 2)];
    }

    // Scaled about the anchor (in the layer's own space), along the handle's axes (a side: one axis); Shift keeps the
    // proportions. The layer moves so the anchor stays where it was on the stage.
    internal static SkinScenes.Layer scaled(SkinScenes.Layer start, Vector2 from, Vector2 to, int handle, Vector2 anchor, bool proportional, bool snapping)
    {
        var (a, b) = (local(start, from) - anchor, local(start, to) - anchor);
        var useX = handle is < 4 or 5 or 7;
        var useY = handle is < 4 or 4 or 6;
        float ratio(float was, float now) => Math.Abs(was) < 1 ? 1 : now / was;
        var (rx, ry) = (useX ? ratio(a.X, b.X) : 1, useY ? ratio(a.Y, b.Y) : 1);
        if (proportional)
        {
            var both = a.LengthSquared() < 1 ? 1 : Vector2.Dot(a, b) / a.LengthSquared();
            (rx, ry) = (both, both);
        }
        float step(float value) => value == 0 ? .001f : snapping ? MathF.Round(value / .05f) * .05f is var rounded && rounded != 0 ? rounded : .05f : MathF.Round(value, 3);
        var next = start with { ScaleX = step(start.ScaleX * rx), ScaleY = step(start.ScaleY * ry) };
        var (wasX, wasY) = start.Transform.Transform(anchor.X, anchor.Y);
        var (nowX, nowY) = (next with { X = 0, Y = 0 }).Transform.Transform(anchor.X, anchor.Y);
        return next with { X = MathF.Round(wasX - nowX, 2), Y = MathF.Round(wasY - nowY, 2) };
    }

    // Moved by the pointer; snapping puts the layer's box edges or middle on the stage's, another layer's, or the grid.
    private SkinScenes.Layer moved(SkinScenes.Layer start, Vector2 delta, IReadOnlyList<SkinScenes.Layer> layers, int index, bool snapping, float scale)
    {
        var place = new Vector2(start.X, start.Y) + delta;
        var moved = start with { X = MathF.Round(place.X), Y = MathF.Round(place.Y) };
        if (!snapping)
            return moved;
        var corners = layerCorners(index, layers[index]);
        if (corners is null)
            return grid(moved);
        // The box where the move puts it.
        var shift = new Vector2(moved.X - layers[index].X, moved.Y - layers[index].Y);
        var (min, max) = extent(corners);
        (min, max) = (min + shift, max + shift);
        var xs = new List<float> { 0, 640, 1280 };
        var ys = new List<float> { 0, 360, 720 };
        for (var other = 0; other < layers.Count; other++)
            if (other != index && layerCorners(other, layers[other]) is { } box)
            {
                var (otherMin, otherMax) = extent(box);
                xs.AddRange([otherMin.X, (otherMin.X + otherMax.X) / 2, otherMax.X]);
                ys.AddRange([otherMin.Y, (otherMin.Y + otherMax.Y) / 2, otherMax.Y]);
            }
        var reach = 8 / scale;
        float? best(float[] mine, List<float> targets, out float target)
        {
            (float Distance, float Offset, float Target)? found = null;
            foreach (var at in mine)
                foreach (var candidate in targets)
                    if (Math.Abs(candidate - at) <= reach && (found is null || Math.Abs(candidate - at) < found.Value.Distance))
                        found = (Math.Abs(candidate - at), candidate - at, candidate);
            target = found?.Target ?? 0;
            return found?.Offset;
        }
        var snapX = best([min.X, (min.X + max.X) / 2, max.X], xs, out var lineX);
        var snapY = best([min.Y, (min.Y + max.Y) / 2, max.Y], ys, out var lineY);
        var gridded = grid(moved);
        var result = moved with
        {
            X = snapX is { } dx ? MathF.Round(moved.X + dx) : gridded.X,
            Y = snapY is { } dy ? MathF.Round(moved.Y + dy) : gridded.Y,
        };
        if (snapX is not null) _sceneGuides.Add((new(lineX, 0), new(lineX, 720)));
        if (snapY is not null) _sceneGuides.Add((new(0, lineY), new(1280, lineY)));
        return result;
    }

    private SkinScenes.Layer grid(SkinScenes.Layer layer)
    {
        var step = GridSteps[Math.Clamp(_grid, 0, GridSteps.Length - 1)];
        return step <= 1 ? layer : layer with { X = MathF.Round(layer.X / step) * step, Y = MathF.Round(layer.Y / step) * step };
    }
}
