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
    public Action<bool>? SetScriptsEnabled { get; set; }
    private bool _scriptsEnabled = true;
    private bool _gesture; // A drag in a field is going on: its edits are one undo step.
    private readonly Dictionary<string, string[]> _arguments = new(StringComparer.Ordinal);
    private readonly List<string> _log = [];
    // Clips opened in the tree beyond the first two levels (a big movie has thousands).
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);
    private string? _selected;
    private string _filter = "";
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private string? _hierarchyDrag;
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
        if (document is { ReadOnly: false })
            _layer.FileDropped = path => attempt(() => document.ImportTexture(path));
    }

    /// <summary>The scripts left out of the running movie because they do not compile (set by the host on each load).</summary>
    public IReadOnlyList<Waddamburo.Formats.Package.ActionScriptException> ScriptErrors { get; set; } = [];

    /// <summary>A package texture by its name in movie.json, for ImGui.Image thumbnails (set by the host).</summary>
    public Func<string, (nint Texture, int Width, int Height)?>? TextureOf { get; set; }

    /// <summary>Shows one sprite of the movie alone on stage, by its name in the document; null: the whole movie (set by the host).</summary>
    public Action<string?>? ShowSprite { get; set; }

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
        _selection.Clear();
        if (path is not null) _selection.Add(path);
        _selectionAnchor = path;
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
        _selection.RemoveWhere(path => clips.All(clip => clip.Path != path));
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
            breadcrumb();
            if (SetScriptsEnabled is not null)
            {
                if (ImGui.Checkbox("Run scripts", ref _scriptsEnabled))
                {
                    SetScriptsEnabled(_scriptsEnabled);
                    ShowSprite?.Invoke(_shown);
                    Paused = true;
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Restarts the preview. Disable for timeline-only art editing; script visibility changes and callbacks will not run.");
                ImGui.SameLine();
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
        ImGui.BeginDisabled(_document!.ReadOnly);
        var keys = layer["keys"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>().OrderBy(static key => (int)key["frame"]!).ToList();
        var onKey = keys.Any(key => (int)key["frame"]! == frame);
        ImGui.SeparatorText(onKey ? $"key on frame {frame}" : $"frame {frame}: a change makes a key here");
        // The values shown are the layer's on this frame, between its keys.
        var probe = (System.Text.Json.Nodes.JsonObject)layer.DeepClone();
        var current = MovieDocument.KeyAt(probe, frame);
        void vector(string label, string x, string y, float speed)
        {
            var value = new Vector2((float)MovieDocument.Value(current, x)!, (float)MovieDocument.Value(current, y)!);
            ImGui.SetNextItemWidth(-90);
            if (ImGui.DragFloat2(label, ref value, speed, 0, 0, "%.3g"))
            {
                var continues = _gesture && !ImGui.IsItemActivated();
                _gesture = true;
                edited(() => _document.Edit(_ =>
                {
                    var key = MovieDocument.KeyAt(layer, frame);
                    MovieDocument.Set(key, x, value.X);
                    MovieDocument.Set(key, y, value.Y);
                }, continues));
            }
            if (ImGui.IsItemDeactivated()) _gesture = false;
        }
        vector("Position", "x", "y", 1);
        vector("Scale", "scaleX", "scaleY", .01f);
        vector("Angle / alpha", "rotation", "alpha", .01f);
        var pivot = layer["editorPivot"] as System.Text.Json.Nodes.JsonArray;
        var pivotValue = pivot is { Count: 2 } ? new Vector2((float)pivot[0]!, (float)pivot[1]!) : new Vector2(.5f);
        if (ImGui.InputFloat2("Transform centre (0–1)", ref pivotValue, "%.2f"))
            attempt(() => _document.Edit(_ => layer["editorPivot"] = new System.Text.Json.Nodes.JsonArray(pivotValue.X, pivotValue.Y)));
        appearanceFields(layer, current, frame);
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
        ImGui.EndDisabled();
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
                if (_selection.Count > 1) ImGui.TextDisabled($"{_selection.Count} selected — inspecting the active object");
                void row(string label, string value)
                {
                    ImGui.TextColored(new(.52f, .56f, .61f, 1), label);
                    ImGui.SameLine(110);
                    ImGui.TextUnformatted(value);
                }
                if (ImGui.BeginTabBar("##inspector tabs"))
                {
                    if (ImGui.BeginTabItem("Properties"))
                    {
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
                    ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem("Info"))
                    {
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
                        ImGui.EndTabItem();
                    }
                    ImGui.EndTabBar();
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
                if (_document is not null && ShowSprite is not null && ImGui.BeginTabItem("Library"))
                {
                    library();
                    ImGui.EndTabItem();
                }
                if (_document is { ReadOnly: false } && ImGui.BeginTabItem("Code"))
                {
                    code();
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
            stageTools();
            var overTools = ImGui.IsAnyItemHovered() || ImGui.IsAnyItemActive();
            var draw = ImGui.GetWindowDrawList();
            draw.AddRect(view.Rectangle.Position, view.Rectangle.Position + view.Rectangle.Size, Muted);
            var mouse = ImGui.GetIO().MousePos;
            var point = view.ToStage(mouse);
            var selected = clips.FirstOrDefault(clip => clip.Path == _selected);
            var hasBox = selectionBounds(player, out var box);
            _handlePivot = new Vector2(.5f);
            if (_selection.Count == 1 && selected is not null && layerOf(clips, selected) is { } pivotSource
                && pivotSource.Layer["editorPivot"] is System.Text.Json.Nodes.JsonArray { Count: 2 } pivotData)
                _handlePivot = new((float)pivotData[0]!, (float)pivotData[1]!);
            var centre = hasBox ? view.ToScreen(new(box.X + box.Width * _handlePivot.X, box.Y + box.Height * _handlePivot.Y)) : Vector2.Zero;
            var handle = hasBox ? stageHandles(draw, view, box.X, box.Y, box.Width, box.Height, mouse) : -1;
            if (!overTools && ImGui.IsWindowHovered() && ImGui.GetIO().KeyShift && handle < 0
                && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && view.Contains(mouse))
                _selectionBox = point;
            selectionBox(player, clips, view, point);
            // Defer click-through until release: pressing selected bounds must never switch the drag target.
            if (_selectionBox is null && (!ImGui.GetIO().KeyShift || handle >= 0) && !overTools && ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && view.Contains(mouse))
            {
                var onSelected = hasBox && point.X >= box.X && point.Y >= box.Y && point.X <= box.X + box.Width && point.Y <= box.Y + box.Height;
                if ((handle >= 0 || onSelected && _stageTool == 0) && selected is not null && _document is { ReadOnly: false } && layerOf(clips, selected) is { } startSource)
                {
                    var key = MovieDocument.KeyAt((System.Text.Json.Nodes.JsonObject)startSource.Layer.DeepClone(), startSource.Frame);
                    _lockedAxis = 0;
                    captureGroup(player, clips);
                    _move = new(point, selected.Transform.X, selected.Transform.Y, false,
                        (float)MovieDocument.Value(key, "rotation")!, (float)MovieDocument.Value(key, "scaleX")!, (float)MovieDocument.Value(key, "scaleY")!, handle, view.ToStage(centre), new(box.X, box.Y), new(box.X + box.Width, box.Y + box.Height));
                }
                else
                    pick(player, point);
            }
            if (_move is { } move)
            {
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && selected is not null && layerOf(clips, selected) is { } source
                    && player.TryInspectParentTransform(selected.Path, out var parent))
                {
                    var delta = point - move.Start;
                    if (_stageTool == 0 && !ImGui.GetIO().KeyShift)
                    {
                        _lockedAxis = 0;
                        if (move.Handle == 1) delta.Y = 0;
                        else if (move.Handle == 2) delta.X = 0;
                    }
                    if (move.Moved || delta.Length() * view.Rectangle.Width / 1280 > 3)
                    {
                        // The pointer's move, taken into the space the clip is placed in.
                        var determinant = parent.M11 * parent.M22 - parent.M12 * parent.M21;
                        if (MathF.Abs(determinant) > 1e-6f)
                        {
                            var x = move.X + (delta.X * parent.M22 - delta.Y * parent.M21) / determinant;
                            var y = move.Y + (delta.Y * parent.M11 - delta.X * parent.M12) / determinant;
                            if (_stageTool == 0 && ImGui.GetIO().KeyShift)
                            {
                                if (_lockedAxis == 0 && Vector2.Distance(point, move.Start) > .001f)
                                {
                                    var movement = EffectGizmo.ParentVector(parent, point - move.Start);
                                    _lockedAxis = Math.Abs(movement.X) >= Math.Abs(movement.Y) ? 1 : 2;
                                }
                                if (_lockedAxis == 1) y = move.Y;
                                else if (_lockedAxis == 2) x = move.X;
                            }
                            var anchor = move.Pivot;
                            if (_stageTool == 2 && !ImGui.GetIO().KeyAlt)
                                anchor = move.Handle switch
                                {
                                    3 => move.Max,
                                    4 => new(move.Pivot.X, move.Max.Y),
                                    5 => new(move.Min.X, move.Max.Y),
                                    6 => new(move.Min.X, move.Pivot.Y),
                                    7 => move.Min,
                                    8 => new(move.Pivot.X, move.Min.Y),
                                    9 => new(move.Max.X, move.Min.Y),
                                    10 => new(move.Max.X, move.Pivot.Y),
                                    _ => move.Pivot,
                                };
                            var continues = move.Moved;
                            _move = move with { Moved = true };
                            if (_selection.Count > 1)
                            {
                                if (_stageTool == 0 && ImGui.GetIO().KeyShift)
                                {
                                    if (_lockedAxis == 0) _lockedAxis = Math.Abs(delta.X) >= Math.Abs(delta.Y) ? 1 : 2;
                                    if (_lockedAxis == 1) delta.Y = 0; else delta.X = 0;
                                }
                                transformGroup(move, point, delta, anchor, continues);
                            }
                            else edited(() => _document!.Edit(_ =>
                            {
                                var key = MovieDocument.KeyAt(source.Layer, source.Frame);
                                if (_stageTool == 0)
                                {
                                    MovieDocument.Set(key, "x", _lockedAxis == 2 ? move.X : MathF.Round(x));
                                    MovieDocument.Set(key, "y", _lockedAxis == 1 ? move.Y : MathF.Round(y));
                                }
                                else if (_stageTool == 1)
                                {
                                    var angle = move.Angle + (float)EffectGizmo.Rotation(move.Start - move.Pivot, point - move.Pivot);
                                    if (ImGui.GetIO().KeyShift) angle = MathF.Round(angle / 15) * 15;
                                    MovieDocument.Set(key, "rotation", angle);
                                }
                                else
                                {
                                    var start = move.Start - anchor;
                                    var now = point - anchor;
                                    var sx = Math.Abs(start.X) > .001f ? now.X / start.X : 1;
                                    var sy = Math.Abs(start.Y) > .001f ? now.Y / start.Y : 1;
                                    if (move.Handle is 4 or 8) sx = 1;
                                    if (move.Handle is 6 or 10) sy = 1;
                                    if (move.Handle == 0) sx = sy = MathF.Exp(delta.X / 150);
                                    else if (ImGui.GetIO().KeyShift)
                                        sx = sy = Math.Abs(sx - 1) > Math.Abs(sy - 1) ? sx : sy;
                                    MovieDocument.Set(key, "scaleX", move.ScaleX * sx);
                                    MovieDocument.Set(key, "scaleY", move.ScaleY * sy);
                                }
                                if (_stageTool != 0 && Math.Abs(move.ScaleX * move.ScaleY) > 1e-6f)
                                {
                                    var pivotParent = EffectGizmo.ParentVector(parent, anchor - new Vector2(parent.X, parent.Y));
                                    var offset = pivotParent - new Vector2(move.X, move.Y);
                                    var radians = move.Angle * MathF.PI / 180;
                                    var local = new Vector2((offset.X * MathF.Cos(radians) + offset.Y * MathF.Sin(radians)) / move.ScaleX,
                                        (-offset.X * MathF.Sin(radians) + offset.Y * MathF.Cos(radians)) / move.ScaleY);
                                    var nextAngle = (float)MovieDocument.Value(key, "rotation")! * MathF.PI / 180;
                                    var scaled = local * new Vector2((float)MovieDocument.Value(key, "scaleX")!, (float)MovieDocument.Value(key, "scaleY")!);
                                    var rotated = new Vector2(scaled.X * MathF.Cos(nextAngle) - scaled.Y * MathF.Sin(nextAngle), scaled.X * MathF.Sin(nextAngle) + scaled.Y * MathF.Cos(nextAngle));
                                    MovieDocument.Set(key, "x", pivotParent.X - rotated.X);
                                    MovieDocument.Set(key, "y", pivotParent.Y - rotated.Y);
                                }
                            }, continues));
                        }
                    }
                }
                else
                {
                    if (!move.Moved && move.Handle < 0)
                        pick(player, move.Start); // A plain click on the selected clip reaches what is inside it.
                    _move = null;
                }
            }
            if (selectionBounds(player, out box))
                draw.AddRect(view.ToScreen(new(box.X, box.Y)), view.ToScreen(new(box.X + box.Width, box.Y + box.Height)), Outline, 0, ImDrawFlags.None, 2);
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    private Vector2 _handlePivot = new(.5f);
    private int _lockedAxis;
    private int _stageTool;
    private void stageTools()
    {
        if (ImGui.IsWindowHovered() && !ImGui.GetIO().WantTextInput && _move is null)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.W)) _stageTool = 0;
            if (ImGui.IsKeyPressed(ImGuiKey.E)) _stageTool = 1;
            if (ImGui.IsKeyPressed(ImGuiKey.R)) _stageTool = 2;
        }
        ImGui.SetCursorPos(new(8, 8));
        ImGui.BeginDisabled(_move is not null);
        void tool(int mode, string label)
        {
            var start = ImGui.GetCursorScreenPos();
            var active = _stageTool == mode;
            if (active) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.28f, .40f, .54f, 1));
            if (ImGui.Button("##tool" + mode, new(30, 26))) _stageTool = mode;
            if (active) ImGui.PopStyleColor();
            var draw = ImGui.GetWindowDrawList();
            var centre = start + new Vector2(15, 13);
            if (mode == 0)
            {
                draw.AddLine(centre - new Vector2(8, 0), centre + new Vector2(8, 0), Text, 2);
                draw.AddLine(centre - new Vector2(0, 8), centre + new Vector2(0, 8), Text, 2);
            }
            else if (mode == 1) draw.AddCircle(centre, 8, Text, 20, 2);
            else
            {
                draw.AddRect(centre - new Vector2(7), centre + new Vector2(7), Text, 0, ImDrawFlags.None, 2);
                draw.AddRectFilled(centre + new Vector2(4), centre + new Vector2(9), Text);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(label);
        }
        tool(0, "Move (W): drag artwork. Shift constrains the axis.");
        ImGui.SameLine();
        tool(1, "Rotate (E): drag the ring. Shift snaps to 15 degrees.");
        ImGui.SameLine();
        tool(2, "Scale (R): drag handles. Shift preserves proportions; Alt scales around the pivot.");
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Drag selected artwork. Shift: uniform scale. Rotate: drag horizontally, Shift snaps to 15 degrees. Move: Shift constrains the axis.");
    }

    private sealed record StageMove(Vector2 Start, float X, float Y, bool Moved, float Angle, float ScaleX, float ScaleY, int Handle, Vector2 Pivot, Vector2 Min, Vector2 Max);
    private StageMove? _move;

    private Vector2? _lastPickPoint;
    private string[] _pickCycle = [];
    private int _pickIndex;

    private void pick(LumenPlayer player, Vector2 point)
    {
        var clips = player.InspectDisplayList();
        var hits = clips.Reverse().Where(clip => clip.Level > 0 && clip.Visible && !clip.HiddenByHost && clip.Multiply.Alpha > 0
            && player.TryInspectBounds(clip.Path, out var box) && point.X >= box.X && point.X <= box.X + box.Width
            && point.Y >= box.Y && point.Y <= box.Y + box.Height).ToArray();
        var paths = hits.Select(clip => clip.Path).ToArray();
        if (hits.FirstOrDefault() is { } first && first.Path.LastIndexOf('/') is >= 0 and var parentCut)
        {
            var parentPath = first.Path[..parentCut];
            if (paths.Contains(parentPath))
                paths = [parentPath, .. paths.Where(path => path != parentPath)];
        }
        if (_lastPickPoint is { } last && Vector2.Distance(last, point) < 6 && paths.SequenceEqual(_pickCycle))
            _pickIndex = (_pickIndex + 1) % Math.Max(1, paths.Length);
        else
        {
            _pickCycle = paths;
            _pickIndex = 0;
        }
        _lastPickPoint = point;
        selectClip(paths.Length == 0 ? null : paths[_pickIndex]);
        _scrollToSelected = _selected is not null;
        for (var path = _selected; path?.LastIndexOf('/') is > 0 and var cut; path = path[..cut])
        {
            _open.Add(path[..cut]);
            _collapsed.Remove(path[..cut]);
        }
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
