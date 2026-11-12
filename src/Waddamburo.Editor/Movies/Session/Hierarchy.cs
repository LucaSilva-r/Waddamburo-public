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
                // Only the root is open at first: nine copies of one sprite are nine lines, not fifty.
                var open = !_collapsed.Contains(clip.Path) && (clip.Level < 1 || _open.Contains(clip.Path));
                var source = clip.Level == 0 ? null : _document?.CharacterName(clip.CharacterId);
                var name = clip.Level == 0 ? "root" : clip.Name.Length > 0 ? clip.Name : source ?? $"#{clip.CharacterId}";
                // An instance named differently from its asset says which asset it is a copy of.
                if (source is not null && clip.Name.Length > 0 && source != clip.Name && clip.Kind == LumenInstanceKind.Sprite)
                    name += $"  ({source})";
                var state = clip.Kind switch
                {
                    LumenInstanceKind.Sprite when clip.FrameCount > 1 => $"  {clip.Frame}/{clip.FrameCount - 1}{(clip.Label is null ? "" : $" {clip.Label}")}{(clip.Playing ? "" : " (stopped)")}",
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
                var icon = clip.Kind switch { LumenInstanceKind.Sprite => "clapperboard", LumenInstanceKind.Text => "type", _ => "shapes" };
                var changed = false;
                var iconAt = ImGui.GetCursorScreenPos() + new Vector2(2, (ImGui.GetFrameHeight() - 15) / 2);
                if (_renaming == clip.Path)
                {
                    // The name is typed where it stands. Enter or a click elsewhere keeps it, Escape does not.
                    ImGui.Dummy(new(20, 0));
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(-48);
                    if (_renameFocus)
                    {
                        ImGui.SetKeyboardFocusHere();
                        _renameFocus = false;
                    }
                    var entered = ImGui.InputText("##rename", ref _renameText, 64, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
                    if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                        _renaming = null;
                    else if (entered || ImGui.IsItemDeactivatedAfterEdit())
                    {
                        var typed = _renameText.Trim();
                        if (layerOf(clips, clip) is { } renamed && typed != clip.Name)
                            changed = attempt(() => _document!.Edit(_ =>
                            {
                                if (typed.Length == 0) renamed.Layer.Remove("name"); else renamed.Layer["name"] = typed;
                            }));
                        _renaming = null;
                    }
                    else if (ImGui.IsItemDeactivated())
                        _renaming = null;
                }
                else
                {
                    if (ImGui.Selectable($"      {name}{state}##{clip.Path}", _selection.Contains(clip.Path),
                            ImGuiSelectableFlags.AllowDoubleClick | ImGuiSelectableFlags.AllowOverlap, new Vector2(0, ImGui.GetFrameHeight())))
                        hierarchySelection(clips, clip.Path);
                    if (clip.Kind == LumenInstanceKind.Sprite && ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        openInstanceTimeline(clip.Path);
                    changed = hierarchyMenu(clips, clip);
                    hierarchyReorder(clips, clip);
                }
                if (ImGui.IsRectVisible(iconAt, iconAt + new Vector2(15))) // thousands of rows: only the ones in view are drawn
                    drawAssetIcon(icon, iconAt, 15, shown ? Text : Muted);
                if (clip.Level > 0)
                {
                    // The eye: out of the way while working (not saved).
                    ImGui.SameLine(ImGui.GetWindowWidth() - 46);
                    if (iconButton(clip.HiddenByHost ? "eye-off" : "eye", clip.HiddenByHost ? "Hidden in the editor: click to show" : "Hide in the editor (not saved)",
                            id: "eye" + clip.Path, size: ImGui.GetFrameHeight() - 2, plain: true))
                        _player().InspectSetHidden(clip.Path, !clip.HiddenByHost);
                }
                if (clip.Path == _selected && _scrollToSelected)
                {
                    ImGui.SetScrollHereY();
                    _scrollToSelected = false;
                }
                if (_filter.Length == 0)
                    ImGui.Unindent(clip.Level * 12f + .01f);
                ImGui.PopStyleColor();
                if (changed)
                    break; // The list is of the movie before the change: drawn again next frame.
            }
            if (clips.Count <= 1 && _document is { ReadOnly: false })
                ImGui.TextWrapped("Nothing on stage yet. Drag an asset from the Assets tab below, or use Add layer in the Timeline.");
            if (!ImGui.GetIO().WantTextInput && _selected is not null
                && clips.FirstOrDefault(each => each.Path == _selected) is { } active && _document is { ReadOnly: false })
            {
                if (_deleteAsked && !_deleteKeys) removeLayer(clips, active);
                else if (!ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows)) { }
                else if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D, false)) duplicateLayer(clips, active);
                else if (ImGui.IsKeyPressed(ImGuiKey.F2, false)) startRename(active);
            }
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _hierarchyDrag = null;
            // A click on nothing in the list selects nothing.
            if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsAnyItemHovered())
                selectClip(null);
            ImGui.EndChild();
        }
        ImGui.End();
    }


    // Right-click on a clip: what can be done with it. True when the movie was changed.
    private bool hierarchyMenu(IReadOnlyList<LumenInstanceInfo> clips, LumenInstanceInfo clip)
    {
        if (!ImGui.BeginPopupContextItem($"##menu{clip.Path}"))
            return false;
        if (!_selection.Contains(clip.Path))
            selectClip(clip.Path);
        var changed = false;
        var editable = _document is { ReadOnly: false } && clip.Level > 0 && layerOf(clips, clip) is not null;
        if (clip.Kind == LumenInstanceKind.Sprite && ImGui.MenuItem("Open timeline", "double-click"))
            openInstanceTimeline(clip.Path);
        if (ImGui.MenuItem("Frame in view", "F"))
            _frameRequest = clip.Path;
        if (clip.Level > 0 && ImGui.MenuItem(clip.HiddenByHost ? "Show in editor" : "Hide in editor"))
            _player().InspectSetHidden(clip.Path, !clip.HiddenByHost);
        if (clip.Level > 0 && _document?.CharacterName(clip.CharacterId) is { } asset && ImGui.MenuItem("Edit source asset"))
            selectAsset(clip.Kind switch { LumenInstanceKind.Shape => "shapes", LumenInstanceKind.Text => "texts", _ => "sprites" }, asset);
        ImGui.Separator();
        if (ImGui.MenuItem("Rename", "F2", false, editable))
            startRename(clip);
        if (ImGui.MenuItem("Duplicate", "Ctrl+D", false, editable))
            changed = duplicateLayer(clips, clip);
        if (ImGui.MenuItem("Delete", "Del", false, editable))
            changed = removeLayer(clips, clip);
        ImGui.EndPopup();
        return changed;
    }

    private string? _renaming; // The clip whose name is being typed in the list.
    private string _renameText = "";
    private bool _renameFocus;

    private void startRename(LumenInstanceInfo clip)
    {
        if (clip.Level == 0 || _document is not { ReadOnly: false }) return;
        (_renaming, _renameText, _renameFocus) = (clip.Path, clip.Name, true);
    }

    private bool duplicateLayer(IReadOnlyList<LumenInstanceInfo> clips, LumenInstanceInfo clip) =>
        layerOf(clips, clip) is { } source && spriteOf(source.Layer) is var (sprite, _, index)
            && attempt(() => _document!.DuplicateLayer(sprite, index));

    private bool removeLayer(IReadOnlyList<LumenInstanceInfo> clips, LumenInstanceInfo clip)
    {
        if (layerOf(clips, clip) is not { } source || spriteOf(source.Layer) is not var (_, layers, index))
            return false;
        var done = attempt(() => _document!.Edit(_ => layers.RemoveAt(index)));
        if (done) selectClip(null);
        return done;
    }

    private void hierarchyReorder(IReadOnlyList<LumenInstanceInfo> clips, LumenInstanceInfo clip)
    {
        if (_document is not { ReadOnly: false } || _filter.Length > 0 || clip.Level == 0) return;
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            _hierarchyDrag ??= clip.Path;
        var source = clips.FirstOrDefault(each => each.Path == _hierarchyDrag);
        if (!ImGui.IsMouseDragging(ImGuiMouseButton.Left) && !ImGui.IsMouseReleased(ImGuiMouseButton.Left)) return;
        if (source is null || source.Path == clip.Path || !ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)) return;
        if (clip.Path.StartsWith(source.Path + "/", StringComparison.Ordinal)) return; // not into itself
        if (layerOf(clips, source) is not { } from || from.Layer.Parent is not JsonArray layers) return;
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var mouseY = ImGui.GetMousePos().Y;
        string spriteName(JsonArray list) => _document.Root["sprites"]!.AsObject().First(pair => ReferenceEquals(pair.Value, list.Parent)).Key;
        // The middle of a sprite's row (or anywhere on the root's): into it, on top of what it holds.
        var target = clip.Level == 0 ? null : layerOf(clips, clip);
        var into = clip.Kind == LumenInstanceKind.Sprite && _document.CharacterName(clip.CharacterId) is { } inside
            && (clip.Level == 0 || mouseY > min.Y + (max.Y - min.Y) * .3f && mouseY < min.Y + (max.Y - min.Y) * .7f) ? inside : null;
        if (into is not null && !(clip.Level == 0 && source.Level == 1))
        {
            ImGui.GetWindowDrawList().AddRect(min, max, KeySelected, 0, ImDrawFlags.None, 2);
            ImGui.SetTooltip($"Move into {(clip.Level == 0 ? "the root" : clip.Name.Length > 0 ? clip.Name : into)}");
            if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left)) return;
            if (attempt(() => _document.MoveLayerTo(spriteName(layers), layers.IndexOf(from.Layer), into, int.MaxValue)))
            {
                selectClip(null);
                _open.Add(clip.Path);
                _collapsed.Remove(clip.Path);
            }
            _hierarchyDrag = null;
            return;
        }
        if (target is null || target.Value.Layer.Parent is not JsonArray targetLayers) return;
        var after = mouseY > (min.Y + max.Y) / 2;
        var y = after ? max.Y : min.Y;
        ImGui.GetWindowDrawList().AddLine(new(min.X, y), new(max.X, y), KeySelected, 2);
        ImGui.SetTooltip(after ? "Place after this" : "Place before this");
        if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left)) return;
        if (!ReferenceEquals(layers, targetLayers))
        {
            // Beside a clip of another timeline: out of its own sprite, into that one's.
            if (attempt(() => _document.MoveLayerTo(spriteName(layers), layers.IndexOf(from.Layer), spriteName(targetLayers),
                    targetLayers.IndexOf(target.Value.Layer) + (after ? 1 : 0))))
                selectClip(null);
            _hierarchyDrag = null;
            return;
        }
        var fromIndex = layers.IndexOf(from.Layer);
        var insertion = layers.IndexOf(target.Value.Layer) + (after ? 1 : 0);
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
