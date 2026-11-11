using System.Text.Json.Nodes;
using ImGuiNET;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private void hierarchyReorder(IReadOnlyList<LumenInstanceInfo> clips, LumenInstanceInfo clip)
    {
        if (_document is not { ReadOnly: false } || _filter.Length > 0 || clip.Level == 0) return;
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            _hierarchyDrag ??= clip.Path;
        var source = clips.FirstOrDefault(each => each.Path == _hierarchyDrag);
        if (!ImGui.IsMouseDragging(ImGuiMouseButton.Left) && !ImGui.IsMouseReleased(ImGuiMouseButton.Left)) return;
        if (source is null || source.Path == clip.Path || !ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)) return;
        if (layerOf(clips, source) is not { } from || layerOf(clips, clip) is not { } target
            || from.Layer.Parent is not JsonArray layers || !ReferenceEquals(layers, target.Layer.Parent)) return;
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var after = ImGui.GetMousePos().Y > (min.Y + max.Y) / 2;
        var y = after ? max.Y : min.Y;
        ImGui.GetWindowDrawList().AddLine(new(min.X, y), new(max.X, y), KeySelected, 2);
        ImGui.SetTooltip(after ? "Place after this sibling" : "Place before this sibling");
        if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left)) return;
        var fromIndex = layers.IndexOf(from.Layer);
        var insertion = layers.IndexOf(target.Layer) + (after ? 1 : 0);
        var toIndex = insertion - (fromIndex < insertion ? 1 : 0);
        if (fromIndex == toIndex) return;
        var sprite = _document.Root["sprites"]!.AsObject().First(pair => ReferenceEquals(pair.Value, layers.Parent)).Key;
        if (attempt(() => _document.MoveLayer(sprite, fromIndex, toIndex)))
        {
            var cut = source.Path.LastIndexOf('/');
            var depth = LumenPlayer.TimelineDepthBase + toIndex + 1;
            selectClip(cut < 0 ? $"{depth}" : $"{source.Path[..cut]}/{depth}");
            _open.Clear();
            _collapsed.Clear();
        }
        _hierarchyDrag = null;
    }
}
