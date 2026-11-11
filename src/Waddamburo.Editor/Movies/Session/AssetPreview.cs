using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    // A package shape is one textured quad. Preserve its authored proportions and atlas region.
    private void shapePreview(JsonObject shape, Vector2 top, Vector2 end)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(top, end, true);
        for (var y = top.Y; y < end.Y; y += 10)
            for (var x = top.X; x < end.X; x += 10)
            {
                var light = ((int)((x - top.X) / 10) + (int)((y - top.Y) / 10)) % 2 == 0;
                draw.AddRectFilled(new(x, y), Vector2.Min(new(x + 10, y + 10), end),
                    light ? color(.24f, .25f, .27f) : color(.17f, .18f, .20f));
            }
        var textureName = (string?)shape["texture"];
        var texture = textureName is null ? null : TextureOf?.Invoke(textureName);
        if (texture is { } image && shape["rect"] is JsonArray bounds && bounds.Count == 4)
        {
            var width = Math.Abs((float)bounds[2]!);
            var height = Math.Abs((float)bounds[3]!);
            if (width > 0 && height > 0)
            {
                var scale = Math.Min(Math.Max(0, end.X - top.X - 8) / width, Math.Max(0, end.Y - top.Y - 8) / height);
                var size = new Vector2(width, height) * scale;
                var start = (top + end - size) / 2;
                var uv = shape["uv"] as JsonArray;
                var first = uv is { Count: 4 } ? new Vector2((float)uv[0]!, (float)uv[1]!) : Vector2.Zero;
                var last = uv is { Count: 4 } ? new Vector2((float)uv[2]!, (float)uv[3]!) : Vector2.One;
                draw.AddImage(image.Texture, start, start + size, first, last);
            }
        }
        else
            draw.AddText(top + new Vector2(6, 6), Text, textureName is null ? "Native fill" : "Texture unavailable");
        draw.PopClipRect();
    }
}
