using System.Numerics;
using ImGuiNET;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private int _stageTool;
    private void stageTools(EditorPanel area)
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
        // Transport in the middle, as the timeline may be out of sight; the view's zoom on the right.
        var style = ImGui.GetStyle();
        var width = ImGui.GetWindowWidth();
        ImGui.SameLine(Math.Max(120, width / 2 - 150));
        transportButtons();
        var percent = $"{MathF.Round(EffectViewport.Fit(area).Rectangle.Width / 1280 * _zoom * 100)}%";
        var right = ImGui.CalcTextSize("Fit").X + ImGui.CalcTextSize("1:1").X + ImGui.CalcTextSize(percent).X + style.FramePadding.X * 4 + style.ItemSpacing.X * 2 + 8;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), width - right));
        ImGui.AlignTextToFramePadding();
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.TextUnformatted(percent); // not TextDisabled: it reads the % as a format
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Wheel: zoom at the pointer. Middle or right drag: move the view.\nF: frame the selection. Home: fit the stage.");
        ImGui.SameLine();
        if (ImGui.Button("Fit")) (_zoom, _pan) = (1, Vector2.Zero);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Fit the stage in the view (Home)");
        ImGui.SameLine();
        if (ImGui.Button("1:1")) (_zoom, _pan) = (1280 / EffectViewport.Fit(area).Rectangle.Width, Vector2.Zero);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("One stage unit per pixel");
    }
}
