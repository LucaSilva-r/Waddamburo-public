using System.Numerics;
using ImGuiNET;
using Waddamburo.Catalog;

namespace Waddamburo.Editor;

public sealed partial class EffectsEditorSession
{
    private static EffectKeyPattern? s_clipboard;
    private bool _copyRequested, _pasteRequested, _cancelCommandPopup;
    private (string Track, double Beat)? _insertRequested;
    private string? _pasteTrack;
    private string? _contextTrack;
    private double _contextBeat;
    private EffectKey? _contextKey, _quickKey;
    private double _quickValue;
    private bool _openContext, _openQuick, _quickFocus, _openRepeat, _repeatFocus;
    private Vector2 _quickPosition;
    private EffectKeyPattern? _repeatPattern;
    private int _repeatCopies = 1;
    private double _repeatPeriod;
    private bool _repeatOverwrite;
    private string? _commandError;

    private void OpenKeyValue(string track, int index)
    {
        if (_history.Current.UsesEmbeddedEffects) return;
        EndDrag();
        if (ChooseKey(track, index, false, keepTracks: true) is not { } id) return;
        _quickKey = id; _quickValue = _history.Current.Tracks[track].First(k => k.Beat == id.Beat).Value;
        _quickPosition = ImGui.GetIO().MousePos; _openQuick = _quickFocus = true; _commandError = null;
    }

    private void KeyContext(string track, int? index, double beat)
    {
        if (_history.Current.UsesEmbeddedEffects) return;
        EndDrag();
        _contextKey = index is { } i ? ChooseKey(track, i, false, keepTracks: true) : null;
        _contextTrack = track; _contextBeat = SnapBeat(beat); _openContext = true;
    }

    private void FinishKeyCommands()
    {
        if (_copyRequested)
        {
            try
            {
                EndDrag();
                if (CommitInspector())
                {
                    s_clipboard = EffectKeyPattern.Capture(_history.Current, _selectedKeys);
                    _message = $"Copied {s_clipboard.Keys.Length} keys"; _messageError = false;
                }
            }
            catch (InvalidDataException e) { CommandError(e); }
        }
        if (_pasteRequested && s_clipboard is { } clipboard)
        {
            try
            {
                EndDrag();
                if (CommitInspector()) ApplyPattern(clipboard.Paste(_history.Current, SnapBeat(CurrentBeat), _pasteTrack), "Paste keys");
            }
            catch (InvalidDataException e) { CommandError(e); }
        }
        if (_insertRequested is { } insert)
        {
            try
            {
                EndDrag();
                if (CommitInspector())
                {
                    var value = _preview.ValueAt(insert.Track, insert.Beat);
                    var key = _history.Current.Tracks.GetValueOrDefault(insert.Track, [])
                        .FirstOrDefault(k => k.Beat == insert.Beat, new ChartModKeyframe(insert.Beat, value));
                    Edit(_history.Current.SetKey(insert.Track, key), "Add key on curve");
                    _selectedKeys.Clear(); _selectedKeys.Add(new(insert.Track, insert.Beat)); FocusKey(new(insert.Track, insert.Beat));
                }
            }
            catch (InvalidDataException e) { CommandError(e); }
        }
        _copyRequested = _pasteRequested = false; _pasteTrack = null; _insertRequested = null;
        DrawKeyContext(); DrawQuickValue(); DrawRepeat();
        _cancelCommandPopup = false;
    }

    private void ApplyPattern(EffectPatternEdit edit, string label)
    {
        Edit(edit.Document, label);
        _selectedKeys.Clear(); _selectedKeys.UnionWith(edit.Keys);
        _selected.Clear(); _selected.UnionWith(edit.Keys.Select(k => k.Track));
        foreach (var track in _selected) _expandedGraphs.Add(track);
        if (edit.Keys.Count > 0)
        {
            var first = edit.Keys.OrderBy(k => k.Beat).ThenBy(k => k.Track, StringComparer.Ordinal).First();
            FocusKey(first); _revealTrack = first.Track;
        }
    }

    private void CommandError(Exception error) { _message = error.Message; _messageError = true; _commandError = error.Message; }

    private void DrawKeyContext()
    {
        if (_openContext) { ImGui.OpenPopup("Key actions"); _openContext = false; }
        if (!ImGui.BeginPopup("Key actions")) return;
        ImGui.TextDisabled($"{_selectedKeys.Count} selected keys");
        if (ImGui.MenuItem("Edit value...", "Double-click", false, _contextKey.HasValue) && _contextKey is { } key)
        {
            var index = Array.FindIndex(_preview.Tracks[key.Track].ToArray(), k => k.Beat == key.Beat);
            if (index >= 0) OpenKeyValue(key.Track, index);
        }
        if (ImGui.MenuItem("Add key here", "", false, _contextTrack is not null)) _insertRequested = (_contextTrack!, _contextBeat);
        ImGui.Separator();
        if (ImGui.MenuItem("Copy keys", "Ctrl+C", false, _selectedKeys.Count > 0)) _copyRequested = true;
        if (ImGui.MenuItem("Paste at playhead", "Ctrl+V", false, s_clipboard is not null)) _pasteRequested = true;
        if (ImGui.MenuItem("Paste into this track at playhead", "", false, s_clipboard?.SingleTrack == true && _contextTrack is not null))
        { _pasteRequested = true; _pasteTrack = _contextTrack; }
        if (ImGui.MenuItem("Repeat selection...", "", false, _selectedKeys.Count > 0))
        {
            try
            {
                if (CommitInspector())
                {
                    _repeatPattern = EffectKeyPattern.Capture(_history.Current, _selectedKeys);
                    var beats = _repeatPattern.Keys.Select(k => k.Key.Beat).Distinct().Order().ToArray();
                    var gap = beats.Zip(beats.Skip(1), (a, b) => b - a).DefaultIfEmpty(1).Min();
                    var closed = _repeatPattern.Span > 0 && _repeatPattern.Keys.GroupBy(k => k.Track).All(g =>
                        g.First().Key.Beat == 0 && g.Last().Key.Beat == _repeatPattern.Span && g.First().Key.Value == g.Last().Key.Value);
                    _repeatPeriod = closed ? _repeatPattern.Span : _repeatPattern.Span + gap;
                    _repeatCopies = 1; _repeatOverwrite = false; _commandError = null; _openRepeat = _repeatFocus = true;
                }
            }
            catch (InvalidDataException e) { CommandError(e); }
        }
        if (ImGui.MenuItem("Delete keys", "Del", false, _selectedKeys.Count > 0)) _deleteRequested = true;
        if (_cancelCommandPopup) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawQuickValue()
    {
        if (_openQuick)
        {
            ImGui.SetNextWindowPos(_quickPosition, ImGuiCond.Appearing);
            ImGui.OpenPopup("Key value"); _openQuick = false;
        }
        if (!ImGui.BeginPopup("Key value", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextUnformatted(_quickKey is { } id ? $"{id.Track} | Beat {F(id.Beat)}" : "Key value");
        if (_quickFocus) { ImGui.SetKeyboardFocusHere(); _quickFocus = false; }
        ImGui.SetNextItemWidth(190);
        ImGui.InputDouble("Value", ref _quickValue, 0, 0, "%.12g");
        var apply = ImGui.IsKeyPressed(ImGuiKey.Enter, false);
        apply |= ImGui.Button("Apply"); ImGui.SameLine();
        if (ImGui.Button("Cancel") || _cancelCommandPopup) ImGui.CloseCurrentPopup();
        else if (apply && _quickKey is { } key)
        {
            try
            {
                var old = _history.Current.Tracks[key.Track].First(k => k.Beat == key.Beat);
                Edit(_history.Current.SetKey(key.Track, old with { Value = _quickValue }, key.Beat), "Edit key value");
                FocusKey(key); ImGui.CloseCurrentPopup();
            }
            catch (InvalidDataException e) { _commandError = e.Message; }
        }
        if (_commandError is { } error) ImGui.TextWrapped(error);
        ImGui.EndPopup();
    }

    private void DrawRepeat()
    {
        if (_openRepeat) { ImGui.OpenPopup("Repeat keys"); _openRepeat = false; }
        if (!ImGui.BeginPopupModal("Repeat keys", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextUnformatted($"Repeat {_repeatPattern?.Keys.Length ?? 0} keys after the original selection.");
        if (_repeatFocus) { ImGui.SetKeyboardFocusHere(); _repeatFocus = false; }
        ImGui.SetNextItemWidth(150); ImGui.InputInt("Additional copies", ref _repeatCopies);
        ImGui.SetNextItemWidth(150); ImGui.InputDouble("Period (beats)", ref _repeatPeriod, 0, 0, "%.12g");
        ImGui.Checkbox("Replace existing keys", ref _repeatOverwrite);
        ImGui.TextUnformatted("Matching shared endpoints keep their incoming curve.");
        if (_commandError is { } error) ImGui.TextWrapped(error);
        if (ImGui.Button("Repeat"))
        {
            try
            {
                if (_repeatPattern is { } pattern)
                    ApplyPattern(pattern.Repeat(_history.Current, _repeatCopies, _repeatPeriod, _repeatOverwrite), "Repeat keys");
                ImGui.CloseCurrentPopup();
            }
            catch (InvalidDataException e) { _commandError = e.Message; }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel") || _cancelCommandPopup) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}
