using System.Numerics;
using ImGuiNET;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private int stageHandles(ImDrawListPtr draw, EffectViewport view, float x, float y, float width, float height, Vector2 mouse)
    {
        var min = view.ToScreen(new(x, y));
        var max = view.ToScreen(new(x + width, y + height));
        var centre = min + (max - min) * _handlePivot;
        // The chosen transform centre is visible in every tool, including on container sprites.
        draw.AddCircle(centre, 6, Text, 20, 1.5f);
        draw.AddLine(centre - new Vector2(10, 0), centre + new Vector2(10, 0), Text, 1);
        draw.AddLine(centre - new Vector2(0, 10), centre + new Vector2(0, 10), Text, 1);
        var hit = -1;
        void square(Vector2 at, int id)
        {
            var hovered = Vector2.Distance(at, mouse) < 9;
            draw.AddRectFilled(at - new Vector2(4), at + new Vector2(4), hovered ? Text : Outline);
            if (hovered) hit = id;
        }
        if (_stageTool == 0)
        {
            var right = centre + new Vector2(48, 0);
            var up = centre - new Vector2(0, 48);
            draw.AddLine(centre, right, color(.95f, .3f, .3f), 2);
            draw.AddTriangleFilled(right, right + new Vector2(-9, -5), right + new Vector2(-9, 5), color(.95f, .3f, .3f));
            draw.AddLine(centre, up, color(.3f, .9f, .4f), 2);
            draw.AddTriangleFilled(up, up + new Vector2(-5, 9), up + new Vector2(5, 9), color(.3f, .9f, .4f));
            if (mouse.X >= centre.X && mouse.X <= right.X + 5 && Math.Abs(mouse.Y - centre.Y) < 8) hit = 1;
            if (mouse.Y <= centre.Y && mouse.Y >= up.Y - 5 && Math.Abs(mouse.X - centre.X) < 8) hit = 2;
            square(centre, 0);
        }
        else if (_stageTool == 1)
        {
            var radius = Math.Max(30, Vector2.Distance(min, max) / 2 + 12);
            draw.AddCircle(centre, radius, Outline, 64, 2);
            draw.AddCircleFilled(centre, 3, Text);
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
}
