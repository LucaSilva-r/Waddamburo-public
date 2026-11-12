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
        fieldLabel("Tint", "Multiplies the artwork's colour: white leaves it as drawn.");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.ColorEdit3("##tint", ref rgb))
        {
            var continues = _tintGesture && !ImGui.IsItemActivated();
            attempt(() => _document!.Edit(_ => MovieDocument.SetTint(MovieDocument.BareKey(layer, frame), rgb.X, rgb.Y, rgb.Z), continues));
            _tintGesture = true;
        }
        if (ImGui.IsItemDeactivated()) _tintGesture = false;
        if (spriteOf(layer) is not var (spriteName, _, index)) return;
        var blend = (string?)layer["blend"] ?? "normal";
        fieldLabel("Blend", "How it mixes with what is under it. For the whole layer, not per key.");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##blend", blend))
        {
            foreach (var option in MovieDocument.Blends)
                if (ImGui.Selectable(option, option == blend))
                    attempt(() => _document!.SetBlend(spriteName, index, option));
            ImGui.EndCombo();
        }
    }

    private (string Sprite, JsonArray Layers, int Index)? spriteOf(JsonObject layer) =>
        layer.Parent is JsonArray layers && layers.Parent is JsonObject sprite
            ? (_document!.Root["sprites"]!.AsObject().First(pair => ReferenceEquals(pair.Value, sprite)).Key, layers, layers.IndexOf(layer)) : null;

    private void interpolationField(JsonObject layer, int frame)
    {
        var keys = layer["keys"]!.AsArray().OfType<JsonObject>().OrderBy(key => (int)key["frame"]!).ToArray();
        var key = keys.LastOrDefault(key => (int)key["frame"]! <= frame) ?? keys[0];
        var ease = MovieDocument.Ease(key);
        fieldLabel("Easing", $"How what the key on frame {(int)key["frame"]!} holds runs to its next key: linear, hold (jump there), or eased in / out.");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##ease", ease))
        {
            foreach (var option in Interpolations)
                if (ImGui.Selectable(option, option == ease))
                    attempt(() => _document!.Edit(_ => MovieDocument.SetEase(key, option)));
            ImGui.EndCombo();
        }
    }

    private void layerLifetimeFields(JsonObject layer, int frame)
    {
        if (spriteOf(layer) is not var (spriteName, layers, index) || layers.Parent is not JsonObject sprite) return;
        var keys = layer["keys"]!.AsArray().OfType<JsonObject>().ToArray();
        var mask = (int?)layer["masks"] ?? 0;
        fieldLabel("Masks", "0: ordinary artwork. Otherwise this layer is not drawn: it clips that many layers above it to its shape.");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderInt("##masks", ref mask, 0, layers.Count - index - 1))
        {
            var path = _selected;
            if (attempt(() => _document!.SetMask(spriteName, index, mask)) && path is not null)
            {
                // SetMask normalizes depths; follow this layer to its new display-list path.
                var cut = path.LastIndexOf('/');
                var depth = Waddamburo.Lumen.Runtime.LumenPlayer.TimelineDepthBase + index + 1;
                _selected = cut < 0 ? $"{depth}" : $"{path[..cut]}/{depth}";
            }
        }
        var length = (int?)sprite["frames"] ?? 1;
        var last = keys.Max(key => (int)key["frame"]!);
        var until = (int?)layer["until"];
        var stays = until is null;
        fieldLabel("Stays", "On: the layer is there to the timeline's end. Off: it is taken away on a frame you choose.");
        ImGui.BeginDisabled(last + 1 >= length && stays);
        if (ImGui.Checkbox("##stays", ref stays))
            attempt(() => _document!.SetUntil(spriteName, index, stays ? null : Math.Clamp(frame + 1, last + 1, length - 1)));
        ImGui.EndDisabled();
        if (until is { } end)
        {
            fieldLabel("Removed on", $"The first frame without it: {last + 1} to {length - 1}.");
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt("##until", ref end, 1, 10))
                attempt(() => _document!.SetUntil(spriteName, index, end));
        }
    }
}
