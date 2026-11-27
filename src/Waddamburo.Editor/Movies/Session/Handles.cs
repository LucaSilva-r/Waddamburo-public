using System.Numerics;
using ImGuiNET;

namespace Waddamburo.Editor;

/// <summary>
/// The transform handles over a selection's box, shared by the component editor and the scene view. Tools: 3 free
/// transform (squares scale, the knob turns, inside moves), 0 move (arrows), 1 rotate (ring), 2 scale (squares). Hit
/// ids: 0 the centre, 1/2 the move arrows (x/y), 3-10 the squares clockwise from top left, <see cref="Rotate"/> the knob.
/// </summary>
public static class StageHandles
{
    public const int Rotate = 11;

    /// <summary>Draws the handles of the box (stage space) and returns the one under the pointer, or -1.</summary>
    public static int Draw(ImDrawListPtr draw, Func<Vector2, Vector2> toScreen, int tool, Vector2 boxMin, Vector2 boxMax, Vector2 pivotFraction,
        Vector2 mouse, uint text, uint outline)
    {
        var min = toScreen(boxMin);
        var max = toScreen(boxMax);
        var centre = min + (max - min) * pivotFraction;
        // The chosen transform centre is visible in every tool.
        draw.AddCircle(centre, 6, text, 20, 1.5f);
        draw.AddLine(centre - new Vector2(10, 0), centre + new Vector2(10, 0), text, 1);
        draw.AddLine(centre - new Vector2(0, 10), centre + new Vector2(0, 10), text, 1);
        var hit = -1;
        void square(Vector2 at, int id)
        {
            var hovered = Vector2.Distance(at, mouse) < 9;
            draw.AddRectFilled(at - new Vector2(4), at + new Vector2(4), hovered ? text : outline);
            if (hovered) hit = id;
        }
        if (tool == 3)
        {
            var knob = new Vector2(centre.X, min.Y - 28);
            draw.AddLine(new(centre.X, min.Y), knob, outline, 1.5f);
            var over = Vector2.Distance(knob, mouse) < 10;
            draw.AddCircleFilled(knob, 6, over ? text : outline);
            if (over) hit = Rotate;
            square(min, 3); square(new(centre.X, min.Y), 4); square(new(max.X, min.Y), 5);
            square(new(max.X, centre.Y), 6); square(max, 7); square(new(centre.X, max.Y), 8);
            square(new(min.X, max.Y), 9); square(new(min.X, centre.Y), 10);
            if (hit == Rotate) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            else if (hit is 4 or 8) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);
            else if (hit is 6 or 10) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
            else if (hit is 3 or 7) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNWSE);
            else if (hit is 5 or 9) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNESW);
        }
        else if (tool == 0)
        {
            var right = centre + new Vector2(48, 0);
            var up = centre - new Vector2(0, 48);
            uint red = ImGui.ColorConvertFloat4ToU32(new(.95f, .3f, .3f, 1)), green = ImGui.ColorConvertFloat4ToU32(new(.3f, .9f, .4f, 1));
            draw.AddLine(centre, right, red, 2);
            draw.AddTriangleFilled(right, right + new Vector2(-9, -5), right + new Vector2(-9, 5), red);
            draw.AddLine(centre, up, green, 2);
            draw.AddTriangleFilled(up, up + new Vector2(-5, 9), up + new Vector2(5, 9), green);
            if (mouse.X >= centre.X && mouse.X <= right.X + 5 && Math.Abs(mouse.Y - centre.Y) < 8) hit = 1;
            if (mouse.Y <= centre.Y && mouse.Y >= up.Y - 5 && Math.Abs(mouse.X - centre.X) < 8) hit = 2;
            square(centre, 0);
        }
        else if (tool == 1)
        {
            var radius = Math.Max(30, Vector2.Distance(min, max) / 2 + 12);
            draw.AddCircle(centre, radius, outline, 64, 2);
            draw.AddCircleFilled(centre, 3, text);
            if (Math.Abs(Vector2.Distance(mouse, centre) - radius) < 9) hit = 0;
        }
        else
        {
            square(min, 3); square(new(centre.X, min.Y), 4); square(new(max.X, min.Y), 5);
            square(new(max.X, centre.Y), 6); square(max, 7); square(new(centre.X, max.Y), 8);
            square(new(min.X, max.Y), 9); square(new(min.X, centre.Y), 10); square(centre, 0);
        }
        return hit;
    }

    /// <summary>What a drag that starts on <paramref name="handle"/> does with <paramref name="tool"/>: 0 move, 1 rotate, 2 scale.</summary>
    public static int Operation(int tool, int handle) => tool != 3 ? tool : handle == Rotate ? 1 : handle >= 3 ? 2 : 0;
}

public sealed partial class MovieInspectorSession
{
    private const int RotateHandle = StageHandles.Rotate;

    private int stageHandles(ImDrawListPtr draw, EffectViewport view, float x, float y, float width, float height, Vector2 mouse) =>
        StageHandles.Draw(draw, view.ToScreen, _stageTool, new(x, y), new(x + width, y + height), _handlePivot, mouse, Text, Outline);

    // The transform tools' buttons (shared by the component editor's stage and the scene view).
    private void transformToolButtons(bool disabled)
    {
        ImGui.BeginDisabled(disabled);
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
        ImGui.EndDisabled();
    }

    // The tools' keys while the pointer is over a stage.
    private void transformToolKeys()
    {
        if (ImGui.GetIO().WantTextInput || ImGui.GetIO().KeyCtrl) return;
        if (ImGui.IsKeyPressed(ImGuiKey.Q)) _stageTool = 3;
        if (ImGui.IsKeyPressed(ImGuiKey.W)) _stageTool = 0;
        if (ImGui.IsKeyPressed(ImGuiKey.E)) _stageTool = 1;
        if (ImGui.IsKeyPressed(ImGuiKey.R)) _stageTool = 2;
    }
}
