using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.Editor;

/// <summary>
/// The movie inspector (docs/development/free-movies-plan.md, the editor's read side): a running movie's clips as
/// a tree, the selected clip's state and timeline, the stage with click selection, and the movie's callbacks to
/// call by hand. It looks and pokes; it changes no file, so it opens the game's own movies as well as packages.
/// </summary>
public sealed partial class MovieInspectorSession : IDisposable
{
    private readonly SdlApplication _application;
    private readonly ImGuiLayer _layer;
    private readonly RenderRectangle? _previousRegion;
    private readonly Func<LumenPlayer> _player;
    private readonly string _title;
    private readonly bool _gameData;
    private readonly MovieDocument? _document;
    private bool _gesture; // A drag in a field is going on: its edits are one undo step.
    private readonly Dictionary<string, string[]> _arguments = new(StringComparer.Ordinal);
    private readonly List<string> _log = [];
    // Clips opened in the tree beyond the first two levels (a big movie has thousands).
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);
    private string? _selected;
    private string _filter = "";
    private bool _step, _scrollToSelected, _disposed;
    private static readonly uint Outline = color(1, .56f, .18f), Muted = color(.52f, .56f, .61f), Text = color(.85f, .87f, .9f);

    /// <param name="application">The window.</param>
    /// <param name="player">The movie being shown (asked every frame: a reloaded package is a new player).</param>
    /// <param name="title">The movie's name.</param>
    /// <param name="gameData">The movie comes from the user's game data: shown as read-only reference.</param>
    /// <param name="select">A clip to select at once, by instance name (for screenshots).</param>
    /// <param name="document">A package's movie.json, to edit it; null shows the movie without editing.</param>
    public MovieInspectorSession(SdlApplication application, Func<LumenPlayer> player, string title, bool gameData, string? select = null,
        MovieDocument? document = null)
    {
        _document = document;
        _selected = select is null ? null : player().InspectDisplayList().FirstOrDefault(clip => clip.Name == select)?.Path;
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(player);
        _application = application;
        _player = player;
        _title = title;
        _gameData = gameData;
        _previousRegion = application.StageRegion;
        _layer = application.AttachImGui(draw);
        EditorTheme.Apply();
        _layer.CaptureKey = static key => key is ImGuiKey.Space or ImGuiKey.RightArrow;
        _layer.CaptureControlShortcuts = true;
        // A PNG or SVG dropped on the window becomes a texture of the package.
        if (document is not null)
            _layer.FileDropped = path => attempt(() => document.ImportTexture(path));
    }

    /// <summary>The scripts left out of the running movie because they do not compile (set by the host on each load).</summary>
    public IReadOnlyList<Waddamburo.Formats.Package.ActionScriptException> ScriptErrors { get; set; } = [];

    /// <summary>A package texture by its name in movie.json, for ImGui.Image thumbnails (set by the host).</summary>
    public Func<string, (nint Texture, int Width, int Height)?>? TextureOf { get; set; }

    // What the last refused operation said (a name taken, an asset in use...).
    private string? _notice;

    /// <summary>Runs a document operation; one it refuses becomes the notice in the toolbar instead of an error.</summary>
    private bool attempt(Action operation)
    {
        try
        {
            edited(operation);
            _notice = null;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _notice = exception.Message;
            return false;
        }
    }

    /// <summary>The movie is held on its current frame.</summary>
    public bool Paused { get; private set; }

    /// <summary>Whether the movie advances this tick: playing, or a single step was asked for.</summary>
    public bool TakeTick()
    {
        var advance = !Paused || _step;
        _step = false;
        return advance;
    }

    private static uint color(float r, float g, float b, float a = 1) => ImGui.ColorConvertFloat4ToU32(new(r, g, b, a));

    private static string number(double value) => (Math.Abs(value) < .0005 ? 0 : value).ToString("0.###", CultureInfo.InvariantCulture);

    private static bool panel(string name, EditorPanel rect, ImGuiWindowFlags extra = 0)
    {
        ImGui.SetNextWindowPos(rect.Position);
        ImGui.SetNextWindowSize(rect.Size);
        return ImGui.Begin(name, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoSavedSettings | extra);
    }

    private void selectClip(string? path)
    {
        _selected = path;
        _selectedAsset = null;
        _assetGesture = false;
    }

    private void draw()
    {
        var io = ImGui.GetIO();
        var size = io.DisplaySize;
        if (size.X < 32 || size.Y < 32)
            return;
        if (!io.WantTextInput)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Space, false))
                Paused = !Paused;
            if (ImGui.IsKeyPressed(ImGuiKey.RightArrow))
                (Paused, _step) = (true, true);
        }
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Z, false))
        {
            _assetGesture = _gesture = false;
            edited(() => { if (io.KeyShift) _document?.Redo(); else _document?.Undo(); });
        }
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Y, false))
        {
            _assetGesture = _gesture = false;
            edited(() => _document?.Redo());
        }
        var player = _player();
        var clips = player.InspectDisplayList();
        if (_selected is not null && clips.All(clip => clip.Path != _selected))
            _selected = null;
        var layout = EditorLayout.Calculate(size.X, size.Y, .24f, .26f, .3f);
        _application.StageRegion = EditorLayout.Preview(layout.Stage).Normalized(size.X, size.Y);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        toolbar(layout.Toolbar, player);
        tree(layout.Targets, clips);
        inspector(layout.Inspector, player, clips, clips.FirstOrDefault(clip => clip.Path == _selected));
        bottom(layout.Tracks, player, clips);
        stage(layout.Stage, player, clips);
        ImGui.PopStyleVar();
    }

    private void toolbar(EditorPanel rect, LumenPlayer player)
    {
        if (panel("##movie toolbar", rect, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.TextUnformatted(_title);
            if (_notice is not null || ScriptErrors.Count > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(new(1, .45f, .35f, 1), _notice ?? $"{ScriptErrors.Count} script error(s): {ScriptErrors[0].Message}");
            }
            ImGui.SameLine();
            ImGui.TextColored(new(.52f, .56f, .61f, 1), _gameData ? "game data: reference only, nothing here is saved"
                : _document is null ? "movie package" : "movie package: changes are written to movie.json as you make them");
            if (_document is not null)
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(!_document.CanUndo);
                if (ImGui.SmallButton("Undo"))
                    edited(_document.Undo);
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.BeginDisabled(!_document.CanRedo);
                if (ImGui.SmallButton("Redo"))
                    edited(_document.Redo);
                ImGui.EndDisabled();
            }
            if (ImGui.Button(Paused ? "Play" : "Pause"))
                Paused = !Paused;
            ImGui.SameLine();
            if (ImGui.Button("Step"))
                (Paused, _step) = (true, true);
            ImGui.SameLine();
            ImGui.TextColored(new(.52f, .56f, .61f, 1), "Space: play/pause   Right: one frame   click the stage or the tree to select");
            ImGui.SameLine();
            var frame = player.CurrentFrame;
            ImGui.SetNextItemWidth(Math.Max(120, rect.Width * .25f));
            if (player.FrameCount > 1 && ImGui.SliderInt("root frame", ref frame, 0, player.FrameCount - 1))
                player.Seek(frame);
        }
        ImGui.End();
    }

    private void tree(EditorPanel rect, IReadOnlyList<LumenInstanceInfo> clips)
    {
        if (panel("Clips", rect))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##filter", "filter by name", ref _filter, 64);
            ImGui.BeginChild("##clips");
            string? closed = null; // The collapsed clip whose children are being skipped.
            for (var index = 0; index < clips.Count; index++)
            {
                var clip = clips[index];
                if (_filter.Length > 0 && !clip.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (_filter.Length == 0)
                {
                    if (closed is not null && clip.Path.StartsWith(closed, StringComparison.Ordinal))
                        continue;
                    closed = null;
                }
                var parent = index + 1 < clips.Count && clips[index + 1].Level > clip.Level;
                var open = clip.Level < 2 || _open.Contains(clip.Path);
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
                    if (parent && clip.Level >= 2)
                    {
                        if (ImGui.SmallButton($"{(open ? "-" : "+")}##open{clip.Path}") && !_open.Remove(clip.Path))
                            _open.Add(clip.Path);
                        ImGui.SameLine();
                    }
                    if (parent && !open)
                        closed = clip.Path + "/";
                }
                if (ImGui.Selectable($"{name}{state}##{clip.Path}", clip.Path == _selected))
                    selectClip(clip.Path);
                if (clip.Path == _selected && _scrollToSelected)
                {
                    ImGui.SetScrollHereY();
                    _scrollToSelected = false;
                }
                if (_filter.Length == 0)
                    ImGui.Unindent(clip.Level * 12f + .01f);
                ImGui.PopStyleColor();
            }
            ImGui.EndChild();
        }
        ImGui.End();
    }

    // A change to the document reloads the movie: every clip goes back to the frame it was on, held there, so the
    // stage shows the same moment with the change in it. (What scripts had set is gone: the movie starts over.)
    private void edited(Action change)
    {
        var frames = _player().InspectDisplayList().Where(static clip => clip.Kind == LumenInstanceKind.Sprite)
            .Select(static clip => (clip.Path, clip.Frame, clip.Playing)).ToArray();
        change();
        var fresh = _player();
        foreach (var (path, frame, playing) in frames)
            fresh.InspectGoto(path, frame, playing);
        Paused = true;
    }

    // The layer of movie.json a clip on stage comes from: its parent's sprite, by depth and character.
    private (System.Text.Json.Nodes.JsonObject Layer, int Frame)? layerOf(IReadOnlyList<LumenInstanceInfo> clips, LumenInstanceInfo clip)
    {
        var cut = clip.Path.LastIndexOf('/');
        var parent = clips.FirstOrDefault(each => each.Path == (cut < 0 ? "" : clip.Path[..cut]));
        if (_document is null || clip.Level == 0 || parent is null
            || _document.CharacterName(parent.CharacterId) is not { } sprite || _document.CharacterName(clip.CharacterId) is not { } character
            || _document.Layer(sprite, clip.Depth - LumenPlayer.TimelineDepthBase, character) is not { } layer)
            return null;
        return (layer, parent.Frame);
    }

    private void layerFields(System.Text.Json.Nodes.JsonObject layer, int frame)
    {
        var keys = layer["keys"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>().OrderBy(static key => (int)key["frame"]!).ToList();
        var onKey = keys.Any(key => (int)key["frame"]! == frame);
        ImGui.SeparatorText(onKey ? $"key on frame {frame}" : $"frame {frame}: a change makes a key here");
        // The values shown are the layer's on this frame, between its keys.
        var probe = (System.Text.Json.Nodes.JsonObject)layer.DeepClone();
        var current = MovieDocument.KeyAt(probe, frame);
        foreach (var property in MovieDocument.Properties)
        {
            var value = (float)(MovieDocument.Value(current, property) ?? 0);
            ImGui.SetNextItemWidth(-90);
            var speed = property is "x" or "y" or "rotation" ? 1f : .01f;
            if (ImGui.DragFloat(property, ref value, speed, 0, 0, "%.3g"))
            {
                var continues = _gesture;
                _gesture = true;
                edited(() => _document!.Edit(_ => MovieDocument.Set(MovieDocument.KeyAt(layer, frame), property, value), continues));
            }
            if (ImGui.IsItemDeactivated())
                _gesture = false;
        }
        var name = (string?)layer["name"] ?? "";
        ImGui.SetNextItemWidth(-90);
        if (ImGui.InputText("name", ref name, 64, ImGuiInputTextFlags.EnterReturnsTrue))
            edited(() => _document!.Edit(_ =>
            {
                if (name.Length == 0) layer.Remove("name"); else layer["name"] = name;
            }));
        if (onKey && keys.Count > 1 && ImGui.Button("Remove this key"))
            edited(() => _document!.Edit(_ => layer["keys"]!.AsArray().Remove(keys.First(key => (int)key["frame"]! == frame))));
        ImGui.TextColored(new(.52f, .56f, .61f, 1), $"keys on frames {string.Join(", ", keys.Select(static key => (int)key["frame"]!))}");
    }

    private void inspector(EditorPanel rect, LumenPlayer player, IReadOnlyList<LumenInstanceInfo> clips, LumenInstanceInfo? clip)
    {
        if (panel("Inspector", rect))
        {
            if (_selectedAsset is not null)
                assetInspector();
            else if (clip is null)
                ImGui.TextColored(new(.52f, .56f, .61f, 1), "Select a clip or asset.");
            else
            {
                void row(string label, string value)
                {
                    ImGui.TextColored(new(.52f, .56f, .61f, 1), label);
                    ImGui.SameLine(110);
                    ImGui.TextUnformatted(value);
                }
                var matrix = clip.Transform;
                row("name", clip.Name.Length > 0 ? clip.Name : "(none)");
                row("character", $"{clip.CharacterId} ({clip.Kind.ToString().ToLowerInvariant()})");
                if (clip.ClassName is not null)
                    row("export", clip.ClassName);
                row("depth", clip.Depth.ToString(CultureInfo.InvariantCulture));
                row("position", $"{number(matrix.X)}, {number(matrix.Y)}");
                row("scale", $"{number(MathF.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12))}, {number(MathF.Sqrt(matrix.M21 * matrix.M21 + matrix.M22 * matrix.M22))}");
                row("rotation", $"{number(MathF.Atan2(matrix.M12, matrix.M11) * 180 / MathF.PI)}°");
                row("colour x", $"{number(clip.Multiply.Red)}, {number(clip.Multiply.Green)}, {number(clip.Multiply.Blue)}, alpha {number(clip.Multiply.Alpha)}");
                row("colour +", $"{number(clip.Add.Red)}, {number(clip.Add.Green)}, {number(clip.Add.Blue)}, {number(clip.Add.Alpha)}");
                row("blend", clip.BlendMode switch { 0 or 1 => "normal", 3 => "multiply", 4 => "screen", 8 => "add", var other => other.ToString(CultureInfo.InvariantCulture) });
                row("visible", clip.Visible ? "yes" : "no (its script hid it)");
                if (player.TryInspectBounds(clip.Path, out var box))
                    row("on stage", $"{number(box.X)}, {number(box.Y)}  {number(box.Width)} x {number(box.Height)}");
                var hidden = clip.HiddenByHost;
                if (ImGui.Checkbox("hide", ref hidden))
                    player.InspectSetHidden(clip.Path, hidden);
                if (layerOf(clips, clip) is { } source)
                    layerFields(source.Layer, source.Frame);
                else if (_document is not null && clip.Level > 0)
                    ImGui.TextColored(new(.52f, .56f, .61f, 1), "Made by a script: nothing of it is in movie.json.");

                if (clip is { Kind: LumenInstanceKind.Sprite, FrameCount: > 1 })
                {
                    ImGui.SeparatorText("timeline");
                    var frame = clip.Frame;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.SliderInt("##frame", ref frame, 0, clip.FrameCount - 1))
                        player.InspectGoto(clip.Path, frame, clip.Playing);
                    if (ImGui.Button(clip.Playing ? "Stop" : "Play"))
                        player.InspectSetPlaying(clip.Path, !clip.Playing);
                    foreach (var (label, at) in player.InspectLabels(clip.Path))
                    {
                        ImGui.SameLine();
                        if (ImGui.GetContentRegionAvail().X < ImGui.CalcTextSize(label).X + 20)
                            ImGui.NewLine();
                        if (ImGui.SmallButton($"{label}##{at}"))
                            player.InspectGoto(clip.Path, at, play: true);
                    }
                }
                var variables = player.InspectVariables(clip.Path);
                if (variables.Count > 0)
                {
                    ImGui.SeparatorText("script variables");
                    foreach (var (name, value) in variables)
                        row(name, value);
                }
            }
        }
        ImGui.End();
    }

    // The host side: every callback the movie registered, with a box per parameter.
    private void bottom(EditorPanel rect, LumenPlayer player, IReadOnlyList<LumenInstanceInfo> clips)
    {
        if (panel("##movie bottom", rect, ImGuiWindowFlags.NoTitleBar))
        {
            if (ImGui.BeginTabBar("##bottom tabs"))
            {
                if (_document is not null && ImGui.BeginTabItem("Timeline"))
                {
                    timeline(player, clips);
                    ImGui.EndTabItem();
                }
                if (_document is not null && ImGui.BeginTabItem("Assets"))
                {
                    assets();
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Callbacks"))
                {
                    host(new(rect.X, rect.Y, rect.Width, rect.Height), player);
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }
        ImGui.End();
    }

    private void host(EditorPanel rect, LumenPlayer player)
    {
        {
            var callbacks = player.Callbacks;
            if (callbacks.IsEmpty)
                ImGui.TextColored(new(.52f, .56f, .61f, 1), "This movie registers no callbacks.");
            ImGui.BeginChild("##callbacks", new(rect.Width * .62f, 0));
            foreach (var callback in callbacks)
            {
                if (!_arguments.TryGetValue(callback.Name, out var values) || values.Length != callback.Parameters.Length)
                    _arguments[callback.Name] = values = [.. callback.Parameters.Select(static _ => "")];
                if (ImGui.Button($"{callback.Name}##call"))
                {
                    var arguments = values.Select(parse).ToArray();
                    var text = player.TryInvokeCallback(callback.Name, arguments, out var result)
                        ? $"{callback.Name}({string.Join(", ", values)}) -> {show(result)}"
                        : $"{callback.Name}: the call failed";
                    _log.Insert(0, text);
                    if (_log.Count > 100)
                        _log.RemoveAt(_log.Count - 1);
                }
                for (var index = 0; index < values.Length; index++)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(110);
                    ImGui.InputTextWithHint($"##{callback.Name}{index}", callback.Parameters[index], ref values[index], 128);
                }
            }
            ImGui.EndChild();
            ImGui.SameLine();
            ImGui.BeginChild("##log");
            ImGui.TextColored(new(.52f, .56f, .61f, 1), "numbers, true / false, null, anything else is text");
            foreach (var line in _log)
                ImGui.TextUnformatted(line);
            ImGui.EndChild();
        }
    }

    private static LumenHostValue parse(string text) => text.Trim() switch
    {
        "" or "undefined" => LumenHostValue.Undefined,
        "null" => LumenHostValue.Null,
        "true" => LumenHostValue.FromBoolean(true),
        "false" => LumenHostValue.FromBoolean(false),
        var value when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)
            => LumenHostValue.FromNumber(parsed),
        var value => LumenHostValue.FromString(value),
    };

    private static string show(LumenHostValue value) => value.Kind switch
    {
        LumenHostValueKind.Number => number(value.AsNumber()),
        LumenHostValueKind.Text => $"\"{value.AsString()}\"",
        LumenHostValueKind.Boolean => value.AsBoolean() ? "true" : "false",
        var kind => kind.ToString().ToLowerInvariant(),
    };

    // Over the stage: the selected clip's outline, and a click selects the smallest clip under the pointer.
    // Over the stage: the selected clip's outline; a click selects the smallest clip under the pointer, and a drag
    // that starts on the selected clip moves it (a key on the current frame of its layer).
    private void stage(EditorPanel rect, LumenPlayer player, IReadOnlyList<LumenInstanceInfo> clips)
    {
        var view = EffectViewport.Fit(EditorLayout.Preview(rect));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (panel("##movie stage", rect, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBringToFrontOnFocus))
        {
            var draw = ImGui.GetWindowDrawList();
            draw.AddRect(view.Rectangle.Position, view.Rectangle.Position + view.Rectangle.Size, Muted);
            var mouse = ImGui.GetIO().MousePos;
            var point = view.ToStage(mouse);
            var selected = clips.FirstOrDefault(clip => clip.Path == _selected);
            var hasBox = player.TryInspectBounds(_selected ?? "?", out var box);
            if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && view.Contains(mouse))
            {
                var onSelected = hasBox && point.X >= box.X && point.Y >= box.Y && point.X <= box.X + box.Width && point.Y <= box.Y + box.Height;
                if (onSelected && selected is not null && layerOf(clips, selected) is not null)
                    _move = new(point, selected.Transform.X, selected.Transform.Y, false);
                else
                    pick(player, point);
            }
            if (_move is { } move)
            {
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && selected is not null && layerOf(clips, selected) is { } source
                    && player.TryInspectParentTransform(selected.Path, out var parent))
                {
                    var delta = point - move.Start;
                    if (move.Moved || delta.Length() * view.Rectangle.Width / 1280 > 3)
                    {
                        // The pointer's move, taken into the space the clip is placed in.
                        var determinant = parent.M11 * parent.M22 - parent.M12 * parent.M21;
                        if (MathF.Abs(determinant) > 1e-6f)
                        {
                            var x = move.X + (delta.X * parent.M22 - delta.Y * parent.M21) / determinant;
                            var y = move.Y + (delta.Y * parent.M11 - delta.X * parent.M12) / determinant;
                            var continues = move.Moved;
                            _move = move with { Moved = true };
                            edited(() => _document!.Edit(_ =>
                            {
                                var key = MovieDocument.KeyAt(source.Layer, source.Frame);
                                MovieDocument.Set(key, "x", MathF.Round(x));
                                MovieDocument.Set(key, "y", MathF.Round(y));
                            }, continues));
                        }
                    }
                }
                else
                {
                    if (!move.Moved)
                        pick(player, move.Start); // A plain click on the selected clip reaches what is inside it.
                    _move = null;
                }
            }
            if (_selected is not null && player.TryInspectBounds(_selected, out box))
                draw.AddRect(view.ToScreen(new(box.X, box.Y)), view.ToScreen(new(box.X + box.Width, box.Y + box.Height)), Outline, 0, ImDrawFlags.None, 2);
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    private sealed record StageMove(Vector2 Start, float X, float Y, bool Moved);
    private StageMove? _move;

    private void pick(LumenPlayer player, Vector2 point)
    {
        selectClip(player.InspectPick(point.X, point.Y));
        _scrollToSelected = _selected is not null;
        // Its parents open in the tree so it can be seen there.
        for (var path = _selected; path?.LastIndexOf('/') is > 0 and var cut; path = path[..cut])
            _open.Add(path[..cut]);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _layer.Dispose();
        _application.StageRegion = _previousRegion;
        _disposed = true;
    }
}
