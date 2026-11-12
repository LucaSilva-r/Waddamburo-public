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
    private static readonly uint KeyColour = color(.61f, .76f, .91f), KeySelected = color(1, .56f, .18f), Life = color(.2f, .225f, .26f),
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
        _timelineShown = true;
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
            if (ImGui.Button(_keys.Count > 1 ? $"Remove {_keys.Count} keys" : "Remove key"))
                deleteKeys(layers);
        }

        ImGui.SameLine();
        if (ImGui.Button(_frameWidth > 0 ? "Fit" : "Fitted"))
            _frameWidth = 0;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show the whole timeline.\nCtrl+wheel over the rows: zoom at the pointer. Shift+wheel: scroll sideways.");

        // So does a click on nothing in the timeline's header.
        if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsAnyItemHovered())
        {
            _keys.Clear();
            _key = null;
        }
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
        var marks = scriptMarks(sprite);
        var headerHeight = RowHeight + labelHeight;
        headerHeight += scriptTrack(name, marks, draw, names, origin.Y + headerHeight, x, step);
        ImGui.PopClipRect();

        if (_keysSprite != name)
        {
            _keys.Clear();
            _keysSprite = name;
        }
        // Rows: a layer, then (opened) one per channel its keys change.
        var rows = new List<(float Top, int Layer, string? Channel, int[] Frames)>();
        var stale = false; // The document changed under this loop: the rest is drawn next frame.
        var selectedLayer = -1;
        var rowTop = origin.Y + headerHeight;
        var mouse = ImGui.GetIO().MousePos;
        for (var index = 0; index < layers.Count && !stale; index++)
        {
            if (layers[index] is not JsonObject layer)
                continue;
            var layerKeys = layer["keys"]!.AsArray().OfType<JsonObject>().OrderBy(static key => (int)key["frame"]!).ToArray();
            var keys = layerKeys.Select(static key => (int)key["frame"]!).ToArray();
            var until = Math.Min((int?)layer["until"] ?? frames, frames);
            var depth = (int?)layer["depth"] ?? index + 1;
            var path = clip.Path.Length == 0 ? $"{LumenPlayer.TimelineDepthBase + depth}" : $"{clip.Path}/{LumenPlayer.TimelineDepthBase + depth}";
            if (path == _selected) selectedLayer = index;
            var open = _expanded.Contains((name, index));
            // A channel is listed when a key holds it; its keys are the ones that do.
            // A channel animates when two keys or more hold it; held by one, it is the clip's resting value: no key shown.
            var animated = Channels.Where(channel => layerKeys.Count(key => MovieDocument.Has(key, channel)) >= 2).ToArray();
            var channels = open ? animated : [];
            int[] marked = [.. layerKeys.Where(key => animated.Any(channel => MovieDocument.Has(key, channel))).Select(static key => (int)key["frame"]!)];
            for (var line = 0; line <= channels.Length && !stale; line++)
            {
                var channel = line == 0 ? null : channels[line - 1];
                var top = rowTop;
                rowTop += RowHeight;
                var shown = channel is null ? marked : [.. layerKeys.Where(key => MovieDocument.Has(key, channel)).Select(static key => (int)key["frame"]!)];
                rows.Add((top, index, channel, shown));
                var shade = channel is not null ? RowChannel : index % 2 == 0 ? Row : RowOdd;
                draw.AddRectFilled(new(origin.X, top), new(origin.X + NameColumn + width, top + RowHeight), shade);
                ImGui.PushClipRect(trackMin, trackMax, true);
                // The band: the frames the layer is on stage. The lines: where a value is on its way from one key to the next.
                var start = MovieDocument.From(layer);
                draw.AddRectFilled(new(x(start), top + 1), new(x(until), top + RowHeight - 1), channel is null ? Life : LifeChannel);
                foreach (var each in channel is null ? animated : [channel])
                {
                    var held = layerKeys.Where(key => MovieDocument.Has(key, each)).ToArray();
                    for (var at = 0; at + 1 < held.Length; at++)
                        if (MovieDocument.Ease(held[at]) != "hold" && channelValue(held[at], each).ToJsonString() != channelValue(held[at + 1], each).ToJsonString())
                            draw.AddLine(new(x((int)held[at]["frame"]!) + step / 2, top + RowHeight / 2), new(x((int)held[at + 1]["frame"]!) + step / 2, top + RowHeight / 2), KeyColour, 2);
                }
                if (channel is null)
                {
                    // Where a script of this timeline sends this clip to one of its own frames.
                    foreach (var mark in marks)
                        foreach (var jump in mark.Jumps.Where(jump => jump.Child == (string?)layer["name"]))
                        {
                            var at = new Vector2(x(mark.Frame) + step / 2, top + RowHeight / 2);
                            draw.AddTriangleFilled(at + new Vector2(-3, -4), at + new Vector2(4, 0), at + new Vector2(-3, 4), ScriptColour);
                            draw.AddText(at + new Vector2(7, -8), ScriptColour, jump.To);
                        }
                    // Its two ends can be dragged.
                    draw.AddRectFilled(new(x(start), top + 1), new(x(start) + 3, top + RowHeight - 1), LifeEdge);
                    draw.AddRectFilled(new(x(until) - 3, top + 1), new(x(until), top + RowHeight - 1), LifeEdge);
                }
                foreach (var key in shown)
                {
                    var centre = new Vector2(x(key) + step / 2, top + RowHeight / 2);
                    var size = channel is null ? 6f : 4.5f;
                    // A whole key lights up its channels too.
                    var chosenKey = _keys.Contains((index, key, channel)) || channel is not null && _keys.Contains((index, key, null));
                    draw.AddQuadFilled(centre + new Vector2(0, -size), centre + new Vector2(size - 1, 0), centre + new Vector2(0, size), centre + new Vector2(1 - size, 0),
                        chosenKey ? KeySelected : KeyColour);
                }
                ImGui.PopClipRect();

                draw.AddRectFilled(new(names, top), new(names + NameColumn, top + RowHeight), shade);
                if (channel is not null)
                    draw.AddText(new(names + 34, top + 3), Muted, ChannelNames[Array.IndexOf(Channels, channel)]);
                else
                {
                    // The arrow opens the layer's channels; the name selects its clip; right-click for the layer's menu.
                    ImGui.SetCursorScreenPos(new(names + 2, top + 2));
                    ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
                    ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(1, 0));
                    if (ImGui.ArrowButton($"##channels {index}", open ? ImGuiDir.Down : ImGuiDir.Right) && !_expanded.Remove((name, index)))
                        _expanded.Add((name, index));
                    ImGui.PopStyleVar();
                    ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show what its keys animate, a row each: position, scale, rotation, opacity, tint.");
                    ImGui.SetCursorScreenPos(new(names + 24, top + 1));
                    var character = (string?)layer["character"] ?? "?";
                    var title = (string?)layer["name"] is { Length: > 0 } instance && instance != character ? $"{instance}  ({character})" : character;
                    if (ImGui.Selectable($"{title}##layer {index}", _selected == path, ImGuiSelectableFlags.AllowDoubleClick, new(NameColumn - 30, RowHeight - 4)))
                        selectClip(path);
                    if (ImGui.BeginPopupContextItem($"##layer menu {index}"))
                    {
                        selectClip(path);
                        var at = index;
                        if (ImGui.MenuItem("Key on this frame", "double-click the row", false, !_document!.ReadOnly && !keys.Contains(clip.Frame)))
                        {
                            edited(() => _document.Edit(_ => MovieDocument.KeyAt(layer, clip.Frame)));
                            stale = true;
                        }
                        if (ImGui.MenuItem("Rename", "F2", false, !_document.ReadOnly))
                            _focusName = true;
                        if (ImGui.MenuItem("Duplicate layer", "", false, !_document.ReadOnly))
                            stale = attempt(() => _document.DuplicateLayer(name, at));
                        ImGui.Separator();
                        if (ImGui.MenuItem("Delete layer", "", false, !_document.ReadOnly))
                        {
                            edited(() => _document.Edit(_ => layers.RemoveAt(at)));
                            _keys.Clear();
                            stale = true;
                        }
                        ImGui.EndPopup();
                        if (stale) break;
                    }
                    var child = clips.FirstOrDefault(each => each.Path == path && each.Kind == LumenInstanceKind.Sprite);
                    if (child is not null && ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        openInstanceTimeline(path);
                }

                // The track: click a key to select it (Shift adds), drag to move the selected keys, drag on empty space
                // for a selection box, double-click empty space for a key.
                ImGui.SetCursorScreenPos(new(origin.X + NameColumn, top));
                ImGui.InvisibleButton($"##row {index} {channel}", new(width, RowHeight));
                var under = frameAt(mouse.X);
                var lifeStart = MovieDocument.From(layer);
                var onStart = channel is null && Math.Abs(mouse.X - x(lifeStart)) < 5;
                var onEnd = channel is null && Math.Abs(mouse.X - x(until)) < 5;
                if (ImGui.IsItemHovered() && (onStart || onEnd) && !_document!.ReadOnly)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
                    ImGui.SetTooltip(onStart ? $"Appears on frame {lifeStart}. Drag to change." : until >= frames ? "Stays to the end. Drag to remove it earlier." : $"Gone from frame {until}. Drag to change.");
                }
                if (ImGui.IsItemActivated() && (onStart || onEnd) && !_document!.ReadOnly)
                    _lifeDrag = (index, onStart);
                if (_lifeDrag is { } life && life.Layer == index && channel is null)
                {
                    if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _lifeDrag = null;
                    else
                    {
                        var to = Math.Clamp((int)MathF.Round((mouse.X - origin.X - NameColumn) / step), 0, frames);
                        var lastKey = keys.Max();
                        // Appears anywhere before it is gone; gone only after its last key.
                        int? edge = life.Start ? Math.Min(to, until - 1) : to >= frames ? null : Math.Max(to, Math.Max(lastKey, lifeStart) + 1);
                        var now = life.Start ? lifeStart : (int?)layer["until"];
                        if (edge != now && !(edge is { } end && !life.Start && end >= frames))
                        {
                            var at = index;
                            stale = attempt(() => { if (life.Start) _document!.SetFrom(name, at, edge); else _document!.SetUntil(name, at, edge); });
                        }
                    }
                }
                else if (ImGui.IsItemActivated())
                {
                    selectClip(path, keepKeys: true);
                    _keysClickedLast = true; // after selectClip: Delete now means the keys
                    selectedLayer = index;
                    // The nearest key within a few pixels, so thin frames can still be grabbed.
                    var near = shown.OrderBy(key => Math.Abs(x(key) + step / 2 - mouse.X)).Cast<int?>().FirstOrDefault();
                    var hit = near is { } found && Math.Abs(x(found) + step / 2 - mouse.X) <= Math.Max(step / 2, 6) ? near : null;
                    var shift = ImGui.GetIO().KeyShift;
                    _keyDrag = false;
                    _keyGrab = null;
                    if (hit is { } frame)
                    {
                        var entry = (index, frame, channel);
                        if (shift && !_keys.Add(entry)) _keys.Remove(entry);
                        else if (!shift && !_keys.Contains(entry)) { _keys.Clear(); _keys.Add(entry); }
                        _key = (name, index, frame);
                        _keyGrab = frame;
                    }
                    else
                    {
                        if (!shift) _keys.Clear();
                        _key = null;
                        _keyBox = mouse;
                    }
                    player.InspectGoto(clip.Path, hit ?? under, clip.Playing);
                    Paused = true;
                }
                if (ImGui.IsItemActive() && _keyGrab is { } grabbed && under != grabbed && !_document!.ReadOnly && moveKeys(layers, under - grabbed, frames))
                {
                    _keyGrab = under;
                    _key = _key is { } primary ? primary with { Frame = primary.Frame + under - grabbed } : null;
                    player.InspectGoto(clip.Path, under, clip.Playing);
                    stale = true;
                }
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !keys.Contains(under) && !_document!.ReadOnly)
                {
                    edited(() => _document.Edit(_ => MovieDocument.KeyAt(layer, under)));
                    _keys.Clear();
                    _keys.Add((index, under, null));
                    _key = (name, index, under);
                    stale = true;
                }
                if (ImGui.BeginPopupContextItem($"##keys menu {index} {channel}"))
                {
                    selectClip(path, keepKeys: true);
                    selectedLayer = index;
                    stale = keyMenu(layers, selectedLayer, clip.Frame);
                    if (channel is null && !_document!.ReadOnly)
                    {
                        // On stage or not: where the layer's band starts and ends.
                        var (at, here) = (index, under);
                        ImGui.Separator();
                        if (ImGui.MenuItem($"Appears on frame {here}", "", false, here < until))
                            stale |= attempt(() => _document.SetFrom(name, at, here));
                        if (ImGui.MenuItem($"Gone from frame {here}", "", false, here > Math.Max(keys.Max(), lifeStart) && here < frames))
                            stale |= attempt(() => _document.SetUntil(name, at, here));
                        if (ImGui.MenuItem("Stays to the end", "", false, layer["until"] is not null))
                            stale |= attempt(() => _document.SetUntil(name, at, null));
                    }
                    ImGui.EndPopup();
                }
            }
        }
        var bottom = origin.Y + headerHeight + RowHeight * Math.Max(rows.Count, layers.Count);
        // The selection box: keys of the rows it crosses, between its sides.
        if (_keyBox is { } boxStart)
        {
            var (min, max) = (Vector2.Min(boxStart, mouse), Vector2.Max(boxStart, mouse));
            if (Vector2.Distance(boxStart, mouse) > 4)
            {
                draw.AddRectFilled(min, max, color(.3f, .6f, 1, .15f));
                draw.AddRect(min, max, Outline);
                if (!ImGui.GetIO().KeyShift) _keys.Clear();
                foreach (var row in rows.Where(row => row.Top < max.Y && row.Top + RowHeight > min.Y))
                    foreach (var frame in row.Frames.Where(frame => x(frame) + step / 2 >= min.X && x(frame) + step / 2 <= max.X))
                        _keys.Add((row.Layer, frame, row.Channel));
            }
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _keyBox = null;
        }
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _keyGrab = null;
        // A click on nothing (under the rows) lets go of the keys.
        if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsAnyItemHovered())
        {
            _keys.Clear();
            _key = null;
        }
        if (!stale && ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) && !ImGui.GetIO().WantTextInput && !_document!.ReadOnly)
        {
            var control = ImGui.GetIO().KeyCtrl;
            if (control && ImGui.IsKeyPressed(ImGuiKey.C, false)) copyKeys(layers);
            else if (control && ImGui.IsKeyPressed(ImGuiKey.X, false)) { copyKeys(layers); deleteKeys(layers); }
            else if (control && ImGui.IsKeyPressed(ImGuiKey.V, false)) pasteKeys(layers, selectedLayer, clip.Frame);
        }
        if (!stale && _deleteKeys) deleteKeys(layers);
        // The playhead, over the tracks.
        var head = x(clip.Frame) + step / 2;
        ImGui.PushClipRect(trackMin, trackMax, true);
        draw.AddLine(new(head, origin.Y), new(head, bottom), KeySelected, 2);
        ImGui.PopClipRect();
        ImGui.SetCursorScreenPos(new(origin.X, bottom));
        ImGui.Dummy(new(NameColumn + width, 4));
        ImGui.EndChild();
    }

    // ------------------------------------------------------------------------------------------- keys: several at once
    // A selected key is a whole key (Channel null) or one channel of it. Internally a key is one pose: a channel is a view.
    private readonly HashSet<(int Layer, int Frame, string? Channel)> _keys = [];
    private string? _keysSprite;
    private readonly HashSet<(string Sprite, int Layer)> _expanded = [];
    private Vector2? _keyBox;
    private (int Layer, bool Start)? _lifeDrag; // An end of a layer's band being dragged.
    private static readonly uint LifeChannel = color(.15f, .17f, .2f), LifeEdge = color(.36f, .42f, .5f);
    private int? _keyGrab; // The frame the grabbed key is on, while keys are dragged.
    private sealed record CopiedKey(int Layer, int Frame, string? Channel, JsonNode Value);
    private readonly List<CopiedKey> _copiedKeys = [];
    private static readonly string[] Channels = MovieDocument.Channels;
    private static readonly string[] ChannelNames = ["X", "Y", "Scale X", "Scale Y", "Rotation", "Opacity", "Tint"];
    private static readonly uint RowChannel = color(.115f, .125f, .145f);

    private static JsonNode channelValue(JsonObject key, string channel)
    {
        if (channel != "tint") return JsonValue.Create(MovieDocument.Value(key, channel)!.Value);
        var tint = MovieDocument.Tint(key);
        return new JsonArray(tint.Red, tint.Green, tint.Blue);
    }

    private static void setChannel(JsonObject key, string channel, JsonNode value)
    {
        if (channel == "tint") MovieDocument.SetTint(key, MovieDocument.Number(value[0]), MovieDocument.Number(value[1]), MovieDocument.Number(value[2]));
        else MovieDocument.Set(key, channel, MovieDocument.Number(value));
    }

    private static JsonObject? keyOn(JsonArray layers, int layer, int frame) => layer >= 0 && layer < layers.Count
        ? layers[layer]?["keys"]?.AsArray().OfType<JsonObject>().FirstOrDefault(key => (int)key["frame"]! == frame) : null;

    // The selected keys move together by a number of frames: a whole key as it is, a channel's key by itself (out of
    // its key, into the one on the new frame). Refused when one would leave the timeline or land on what stays.
    private bool moveKeys(JsonArray layers, int delta, int frames)
    {
        var whole = _keys.Where(static key => key.Channel is null).Select(static key => (key.Layer, key.Frame)).ToHashSet();
        var single = _keys.Where(key => key.Channel is not null && !whole.Contains((key.Layer, key.Frame))).ToHashSet();
        foreach (var (layer, frame) in whole)
            if (frame + delta < 0 || frame + delta >= frames || keyOn(layers, layer, frame) is null
                || keyOn(layers, layer, frame + delta) is not null && !whole.Contains((layer, frame + delta)))
                return false;
        foreach (var (layer, frame, channel) in single)
            if (frame + delta < 0 || frame + delta >= frames || keyOn(layers, layer, frame) is not { } key || !MovieDocument.Has(key, channel!)
                || keyOn(layers, layer, frame + delta) is { } target && MovieDocument.Has(target, channel!) && !single.Contains((layer, frame + delta, channel))
                    && !whole.Contains((layer, frame + delta)))
                return false;
        var continues = _keyDrag;
        _keyDrag = true;
        edited(() => _document!.Edit(_ =>
        {
            // Lifted first, put down after: keys moving onto each other's frames do not meet.
            var lifted = single.Select(each => (each.Layer, each.Frame, Channel: each.Channel!, Value: channelValue(keyOn(layers, each.Layer, each.Frame)!, each.Channel!))).ToArray();
            foreach (var each in lifted)
                MovieDocument.RemoveChannel((JsonObject)layers[each.Layer]!, each.Frame, each.Channel);
            foreach (var key in whole.Select(each => keyOn(layers, each.Layer, each.Frame)!).ToArray())
                key["frame"] = (int)key["frame"]! + delta;
            foreach (var each in lifted)
                setChannel(MovieDocument.BareKey((JsonObject)layers[each.Layer]!, each.Frame + delta), each.Channel, each.Value);
        }, continues));
        var moved = _keys.Select(key => (key.Layer, key.Frame + delta, key.Channel)).ToArray();
        _keys.Clear();
        _keys.UnionWith(moved);
        return true;
    }

    private void copyKeys(JsonArray layers)
    {
        if (_keys.Count == 0) return;
        _copiedKeys.Clear();
        var (firstLayer, firstFrame) = (_keys.Min(static key => key.Layer), _keys.Min(static key => key.Frame));
        foreach (var (layer, frame, channel) in _keys)
            // A whole key covers its channels.
            if (keyOn(layers, layer, frame) is { } key && (channel is null || !_keys.Contains((layer, frame, null))))
                _copiedKeys.Add(new(layer - firstLayer, frame - firstFrame, channel, channel is null ? key.DeepClone() : channelValue(key, channel)));
        _notice = null;
    }

    // Onto the selected layer (and the ones after it, for keys copied from several), the first key on the playhead.
    private void pasteKeys(JsonArray layers, int layer, int frame)
    {
        if (_copiedKeys.Count == 0 || layer < 0)
        {
            if (_copiedKeys.Count > 0) _notice = "Select the layer to paste the keys on.";
            return;
        }
        var length = (int?)((JsonObject)layers.Parent!)["frames"] ?? 1;
        var pasted = new List<(int, int, string?)>();
        attempt(() => _document!.Edit(_ =>
        {
            foreach (var copied in _copiedKeys.OrderBy(static each => each.Channel is null ? 0 : 1))
            {
                var (to, at) = (layer + copied.Layer, frame + copied.Frame);
                if (to >= layers.Count || at >= length || layers[to] is not JsonObject target) continue;
                if (copied.Channel is null)
                {
                    var keys = target["keys"]!.AsArray();
                    if (keyOn(layers, to, at) is { } old) keys.Remove(old);
                    var key = (JsonObject)copied.Value.DeepClone();
                    key["frame"] = at;
                    keys.Insert(keys.OfType<JsonObject>().Count(each => (int)each["frame"]! < at), key);
                }
                else setChannel(MovieDocument.BareKey(target, at), copied.Channel, copied.Value.DeepClone());
                pasted.Add((to, at, copied.Channel));
            }
        }));
        _keys.Clear();
        _keys.UnionWith(pasted);
    }

    // A whole key is removed (a layer keeps one, and says so); a channel is taken out of its key.
    private void deleteKeys(JsonArray layers)
    {
        if (_keys.Count == 0) return;
        edited(() => _document!.Edit(_ =>
        {
            foreach (var (layer, frame, channel) in _keys.OrderByDescending(static key => key.Frame))
            {
                if (keyOn(layers, layer, frame) is not { } key || key.Parent is not JsonArray keys) continue;
                if (channel is null)
                {
                    // The key goes, but not what rests on it: values no other key holds move to the layer's first key,
                    // and the first key itself only loses what animates (the layer starts there).
                    var all = keys.OfType<JsonObject>().OrderBy(static each => (int)each["frame"]!).ToArray();
                    var first = ReferenceEquals(all[0], key);
                    foreach (var each in Channels.Where(each => MovieDocument.Has(key, each)).ToArray())
                    {
                        var alone = all.Count(other => MovieDocument.Has(other, each)) == 1;
                        if (alone && !first) setChannel(all[0], each, channelValue(key, each));
                        else if (!alone && first) MovieDocument.RemoveChannel((JsonObject)layers[layer]!, frame, each);
                    }
                    if (!first) keys.Remove(key);
                }
                // A channel's last key may go: the channel is then at its default, off the list.
                else if (!_keys.Contains((layer, frame, null)))
                    MovieDocument.RemoveChannel((JsonObject)layers[layer]!, frame, channel);
            }
        }));
        _keys.Clear();
        _key = null;
    }

    private bool keyMenu(JsonArray layers, int layer, int frame)
    {
        var changed = false;
        var editable = !_document!.ReadOnly;
        if (ImGui.MenuItem("Copy", "Ctrl+C", false, _keys.Count > 0)) copyKeys(layers);
        if (ImGui.MenuItem("Cut", "Ctrl+X", false, editable && _keys.Count > 0)) { copyKeys(layers); deleteKeys(layers); changed = true; }
        if (ImGui.MenuItem($"Paste on frame {frame}", "Ctrl+V", false, editable && _copiedKeys.Count > 0)) { pasteKeys(layers, layer, frame); changed = true; }
        ImGui.Separator();
        if (ImGui.MenuItem("Delete", "Del", false, editable && _keys.Count > 0)) { deleteKeys(layers); changed = true; }
        return changed;
    }
}
