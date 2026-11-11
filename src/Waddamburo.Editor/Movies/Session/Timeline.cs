using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

/// <summary>
/// The explicitly opened sprite timeline. Selecting a layer or key never changes the timeline being edited.
/// </summary>
public sealed partial class MovieInspectorSession
{
    private const float NameColumn = 190, RowHeight = 22;
    private (string Sprite, int Layer, int Frame)? _key; // The selected key.
    private bool _keyDrag;
    private int? _length; // The frame count being typed.
    private string _newLabel = "";
    private float _frameWidth; // pixels per frame; 0: the whole timeline fitted to the panel
    private static readonly uint KeyColour = color(.61f, .76f, .91f), KeySelected = color(1, .56f, .18f), Life = color(.26f, .35f, .44f, .8f),
        Row = color(.16f, .175f, .2f), RowOdd = color(.14f, .15f, .17f), Ruler = color(.2f, .22f, .25f);

    // The clip whose timeline is shown, and its sprite's entry in movie.json.
    private (LumenInstanceInfo Clip, string Name, JsonObject Sprite)? timelineOf(IReadOnlyList<LumenInstanceInfo> clips)
    {
        (LumenInstanceInfo, string, JsonObject)? sprite(LumenInstanceInfo? clip) =>
            clip is { Kind: LumenInstanceKind.Sprite } && _document!.CharacterName(clip.CharacterId) is { } name
                && _document.Root["sprites"]?[name] is JsonObject entry ? (clip, name, entry) : null;
        return sprite(resolveTimeline(clips));
    }

    private void timeline(LumenPlayer player, IReadOnlyList<LumenInstanceInfo> clips)
    {
        timelineNavigation(clips);
        if (timelineOf(clips) is not var (clip, name, sprite))
        {
            ImGui.TextWrapped("This instance is absent at the current parent frame. The timeline context has been kept.");
            if (ImGui.Button("Back to parent timeline"))
            {
                _timelineContext.Parent(); _timelineAssetId = null; _key = null; _length = null;
                openInstanceTimeline(_timelineContext.Path);
            }
            return;
        }
        var layers = sprite["layers"] as JsonArray ?? [];
        var frames = Math.Max(1, (int?)sprite["frames"] ?? 1);
        var labels = sprite["labels"] as JsonObject;

        // Header: the sprite, its length, a new label, a new layer.
        ImGui.TextUnformatted(name);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        var length = frames;
        if (ImGui.InputInt("frames", ref length, 1, 10))
            _length = length;
        if (ImGui.IsItemDeactivatedAfterEdit() && _length is { } wanted)
        {
            // Not shorter than its last key or label.
            var last = layers.OfType<JsonObject>().SelectMany(static layer => layer["keys"]!.AsArray()).Select(static key => (int)key!["frame"]!)
                .Concat(labels?.Select(static label => (int)label.Value!) ?? []).DefaultIfEmpty(0).Max();
            wanted = Math.Clamp(wanted, last + 1, ushort.MaxValue);
            edited(() => _document!.Edit(_ => sprite["frames"] = wanted));
            _length = null;
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.InputTextWithHint("##label", "label name", ref _newLabel, 48);
        ImGui.SameLine();
        if (ImGui.Button($"Label frame {clip.Frame}") && _newLabel.Length > 0)
        {
            var (label, at) = (_newLabel, clip.Frame);
            edited(() => _document!.Edit(_ => (sprite["labels"] as JsonObject ?? (JsonObject)(sprite["labels"] = new JsonObject()))[label] = at));
            _newLabel = "";
        }
        ImGui.SameLine();
        if (ImGui.Button("Add layer"))
            ImGui.OpenPopup("##add layer");
        if (ImGui.BeginPopup("##add layer"))
        {
            // Anything but the sprite itself (a sprite inside itself never ends).
            foreach (var group in new[] { "sprites", "shapes", "texts" })
                foreach (var entry in _document!.Root[group] as JsonObject ?? [])
                    if (entry.Key != name && ImGui.Selectable($"{entry.Key}  ({group[..^1]})"))
                    {
                        var (character, at) = (entry.Key, clip.Frame);
                        edited(() => _document.Edit(_ =>
                        {
                            if (sprite["layers"] is not JsonArray list)
                                sprite["layers"] = list = [];
                            // Above the others: a depth of its own.
                            var depth = list.OfType<JsonObject>().Select((layer, index) => (int?)layer["depth"] ?? index + 1).DefaultIfEmpty(0).Max() + 1;
                            list.Add((JsonNode)new JsonObject { ["character"] = character, ["name"] = character, ["depth"] = depth,
                                ["keys"] = new JsonArray(new JsonObject { ["frame"] = at }) });
                        }));
                    }
            ImGui.EndPopup();
        }
        if (sprite["scripts"] is JsonObject { Count: > 0 } frameScripts)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"{frameScripts.Count} frame script(s)");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(_document!.ReadOnly
                ? "Compiled frame scripts are present. They can control child timelines. Readable source is not available in this reference."
                : "Frame scripts can control child timelines. See Code for their source files.");
        }
        if (_key is { } chosen && chosen.Sprite == name)
        {
            ImGui.SameLine();
            if (ImGui.Button("Remove key") || (!ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Delete, false)))
                removeKey(layers, chosen);
        }

        ImGui.SameLine();
        if (ImGui.Button(_frameWidth > 0 ? "Fit" : "Fitted"))
            _frameWidth = 0;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show the whole timeline.\nCtrl+wheel over the rows: zoom at the pointer. Shift+wheel: scroll sideways.");

        ImGui.BeginChild("##timeline rows", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var available = ImGui.GetContentRegionAvail().X - NameColumn;
        // Fitted: the whole timeline in view, but a frame is never wider than a key is clear (a one-frame sprite is one frame).
        var step = _frameWidth > 0 ? _frameWidth : Math.Clamp(available / frames, 4f, 24f);
        var width = Math.Max(available, frames * step);
        if (ImGui.IsWindowHovered() && ImGui.GetIO().KeyCtrl && ImGui.GetIO().MouseWheel != 0)
        {
            var next = Math.Clamp(step * MathF.Pow(1.2f, ImGui.GetIO().MouseWheel), 2f, 64f);
            // The frame under the pointer stays under it.
            ImGui.SetScrollX(Math.Max(0, ImGui.GetScrollX() + (ImGui.GetIO().MousePos.X - origin.X - NameColumn) / step * (next - step)));
            _frameWidth = next;
        }
        // The names stay in view when the rows are scrolled sideways; the tracks are cut off at their edge.
        var names = origin.X + ImGui.GetScrollX();
        var trackMin = new Vector2(names + NameColumn, ImGui.GetWindowPos().Y);
        var trackMax = ImGui.GetWindowPos() + ImGui.GetWindowSize();
        float x(int frame) => origin.X + NameColumn + frame * step;
        int frameAt(float screenX) => Math.Clamp((int)MathF.Floor((screenX - origin.X - NameColumn) / step), 0, frames - 1);

        // Ruler: frame numbers only; label flags have their own track below it.
        ImGui.PushClipRect(trackMin, trackMax, true);
        draw.AddRectFilled(origin with { X = origin.X + NameColumn }, new(origin.X + NameColumn + frames * step, origin.Y + RowHeight), Ruler);
        var every = Math.Max(1, (int)MathF.Ceiling(40 / step / 5) * 5);
        for (var frame = 0; frame < frames; frame += every)
            draw.AddText(new(x(frame) + 2, origin.Y + 3), Muted, frame.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ImGui.SetCursorScreenPos(origin with { X = origin.X + NameColumn });
        ImGui.InvisibleButton("##ruler", new(width, RowHeight));
        if (ImGui.IsItemActive())
        {
            player.InspectGoto(clip.Path, frameAt(ImGui.GetIO().MousePos.X), clip.Playing);
            Paused = true;
        }
        var labelHeight = timelineLabels(player, clip.Path, labels, origin, width, step);
        ImGui.PopClipRect();
        var headerHeight = RowHeight + labelHeight;

        for (var index = 0; index < layers.Count; index++)
        {
            if (layers[index] is not JsonObject layer)
                continue;
            var top = origin.Y + headerHeight + RowHeight * index;
            var keys = layer["keys"]!.AsArray().OfType<JsonObject>().Select(static key => (int)key["frame"]!).Order().ToArray();
            var until = Math.Min((int?)layer["until"] ?? frames, frames);
            var depth = (int?)layer["depth"] ?? index + 1;
            var path = clip.Path.Length == 0 ? $"{LumenPlayer.TimelineDepthBase + depth}" : $"{clip.Path}/{LumenPlayer.TimelineDepthBase + depth}";
            draw.AddRectFilled(new(origin.X, top), new(origin.X + NameColumn + width, top + RowHeight), index % 2 == 0 ? Row : RowOdd);
            ImGui.PushClipRect(trackMin, trackMax, true);
            if (keys.Length > 0)
                draw.AddRectFilled(new(x(keys[0]), top + 7), new(x(until), top + RowHeight - 7), Life);
            foreach (var key in keys)
            {
                var centre = new Vector2(x(key) + step / 2, top + RowHeight / 2);
                var chosenKey = _key == (name, index, key);
                draw.AddQuadFilled(centre + new Vector2(0, -6), centre + new Vector2(5, 0), centre + new Vector2(0, 6), centre + new Vector2(-5, 0),
                    chosenKey ? KeySelected : KeyColour);
            }
            ImGui.PopClipRect();

            // The name selects the layer's clip; the x removes the layer.
            ImGui.SetCursorScreenPos(new(names + 2, top + 1));
            if (ImGui.SmallButton($"x##remove layer {index}"))
            {
                var at = index;
                edited(() => _document!.Edit(_ => layers.RemoveAt(at)));
                break;
            }
            ImGui.SameLine();
            var title = (string?)layer["name"] is { Length: > 0 } instance ? $"{instance}  ({(string?)layer["character"]})" : (string?)layer["character"] ?? "?";
            if (ImGui.Selectable($"{title}##layer {index}", _selected == path, ImGuiSelectableFlags.AllowDoubleClick, new(NameColumn - 34, RowHeight - 4)))
                selectClip(path);
            var child = clips.FirstOrDefault(each => each.Path == path && each.Kind == LumenInstanceKind.Sprite);
            if (child is not null && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Select: inspect this layer. Double-click: open its child timeline.");
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) openInstanceTimeline(path);
            }

            // The row: a click on a key selects it, a drag moves it, a double click on empty space makes one.
            ImGui.SetCursorScreenPos(new(origin.X + NameColumn, top));
            ImGui.InvisibleButton($"##row {index}", new(width, RowHeight));
            var under = frameAt(ImGui.GetIO().MousePos.X);
            if (ImGui.IsItemActivated())
            {
                selectClip(path);
                // The nearest key within a few pixels, so thin frames can still be grabbed.
                var near = keys.OrderBy(key => Math.Abs(x(key) + step / 2 - ImGui.GetIO().MousePos.X)).Cast<int?>().FirstOrDefault();
                _key = near is { } hit && Math.Abs(x(hit) + step / 2 - ImGui.GetIO().MousePos.X) <= Math.Max(step / 2, 6) ? (name, index, hit) : null;
                _keyDrag = false;
                player.InspectGoto(clip.Path, _key?.Frame ?? under, clip.Playing);
                Paused = true;
            }
            if (ImGui.IsItemActive() && _key is { } held && held.Layer == index && held.Sprite == name && under != held.Frame && !keys.Contains(under))
            {
                var continues = _keyDrag;
                _keyDrag = true;
                edited(() => _document!.Edit(_ => layer["keys"]!.AsArray().OfType<JsonObject>().First(key => (int)key["frame"]! == held.Frame)["frame"] = under, continues));
                _key = (name, index, under);
                player.InspectGoto(clip.Path, under, clip.Playing);
            }
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !keys.Contains(under))
            {
                edited(() => _document!.Edit(_ => MovieDocument.KeyAt(layer, under)));
                _key = (name, index, under);
            }
        }
        // The playhead, over the tracks.
        var head = x(clip.Frame) + step / 2;
        ImGui.PushClipRect(trackMin, trackMax, true);
        draw.AddLine(new(head, origin.Y), new(head, origin.Y + headerHeight + RowHeight * layers.Count), KeySelected, 2);
        ImGui.PopClipRect();
        ImGui.SetCursorScreenPos(new(origin.X, origin.Y + headerHeight + RowHeight * layers.Count));
        ImGui.Dummy(new(NameColumn + width, 4));
        ImGui.EndChild();
    }

    private void removeKey(JsonArray layers, (string Sprite, int Layer, int Frame) key)
    {
        if (key.Layer >= layers.Count || layers[key.Layer]?["keys"] is not JsonArray keys || keys.Count <= 1)
            return; // A layer keeps one key: without any it would be nowhere.
        edited(() => _document!.Edit(_ => keys.Remove(keys.OfType<JsonObject>().FirstOrDefault(each => (int)each["frame"]! == key.Frame))));
        _key = null;
    }
}
