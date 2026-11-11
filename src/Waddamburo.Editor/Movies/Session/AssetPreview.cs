using System.Numerics;
using System.Globalization;
using System.Text.Json.Nodes;
using ImGuiNET;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    // Parsed numbers and newly edited numbers can have different CLR types inside JsonValue.
    private static float assetNumber(JsonNode? node) => node is JsonValue value && value.TryGetValue<float>(out var number)
        ? number : float.Parse(node!.ToJsonString(), CultureInfo.InvariantCulture);

    private void texturePreview(string name, Vector2 top, Vector2 end) =>
        shapePreview(new JsonObject { ["texture"] = name, ["rect"] = TextureOf?.Invoke(name) is { } image
            ? new JsonArray(0f, 0f, (float)image.Width, (float)image.Height) : new JsonArray(0f, 0f, 1f, 1f) }, top, end);

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
            var width = Math.Abs(assetNumber(bounds[2]));
            var height = Math.Abs(assetNumber(bounds[3]));
            if (width > 0 && height > 0)
            {
                var scale = Math.Min(Math.Max(0, end.X - top.X - 8) / width, Math.Max(0, end.Y - top.Y - 8) / height);
                var size = new Vector2(width, height) * scale;
                var start = (top + end - size) / 2;
                var uv = shape["uv"] as JsonArray;
                var first = uv is { Count: 4 } ? new Vector2(assetNumber(uv[0]), assetNumber(uv[1])) : Vector2.Zero;
                var last = uv is { Count: 4 } ? new Vector2(assetNumber(uv[2]), assetNumber(uv[3])) : Vector2.One;
                draw.AddImage(image.Texture, start, start + size, first, last);
            }
        }
        else
            draw.AddText(top + new Vector2(6, 6), Text, textureName is null ? "Native fill" : "Texture unavailable");
        draw.PopClipRect();
    }
}
