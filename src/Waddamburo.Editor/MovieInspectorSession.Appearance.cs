using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private static readonly string[] Interpolations = ["linear", "hold", "in", "out", "inOut"];
    private bool _tintGesture;

    private void appearanceFields(JsonObject layer, JsonObject current, int frame)
    {
        var tint = MovieDocument.Tint(current);
        var rgb = new Vector3((float)tint.Red, (float)tint.Green, (float)tint.Blue);
        if (ImGui.ColorEdit3("Tint", ref rgb))
        {
            var continues = _tintGesture && !ImGui.IsItemActivated();
            attempt(() => _document!.Edit(_ => MovieDocument.SetTint(MovieDocument.KeyAt(layer, frame), rgb.X, rgb.Y, rgb.Z), continues));
            _tintGesture = true;
        }
        if (ImGui.IsItemDeactivated()) _tintGesture = false;
        ImGui.TextDisabled("Tint multiplies the artwork colour.");

        var keys = layer["keys"]!.AsArray().OfType<JsonObject>().OrderBy(key => (int)key["frame"]!).ToArray();
        var key = keys.LastOrDefault(key => (int)key["frame"]! <= frame);
        var ease = key is null ? "linear" : MovieDocument.Ease(key);
        if (ImGui.BeginCombo("Interpolation", ease))
        {
            foreach (var option in Interpolations)
                if (ImGui.Selectable(option, option == ease))
                    attempt(() => _document!.Edit(_ => MovieDocument.SetEase(MovieDocument.KeyAt(layer, frame), option)));
            ImGui.EndCombo();
        }
        ImGui.TextWrapped("Interpolation runs from this key to the next. Editing between keys creates a key here.");

        if (layer.Parent is not JsonArray layers || layers.Parent is not JsonObject sprite) return;
        var spriteName = _document!.Root["sprites"]!.AsObject().First(pair => ReferenceEquals(pair.Value, sprite)).Key;
        var index = layers.IndexOf(layer);
        ImGui.SeparatorText("Layer appearance and lifetime");
        var blend = (string?)layer["blend"] ?? "normal";
        if (ImGui.BeginCombo("Blend", blend))
        {
            foreach (var option in MovieDocument.Blends)
                if (ImGui.Selectable(option, option == blend))
                    attempt(() => _document.SetBlend(spriteName, index, option));
            ImGui.EndCombo();
        }
        var mask = (int?)layer["masks"] ?? 0;
        if (ImGui.SliderInt("Mask layers", ref mask, 0, layers.Count - index - 1))
        {
            var path = _selected;
            if (attempt(() => _document.SetMask(spriteName, index, mask)) && path is not null)
            {
                // SetMask normalizes depths; follow this layer to its new display-list path.
                var cut = path.LastIndexOf('/');
                var depth = Waddamburo.Lumen.Runtime.LumenPlayer.TimelineDepthBase + index + 1;
                _selected = cut < 0 ? $"{depth}" : $"{path[..cut]}/{depth}";
            }
        }
        ImGui.TextWrapped("0: ordinary artwork. Otherwise this layer clips the next N layers above it and is not drawn itself.");
        var length = (int?)sprite["frames"] ?? 1;
        var last = keys.Max(key => (int)key["frame"]!);
        var until = (int?)layer["until"];
        var stays = until is null;
        ImGui.BeginDisabled(last + 1 >= length && stays);
        if (ImGui.Checkbox("Keep to end", ref stays))
            attempt(() => _document.SetUntil(spriteName, index, stays ? null : Math.Clamp(frame + 1, last + 1, length - 1)));
        ImGui.EndDisabled();
        if (until is { } end)
        {
            if (ImGui.InputInt("Remove on frame", ref end, 1, 10))
                attempt(() => _document.SetUntil(spriteName, index, end));
            ImGui.TextWrapped($"Valid removal frames: {last + 1}–{length - 1}. The layer is absent starting on this frame.");
        }
    }
}
