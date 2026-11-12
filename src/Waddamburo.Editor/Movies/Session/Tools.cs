using System.Numerics;
using ImGuiNET;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private int _stageTool = 3;
    private void stageTools(EditorPanel area)
    {
        if (ImGui.IsWindowHovered() && !ImGui.GetIO().WantTextInput && _move is null)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Q)) _stageTool = 3;
            if (ImGui.IsKeyPressed(ImGuiKey.W)) _stageTool = 0;
            if (ImGui.IsKeyPressed(ImGuiKey.E)) _stageTool = 1;
            if (ImGui.IsKeyPressed(ImGuiKey.R)) _stageTool = 2;
        }
        ImGui.SetCursorPos(new(8, 8));
        ImGui.BeginDisabled(_move is not null);
        void tool(int mode, string icon, string label)
        {
            if (iconButton(icon, label, active: _stageTool == mode, accent: new Vector4(.2f, .5f, .3f, 1))) _stageTool = mode;
        }
        tool(3, "square-dashed-mouse-pointer", "Free transform (Q): drag inside to move, the squares to scale, the knob on top to turn.\nShift: one axis / keep proportions / 15 degree steps. Alt: scale about the pivot. Ctrl: no snapping.");
        ImGui.SameLine();
        tool(0, "move", "Move (W): drag artwork. Shift keeps to one axis, Ctrl turns snapping off.");
        ImGui.SameLine();
        tool(1, "rotate-cw", "Rotate (E): drag the ring. Shift snaps to 15 degrees.");
        ImGui.SameLine();
        tool(2, "scaling", "Scale (R): drag handles. Shift preserves proportions; Alt scales around the pivot.");
        ImGui.SameLine(0, 14);
        if (iconButton("circle", _autoKey
                ? "Auto key is on: changing something makes a key on the current frame.\nMove the playhead, move the thing, and it animates."
                : "Auto key is off: changing something moves its whole animation; no key is added.", active: _autoKey, accent: new Vector4(.78f, .16f, .16f, 1)))
            _autoKey = !_autoKey;
        ImGui.EndDisabled();
        // Transport in the middle, as the timeline may be out of sight; the view's zoom on the right.
        var style = ImGui.GetStyle();
        var width = ImGui.GetWindowWidth();
        ImGui.SameLine(Math.Max(120, width / 2 - 130));
        transportButtons();
        var percent = $"{MathF.Round(EffectViewport.Fit(area).Rectangle.Width / 1280 * _zoom * 100)}%";
        var right = 30 + ImGui.CalcTextSize("1:1").X + ImGui.CalcTextSize(percent).X + style.FramePadding.X * 2 + style.ItemSpacing.X * 2 + 8;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), width - right));
        ImGui.AlignTextToFramePadding();
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.TextUnformatted(percent); // not TextDisabled: it reads the % as a format
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Wheel: zoom at the pointer. Middle or right drag: move the view.\nF: frame the selection. Home: fit the stage.");
        ImGui.SameLine();
        if (iconButton("maximize", "Fit the stage in the view (Home)")) (_zoom, _pan) = (1, Vector2.Zero);
        ImGui.SameLine();
        if (ImGui.Button("1:1")) (_zoom, _pan) = (1280 / EffectViewport.Fit(area).Rectangle.Width, Vector2.Zero);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("One stage unit per pixel");
    }
}
