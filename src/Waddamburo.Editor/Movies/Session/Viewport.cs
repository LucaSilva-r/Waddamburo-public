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
        if (hovered && (ImGui.IsMouseClicked(ImGuiMouseButton.Middle) || ImGui.IsMouseClicked(ImGuiMouseButton.Right)))
            _panning = true;
        if (_panning)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Middle) && !ImGui.IsMouseDown(ImGuiMouseButton.Right))
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
            ImGui.PopClipRect();
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

}
