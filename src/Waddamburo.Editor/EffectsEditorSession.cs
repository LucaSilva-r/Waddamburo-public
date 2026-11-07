using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.Editor;

/// <summary>The effect preview workspace, attached to the current practice session.</summary>
public sealed partial class EffectsEditorSession : IDisposable
{
    private readonly SdlApplication _application;
    private readonly ImGuiLayer _layer;
    private readonly RenderRectangle? _previousRegion;
    private EffectPreview _preview;
    private IEffectsEditorTransport _transport;
    private readonly BeatView _view = new();
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedGraphs = new(StringComparer.Ordinal);
    private string? _revealTrack;
    private string[] _trackNames;
    private (string Track, int Index)? _key;
    private string? _primary;
    private float _left = .18f, _right = .22f, _bottom = .44f;
    private bool _disposed;
    private bool _seekDrag;
    private int _divider;
    private float _dividerOrigin;
    private const string Status = "Double-click: add/edit key | Ctrl+C/V: copy/paste | Right-click: repeat | Alt: bypass snap | Shift+wheel: scroll";
    private static readonly uint Background = Color(.115f, .125f, .14f), Grid = Color(.20f, .22f, .25f),
        Bar = Color(.31f, .34f, .39f), Text = Color(.85f, .87f, .9f), Muted = Color(.52f, .56f, .61f),
        Playhead = Color(1, .56f, .18f), Accent = Color(.61f, .76f, .91f), SelectedRow = Color(.26f, .35f, .44f, .65f);

    public EffectsEditorSession(SdlApplication application, PlayableChart chart, IEffectsEditorTransport transport, EffectEditorFiles? files = null, Action<ChartEffects>? replaceEffects = null,
        Func<TimeSpan, IReadOnlyList<StageEffectElement>>? elements = null,
        Func<string, TaikoCourse?, EffectEditorSource>? openTja = null, string? tjaPath = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(transport);
        _application = application;
        _transport = transport;
        _sourceChart = chart;
        _files = files;
        _replaceEffects = replaceEffects;
        _getElements = elements;
        _openTja = openTja; _tjaPath = tjaPath;
        _history = new(chart.Effects.ModTracks.Count == 0 && !chart.Effects.Mods.IsEmpty
            ? EffectDocument.EmbeddedPreview : EffectDocument.FromTracks(chart.Effects.ModTracks));
        _preview = new(chart);
        _reference = new(chart, _preview.BeatMap);
        _trackNames = [.. _preview.Tracks.Keys.Order(StringComparer.Ordinal)];
        if (_trackNames.Length > 0) Select(_trackNames[0], false);
        _previousRegion = application.StageRegion;
        FrameAll();
        _layer = application.AttachImGui(Draw);
        EditorTheme.Apply();
        _layer.CaptureControlShortcuts = true;
        _layer.CaptureKey = key => key is ImGuiKey.Space or ImGuiKey.LeftArrow or ImGuiKey.RightArrow
            or ImGuiKey.A or ImGuiKey.B or ImGuiKey.Escape or ImGuiKey.F8 or ImGuiKey.Delete;
        StartWatching(refresh: true);
    }

    public bool CloseRequested { get; private set; }

    private void Draw()
    {
        PollFiles();
        var io = ImGui.GetIO();
        HandleEditorShortcuts();
        if (!io.WantTextInput && _pending == PendingAction.None && !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Space, false)) _transport.Clock.TogglePause();
            var step = io.KeyShift ? _preview.MeasureAt(_transport.Clock.Position) : 1;
            if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow)) SeekBeat(CurrentBeat - step);
            if (ImGui.IsKeyPressed(ImGuiKey.RightArrow)) SeekBeat(CurrentBeat + step);
            if (!io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.A, false)) _transport.MarkLoopStart();
            if (!io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.B, false)) _transport.MarkLoopEnd();
        }
        var size = io.DisplaySize;
        if (size.X < 32 || size.Y < 32) return;
        var layout = EditorLayout.Calculate(size.X, size.Y, _left, _right, _bottom);
        _application.StageRegion = EditorLayout.Preview(layout.Stage).Normalized(size.X, size.Y);
        _elements = _getElements?.Invoke(_transport.Clock.Position) ?? [];
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        Toolbar(layout.Toolbar);
        Targets(layout.Targets);
        Inspector(layout.Inspector);
        TrackPanel(layout.Tracks);
        Viewport(layout.Stage);
        Dividers(layout, size);
        ImGui.PopStyleVar();
        FinishEditingFrame();
    }

    private double CurrentBeat => _preview.BeatAt(_transport.Clock.Position);
    private void SeekBeat(double beat) => _transport.Seek(_preview.TimeAt(beat));
    private static string F(double value) => (Math.Abs(value) < .0005 ? 0 : value).ToString("0.###", CultureInfo.InvariantCulture);
    private static string Exact(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static uint Color(float r, float g, float b, float a = 1) => ImGui.ColorConvertFloat4ToU32(new(r, g, b, a));

    private static bool Panel(string name, EditorPanel rect, bool title = true, ImGuiWindowFlags extra = 0)
    {
        ImGui.SetNextWindowPos(rect.Position);
        ImGui.SetNextWindowSize(rect.Size);
        return ImGui.Begin(name, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoSavedSettings | extra | (title ? 0 : ImGuiWindowFlags.NoTitleBar));
    }

    private void Toolbar(EditorPanel rect)
    {
        if (Panel("Effects preview", rect, false, ImGuiWindowFlags.MenuBar))
        {
            FileMenu();
            if (ImGui.Button("Back (Esc)")) _closeQueued = true;
            ImGui.SameLine();
            if (ImGui.Button(_transport.Clock.Paused ? "Play (Space)" : "Pause (Space)")) _transport.Clock.TogglePause();
            ImGui.SameLine();
            if (ImGui.Button("-##speed")) _transport.Clock.Slower();
            ImGui.SameLine(); ImGui.TextUnformatted($"{F(_transport.Clock.Speed)}x");
            ImGui.SameLine();
            if (ImGui.Button("+##speed")) _transport.Clock.Faster();
            ImGui.SameLine();
            if (ImGui.Button("Set A")) _transport.MarkLoopStart();
            ImGui.SameLine();
            if (ImGui.Button("Set B")) _transport.MarkLoopEnd();
            ImGui.SameLine();
            if (ImGui.Button("Clear loop")) _transport.ClearLoop();
            ImGui.SameLine();
            if (ImGui.Button("Reset panels")) (_left, _right, _bottom) = (.18f, .22f, .44f);
            ImGui.SameLine();
            ImGui.BeginDisabled(_files is null || _history.Current.UsesEmbeddedEffects);
            if (ImGui.Button("Save (Ctrl+S)")) _saveRequested = true;
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(!_history.CanUndo);
            if (ImGui.Button("Undo")) _undoRequested = true;
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(!_history.CanRedo);
            if (ImGui.Button("Redo")) _redoRequested = true;
            ImGui.EndDisabled();
            TransportReadout();
        }
        ImGui.End();
    }

    private string LoopLabel(TimeSpan? time) => time is { } t ? F(_preview.BeatAt(t)) : "--";

    private void Targets(EditorPanel rect)
    {
        if (Panel("Targets", rect))
        {
            AddTrackControls();
            foreach (var target in ChartMods.Elements.Append("notes"))
            {
                var tracks = _trackNames.Where(k => k.StartsWith(target + ".", StringComparison.Ordinal)).ToArray();
                ImGui.SetNextItemOpen(tracks.Length > 0, ImGuiCond.Once);
                var open = ImGui.TreeNodeEx($"{target} ({tracks.Length})", ImGuiTreeNodeFlags.OpenOnArrow
                    | ImGuiTreeNodeFlags.SpanAvailWidth | (_viewportTarget == target ? ImGuiTreeNodeFlags.Selected : 0));
                if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen()) SelectElement(target);
                if (!open) continue;
                foreach (var key in tracks)
                    if (ImGui.Selectable(key[(key.IndexOf('.') + 1)..], _selected.Contains(key)))
                        Select(key, ImGui.GetIO().KeyCtrl);
                if (tracks.Length == 0) ImGui.TextDisabled("No tracks");
                ImGui.TreePop();
            }
        }
        ImGui.End();
    }

    private void Select(string track, bool toggle, bool revealGraph = true)
    {
        CommitInspector();
        if (!toggle) _selected.Clear();
        if (!toggle || !_selected.Remove(track)) _selected.Add(track);
        _primary = _selected.Contains(track) ? track : _selected.Order(StringComparer.Ordinal).FirstOrDefault();
        _viewportTarget = _primary?.Split('.')[0];
        if (revealGraph && _primary is { } primary) { _expandedGraphs.Add(primary); _revealTrack = primary; }
        _key = null;
        _selectedKeys.Clear();
        _inspectorKey = null;
    }

    private void Inspector(EditorPanel rect)
    {
        if (Panel("Inspector", rect))
        {
            ElementInspector();
            if (_primary is { } track)
            {
                ImGui.TextUnformatted(track);
                ImGui.Separator();
                ImGui.TextUnformatted($"Now: {F(_preview.ValueAt(track, _transport.Clock.Position))}");
                ImGui.TextUnformatted($"Keys: {_preview.Tracks[track].Length}");
                if (ImGui.Button("Frame selected")) FrameAll(selectedOnly: true);
                if (_key is { } selected && selected.Track == track)
                {
                    var k = _preview.Tracks[track][selected.Index];
                    ImGui.Separator();
                    ImGui.TextUnformatted($"Key {selected.Index + 1}");
                    KeyInspector(track, k);
                    if (ImGui.Button("Seek to key")) SeekBeat(k.Beat);
                }
                TrackEditingControls(track);
                ImGui.Separator();
                ImGui.TextWrapped("K: add key | Delete: remove keys | Ctrl-click: select multiple");
            }
            else ImGui.TextWrapped("Select an effect track in Targets or the timeline.");
        }
        ImGui.End();
    }

    private void FrameAll(bool selectedOnly = false)
    {
        var keys = _preview.Tracks.Where(p => !selectedOnly || _selected.Contains(p.Key)).SelectMany(p => p.Value).ToArray();
        var first = keys.Length == 0 ? 0 : keys.Min(k => k.Beat);
        var last = keys.Length == 0 ? Math.Max(16, _preview.BeatAt(_transport.Clock.End)) : keys.Max(k => k.Beat);
        _view.Frame(first, last);
    }

    private void TrackPanel(EditorPanel rect)
    {
        if (Panel("Tracks", rect, false))
        {
            if (ImGui.Button("Frame all")) FrameAll();
            ImGui.SameLine();
            if (ImGui.Button("Collapse curves")) _expandedGraphs.Clear();
            ChartControls();
            ImGui.TextColored(_messageError ? new(1, .4f, .35f, 1) : new(.6f, .68f, .75f, 1), _message ?? Status);
            Timeline();
        }
        ImGui.End();
    }

    private void Navigate(Vector2 origin, float width, bool hovered)
    {
        if (!hovered || _dragBefore is not null || _boxStart is not null) return;
        var io = ImGui.GetIO();
        if (io.MouseWheel != 0)
        {
            if (io.KeyShift) ImGui.SetScrollY(ImGui.GetScrollY() - io.MouseWheel * 70);
            else _view.Zoom(io.MouseWheel, (io.MousePos.X - origin.X) / width);
        }
        if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle)) _view.Pan(io.MouseDelta.X / width);
    }

    private float X(double beat, Vector2 origin, float width) => origin.X + (float)_view.FractionAt(beat) * width;

    private void GridLines(ImDrawListPtr draw, Vector2 origin, Vector2 size, bool labels)
    {
        var step = _view.GridStep(size.X);
        var first = Math.Ceiling(_view.Start / step) * step;
        for (var beat = first; beat <= _view.Start + _view.Span; beat += step)
        {
            var x = X(beat, origin, size.X);
            draw.AddLine(new(x, origin.Y), new(x, origin.Y + size.Y), Grid);
            if (labels) draw.AddText(new(x + 3, origin.Y + 3), Text, F(beat));
        }
        foreach (var measure in _reference.Measures)
        {
            var beat = measure.Beat;
            if (beat < _view.Start || beat > _view.Start + _view.Span) continue;
            var x = X(beat, origin, size.X);
            draw.AddLine(new(x, origin.Y), new(x, origin.Y + size.Y), Bar);
        }
        if (_snapChoice == Snaps.Length)
        {
            var last = float.NegativeInfinity;
            foreach (var beat in _reference.SnapBeats)
            {
                if (beat < _view.Start || beat > _view.Start + _view.Span) continue;
                var x = X(beat, origin, size.X);
                if (x - last < 2) continue;
                draw.AddLine(new(x, origin.Y), new(x, origin.Y + size.Y), Color(.52f, .7f, .85f, .22f)); last = x;
            }
        }
        if (_transport.LoopStart is { } a && _transport.LoopEnd is { } b)
        {
            var left = Math.Clamp(X(_preview.BeatAt(a), origin, size.X), origin.X, origin.X + size.X);
            var right = Math.Clamp(X(_preview.BeatAt(b), origin, size.X), origin.X, origin.X + size.X);
            draw.AddRectFilled(new(left, origin.Y), new(right, origin.Y + size.Y), Color(.3f, .55f, .35f, .16f));
        }
    }

    private void DrawPlayhead(ImDrawListPtr draw, Vector2 origin, Vector2 size)
    {
        var x = X(CurrentBeat, origin, size.X);
        if (x < origin.X || x > origin.X + size.X) return;
        draw.AddLine(new(x, origin.Y), new(x, origin.Y + size.Y), Playhead, 2);
        draw.AddTriangleFilled(new(x - 5, origin.Y), new(x + 5, origin.Y), new(x, origin.Y + 7), Playhead);
    }

    private void Timeline()
    {
        const float labelWidth = 194;
        var available = ImGui.GetContentRegionAvail();
        var start = ImGui.GetCursorScreenPos();
        var width = Math.Max(32, available.X - labelWidth - 16);
        var ruler = start + new Vector2(labelWidth, 0);
        var draw = ImGui.GetWindowDrawList();
        ImGui.SetCursorScreenPos(ruler);
        ImGui.InvisibleButton("Beat ruler", new(width, 40));
        var hovered = ImGui.IsItemHovered();
        Navigate(ruler, width, hovered);
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) _seekDrag = true;
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _seekDrag = false;
        if (_seekDrag) SeekBeat(_view.BeatAt(Math.Clamp((ImGui.GetIO().MousePos.X - ruler.X) / width, 0, 1)));
        draw.AddRectFilled(ruler, ruler + new Vector2(width, 40), Background);
        ChartRuler(draw, ruler, width, hovered);
        DrawPlayhead(draw, ruler, new(width, 40));
        ChartLanes(start + new Vector2(0, 41), labelWidth, width);
        ImGui.SetCursorScreenPos(start + new Vector2(0, 94));
        if (ImGui.BeginChild("Timeline rows", new(0, Math.Max(32, available.Y - 94)), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse))
        {
            var origin = ImGui.GetCursorScreenPos();
            var rows = TimelineTrackRow.Arrange(_trackNames, _expandedGraphs, graphHeight: Math.Clamp(available.Y - 138, 100, 180));
            var height = Math.Max(32, rows.Sum(r => r.Height));
            var plot = origin + new Vector2(labelWidth, 0);
            draw = ImGui.GetWindowDrawList();
            var headerHovered = false;
            var hitAnyKey = false;
            foreach (var row in rows)
            {
                var track = row.Track;
                var y = origin.Y + row.Top;
                ImGui.SetCursorScreenPos(new(origin.X, y));
                if (_revealTrack == track) { ImGui.SetScrollY(row.Top); _revealTrack = null; }
                ImGui.InvisibleButton("Track##" + track, new(Math.Max(32, available.X - 16), row.HeaderHeight),
                    ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);
                hovered = ImGui.IsItemHovered(); headerHovered |= hovered;
                var mouse = ImGui.GetIO().MousePos;
                draw.AddRectFilled(new(origin.X, y), new(origin.X + available.X, y + row.HeaderHeight),
                    _selected.Contains(track) ? SelectedRow : Background);
                draw.AddLine(new(origin.X, y + row.HeaderHeight), new(origin.X + available.X, y + row.HeaderHeight), Grid);
                var arrow = new Vector2(origin.X + 12, y + row.HeaderHeight / 2);
                if (_expandedGraphs.Contains(track))
                    draw.AddTriangleFilled(arrow + new Vector2(-4, -2), arrow + new Vector2(4, -2), arrow + new Vector2(0, 3), Muted);
                else draw.AddTriangleFilled(arrow + new Vector2(-2, -4), arrow + new Vector2(-2, 4), arrow + new Vector2(3, 0), Muted);
                draw.AddText(new(origin.X + 26, y + 8), _selected.Contains(track) ? Accent : Text, track);
                if (hovered && mouse.X < plot.X)
                {
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        if (mouse.X < origin.X + 24)
                        {
                            if (!_expandedGraphs.Remove(track)) { _expandedGraphs.Add(track); _revealTrack = track; }
                            Select(track, ImGui.GetIO().KeyCtrl, revealGraph: false);
                        }
                        else Select(track, ImGui.GetIO().KeyCtrl);
                    }
                    ImGui.SetTooltip("Click the arrow to expand or collapse this track's curve.");
                }
                if (hovered && mouse.X >= plot.X) Navigate(plot, width, true);
                draw.PushClipRect(new(plot.X, y), new(plot.X + width, y + row.HeaderHeight), true);
                GridLines(draw, new(plot.X, y), new(width, row.HeaderHeight), false);
                var keys = _preview.Tracks[track];
                int? contextIndex = null;
                for (var i = 0; i < keys.Length; i++)
                {
                    var key = keys[i];
                    var x = X(key.Beat, plot, width);
                    if (x < plot.X - 6 || x > plot.X + width + 6) continue;
                    var center = new Vector2(x, y + row.HeaderHeight / 2);
                    var hit = hovered && Vector2.Distance(mouse, center) < 9;
                    var selected = _selectedKeys.Contains(new(track, key.Beat));
                    draw.AddQuadFilled(center + new Vector2(0, -5), center + new Vector2(5, 0), center + new Vector2(0, 5),
                        center + new Vector2(-5, 0), selected || hit ? Playhead : Accent);
                    if (i > 0 && x - X(keys[i - 1].Beat, plot, width) > 95)
                        draw.AddText(new(Math.Max(plot.X, X(keys[i - 1].Beat, plot, width)) + 10, y + 8), Muted,
                            key.Curve is null ? key.Ease.ToString() : "Bezier");
                    if (!hit) continue;
                    hitAnyKey = true;
                    contextIndex = i;
                    ImGui.SetTooltip($"{track}\nBeat {F(key.Beat)} | Value {F(key.Value)}");
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) BeginKeyDrag(track, i, width);
                }
                if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    KeyContext(track, contextIndex, mouse.X < plot.X ? CurrentBeat : _view.BeatAt((mouse.X - plot.X) / width));
                draw.PopClipRect();
                if (row.GraphHeight > 0)
                {
                    var graphPosition = new Vector2(origin.X, y + row.HeaderHeight);
                    ImGui.SetCursorScreenPos(graphPosition);
                    ImGui.InvisibleButton("Curve##" + track, new(Math.Max(32, available.X - 16), row.GraphHeight),
                        ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);
                    InlineGraph(track, graphPosition, new(labelWidth + width, row.GraphHeight), labelWidth, ImGui.IsItemHovered());
                }
            }
            BoxSelection(draw, origin, plot, width, rows, height, headerHovered, hitAnyKey);
            if (_trackNames.Length == 0)
            {
                ImGui.Dummy(new(32, 40));
                draw.AddText(origin + new Vector2(12, 10), Muted, "Add a track in Targets, or select an element in the preview.");
            }
            draw.PushClipRect(plot, plot + new Vector2(width, height), true);
            DrawPlayhead(draw, plot, new(width, height));
            draw.PopClipRect();
        }
        ImGui.EndChild();
    }

    private void Dividers(EditorLayout layout, Vector2 size)
    {
        Divider(1, new(layout.Stage.X - 6, layout.Targets.Y, 8, layout.Targets.Height), size);
        Divider(2, new(layout.Inspector.X - 6, layout.Targets.Y, 8, layout.Targets.Height), size);
        Divider(3, new(0, layout.Tracks.Y - 6, size.X, 8), size);
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _divider = 0;
    }

    private void Divider(int id, EditorPanel rect, Vector2 size)
    {
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (Panel($"##divider{id}", rect, false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.InvisibleButton("Resize", Vector2.Max(new(1, 1), rect.Size));
            if (ImGui.IsItemHovered() || _divider == id)
                ImGui.SetMouseCursor(id == 3 ? ImGuiMouseCursor.ResizeNS : ImGuiMouseCursor.ResizeEW);
            if (ImGui.IsItemActivated())
            {
                _divider = id;
                _dividerOrigin = id == 1 ? _left : id == 2 ? _right : _bottom;
            }
            if (_divider == id && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var delta = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left);
                if (id == 1) _left = Math.Clamp(EditorLayout.Snap(_dividerOrigin + delta.X / size.X, .18f), .1f, .3f);
                else if (id == 2) _right = Math.Clamp(EditorLayout.Snap(_dividerOrigin - delta.X / size.X, .22f), .12f, .32f);
                else _bottom = Math.Clamp(EditorLayout.Snap(_dividerOrigin - delta.Y / size.Y, .44f), .22f, .6f);
            }
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _watch?.Dispose();
        _layer.Dispose();
        _application.StageRegion = _previousRegion;
        _disposed = true;
    }
}
