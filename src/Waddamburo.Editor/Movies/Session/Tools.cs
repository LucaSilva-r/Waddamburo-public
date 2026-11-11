using System.Numerics;
using ImGuiNET;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
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
}
