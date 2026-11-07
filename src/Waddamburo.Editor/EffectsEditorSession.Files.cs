using ImGuiNET;
using Waddamburo.Catalog;

namespace Waddamburo.Editor;

public sealed partial class EffectsEditorSession
{
    private readonly Func<string, TaikoCourse?, EffectEditorSource>? _openTja;
    private string? _tjaPath, _pendingPath;
    private TaikoCourse? _pendingCourse;
    private IReadOnlyList<TaikoCourse> _courses = [];
    private ChartFileWatch? _watch;
    private Task<string?>? _fileDialog;
    private enum FileAction { OpenTja, Import, Export }
    private FileAction _fileAction;
    private readonly EditorReadout _readout = new();

    private void StartWatching(bool refresh = false)
    {
        _watch?.Dispose(); _watch = null;
        if (_tjaPath is null || _openTja is null) return;
        try { _watch = new(_tjaPath); if (refresh) _watch.Schedule(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { _message = "File watch unavailable: " + e.Message; _messageError = true; }
    }

    private void FileMenu()
    {
        if (!ImGui.BeginMenuBar()) return;
        if (ImGui.BeginMenu("File"))
        {
            var idle = _fileDialog is null && _pending == PendingAction.None;
            if (ImGui.MenuItem("Open TJA...", "", false, idle && _openTja is not null)) Pick(FileAction.OpenTja);
            if (ImGui.MenuItem("Reload TJA now", "", false, _watch is not null)) _watch?.Schedule();
            if (ImGui.MenuItem("Frame entire chart")) _view.Frame(0, _preview.BeatAt(_sourceChart.Duration));
            ImGui.Separator();
            if (ImGui.MenuItem("Import effects...", "", false, idle && _files is not null)) Pick(FileAction.Import);
            if (ImGui.MenuItem("Export effects...", "", false, idle && !_history.Current.UsesEmbeddedEffects)) Pick(FileAction.Export);
            if (ImGui.MenuItem("Save", "Ctrl+S", false, _files is not null && !_history.Current.UsesEmbeddedEffects)) _saveRequested = true;
            if (ImGui.MenuItem("Reload saved effects", "", false, _files is not null)) _reloadRequested = true;
            if (_files is { SongPath: not null } legacy && File.Exists(legacy.PlayerPath)
                && ImGui.MenuItem("Import previous player override..."))
            { _pendingPath = legacy.PlayerPath; Request(PendingAction.ImportEffects); }
            ImGui.Separator();
            if (_files is { } files) ImGui.TextDisabled("Save: " + Path.GetFileName(files.PrimaryPath));
            if (ImGui.MenuItem("Close editor", "F8 / Esc")) _closeQueued = true;
            ImGui.EndMenu();
        }
        if (_courses.Count > 1 && ImGui.BeginMenu("Course"))
        {
            foreach (var course in _courses)
                if (ImGui.MenuItem(course.ToString(), "", _files?.Course == course))
                { _pendingPath = _tjaPath; _pendingCourse = course; Request(PendingAction.OpenSource); }
            ImGui.EndMenu();
        }
        ImGui.TextDisabled(_tjaPath is null ? "Effects editor | Unranked" : Path.GetFileName(_tjaPath) + " | Watching | Unranked");
        ImGui.EndMenuBar();
    }

    private void Pick(FileAction action)
    {
        if (_fileDialog is not null) return;
        EndDrag(); if (!CommitInspector()) return;
        _transport.Clock.SetPaused(true);
        _fileAction = action;
        _fileDialog = _application.PickFileAsync(action == FileAction.Export,
            action == FileAction.OpenTja ? "TJA chart" : "Waddamburo effects", action == FileAction.OpenTja ? "tja" : "wddmod",
            action == FileAction.Export ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"effects-{_files?.Course ?? TaikoCourse.Oni}.wddmod")
                : action == FileAction.OpenTja ? _tjaPath : _files?.PrimaryPath);
    }

    private void PollFiles()
    {
        if (_fileDialog is { IsCompleted: true } dialog)
        {
            _fileDialog = null;
            try
            {
                if (dialog.GetAwaiter().GetResult() is { } path)
                {
                    if (_fileAction == FileAction.Export)
                    { if (CommitInspector()) { EffectEditorFiles.Export(path, _history.Current); _message = "Exported " + Path.GetFileName(path); _messageError = false; } }
                    else
                    {
                        _pendingPath = path; _pendingCourse = null;
                        Request(_fileAction == FileAction.OpenTja ? PendingAction.OpenSource : PendingAction.ImportEffects);
                    }
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { _message = "File operation failed: " + e.Message; _messageError = true; }
        }
        if (_pending != PendingAction.None || _dragBefore is not null || ImGui.IsAnyItemActive()
            || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) return;
        if (_watch?.Poll(() =>
            {
                if (!CommitInspector()) throw new InvalidDataException("Finish correcting the key value before reloading the chart.");
                if (_openTja is { } open && _tjaPath is { } path)
                    SetSource(open(path, _files?.Course), preserveEdits: true);
            }) == true)
        { _message = "TJA updated; effect edits retained"; _messageError = false; }
        else if (_watch?.Error is { } error)
        { _message = "Waiting for a valid TJA save: " + error; _messageError = true; }
    }

    private void OpenSource()
    {
        try
        {
            if (_openTja is null || _pendingPath is null) return;
            SetSource(_openTja(_pendingPath, _pendingCourse), preserveEdits: false);
            StartWatching();
            _message = "TJA opened; save writes beside the chart"; _messageError = false;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or OverflowException)
        { _message = "TJA not opened: " + e.Message; _messageError = true; }
    }

    private void SetSource(EffectEditorSource source, bool preserveEdits)
    {
        _sourceChart = source.Chart; _transport = source.Transport; _files = source.Files;
        _tjaPath = source.TjaPath; _courses = source.Courses ?? [source.Files.Course];
        if (!preserveEdits)
        {
            _history = new(OpeningDocument(source.Chart, source.Files));
            _selectedKeys.Clear(); _key = null; _inspectorKey = null; _primary = null;
            _selected.Clear(); _expandedGraphs.Clear(); _noteBranch = 0;
        }
        ApplyCurrent(); _reference = new(_sourceChart, _preview.BeatMap, (ChartBranch)_noteBranch);
        _readout.Update(Environment.TickCount64, _transport.Clock.Position, CurrentBeat, true);
        if (!preserveEdits) _view.Frame(0, _preview.BeatAt(_sourceChart.Duration));
    }

    // The keyframes of the file that plays; effects without one (a file that could not be read) show read-only.
    private static EffectDocument OpeningDocument(PlayableChart chart, EffectEditorFiles? files)
    {
        try
        {
            if (files?.Load() is { } document) return document;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { }
        return chart.Effects.Mods.IsEmpty ? EffectDocument.Empty : EffectDocument.EmbeddedPreview;
    }

    private void ImportEffects()
    {
        try
        {
            if (_files is null || _pendingPath is null) return;
            Edit(_files.Import(_pendingPath), "Import effects (Save to apply on future plays)");
            _message = "Imported " + Path.GetFileName(_pendingPath) + "; Save applies it on future plays"; _messageError = false;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { _message = "Import failed: " + e.Message; _messageError = true; }
    }

    private void TransportReadout()
    {
        _readout.Update(Environment.TickCount64, _transport.Clock.Position, CurrentBeat, _transport.Clock.Paused);
        var timing = _reference.TimingAt(_readout.Position);
        var measure = _reference.MeasureAt(_readout.Beat)?.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "--";
        ImGui.TextUnformatted("M" + measure);
        ImGui.SameLine(65); ImGui.TextUnformatted($"Beat {_readout.Beat:0.00}");
        ImGui.SameLine(185); ImGui.TextUnformatted($"{timing.BeatsPerMinute:0.##} BPM {timing.BeatsPerMeasure}/{timing.BeatUnit}");
        ImGui.SameLine(350); ImGui.TextUnformatted($"{_readout.Position.TotalSeconds:0.00} s");
        ImGui.SameLine(450); ImGui.TextUnformatted(_history.Dirty ? "Unsaved" : "Saved");
        ImGui.SameLine(535); ImGui.TextUnformatted($"A: {LoopLabel(_transport.LoopStart)}  B: {LoopLabel(_transport.LoopEnd)}");
    }
}
