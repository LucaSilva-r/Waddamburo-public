using System.Numerics;
using ImGuiNET;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    // Stage selection, transform gestures and tool controls. Handle drawing lives in Handles; group math in Group.
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
                if ((handle >= 0 || onSelected && _stageTool == 0) && selected is not null && _document is { ReadOnly: false } && layerOf(clips, selected) is { } startSource)
                {
                    var key = MovieDocument.KeyAt((System.Text.Json.Nodes.JsonObject)startSource.Layer.DeepClone(), startSource.Frame);
                    _lockedAxis = 0;
                    captureGroup(player, clips);
                    _move = new(point, selected.Transform.X, selected.Transform.Y, false,
                        (float)MovieDocument.Value(key, "rotation")!, (float)MovieDocument.Value(key, "scaleX")!, (float)MovieDocument.Value(key, "scaleY")!, handle, view.ToStage(centre), new(box.X, box.Y), new(box.X + box.Width, box.Y + box.Height));
                }
                else
                    pick(player, point);
            }
            if (_move is { } move)
            {
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && selected is not null && layerOf(clips, selected) is { } source
                    && player.TryInspectParentTransform(selected.Path, out var parent))
                {
                    var delta = point - move.Start;
                    if (_stageTool == 0 && !ImGui.GetIO().KeyShift)
                    {
                        _lockedAxis = 0;
                        if (move.Handle == 1) delta.Y = 0;
                        else if (move.Handle == 2) delta.X = 0;
                    }
                    if (move.Moved || delta.Length() * view.Rectangle.Width / 1280 > 3)
                    {
                        // The pointer's move, taken into the space the clip is placed in.
                        var determinant = parent.M11 * parent.M22 - parent.M12 * parent.M21;
                        if (MathF.Abs(determinant) > 1e-6f)
                        {
                            var x = move.X + (delta.X * parent.M22 - delta.Y * parent.M21) / determinant;
                            var y = move.Y + (delta.Y * parent.M11 - delta.X * parent.M12) / determinant;
                            if (_stageTool == 0 && ImGui.GetIO().KeyShift)
                            {
                                if (_lockedAxis == 0 && Vector2.Distance(point, move.Start) > .001f)
                                {
                                    var movement = EffectGizmo.ParentVector(parent, point - move.Start);
                                    _lockedAxis = Math.Abs(movement.X) >= Math.Abs(movement.Y) ? 1 : 2;
                                }
                                if (_lockedAxis == 1) y = move.Y;
                                else if (_lockedAxis == 2) x = move.X;
                            }
                            var anchor = move.Pivot;
                            if (_stageTool == 2 && !ImGui.GetIO().KeyAlt)
                                anchor = move.Handle switch
                                {
                                    3 => move.Max,
                                    4 => new(move.Pivot.X, move.Max.Y),
                                    5 => new(move.Min.X, move.Max.Y),
                                    6 => new(move.Min.X, move.Pivot.Y),
                                    7 => move.Min,
                                    8 => new(move.Pivot.X, move.Min.Y),
                                    9 => new(move.Max.X, move.Min.Y),
                                    10 => new(move.Max.X, move.Pivot.Y),
                                    _ => move.Pivot,
                                };
                            var continues = move.Moved;
                            _move = move with { Moved = true };
                            if (_selection.Count > 1)
                            {
                                if (_stageTool == 0 && ImGui.GetIO().KeyShift)
                                {
                                    if (_lockedAxis == 0) _lockedAxis = Math.Abs(delta.X) >= Math.Abs(delta.Y) ? 1 : 2;
                                    if (_lockedAxis == 1) delta.Y = 0; else delta.X = 0;
                                }
                                transformGroup(move, point, delta, anchor, continues);
                            }
                            else edited(() => _document!.Edit(_ =>
                            {
                                var key = MovieDocument.KeyAt(source.Layer, source.Frame);
                                if (_stageTool == 0)
                                {
                                    MovieDocument.Set(key, "x", _lockedAxis == 2 ? move.X : MathF.Round(x));
                                    MovieDocument.Set(key, "y", _lockedAxis == 1 ? move.Y : MathF.Round(y));
                                }
                                else if (_stageTool == 1)
                                {
                                    var angle = move.Angle + (float)EffectGizmo.Rotation(move.Start - move.Pivot, point - move.Pivot);
                                    if (ImGui.GetIO().KeyShift) angle = MathF.Round(angle / 15) * 15;
                                    MovieDocument.Set(key, "rotation", angle);
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
                                    else if (ImGui.GetIO().KeyShift)
                                        sx = sy = Math.Abs(sx - 1) > Math.Abs(sy - 1) ? sx : sy;
                                    MovieDocument.Set(key, "scaleX", move.ScaleX * sx);
                                    MovieDocument.Set(key, "scaleY", move.ScaleY * sy);
                                }
                                if (_stageTool != 0 && Math.Abs(move.ScaleX * move.ScaleY) > 1e-6f)
                                {
                                    var pivotParent = EffectGizmo.ParentVector(parent, anchor - new Vector2(parent.X, parent.Y));
                                    var offset = pivotParent - new Vector2(move.X, move.Y);
                                    var radians = move.Angle * MathF.PI / 180;
                                    var local = new Vector2((offset.X * MathF.Cos(radians) + offset.Y * MathF.Sin(radians)) / move.ScaleX,
                                        (-offset.X * MathF.Sin(radians) + offset.Y * MathF.Cos(radians)) / move.ScaleY);
                                    var nextAngle = (float)MovieDocument.Value(key, "rotation")! * MathF.PI / 180;
                                    var scaled = local * new Vector2((float)MovieDocument.Value(key, "scaleX")!, (float)MovieDocument.Value(key, "scaleY")!);
                                    var rotated = new Vector2(scaled.X * MathF.Cos(nextAngle) - scaled.Y * MathF.Sin(nextAngle), scaled.X * MathF.Sin(nextAngle) + scaled.Y * MathF.Cos(nextAngle));
                                    MovieDocument.Set(key, "x", pivotParent.X - rotated.X);
                                    MovieDocument.Set(key, "y", pivotParent.Y - rotated.Y);
                                }
                            }, continues));
                        }
                    }
                }
                else
                {
                    if (!move.Moved && move.Handle < 0)
                        pick(player, move.Start); // A plain click on the selected clip reaches what is inside it.
                    _move = null;
                }
            }
            if (selectionBounds(player, out box))
                draw.AddRect(view.ToScreen(new(box.X, box.Y)), view.ToScreen(new(box.X + box.Width, box.Y + box.Height)), Outline, 0, ImDrawFlags.None, 2);
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    private Vector2 _handlePivot = new(.5f);
    private int _lockedAxis;
    private int _stageTool;
    private void stageTools()
    {
        if (ImGui.IsWindowHovered() && !ImGui.GetIO().WantTextInput && _move is null)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.W)) _stageTool = 0;
            if (ImGui.IsKeyPressed(ImGuiKey.E)) _stageTool = 1;
            if (ImGui.IsKeyPressed(ImGuiKey.R)) _stageTool = 2;
        }
        ImGui.SetCursorPos(new(8, 8));
        ImGui.BeginDisabled(_move is not null);
        void tool(int mode, string label)
        {
            var start = ImGui.GetCursorScreenPos();
            var active = _stageTool == mode;
            if (active) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.28f, .40f, .54f, 1));
            if (ImGui.Button("##tool" + mode, new(30, 26))) _stageTool = mode;
            if (active) ImGui.PopStyleColor();
            var draw = ImGui.GetWindowDrawList();
            var centre = start + new Vector2(15, 13);
            if (mode == 0)
            {
                draw.AddLine(centre - new Vector2(8, 0), centre + new Vector2(8, 0), Text, 2);
                draw.AddLine(centre - new Vector2(0, 8), centre + new Vector2(0, 8), Text, 2);
            }
            else if (mode == 1) draw.AddCircle(centre, 8, Text, 20, 2);
            else
            {
                draw.AddRect(centre - new Vector2(7), centre + new Vector2(7), Text, 0, ImDrawFlags.None, 2);
                draw.AddRectFilled(centre + new Vector2(4), centre + new Vector2(9), Text);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(label);
        }
        tool(0, "Move (W): drag artwork. Shift constrains the axis.");
        ImGui.SameLine();
        tool(1, "Rotate (E): drag the ring. Shift snaps to 15 degrees.");
        ImGui.SameLine();
        tool(2, "Scale (R): drag handles. Shift preserves proportions; Alt scales around the pivot.");
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Drag selected artwork. Shift: uniform scale. Rotate: drag horizontally, Shift snaps to 15 degrees. Move: Shift constrains the axis.");
    }

    private sealed record StageMove(Vector2 Start, float X, float Y, bool Moved, float Angle, float ScaleX, float ScaleY, int Handle, Vector2 Pivot, Vector2 Min, Vector2 Max);
    private StageMove? _move;
}
