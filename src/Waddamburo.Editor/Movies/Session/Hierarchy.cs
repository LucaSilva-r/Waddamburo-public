using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private string? _hierarchyDrag;
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private string _filter = "";
    // Clips opened in the tree beyond the first two levels (a big movie has thousands).
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);
    private void tree(EditorPanel rect, IReadOnlyList<LumenInstanceInfo> clips)
    {
        if (panel("Clips", rect))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##filter", "filter by name", ref _filter, 64);
            ImGui.BeginChild("##clips");
            int? closed = null; // The collapsed clip whose children are being skipped.
            for (var index = 0; index < clips.Count; index++)
            {
                var clip = clips[index];
                if (_filter.Length > 0 && !clip.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (_filter.Length == 0)
                {
                    if (closed is { } level && clip.Level > level)
                        continue;
                    closed = null;
                }
                var parent = index + 1 < clips.Count && clips[index + 1].Level > clip.Level;
                var open = !_collapsed.Contains(clip.Path) && (clip.Level < 2 || _open.Contains(clip.Path));
                var name = clip.Level == 0 ? "root" : clip.Name.Length > 0 ? clip.Name : $"#{clip.CharacterId}";
                var state = clip.Kind switch
                {
                    LumenInstanceKind.Sprite when clip.FrameCount > 1 => $"  {clip.Frame}/{clip.FrameCount - 1}{(clip.Label is null ? "" : $" {clip.Label}")}{(clip.Playing ? "" : " (stopped)")}",
                    LumenInstanceKind.Shape => "  shape",
                    LumenInstanceKind.Text => "  text",
                    _ => "",
                };
                var shown = clip.Visible && !clip.HiddenByHost && clip.Multiply.Alpha > 0;
                ImGui.PushStyleColor(ImGuiCol.Text, shown ? Text : Muted);
                if (_filter.Length == 0)
                {
                    ImGui.Indent(clip.Level * 12f + .01f);
                    if (parent)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
                        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 0));
                        if (ImGui.ArrowButton($"##open{clip.Path}", open ? ImGuiDir.Down : ImGuiDir.Right))
                        {
                            if (open) { _collapsed.Add(clip.Path); _open.Remove(clip.Path); }
                            else { _collapsed.Remove(clip.Path); _open.Add(clip.Path); }
                        }
                        ImGui.PopStyleVar();
                        ImGui.PopStyleColor();
                        ImGui.SameLine();
                    }
                    else
                    {
                        ImGui.Dummy(new(ImGui.GetFrameHeight(), 0));
                        ImGui.SameLine();
                    }
                    if (parent && !open)
                        closed = clip.Level;
                }
                var icon = clip.Kind switch { LumenInstanceKind.Sprite => "S", LumenInstanceKind.Shape => "[]", LumenInstanceKind.Text => "T", _ => "?" };
                if (ImGui.Selectable($"{icon}  {name}{state}##{clip.Path}", _selection.Contains(clip.Path), ImGuiSelectableFlags.None, new Vector2(0, ImGui.GetFrameHeight())))
                    hierarchySelection(clips, clip.Path);
                hierarchyReorder(clips, clip);
                if (clip.Path == _selected && _scrollToSelected)
                {
                    ImGui.SetScrollHereY();
                    _scrollToSelected = false;
                }
                if (_filter.Length == 0)
                    ImGui.Unindent(clip.Level * 12f + .01f);
                ImGui.PopStyleColor();
            }
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _hierarchyDrag = null;
            ImGui.EndChild();
        }
        ImGui.End();
    }


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
