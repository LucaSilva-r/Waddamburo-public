using System.Numerics;
using ImGuiNET;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private Vector2 _handlePivot = new(.5f);

    // Stage composition delegates gestures, tool controls and handle drawing to their feature files.
    private void stage(EditorPanel rect, LumenPlayer player, IReadOnlyList<LumenInstanceInfo> clips)
    {
        var view = EffectViewport.Fit(EditorLayout.Preview(rect));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (panel("##movie stage", rect, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBringToFrontOnFocus))
        {
            stageTools();
            var overTools = ImGui.IsAnyItemHovered() || ImGui.IsAnyItemActive();
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
                && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && view.Contains(mouse))
                _selectionBox = point;
            selectionBox(player, clips, view, point);
            // Defer click-through until release: pressing selected bounds must never switch the drag target.
            if (_selectionBox is null && (!ImGui.GetIO().KeyShift || handle >= 0) && !overTools && ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && view.Contains(mouse))
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
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

}
