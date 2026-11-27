using System.Numerics;
using ImGuiNET;
using Waddamburo.Game.Scenes;

namespace Waddamburo.Editor;

/// <summary>
/// The scene view: a screen's layers listed, the screen playing in the middle, the active layer's place, scale, turn,
/// role and drum on the right. Gameplay's own movies (the lane, hit target, gauge...) are listed first and placed the
/// same way, written as the skin's places (scenes.json gameplay places). The stage works as the component editor's:
/// the same tools (Q free transform, W move, E rotate, R scale: <see cref="StageHandles"/>), a selection of several
/// layers (Ctrl+click adds or drops one, Shift+drag draws a box, a click again on the same spot reaches the layer
/// under), moved, turned and scaled together (<see cref="MovieTransform"/>), snapping to the stage, the other layers
/// and a grid (Ctrl held: free). One gesture is one undo step.
/// </summary>
public sealed partial class MovieInspectorSession
{
    private bool _snap = true;
    private int _grid = 4; // an index of GridSteps: 8 px
    private bool _fieldEdited;
    private (float? X, float? Y) _sceneGuides;
    private float _sceneZoom = 1;   // 1: the screen fitted to the panel with a margin
    private Vector2 _scenePan;
    private bool _scenePanning;
    // The selection, by index of the whole screen (the screen's own layers first, then the skin's); the active one is
    // _layoutLayer (one of the screen's own) or _sceneLayer (one of the skin's), whose fields the inspector shows.
    private readonly HashSet<int> _sceneSelection = [];
    private int _layoutLayer = -1;
    private Vector2? _sceneBox;
    private Vector2? _scenePickPoint;
    private int[] _scenePickCycle = [];
    private int _scenePickIndex;

    // A drag on the stage: what it does (0 move, 1 rotate, 2 scale), the handle taken, the selection's box and the
    // layers as they were when it started.
    private sealed record SceneMove(int Op, int Handle, Vector2 Start, Vector2 Pivot, Vector2 Min, Vector2 Max,
        (int Index, SkinScenes.Layer Layer)[] Members)
    {
        public bool Moved { get; init; }
        public bool Edited { get; init; }
    }

    private SceneMove? _sceneMove;

    // The name a screen's own movie is placed by (null: placed by the engine, not by the skin).
    // A gameplay movie's place name; none for the second player's lane (two players): it keeps the layout's places.
    private static string? placeName(SkinScenes.Layer layer) => layer.Role == GameplaySceneComposition.PlayerTwoHostId ? null
        : GameplaySceneComposition.PlaceName(layer.Movie[(layer.Movie.LastIndexOf('/') + 1)..]);

    // The active layer as an index of the whole screen.
    private int selectedIndex(int ownCount, int skinCount) => _layoutLayer >= 0 && _layoutLayer < ownCount ? _layoutLayer
        : _sceneLayer >= 0 && _sceneLayer < skinCount ? ownCount + _sceneLayer : -1;

    private void activate(int index, int ownCount) =>
        (_layoutLayer, _sceneLayer) = index < 0 ? (-1, -1) : index < ownCount ? (index, -1) : (-1, index - ownCount);

    // Only this layer (or none).
    private void select(int index, int ownCount)
    {
        _sceneSelection.Clear();
        if (index >= 0) _sceneSelection.Add(index);
        activate(index, ownCount);
    }

    // Adds a layer to the selection, or drops it (the active one moves to the last left).
    private void toggleScene(int index, int ownCount)
    {
        if (index < 0) return;
        if (_sceneSelection.Add(index)) activate(index, ownCount);
        else
        {
            _sceneSelection.Remove(index);
            if (selectedIndex(ownCount, int.MaxValue) == index) activate(_sceneSelection.Count == 0 ? -1 : _sceneSelection.Last(), ownCount);
        }
    }

    // Layers changed as one step: the skin's in its list, the screen's own as places of the skin's.
    private void commit(string key, IReadOnlyList<SkinScenes.Layer> own, IEnumerable<(int Index, SkinScenes.Layer Layer)> changes, bool continues)
    {
        var mine = new List<(int At, SkinScenes.Layer Layer)>();
        var places = new Dictionary<string, SkinScenes.Layer?>(StringComparer.Ordinal);
        foreach (var (index, layer) in changes)
        {
            if (index >= own.Count) mine.Add((index - own.Count, layer));
            else if (placeName(own[index]) is { } name) places[name] = layer;
        }
        try
        {
            Scenes!.EditMany(key, mine.Count == 0 ? null : list => { foreach (var (at, layer) in mine) list[at] = layer; }, places, continues);
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
        var all = (IReadOnlyList<SkinScenes.Layer>)[.. own, .. layers];
        _sceneSelection.RemoveWhere(index => index >= all.Count);
        if (_sceneLayer >= layers.Count) _sceneLayer = -1;
        if (_layoutLayer >= own.Count) _layoutLayer = -1;
        const float side = 290;
        var height = size.Y - top;
        // A click in the list: alone, Ctrl adds or drops it, Shift takes the run from the active one.
        void listClick(int index, string movie)
        {
            var io = ImGui.GetIO();
            var from = selectedIndex(own.Count, layers.Count);
            if (io.KeyShift && from >= 0)
            {
                if (!io.KeyCtrl) _sceneSelection.Clear();
                for (var each = Math.Min(from, index); each <= Math.Max(from, index); each++) _sceneSelection.Add(each);
                activate(index, own.Count);
            }
            else if (io.KeyCtrl) toggleScene(index, own.Count);
            else select(index, own.Count);
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && Skin.Exists(movie)) openPackage(movie);
        }
        if (panel("##scene layers", new(0, top, side, height), ImGuiWindowFlags.NoTitleBar))
        {
            if (ImGui.Button("< Home")) _page = Page.Home;
            ImGui.SameLine();
            ImGui.TextUnformatted(key);
            if (Skin.Live is { Connected: true } live)
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(live.Screen == key);
                if (ImGui.SmallButton(live.Screen == key ? "in game" : "Show in game")) live.Show(key);
                ImGui.EndDisabled();
            }
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
                        _sceneSelection.Contains(index), ImGuiSelectableFlags.AllowDoubleClick))
                        listClick(index, layer.Movie);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{layer.Movie} ({layer.Role})\nDouble-click to edit it. Ctrl: add to the selection, Shift: a run.");
                    ImGui.PopID();
                }
                ImGui.Spacing();
            }
            for (var index = 0; index < layers.Count; index++)
            {
                var layer = layers[index];
                ImGui.PushID(index);
                var unseen = !Skin!.Variant.Shows(layer.Side);
                if (unseen) ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                var picked = ImGui.Selectable($"{layer.Movie[(layer.Movie.LastIndexOf('/') + 1)..]}  ({layer.Role}){(layer.Side is { } drum ? drum == 0 ? " left" : " right" : "")}{(unseen ? "  · not this credit's" : "")}",
                    _sceneSelection.Contains(own.Count + index), ImGuiSelectableFlags.AllowDoubleClick);
                if (unseen) ImGui.PopStyleColor();
                if (picked)
                    listClick(own.Count + index, layer.Movie);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{layer.Movie}\nDouble-click to edit it. Ctrl: add to the selection, Shift: a run.");
                ImGui.PopID();
            }
            if (layers.Count == 0)
                ImGui.TextDisabled("None of the skin's: drag a component onto the stage.");
            var chosen = _sceneLayer;
            ImGui.BeginDisabled(chosen < 0);
            if (ImGui.Button("Up") && chosen > 0) { editScene(list => (list[chosen - 1], list[chosen]) = (list[chosen], list[chosen - 1])); select(own.Count + chosen - 1, own.Count); }
            ImGui.SameLine();
            if (ImGui.Button("Down") && chosen >= 0 && chosen < layers.Count - 1) { editScene(list => (list[chosen + 1], list[chosen]) = (list[chosen], list[chosen + 1])); select(own.Count + chosen + 1, own.Count); }
            ImGui.SameLine();
            if (ImGui.Button("Remove") && chosen >= 0) removeSelected(own.Count);
            ImGui.SameLine();
            if (ImGui.Button("Edit") && chosen >= 0) openPackage(layers[chosen].Movie);
            ImGui.EndDisabled();
            ImGui.SeparatorText("Components (drag onto the stage)");
            components(open: openPackage, columns: 2);
        }
        ImGui.End();
        if (panel("##scene layer", new(size.X - side, top, side, height), ImGuiWindowFlags.NoTitleBar))
        {
            if (_sceneSelection.Count > 1)
                ImGui.TextDisabled($"{_sceneSelection.Count} selected: the fields edit the active one.");
            var active = selectedIndex(own.Count, layers.Count);
            if (active >= 0)
                layerInspector(key, all[active], own, active);
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
            sceneTools();
            scenarioPicker(key);
            ImGui.SameLine(0, 16);
            variantPicker(key);
            sceneStage(key, all, own);
        }
        ImGui.End();
    }

    private float _sceneStageWidth = Lumen.Rendering.LumenStage.BaseWidth;

    /// <summary>The stage width the scene view shows (LumenStage: 960 at 4:3 to 1680 at 21:9).</summary>
    public float SceneStageWidth { get => _sceneStageWidth; set => _sceneStageWidth = Math.Clamp(value, Lumen.Rendering.LumenStage.MinWidth, 2560); }

    // Who plays (and, in gameplay, a dan course's lane): the screen as that credit sees it.
    private void variantPicker(string key)
    {
        var variant = Skin!.Variant;
        string[] names = ["Left drum", "Right drum", "Both drums"];
        ImGui.SetNextItemWidth(120);
        if (ImGui.BeginCombo("##players", names[(int)variant.Players]))
        {
            for (var index = 0; index < names.Length; index++)
                if (ImGui.Selectable(names[index], index == (int)variant.Players))
                    Skin.Variant = variant with { Players = (SkinStage.Players)index };
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Who has joined: the screen as that credit sees it.\nA screen built per drum (Song Select's name boards, the dan list) shows each joined drum's parts;\ngameplay: one lane (the left or the right drum's) or two.");
        // The screen's shape: everything placed on 1280, anchored layers moved to their edges.
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130);
        var widths = new (string Name, float Width)[] { ("16:9", 1280), ("4:3", 960), ("16:10", 1152), ("Phone 19.5:9", 1560), ("21:9", 1680) };
        var shown = widths.FirstOrDefault(each => each.Width == _sceneStageWidth).Name ?? $"{_sceneStageWidth:0}";
        if (ImGui.BeginCombo("##stage width", shown))
        {
            foreach (var (name, width) in widths)
                if (ImGui.Selectable($"{name}  ({width:0} wide)", width == _sceneStageWidth))
                    _sceneStageWidth = width;
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The screen's shape: the stage is always 720 tall, as wide as the screen asks (4:3 to 21:9).\nLayers hold their anchor's edge (the Anchor field); the faint box is the 16:9 one.");
        if (key == "gameplay")
        {
            ImGui.SameLine();
            var dojo = variant.Dojo;
            if (ImGui.Checkbox("Dan course", ref dojo))
                Skin.Variant = variant with { Dojo = dojo };
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("The lane as a dan course shows it (its gauge and exam panels).");
        }
    }

    // The skin's selected layers removed (the screen's own stay: they are the engine's).
    private void removeSelected(int ownCount)
    {
        var gone = _sceneSelection.Where(index => index >= ownCount).Select(index => index - ownCount).OrderDescending().ToArray();
        if (gone.Length == 0) return;
        editScene(list => { foreach (var at in gone) list.RemoveAt(at); });
        select(-1, ownCount);
    }

    private void sceneTools()
    {
        transformToolButtons(_sceneMove is not null);
        ImGui.SameLine(0, 24);
        ImGui.Checkbox("Snap", ref _snap);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("To the stage's edges and middle, the other layers' edges, else the grid.\nHold Ctrl to turn it off for a moment.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        ImGui.Combo("##grid", ref _grid, GridNames, GridNames.Length);
        ImGui.SameLine(0, 24);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled($"zoom {_sceneZoom * 100:0}%%");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Wheel zooms, middle drag moves, Home fits the screen, F frames the selection.\nCtrl+click adds to the selection, Shift+drag draws a box, a click again reaches the layer under.\nArrows nudge (Shift: 10). Delete removes.");
    }

    private static readonly int[] GridSteps = [0, 1, 2, 4, 8, 16, 32, 64];
    private static readonly string[] GridNames = [.. GridSteps.Select(static step => step == 0 ? "no grid" : $"grid {step}")];

    private void layerInspector(string key, SkinScenes.Layer layer, IReadOnlyList<SkinScenes.Layer> own, int index)
    {
        var mine = index >= own.Count; // the skin's layer (else one of the screen's own)
        ImGui.TextUnformatted(layer.Movie);
        if (!Skin!.Exists(layer.Movie)) ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(Bad), "No package yet (shown as a label).");
        if (ImGui.Button("Edit this component")) openPackage(layer.Movie);
        ImGui.Spacing();
        if (mine)
        {
            var role = layer.Role;
            var at = index - own.Count;
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
                commit(key, own, [(index, change(layer))], _fieldEdited);
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
        var anchor = (int)layer.Anchor;
        field("Anchor", () => ImGui.Combo("##anchor", ref anchor, "Centre (with the stage)\0Left edge\0Right edge\0Stretch across\0Cover (fill, crop)\0Stretch to the right edge\0Stretch to the left edge\0Left when narrower, else centred\0"),
            each => each with { Anchor = (Lumen.Rendering.LumenAnchor)anchor });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Where it holds when the screen is narrower or wider than 16:9 (4:3, 21:9, a phone).\nTry it with the stage width above the stage.");
        if (ImGui.Button("Reset scale and rotation"))
            commit(key, own, [(index, layer with { ScaleX = 1, ScaleY = 1, Rotation = 0 })], false);
    }

    // Stage space: the 1280x720 screen. A layer's box: its movie's bounds (layer = its index in the whole screen)
    // through its transform (four corners).
    private Vector2[]? layerCorners(int index, SkinScenes.Layer layer)
    {
        if (_liveScene is null || !_liveScene.Bounds(index, out var bounds)) return null;
        var matrix = Lumen.Rendering.LumenStage.Anchored(layer.Transform, layer.Anchor); // where it is at this stage width
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

    /// <summary>A layer under a stage-space change (a group drag): its place, turn and scale read back; null when that would need skew.</summary>
    internal static SkinScenes.Layer? Transformed(SkinScenes.Layer layer, Matrix3x2 change, int op)
    {
        var local = Matrix3x2.CreateScale(layer.ScaleX, layer.ScaleY) * Matrix3x2.CreateRotation(layer.Rotation * MathF.PI / 180)
            * Matrix3x2.CreateTranslation(layer.X, layer.Y);
        if (!MovieTransform.TryApply(local, Matrix3x2.Identity, change, out var position, out var angle, out var scale))
            return null;
        return op == 0
            ? layer with { X = MathF.Round(position.X, 2), Y = MathF.Round(position.Y, 2) }
            : layer with
            {
                X = MathF.Round(position.X, 2), Y = MathF.Round(position.Y, 2), Rotation = MathF.Round(angle, 2),
                ScaleX = MathF.Round(scale.X, 4), ScaleY = MathF.Round(scale.Y, 4),
            };
    }

    // all: the screen's own layers, then the skin's (an index of it is the live scene's layer index).
    private void sceneStage(string key, IReadOnlyList<SkinScenes.Layer> all, IReadOnlyList<SkinScenes.Layer> own)
    {
        // The canvas: the whole panel, the screen in it fitted with a margin (so handles on its edges can be taken),
        // then zoomed about the pointer (wheel) and moved (middle drag). Home: fitted again; F: the selection framed.
        var area = ImGui.GetContentRegionAvail();
        var origin = ImGui.GetCursorScreenPos();
        // The screen at the stage width shown (LumenStage): the 1280 box everything is placed on, centred in it.
        var stageWidth = Lumen.Rendering.LumenStage.Width;
        var fit = Math.Min(area.X / stageWidth, area.Y / 720) * .85f;
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
        // The selection's box (what the handles hold): every selected layer's box together.
        (Vector2 Min, Vector2 Max)? selectionBox()
        {
            (Vector2 Min, Vector2 Max)? box = null;
            foreach (var index in _sceneSelection)
                if (layerCorners(index, all[index]) is { } quad)
                {
                    var (low, high) = extent(quad);
                    box = box is { } was ? (Vector2.Min(was.Min, low), Vector2.Max(was.Max, high)) : (low, high);
                }
            return box;
        }
        if (over && io.MouseWheel != 0 && _sceneMove is null)
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
        if (over && !io.WantTextInput && !io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.F, false) && selectionBox() is var (low, high))
        {
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
        var screenLeft = corner.X - Lumen.Rendering.LumenStage.Extra * scale;
        var (left, right) = (Math.Clamp(screenLeft, origin.X, origin.X + panelSize.X), Math.Clamp(screenLeft + stageWidth * scale, origin.X, origin.X + panelSize.X));
        var (topEdge, bottomEdge) = (Math.Clamp(corner.Y, origin.Y, origin.Y + panelSize.Y), Math.Clamp(corner.Y + size.Y, origin.Y, origin.Y + panelSize.Y));
        list.AddRectFilled(origin, new(origin.X + panelSize.X, topEdge), shade);
        list.AddRectFilled(new(origin.X, bottomEdge), origin + panelSize, shade);
        list.AddRectFilled(new(origin.X, topEdge), new(left, bottomEdge), shade);
        list.AddRectFilled(new(right, topEdge), new(origin.X + panelSize.X, bottomEdge), shade);
        list.AddRect(new Vector2(screenLeft, corner.Y) - Vector2.One, new Vector2(screenLeft + stageWidth * scale, corner.Y + size.Y) + Vector2.One,
            color(.55f, .6f, .66f), 0, ImDrawFlags.None, 1);
        // Another width: the 16:9 box layers are placed on, faint.
        if (stageWidth != Lumen.Rendering.LumenStage.BaseWidth)
            list.AddRect(corner, corner + size, color(.55f, .6f, .66f, .35f), 0, ImDrawFlags.None, 1);
        var point = (mouse - corner) / scale;
        Vector2 screen(Vector2 at) => corner + at * scale;
        if (over && _sceneMove is null) transformToolKeys();
        // The layers under a point that can be placed (the engine's notes cannot), top first.
        int[] hits(Vector2 at) => [.. Enumerable.Range(0, all.Count).Reverse()
            .Where(index => (index >= own.Count || placeName(all[index]) is not null) && layerCorners(index, all[index]) is { } quad && inside(quad, at))];
        // A click picks the top layer there; another click on the same spot the one under it, and so on.
        void pick(Vector2 at)
        {
            var found = hits(at);
            if (_scenePickPoint is { } last && Vector2.Distance(last, at) * scale < 6 && found.SequenceEqual(_scenePickCycle))
                _scenePickIndex = (_scenePickIndex + 1) % Math.Max(1, found.Length);
            else
                (_scenePickCycle, _scenePickIndex) = (found, 0);
            _scenePickPoint = at;
            select(found.Length == 0 ? -1 : found[_scenePickIndex], own.Count);
        }
        var box = selectionBox();
        var handle = box is var (boxMin, boxMax) ? StageHandles.Draw(list, screen, _stageTool, boxMin, boxMax, new(.5f), mouse, Text, Outline) : -1;
        // A component dropped on the stage: a new layer of the skin's there, on top.
        if (_dragPackage is { } dropped && over && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var at = new Vector2(MathF.Round(point.X), MathF.Round(point.Y));
            editScene(each => each.Add(new SkinScenes.Layer(dropped, Decoration, at.X, at.Y, null)));
            select(own.Count + Scenes!.Layers(key).Count - 1, own.Count);
            _dragPackage = null;
        }
        if (over && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && _dragPackage is null)
        {
            var onSelected = box is var (min, max) && point.X >= min.X && point.Y >= min.Y && point.X <= max.X && point.Y <= max.Y;
            if (io.KeyShift && handle < 0)
                _sceneBox = point;
            else if (io.KeyCtrl && handle < 0)
                toggleScene(hits(point).FirstOrDefault(-1), own.Count);
            else if ((handle >= 0 || onSelected && _stageTool is 0 or 3) && box is var (from, to))
            {
                _lockedAxis = 0;
                _sceneMove = new(StageHandles.Operation(_stageTool, handle), handle, point, (from + to) / 2, from, to,
                    [.. _sceneSelection.Order().Select(index => (index, all[index]))]);
            }
            else
                pick(point);
        }
        // The box drawn with Shift: what it touches joins the selection (a click without dragging: the layer there).
        if (_sceneBox is { } start)
        {
            var (low2, high2) = (Vector2.Min(start, point), Vector2.Max(start, point));
            list.AddRectFilled(screen(low2), screen(high2), color(.3f, .6f, 1, .15f));
            list.AddRect(screen(low2), screen(high2), Outline);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (Vector2.Distance(start, point) * scale < 4)
                    toggleScene(hits(point).FirstOrDefault(-1), own.Count);
                else
                    for (var index = 0; index < all.Count; index++)
                        if ((index >= own.Count || placeName(all[index]) is not null) && layerCorners(index, all[index]) is { } quad
                            && extent(quad) is var (qMin, qMax) && qMin.X <= high2.X && qMin.Y <= high2.Y && qMax.X >= low2.X && qMax.Y >= low2.Y)
                        {
                            _sceneSelection.Add(index);
                            activate(index, own.Count);
                        }
                _sceneBox = null;
            }
        }
        if (_sceneMove is { } move)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (!move.Moved && move.Handle < 0)
                    pick(move.Start); // a plain click on the selection reaches the layer there (or under it)
                _sceneMove = null;
                _sceneGuides = (null, null);
            }
            else
            {
                var delta = point - move.Start;
                if (move.Op == 0)
                {
                    delta = MovieTransform.ConstrainMove(delta, move.Handle, io.KeyShift, ref _lockedAxis);
                    _sceneGuides = (null, null);
                    if (_snap && !io.KeyCtrl)
                        delta = sceneSnap(move, delta, all, 6 / scale);
                }
                if (move.Moved || delta.Length() * scale > 3)
                {
                    var anchor = move.Op == 2 ? MovieTransform.ScaleAnchor(move.Handle, move.Min, move.Max, move.Pivot, io.KeyAlt) : move.Pivot;
                    var change = MovieTransform.Change(move.Op, move.Handle, move.Start, point, delta, move.Pivot, anchor, io.KeyShift);
                    var changed = move.Members.Select(member => (member.Index, Layer: Transformed(member.Layer, change, move.Op))).ToArray();
                    if (changed.Any(static member => member.Layer is null))
                        _notice = "This transform needs skew: scale turned layers with Shift (proportions kept), or one at a time.";
                    else
                    {
                        commit(key, own, changed.Select(static member => (member.Index, member.Layer!)), move.Edited);
                        _sceneMove = move with { Moved = true, Edited = true };
                    }
                }
            }
        }
        if (over && !io.WantTextInput && _sceneSelection.Count > 0)
        {
            var step = io.KeyShift ? 10 : 1;
            var nudge = new Vector2(ImGui.IsKeyPressed(ImGuiKey.RightArrow) ? step : ImGui.IsKeyPressed(ImGuiKey.LeftArrow) ? -step : 0,
                ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? step : ImGui.IsKeyPressed(ImGuiKey.UpArrow) ? -step : 0);
            if (nudge != Vector2.Zero)
                commit(key, own, _sceneSelection.Select(index => (index, all[index] with { X = all[index].X + nudge.X, Y = all[index].Y + nudge.Y })), false);
            if (ImGui.IsKeyPressed(ImGuiKey.Delete, false) || ImGui.IsKeyPressed(ImGuiKey.Backspace, false))
                removeSelected(own.Count);
        }
        // What the pointer is over, each selected layer's own box, the selection's box, the snapping guides.
        if (over && _sceneMove is null && _sceneBox is null && handle < 0 && hits(point) is [var under, ..] && !_sceneSelection.Contains(under)
            && layerCorners(under, all[under]) is { } hovered)
            list.AddPolyline(ref hovered.Select(screen).ToArray()[0], 4, color(1, 1, 1, .5f), ImDrawFlags.Closed, 1);
        foreach (var index in _sceneSelection)
            if (layerCorners(index, all[index]) is { } quad)
            {
                var points = quad.Select(screen).ToArray();
                list.AddPolyline(ref points[0], 4, Accent, ImDrawFlags.Closed, 1.5f);
                if (_sceneSelection.Count == 1) list.AddText(points[0] - new Vector2(0, 17), Accent, all[index].Movie);
            }
        if (_sceneSelection.Count > 1 && selectionBox() is var (selMin, selMax))
            list.AddRect(screen(selMin), screen(selMax), Outline, 0, ImDrawFlags.None, 2);
        if (_sceneGuides.X is { } guideX)
            list.AddLine(new(screen(new(guideX, 0)).X, origin.Y), new(screen(new(guideX, 0)).X, origin.Y + panelSize.Y), color(1, .2f, .75f));
        if (_sceneGuides.Y is { } guideY)
            list.AddLine(new(origin.X, screen(new(0, guideY)).Y), new(origin.X + panelSize.X, screen(new(0, guideY)).Y), color(1, .2f, .75f));
        if (_dragPackage is not null && over)
            list.AddRect(corner, corner + size, Accent, 0, ImDrawFlags.None, 3);
        list.PopClipRect();
    }

    // A move snaps the selection's edges and middle to the stage's and to the other layers' (as the component editor's
    // does), within a few pixels on screen; an axis that finds nothing goes to the grid.
    private Vector2 sceneSnap(SceneMove move, Vector2 delta, IReadOnlyList<SkinScenes.Layer> all, float threshold)
    {
        var (min, max) = (move.Min + delta, move.Max + delta);
        List<float> xs = [0, 640, 1280, -Lumen.Rendering.LumenStage.Extra, 1280 + Lumen.Rendering.LumenStage.Extra], ys = [0, 360, 720];
        for (var index = 0; index < all.Count; index++)
            if (!_sceneSelection.Contains(index) && layerCorners(index, all[index]) is { } quad)
            {
                var (low, high) = extent(quad);
                xs.AddRange([low.X, (low.X + high.X) / 2, high.X]);
                ys.AddRange([low.Y, (low.Y + high.Y) / 2, high.Y]);
            }
        var (shiftX, guideX) = delta.X == 0 ? (0, null) : EffectGizmo.Snap([min.X, (min.X + max.X) / 2, max.X], xs, threshold);
        var (shiftY, guideY) = delta.Y == 0 ? (0, null) : EffectGizmo.Snap([min.Y, (min.Y + max.Y) / 2, max.Y], ys, threshold);
        _sceneGuides = (guideX, guideY);
        var step = GridSteps[Math.Clamp(_grid, 0, GridSteps.Length - 1)];
        float gridded(float edge, float moved) => step <= 1 || moved == 0 ? 0 : MathF.Round(edge / step) * step - edge;
        return delta + new Vector2(guideX is null ? gridded(min.X, delta.X) : shiftX, guideY is null ? gridded(min.Y, delta.Y) : shiftY);
    }
}
