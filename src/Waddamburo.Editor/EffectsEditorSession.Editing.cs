using System.Numerics;
using ImGuiNET;
using Waddamburo.Catalog;

namespace Waddamburo.Editor;

public sealed partial class EffectsEditorSession
{
    private readonly PlayableChart _sourceChart;
    private readonly EffectHistory _history;
    private readonly EffectEditorFiles? _files;
    private readonly Action<ChartEffects>? _replaceEffects;
    private readonly HashSet<EffectKey> _selectedKeys = [];
    private string? _message;
    private bool _messageError;
    private bool _closeQueued;
    private bool _saveRequested, _saveSongRequested, _undoRequested, _redoRequested, _deleteRequested, _reloadRequested;
    private int _addTarget, _addProperty, _snapChoice = 3;
    private static readonly double[] Snaps = [0, 1, .5, .25, .125, .0625];
    private static readonly string[] SnapNames = ["Off", "1 beat", "1/2 beat", "1/4 beat", "1/8 beat", "1/16 beat"];
    private static readonly string[] TargetsList = [.. ChartMods.Elements, "notes"];
    private static readonly ChartEase[] Eases = Enum.GetValues<ChartEase>();
    private EffectKey? _inspectorKey;
    private double _inspectBeat, _inspectValue;
    private ChartEase _inspectEase;
    private ChartModCurve? _inspectCurve;
    private enum PendingAction { None, Close, Reload }
    private PendingAction _pending;
    private bool _openConfirm, _cancelConfirm;
    private EffectDocument? _dragBefore;
    private HashSet<EffectKey> _dragKeys = [];
    private EffectKey _dragAnchor;
    private float _dragWidth;
    private double _dragSpan;
    private Vector2? _boxStart;
    private HashSet<EffectKey> _boxOriginal = [];
    private bool _boxAppend;

    public void RequestClose() => Request(PendingAction.Close);

    private void Request(PendingAction action)
    {
        if (_pending != PendingAction.None) return;
        _boxStart = null;
        EndDrag();
        CommitInspector();
        if (!_history.Dirty) { Perform(action); return; }
        _pending = action; _openConfirm = true;
    }

    private void Perform(PendingAction action)
    {
        if (action == PendingAction.Close) CloseRequested = true;
        else if (action == PendingAction.Reload) LoadDocument();
    }

    private void HandleEditorShortcuts()
    {
        var io = ImGui.GetIO();
        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false) || ImGui.IsKeyPressed(ImGuiKey.F8, false))
        {
            if (io.WantTextInput && !ImGui.IsKeyPressed(ImGuiKey.F8, false)) return;
            if (_dragBefore is not null)
            {
                _history.CancelGesture(); _dragBefore = null; _handleDrag = null; _graphDragScale = null; ApplyCurrent();
            }
            else if (_boxStart is not null) _boxStart = null;
            else if (_pending != PendingAction.None) _cancelConfirm = true;
            else _closeQueued = true;
        }
        if (_pending != PendingAction.None) return;
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S, false)) _saveRequested = true;
        if (io.WantTextInput) return;
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Z))
        {
            if (io.KeyShift) _redoRequested = true; else _undoRequested = true;
        }
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Y)) _redoRequested = true;
        if (ImGui.IsKeyPressed(ImGuiKey.Delete)) _deleteRequested = true;
        if (!io.WantCaptureKeyboard) return;
        if (!io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.K, false)) AddKey();
    }

    private void FinishEditingFrame()
    {
        MoveDraggedKeys();
        if (_dragBefore is not null && !ImGui.IsMouseDown(ImGuiMouseButton.Left)) EndDrag();
        var inspectorValid = true;
        if (_saveRequested || _saveSongRequested || _undoRequested || _redoRequested || _reloadRequested) inspectorValid = CommitInspector();
        if (_undoRequested) { EndDrag(); if (_history.Undo()) ApplyCurrent(); }
        if (_redoRequested) { EndDrag(); if (_history.Redo()) ApplyCurrent(); }
        if (_deleteRequested) DeleteSelected();
        if ((_saveRequested || _saveSongRequested) && inspectorValid) SaveDocument(_saveSongRequested);
        if (_reloadRequested) Request(PendingAction.Reload);
        (_saveRequested, _saveSongRequested, _undoRequested, _redoRequested, _deleteRequested, _reloadRequested) = (false, false, false, false, false, false);
        if (_closeQueued) { _closeQueued = false; RequestClose(); }
        ConfirmUnsaved();
    }

    private void ConfirmUnsaved()
    {
        if (_openConfirm) { ImGui.OpenPopup("Unsaved effects"); _openConfirm = false; }
        if (!ImGui.BeginPopupModal("Unsaved effects", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextUnformatted("Save your effects before continuing?");
        if (_messageError && _message is { } error) ImGui.TextWrapped(error);
        if (ImGui.Button("Save and continue"))
        {
            if (SaveDocument(false))
            {
                var action = _pending; _pending = PendingAction.None;
                ImGui.CloseCurrentPopup(); Perform(action);
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Discard changes"))
        {
            var action = _pending; _pending = PendingAction.None;
            if (action == PendingAction.Close)
            {
                _history.Edit(_history.Saved, "Discard edits"); ApplyCurrent();
            }
            ImGui.CloseCurrentPopup(); Perform(action);
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel") || _cancelConfirm)
        {
            _pending = PendingAction.None; _cancelConfirm = false; ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private bool Edit(EffectDocument document, string label)
    {
        if (!_history.Edit(document, label)) return false;
        ApplyCurrent();
        _message = label; _messageError = false;
        return true;
    }

    private void ApplyCurrent()
    {
        var chart = _history.Current.UsesEmbeddedEffects ? _sourceChart : ChartModFile.Apply(_sourceChart, _history.Current.Tracks);
        _replaceEffects?.Invoke(chart.Effects);
        _preview = new(chart);
        _trackNames = [.. _preview.Tracks.Keys.Order(StringComparer.Ordinal)];
        _selected.RemoveWhere(key => !_preview.Tracks.ContainsKey(key));
        _selectedKeys.RemoveWhere(key => !_history.Current.Contains(key));
        if (_primary is null || !_preview.Tracks.ContainsKey(_primary))
        {
            _primary = _trackNames.FirstOrDefault();
            if (_primary is { } first) _selected.Add(first);
        }
        _key = null; _inspectorKey = null;
        if (_selectedKeys.Count > 0) FocusKey(_selectedKeys.First());
    }

    private bool SaveDocument(bool intoSong)
    {
        var playerSaved = false;
        try
        {
            EndDrag();
            if (!CommitInspector()) return false;
            if (_files is null) throw new InvalidOperationException("No save destination is available.");
            _files.SavePlayer(_history.Current);
            _history.MarkSaved(); playerSaved = true;
            if (intoSong) _files.SaveSong(_history.Current);
            _message = intoSong ? "Saved player modchart and song course" : "Saved player modchart";
            _messageError = false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            _message = (playerSaved ? "Player saved; song save failed: " : "Save failed: ") + e.Message; _messageError = true; return false;
        }
    }

    private void LoadDocument()
    {
        try
        {
            var document = _files?.Load() ?? throw new FileNotFoundException("No saved modchart file was found.");
            EndDrag(); _history.Edit(document, "Reload saved effects"); _history.MarkSaved(); ApplyCurrent();
            _message = "Loaded saved effects"; _messageError = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        { _message = "Load failed: " + e.Message; _messageError = true; }
    }

    private void AddTrackControls()
    {
        if (_history.Current.UsesEmbeddedEffects)
        {
            ImGui.TextWrapped("These effects come from chart commands. A keyframe modchart replaces those effects.");
            if (ImGui.Button("Start keyframe modchart")) Edit(EffectDocument.Empty, "Start keyframe modchart");
            ImGui.Separator();
            return;
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.Combo("##target", ref _addTarget, TargetsList, TargetsList.Length);
        var target = TargetsList[_addTarget];
        var properties = target == "notes" ? ChartMods.NoteProperties
            : target == "don" ? ChartMods.ElementProperties.AddRange(ChartMods.DonModelProperties) : ChartMods.ElementProperties;
        _addProperty = Math.Clamp(_addProperty, 0, properties.Length - 1);
        ImGui.SetNextItemWidth(-1);
        ImGui.Combo("##property", ref _addProperty, properties.ToArray(), properties.Length);
        if (ImGui.Button("+ Add track"))
        {
            var key = target + "." + properties[_addProperty];
            Edit(_history.Current.AddTrack(key), "Add " + key); Select(key, false);
        }
        ImGui.Separator();
    }

    private void TrackEditingControls(string track)
    {
        if (_history.Current.UsesEmbeddedEffects) return;
        ImGui.Separator();
        ImGui.SetNextItemWidth(-52);
        ImGui.Combo("Snap", ref _snapChoice, SnapNames, SnapNames.Length);
        if (ImGui.Button("Key at playhead (K)")) AddKey();
        ImGui.SameLine();
        ImGui.BeginDisabled(_selectedKeys.Count == 0);
        if (ImGui.Button("Delete")) _deleteRequested = true;
        ImGui.EndDisabled();
        if (ImGui.Button("Remove track")) Edit(_history.Current.RemoveTrack(track), "Remove " + track);
        if (_files is not null)
        {
            if (ImGui.Button("Reload saved")) _reloadRequested = true;
            if (_files.SongPath is not null && ImGui.Button("Save into song")) _saveSongRequested = true;
        }
    }

    private double SnapBeat(double beat) => Math.Max(0, EffectDocument.Snap(beat, Snaps[_snapChoice]));

    private void AddKey()
    {
        if (_primary is not { } track || _history.Current.UsesEmbeddedEffects) return;
        var beat = SnapBeat(CurrentBeat);
        var value = _preview.ValueAt(track, _transport.Clock.Position);
        try
        {
            var keys = _history.Current.Tracks.GetValueOrDefault(track, []);
            var key = keys.FirstOrDefault(k => k.Beat == beat, new ChartModKeyframe(beat, value)) with { Value = value };
            Edit(_history.Current.SetKey(track, key), "Add key");
            _selectedKeys.Clear(); _selectedKeys.Add(new(track, beat));
            FocusKey(new(track, beat));
        }
        catch (InvalidDataException e) { _message = e.Message; _messageError = true; }
    }

    private void FocusKey(EffectKey key)
    {
        _primary = key.Track; _selected.Add(key.Track);
        if (!_preview.Tracks.TryGetValue(key.Track, out var keys)) return;
        for (var i = 0; i < keys.Length; i++)
            if (keys[i].Beat == key.Beat) { _key = (key.Track, i); _inspectorKey = null; return; }
    }

    private EffectKey? ChooseKey(string track, int index, bool toggle, bool keepTracks = false)
    {
        var key = new EffectKey(track, _preview.Tracks[track][index].Beat);
        CommitInspector();
        if (!_preview.Tracks[track].Any(k => k.Beat == key.Beat))
        {
            if (_key is not { } focus || focus.Track != track) return null;
            key = new(track, _preview.Tracks[track][focus.Index].Beat);
        }
        if (toggle && !_selectedKeys.Add(key)) { _selectedKeys.Remove(key); _key = null; _inspectorKey = null; return null; }
        if (!toggle && !_selectedKeys.Contains(key))
        {
            _selectedKeys.Clear();
            if (!keepTracks) _selected.Clear();
        }
        _selectedKeys.Add(key); FocusKey(key);
        return key;
    }

    private void KeyInspector(string track, ChartModKeyframe key)
    {
        var id = new EffectKey(track, key.Beat);
        if (_inspectorKey != id)
        {
            _inspectorKey = id; _inspectBeat = key.Beat; _inspectValue = key.Value; _inspectEase = key.Ease; _inspectCurve = key.Curve;
        }
        if (_history.Current.UsesEmbeddedEffects)
        {
            ImGui.TextUnformatted($"Beat: {Exact(key.Beat)}");
            ImGui.TextUnformatted($"Value: {Exact(key.Value)}");
            ImGui.TextUnformatted($"Ease: {key.Ease}");
            return;
        }
        var commit = NumberInput("Beat", ref _inspectBeat);
        commit |= NumberInput("Value", ref _inspectValue);
        var useCurve = _inspectCurve.HasValue;
        if (ImGui.Checkbox("Bezier curve", ref useCurve)) { _inspectCurve = useCurve ? new(.25, 0, .75, 1) : null; commit = true; }
        if (_inspectCurve is { } curve)
        {
            var x1 = curve.X1; var y1 = curve.Y1; var x2 = curve.X2; var y2 = curve.Y2;
            commit |= NumberInput("x1", ref x1);
            commit |= NumberInput("y1", ref y1);
            commit |= NumberInput("x2", ref x2);
            commit |= NumberInput("y2", ref y2);
            _inspectCurve = new(x1, y1, x2, y2);
        }
        else
        {
            ImGui.SetNextItemWidth(-52);
            if (ImGui.BeginCombo("Ease", _inspectEase.ToString()))
            {
                foreach (var ease in Eases)
                {
                    if (ImGui.Selectable(ease.ToString(), ease == _inspectEase)) { _inspectEase = ease; commit = true; }
                    ImGui.SameLine(125);
                    var thumb = ImGui.GetCursorScreenPos(); ImGui.Dummy(new(65, 16));
                    CurveThumb(thumb, new(65, 16), ease, null);
                }
                ImGui.EndCombo();
            }
        }
        var preview = ImGui.GetCursorScreenPos();
        var size = new Vector2(Math.Max(32, ImGui.GetContentRegionAvail().X), 44);
        ImGui.Dummy(size); CurveThumb(preview, size, _inspectEase, _inspectCurve);
        if (commit) CommitInspector();
    }

    private static bool NumberInput(string label, ref double value)
    {
        ImGui.SetNextItemWidth(-52);
        ImGui.InputDouble(label, ref value, 0, 0, "%.12g");
        return ImGui.IsItemDeactivatedAfterEdit() || ImGui.IsItemActive() && ImGui.IsKeyPressed(ImGuiKey.Enter, false);
    }

    private static void CurveThumb(Vector2 origin, Vector2 size, ChartEase ease, ChartModCurve? curve)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, Background);
        if (curve is { IsValid: false }) return;
        var samples = Enumerable.Range(0, 33).Select(i => ChartMods.Eased(ease, i / 32d, curve)).ToArray();
        var scale = GraphScale.Fit(samples);
        draw.PushClipRect(origin, origin + size, true);
        for (var i = 1; i < samples.Length; i++)
            draw.AddLine(origin + new Vector2((i - 1) / 32f * size.X, (float)(1 - scale.Fraction(samples[i - 1])) * size.Y),
                origin + new Vector2(i / 32f * size.X, (float)(1 - scale.Fraction(samples[i])) * size.Y), Accent, 1.5f);
        draw.PopClipRect();
    }

    private bool CommitInspector()
    {
        if (_inspectorKey is not { } id || !_history.Current.Contains(id)) return true;
        var key = _history.Current.Tracks[id.Track].First(k => k.Beat == id.Beat);
        var edited = new ChartModKeyframe(_inspectBeat, _inspectValue, _inspectEase, _inspectCurve);
        if (key == edited) return true;
        try
        {
            if (edited.Beat < 0) throw new InvalidDataException("Keyframe beats cannot be negative.");
            Edit(_history.Current.SetKey(id.Track, edited, id.Beat), "Edit key");
            _selectedKeys.Clear(); _selectedKeys.Add(new(id.Track, edited.Beat)); FocusKey(new(id.Track, edited.Beat));
            return true;
        }
        catch (InvalidDataException e) { _message = e.Message; _messageError = true; return false; }
    }

    private void DeleteSelected()
    {
        if (_history.Current.UsesEmbeddedEffects) return;
        EndDrag();
        Edit(_history.Current.DeleteKeys(_selectedKeys), "Delete keys");
        _selectedKeys.Clear();
    }

    private void BeginKeyDrag(string track, int index, float width, GraphScale? scale = null, float height = 0)
    {
        if (ChooseKey(track, index, ImGui.GetIO().KeyCtrl, keepTracks: scale.HasValue) is not { } anchor) return;
        if (_history.Current.UsesEmbeddedEffects) return;
        if (!_selectedKeys.Contains(anchor)) return;
        _dragBefore = _history.Current; _dragKeys = [.. _selectedKeys]; _dragAnchor = anchor;
        _graphDragScale = scale; _graphDragHeight = height;
        _dragWidth = width; _dragSpan = _view.Span; _history.BeginGesture();
    }

    private void MoveDraggedKeys()
    {
        if (_dragBefore is null || !ImGui.IsMouseDragging(ImGuiMouseButton.Left)) return;
        if (_handleDrag is not null) { MoveCurveHandle(); return; }
        var delta = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left).X / _dragWidth * _dragSpan;
        delta = SnapBeat(_dragAnchor.Beat + delta) - _dragAnchor.Beat;
        delta = Math.Max(delta, -_dragKeys.Min(k => k.Beat));
        try
        {
            var valueDelta = 0d;
            if (_graphDragScale is { } scale)
            {
                var original = _dragBefore.Tracks[_dragAnchor.Track].First(k => k.Beat == _dragAnchor.Beat).Value;
                valueDelta = scale.ValueAt(scale.Fraction(original) - ImGui.GetMouseDragDelta(ImGuiMouseButton.Left).Y / _graphDragHeight) - original;
            }
            var document = _dragBefore.MoveKeys(_dragKeys, delta, valueDelta);
            if (_history.Preview(document)) ApplyCurrent();
            _selectedKeys.Clear(); _selectedKeys.UnionWith(_dragKeys.Select(k => k with { Beat = k.Beat + delta }));
            FocusKey(_dragAnchor with { Beat = _dragAnchor.Beat + delta });
        }
        catch (InvalidDataException e) { _message = e.Message; _messageError = true; }
    }

    private void BoxSelection(ImDrawListPtr draw, Vector2 origin, Vector2 plot, float width, float rowHeight,
        float height, bool hovered, bool hitKey)
    {
        if (_history.Current.UsesEmbeddedEffects) return;
        var mouse = ImGui.GetIO().MousePos;
        if (mouse.X < -1e20f) { _boxStart = null; return; }
        if (hovered && mouse.X >= plot.X && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !hitKey)
        {
            _boxStart = mouse; _boxAppend = ImGui.GetIO().KeyCtrl;
            _boxOriginal = _boxAppend ? [.. _selectedKeys] : [];
            if (!_boxAppend)
            {
                _selectedKeys.Clear(); _key = null; _inspectorKey = null;
                var row = (int)((mouse.Y - origin.Y) / rowHeight);
                if (row >= 0 && row < _trackNames.Length) Select(_trackNames[row], false);
            }
        }
        if (_boxStart is not { } start) return;
        if (ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            var min = Vector2.Min(start, mouse); var max = Vector2.Max(start, mouse);
            _selectedKeys.Clear(); _selectedKeys.UnionWith(_boxOriginal);
            for (var row = 0; row < _trackNames.Length; row++)
            {
                var y = origin.Y + row * rowHeight + rowHeight / 2;
                if (y < min.Y || y > max.Y) continue;
                var track = _trackNames[row];
                foreach (var key in _preview.Tracks[track])
                {
                    var x = X(key.Beat, plot, width);
                    if (x >= min.X && x <= max.X) { _selectedKeys.Add(new(track, key.Beat)); _selected.Add(track); }
                }
            }
            draw.PushClipRect(plot, new(plot.X + width, origin.Y + height), true);
            draw.AddRectFilled(min, max, Color(.3f, .7f, 1, .12f));
            draw.AddRect(min, max, Accent);
            draw.PopClipRect();
        }
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _boxStart = null;
            if (_selectedKeys.Count > 0) FocusKey(_selectedKeys.OrderBy(k => k.Track, StringComparer.Ordinal).ThenBy(k => k.Beat).First());
        }
    }

    private void EndDrag()
    {
        if (_dragBefore is null) return;
        _history.EndGesture(); _dragBefore = null; _handleDrag = null; _graphDragScale = null;
    }
}
