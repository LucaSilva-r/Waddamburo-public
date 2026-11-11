using System.Numerics;
using ImGuiNET;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private Vector2 _handlePivot = new(.5f);
    private float _zoom = 1;       // 1: the stage fitted to the panel
    private Vector2 _pan;          // the stage's centre, in pixels from the panel's
    private bool _panning;

    // Where the stage is on screen: fitted to the panel, then zoomed about its centre and moved.
    private EffectViewport stageView(EditorPanel area)
    {
        // Fitted beside the rulers, not under them.
        area = new(area.X + RulerSize, area.Y + RulerSize, Math.Max(1, area.Width - RulerSize), Math.Max(1, area.Height - RulerSize));
        var fit = EffectViewport.Fit(area).Rectangle;
        var size = fit.Size * _zoom;
        var centre = area.Position + area.Size / 2 + _pan;
        return new(new(centre.X - size.X / 2, centre.Y - size.Y / 2, size.X, size.Y));
    }

    private void zoomStage(EditorPanel area, Vector2 about, float factor)
    {
        var held = stageView(area).ToStage(about);
        _zoom = Math.Clamp(_zoom * factor, .1f, 32);
        _pan += about - stageView(area).ToScreen(held); // the point under the pointer stays under it
    }

    // The given stage rectangle fills the panel (with a margin).
    private void frameStage(EditorPanel area, float x, float y, float width, float height)
    {
        var fit = EffectViewport.Fit(area).Rectangle.Width / 1280;
        var scale = Math.Min(area.Width / Math.Max(1, width * 1.25f), area.Height / Math.Max(1, height * 1.25f));
        _zoom = Math.Clamp(scale / fit, .1f, 32);
        scale = fit * _zoom;
        _pan = new Vector2(640, 360) * scale - new Vector2(x + width / 2, y + height / 2) * scale;
    }

    private static bool inArea(EditorPanel area, Vector2 point) =>
        point.X >= area.X && point.Y >= area.Y && point.X <= area.X + area.Width && point.Y <= area.Y + area.Height;

    private void stageNavigation(EditorPanel area, LumenPlayer player, Vector2 mouse, bool overTools)
    {
        var io = ImGui.GetIO();
        var inside = mouse.X >= area.X && mouse.Y >= area.Y && mouse.X <= area.X + area.Width && mouse.Y <= area.Y + area.Height;
        var hovered = inside && !overTools && ImGui.IsWindowHovered();
        if (hovered && io.MouseWheel != 0 && _move is null)
            zoomStage(area, mouse, MathF.Pow(1.15f, io.MouseWheel));
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
            _panning = true;
        if (_panning)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Middle))
                _panning = false;
            else
            {
                _pan += io.MouseDelta;
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
            }
        }
        if (hovered && !io.WantTextInput && _move is null)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Home, false))
                (_zoom, _pan) = (1, Vector2.Zero);
            if (ImGui.IsKeyPressed(ImGuiKey.F, false) && selectionBounds(player, out var chosen))
                frameStage(area, chosen.X, chosen.Y, chosen.Width, chosen.Height);
        }
        // Asked for from a menu elsewhere (the Clips list).
        if (_frameRequest is { } wanted)
        {
            if (player.TryInspectBounds(wanted, out var box))
                frameStage(area, box.X, box.Y, box.Width, box.Height);
            _frameRequest = null;
        }
    }

    private string? _frameRequest;

    // An asset let go over the stage: a new layer of the open timeline, on its current frame, where the pointer is.
    // A texture is placed through its shape (made when there is none yet).
    private void placeAsset(LumenPlayer player, IReadOnlyList<LumenInstanceInfo> clips, string assetId, Vector2 point)
    {
        if (_document is not { ReadOnly: false } document || document.Assets().FirstOrDefault(each => each.Id == assetId) is not { } asset)
            return;
        if (timelineOf(clips) is not var (clip, name, sprite))
        {
            _notice = "Open a timeline to place this into.";
            return;
        }
        // Into the timeline's own coordinates when it is not the root's.
        if (clip.Path.Length > 0 && player.TryInspectParentTransform(clip.Path, out var parent))
        {
            var world = clip.Transform.Then(parent);
            if (Matrix3x2.Invert(new(world.M11, world.M12, world.M21, world.M22, world.X, world.Y), out var inverse))
                point = Vector2.Transform(point, inverse);
        }
        attempt(() =>
        {
            var character = asset.Name;
            if (asset.Group == "textures")
                character = (document.Root["shapes"] as System.Text.Json.Nodes.JsonObject)?.FirstOrDefault(shape => (string?)shape.Value?["texture"] == asset.Name).Key
                    ?? document.AddShape(asset.Name, asset.Name, TextureOf?.Invoke(asset.Name)?.Width ?? 128, TextureOf?.Invoke(asset.Name)?.Height ?? 128, folder: asset.Folder);
            if (character == name)
                throw new InvalidOperationException("A sprite cannot be placed inside itself.");
            var frame = clip.Frame;
            document.Edit(_ =>
            {
                if (sprite["layers"] is not System.Text.Json.Nodes.JsonArray list)
                    sprite["layers"] = list = [];
                var depth = list.OfType<System.Text.Json.Nodes.JsonObject>().Select((layer, index) => (int?)layer["depth"] ?? index + 1).DefaultIfEmpty(0).Max() + 1;
                list.Add((System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
                {
                    ["character"] = character, ["name"] = character, ["depth"] = depth,
                    ["keys"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                        { ["frame"] = frame, ["x"] = MathF.Round(point.X), ["y"] = MathF.Round(point.Y) }),
                });
            });
        });
    }
    private const float RulerSize = 18;

    // Stage units along the top and left edges, with the pointer's place marked on both.
    private static void stageRulers(ImDrawListPtr draw, EditorPanel area, EffectViewport view, Vector2 mouse)
    {
        var scale = view.Rectangle.Width / 1280; // pixels per stage unit
        float[] steps = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000];
        var major = steps.FirstOrDefault(step => step * scale >= 60, 5000);
        var minor = major / 5;
        var end = area.Position + area.Size;
        var first = view.ToStage(area.Position);
        var last = view.ToStage(end);
        draw.AddRectFilled(area.Position, new(end.X, area.Y + RulerSize), Ruler);
        draw.AddRectFilled(area.Position, new(area.X + RulerSize, end.Y), Ruler);
        for (var index = (int)MathF.Floor(first.X / minor); index * minor <= last.X; index++)
        {
            var x = view.ToScreen(new(index * minor, 0)).X;
            if (x < area.X + RulerSize) continue;
            draw.AddLine(new(x, area.Y + (index % 5 == 0 ? 3 : 12)), new(x, area.Y + RulerSize), Muted);
            if (index % 5 == 0) draw.AddText(new(x + 3, area.Y), Muted, number(index * minor));
        }
        for (var index = (int)MathF.Floor(first.Y / minor); index * minor <= last.Y; index++)
        {
            var y = view.ToScreen(new(0, index * minor)).Y;
            if (y < area.Y + RulerSize) continue;
            draw.AddLine(new(area.X + (index % 5 == 0 ? 3 : 12), y), new(area.X + RulerSize, y), Muted);
            if (index % 5 == 0) draw.AddText(new(area.X + 1, y), Muted, number(index * minor));
        }
        if (inArea(area, mouse))
        {
            draw.AddLine(new(mouse.X, area.Y), new(mouse.X, area.Y + RulerSize), Outline, 2);
            draw.AddLine(new(area.X, mouse.Y), new(area.X + RulerSize, mouse.Y), Outline, 2);
        }
        draw.AddRectFilled(area.Position, area.Position + new Vector2(RulerSize), Ruler);
    }

    // Stage composition delegates gestures, tool controls and handle drawing to their feature files.
    private void stage(EditorPanel rect, LumenPlayer player, IReadOnlyList<LumenInstanceInfo> clips)
    {
        var area = EditorLayout.Preview(rect);
        var view = stageView(area);
        var window = ImGui.GetIO().DisplaySize;
        _application.StageBox = view.Rectangle.Normalized(window.X, window.Y);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (panel("##movie stage", rect, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBringToFrontOnFocus))
        {
            stageTools(area);
            var overTools = ImGui.IsAnyItemHovered() || ImGui.IsAnyItemActive();
            stageNavigation(area, player, ImGui.GetIO().MousePos, overTools);
            if (_dragAsset is { } dropped && inArea(area, ImGui.GetIO().MousePos) && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                placeAsset(player, clips, dropped, view.ToStage(ImGui.GetIO().MousePos));
                _dragAsset = null;
            }
            ImGui.PushClipRect(area.Position, area.Position + area.Size, true);
            var draw = ImGui.GetWindowDrawList();
            draw.AddRect(view.Rectangle.Position, view.Rectangle.Position + view.Rectangle.Size, Muted);
            var mouse = ImGui.GetIO().MousePos;
            var point = view.ToStage(mouse);
            var selected = clips.FirstOrDefault(clip => clip.Path == _selected);
            var hasBox = selectionBounds(player, out var box);
            _handlePivot = new Vector2(.5f);
            if (_selection.Count == 1 && selected is not null && layerOf(clips, selected) is { } pivotSource
                && pivotSource.Layer["editorPivot"] is System.Text.Json.Nodes.JsonArray { Count: 2 } pivotData)
                _handlePivot = new((float)pivotData[0]!, (float)pivotData[1]!);
            var centre = hasBox ? view.ToScreen(new(box.X + box.Width * _handlePivot.X, box.Y + box.Height * _handlePivot.Y)) : Vector2.Zero;
            var handle = hasBox ? stageHandles(draw, view, box.X, box.Y, box.Width, box.Height, mouse) : -1;
            if (!overTools && ImGui.IsWindowHovered() && ImGui.GetIO().KeyShift && handle < 0
                && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && inArea(area, mouse))
                _selectionBox = point;
            selectionBox(player, clips, view, point);
            // Defer click-through until release: pressing selected bounds must never switch the drag target.
            if (_selectionBox is null && (!ImGui.GetIO().KeyShift || handle >= 0) && !overTools && ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && inArea(area, mouse))
            {
                var onSelected = hasBox && point.X >= box.X && point.Y >= box.Y && point.X <= box.X + box.Width && point.Y <= box.Y + box.Height;
                if ((handle >= 0 || onSelected && _stageTool == 0) && selected is not null && _document is { ReadOnly: false } && layerOf(clips, selected) is not null)
                {
                    _lockedAxis = 0;
                    captureGroup(player, clips);
                    _move = new(point, false, handle, view.ToStage(centre), new(box.X, box.Y), new(box.X + box.Width, box.Y + box.Height));
                }
                else
                    pick(player, point);
            }
            updateStageGesture(player, selected, view, point);
            if (selectionBounds(player, out box))
                draw.AddRect(view.ToScreen(new(box.X, box.Y)), view.ToScreen(new(box.X + box.Width, box.Y + box.Height)), Outline, 0, ImDrawFlags.None, 2);
            // What the move snapped to: a line across the view.
            if (_move is null) _guides = (null, null);
            if (_guides.X is { } guideX)
                draw.AddLine(new(view.ToScreen(new(guideX, 0)).X, area.Y), new(view.ToScreen(new(guideX, 0)).X, area.Y + area.Height), Guide);
            if (_guides.Y is { } guideY)
                draw.AddLine(new(area.X, view.ToScreen(new(0, guideY)).Y), new(area.X + area.Width, view.ToScreen(new(0, guideY)).Y), Guide);
            stageRulers(draw, area, view, mouse);
            ImGui.PopClipRect();
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

}
