using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Waddamburo.Catalog;
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
    private readonly IEffectsEditorTransport _transport;
    private readonly BeatView _view = new();
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private string[] _trackNames;
    private (string Track, int Index)? _key;
    private string? _primary;
    private float _left = .18f, _right = .22f, _bottom = .34f;
    private bool _disposed;
    private bool _seekDrag;
    private int _divider;
    private float _dividerOrigin;
    private const string Status = "Wheel: zoom | Middle drag: pan | Click a key to inspect";
    private static readonly uint Background = Color(.055f, .065f, .085f), Grid = Color(.17f, .19f, .23f),
        Bar = Color(.28f, .32f, .4f), Text = Color(.8f, .84f, .9f), Muted = Color(.48f, .54f, .63f),
        Playhead = Color(1, .56f, .18f), Accent = Color(.32f, .8f, .95f), SelectedRow = Color(.11f, .2f, .28f, .65f);
    private static readonly uint[] TrackColors = [Color(.32f, .8f, .95f), Color(.9f, .54f, .82f), Color(.55f, .88f, .55f), Color(1, .8f, .3f)];

    public EffectsEditorSession(SdlApplication application, PlayableChart chart, IEffectsEditorTransport transport, EffectEditorFiles? files = null, Action<ChartEffects>? replaceEffects = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(transport);
        _application = application;
        _transport = transport;
        _sourceChart = chart;
        _files = files;
        _replaceEffects = replaceEffects;
        _history = new(chart.Effects.ModTracks.Count == 0 && !chart.Effects.Mods.IsEmpty
            ? EffectDocument.EmbeddedPreview : EffectDocument.FromTracks(chart.Effects.ModTracks));
        _preview = new(chart);
        _trackNames = [.. _preview.Tracks.Keys.Order(StringComparer.Ordinal)];
        if (_trackNames.Length > 0) Select(_trackNames[0], false);
        _previousRegion = application.StageRegion;
        FrameAll();
        _layer = application.AttachImGui(Draw);
        _layer.CaptureControlShortcuts = true;
        _layer.CaptureKey = key => key is ImGuiKey.Space or ImGuiKey.LeftArrow or ImGuiKey.RightArrow
            or ImGuiKey.A or ImGuiKey.B or ImGuiKey.Escape or ImGuiKey.F8 or ImGuiKey.Delete;
    }

    public bool CloseRequested { get; private set; }

    private void Draw()
    {
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
        _application.StageRegion = layout.Stage.Normalized(size.X, size.Y);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        Toolbar(layout.Toolbar);
        Targets(layout.Targets);
        Inspector(layout.Inspector);
        TrackPanel(layout.Tracks);
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
        if (Panel("Effects preview", rect, false))
        {
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
            if (ImGui.Button("Reset panels")) (_left, _right, _bottom) = (.18f, .22f, .34f);
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
            ImGui.TextUnformatted($"Beat {F(CurrentBeat)}  |  {_transport.Clock.Position.TotalSeconds:0.00} s  |  {(_history.Dirty ? "Unsaved" : "Saved")}  |  A: {LoopLabel(_transport.LoopStart)}  B: {LoopLabel(_transport.LoopEnd)}");
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
                if (!ImGui.TreeNode($"{target} ({tracks.Length})")) continue;
                foreach (var key in tracks)
                    if (ImGui.Selectable(key[(key.IndexOf('.') + 1)..], _selected.Contains(key)))
                        Select(key, ImGui.GetIO().KeyCtrl);
                if (tracks.Length == 0) ImGui.TextDisabled("No tracks");
                ImGui.TreePop();
            }
        }
        ImGui.End();
    }

    private void Select(string track, bool toggle)
    {
        CommitInspector();
        if (!toggle) _selected.Clear();
        if (!toggle || !_selected.Remove(track)) _selected.Add(track);
        _primary = _selected.Contains(track) ? track : _selected.Order(StringComparer.Ordinal).FirstOrDefault();
        _key = null;
        _selectedKeys.Clear();
        _inspectorKey = null;
    }

    private void Inspector(EditorPanel rect)
    {
        if (Panel("Inspector", rect))
        {
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
            ImGui.SameLine(); ImGui.TextColored(_messageError ? new(1, .4f, .35f, 1) : new(.6f, .68f, .75f, 1), _message ?? Status);
            var visible = false;
            var graph = false;
            if (ImGui.BeginTabBar("Track views"))
            {
                if (ImGui.BeginTabItem("Timeline")) { visible = true; ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Graph")) { visible = graph = true; ImGui.EndTabItem(); }
                ImGui.EndTabBar();
            }
            // Submit both selectors together before drawing the selected canvas.
            if (visible)
            {
                if (graph) Graph(); else Timeline();
            }
        }
        ImGui.End();
    }

    private void Navigate(Vector2 origin, float width, bool hovered)
    {
        if (!hovered || _dragBefore is not null || _boxStart is not null) return;
        var io = ImGui.GetIO();
        if (io.MouseWheel != 0) _view.Zoom(io.MouseWheel, (io.MousePos.X - origin.X) / width);
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
        foreach (var beat in _preview.Bars)
        {
            if (beat < _view.Start || beat > _view.Start + _view.Span) continue;
            var x = X(beat, origin, size.X);
            draw.AddLine(new(x, origin.Y), new(x, origin.Y + size.Y), Bar);
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
        const float labelWidth = 148, rowHeight = 26;
        var available = ImGui.GetContentRegionAvail();
        var start = ImGui.GetCursorScreenPos();
        var width = Math.Max(32, available.X - labelWidth - 16);
        var ruler = start + new Vector2(labelWidth, 0);
        var draw = ImGui.GetWindowDrawList();
        ImGui.SetCursorScreenPos(ruler);
        ImGui.InvisibleButton("Beat ruler", new(width, 25));
        var hovered = ImGui.IsItemHovered();
        Navigate(ruler, width, hovered);
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) _seekDrag = true;
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _seekDrag = false;
        if (_seekDrag) SeekBeat(_view.BeatAt(Math.Clamp((ImGui.GetIO().MousePos.X - ruler.X) / width, 0, 1)));
        draw.AddRectFilled(ruler, ruler + new Vector2(width, 25), Background);
        GridLines(draw, ruler, new(width, 25), true);
        DrawPlayhead(draw, ruler, new(width, 25));
        ImGui.SetCursorScreenPos(start + new Vector2(0, 27));
        if (ImGui.BeginChild("Timeline rows", new(0, Math.Max(32, available.Y - 27)), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse))
        {
            var origin = ImGui.GetCursorScreenPos();
            var height = Math.Max(32, _trackNames.Length * rowHeight);
            ImGui.InvisibleButton("Timeline canvas", new(Math.Max(32, available.X - 16), height), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle);
            hovered = ImGui.IsItemHovered();
            var plot = origin + new Vector2(labelWidth, 0);
            draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(origin, origin + new Vector2(available.X, height), Background);
            Navigate(plot, width, hovered);
            draw.PushClipRect(plot, plot + new Vector2(width, height), true);
            GridLines(draw, plot, new(width, height), false);
            draw.PopClipRect();
            var hitAnyKey = false;
            for (var row = 0; row < _trackNames.Length; row++)
            {
                var track = _trackNames[row];
                var y = origin.Y + row * rowHeight;
                if (_selected.Contains(track)) draw.AddRectFilled(new(origin.X, y), new(origin.X + available.X, y + rowHeight), SelectedRow);
                draw.AddText(new(origin.X + 5, y + 5), _selected.Contains(track) ? Accent : Text, track);
                draw.AddLine(new(origin.X, y + rowHeight), new(origin.X + available.X, y + rowHeight), Grid);
                if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && ImGui.GetIO().MousePos.X < plot.X && ImGui.GetIO().MousePos.Y >= y && ImGui.GetIO().MousePos.Y < y + rowHeight)
                    Select(track, ImGui.GetIO().KeyCtrl);
                var keys = _preview.Tracks[track];
                draw.PushClipRect(new(plot.X, y), new(plot.X + width, y + rowHeight), true);
                for (var i = 0; i < keys.Length; i++)
                {
                    var k = keys[i];
                    var x = X(k.Beat, plot, width);
                    if (x < plot.X - 6 || x > plot.X + width + 6) continue;
                    var center = new Vector2(x, y + rowHeight / 2);
                    var hit = hovered && Vector2.Distance(ImGui.GetIO().MousePos, center) < 8;
                    var selected = _selectedKeys.Contains(new(track, k.Beat));
                    draw.AddQuadFilled(center + new Vector2(0, -5), center + new Vector2(5, 0), center + new Vector2(0, 5), center + new Vector2(-5, 0), selected || hit ? Playhead : Accent);
                    if (i > 0 && x - X(keys[i - 1].Beat, plot, width) > 95)
                        draw.AddText(new(Math.Max(plot.X, X(keys[i - 1].Beat, plot, width)) + 9, y + 5), Muted, k.Curve is null ? k.Ease.ToString() : "Bezier");
                    if (hit)
                    {
                        hitAnyKey = true;
                        ImGui.SetTooltip($"{track}\nBeat {F(k.Beat)} | Value {F(k.Value)}");
                        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) BeginKeyDrag(track, i, width);
                    }
                }
                draw.PopClipRect();
            }
            BoxSelection(draw, origin, plot, width, rowHeight, height, hovered, hitAnyKey);
            if (_trackNames.Length == 0) draw.AddText(origin + new Vector2(12, 10), Muted, "This chart has no effect tracks.");
            draw.PushClipRect(plot, plot + new Vector2(width, height), true);
            DrawPlayhead(draw, plot, new(width, height));
            draw.PopClipRect();
        }
        ImGui.EndChild();
    }

    private void Graph()
    {
        var position = ImGui.GetCursorScreenPos();
        var size = Vector2.Max(new(60, 40), ImGui.GetContentRegionAvail());
        ImGui.InvisibleButton("Graph canvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle);
        var hovered = ImGui.IsItemHovered();
        var origin = position + new Vector2(54, 24);
        var plotSize = Vector2.Max(new(1, 1), size - new Vector2(62, 30));
        Navigate(origin, plotSize.X, hovered);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(position, position + size, Background);
        if (_selected.Count == 0) { draw.AddText(position + new Vector2(10, 10), Muted, "Select a track to view its curve."); return; }
        var samples = _selected.Order(StringComparer.Ordinal).Select(key => (Key: key,
            Values: Enumerable.Range(0, 257).Select(i => _preview.ValueAt(key, _view.BeatAt(i / 256d))).ToArray())).ToArray();
        var values = samples.SelectMany(s => s.Values).Concat(_selected.SelectMany(key => _preview.Tracks[key])
            .Where(k => k.Beat >= _view.Start && k.Beat <= _view.Start + _view.Span).Select(k => k.Value)).Where(double.IsFinite).ToArray();
        var legendX = position.X + 6;
        for (var i = 0; i < samples.Length; i++)
        {
            var label = samples[i].Key;
            var width = ImGui.CalcTextSize(label).X + 18;
            if (legendX + width > position.X + size.X) break;
            draw.AddText(new(legendX, position.Y + 3), TrackColors[i % TrackColors.Length], label);
            legendX += width;
        }
        var scale = _graphDragScale ?? GraphScale.Fit(values.Concat(VisibleHandleValues()));
        float Y(double value) => origin.Y + (float)(1 - scale.Fraction(value)) * plotSize.Y;
        GridLines(draw, origin, plotSize, true);
        for (var i = 0; i <= 4; i++)
        {
            var value = scale.ValueAt(i / 4d);
            var y = origin.Y + (1 - i / 4f) * plotSize.Y;
            draw.AddLine(new(origin.X, y), new(origin.X + plotSize.X, y), Grid);
            draw.AddText(new(position.X + 2, y - 6), Muted, F(value));
        }
        draw.PushClipRect(origin, origin + plotSize, true);
        var hitHandle = GraphHandles(draw, origin, plotSize, scale, hovered);
        for (var s = 0; s < samples.Length; s++)
        {
            var sample = samples[s];
            var color = TrackColors[s % TrackColors.Length];
            for (var i = 1; i < sample.Values.Length; i++)
                if (double.IsFinite(sample.Values[i - 1]) && double.IsFinite(sample.Values[i]))
                    draw.AddLine(new(origin.X + (i - 1) / 256f * plotSize.X, Y(sample.Values[i - 1])),
                        new(origin.X + i / 256f * plotSize.X, Y(sample.Values[i])), color, 2);
            var keys = _preview.Tracks[sample.Key];
            for (var i = 0; i < keys.Length; i++)
            {
                var center = new Vector2(X(keys[i].Beat, origin, plotSize.X), Y(keys[i].Value));
                if (center.X < origin.X || center.X > origin.X + plotSize.X) continue;
                var hit = hovered && Vector2.Distance(ImGui.GetIO().MousePos, center) < 8;
                draw.AddCircleFilled(center, 4, hit || _selectedKeys.Contains(new(sample.Key, keys[i].Beat)) ? Playhead : color);
                if (hit)
                {
                    ImGui.SetTooltip($"{sample.Key}\nBeat {F(keys[i].Beat)} | Value {F(keys[i].Value)}");
                    if (!hitHandle && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) BeginKeyDrag(sample.Key, i, plotSize.X, scale, plotSize.Y);
                }
            }
        }
        DrawPlayhead(draw, origin, plotSize);
        draw.PopClipRect();
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
                else _bottom = Math.Clamp(EditorLayout.Snap(_dividerOrigin - delta.Y / size.Y, .34f), .22f, .6f);
            }
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _layer.Dispose();
        _application.StageRegion = _previousRegion;
        _disposed = true;
    }
}
