using System.Numerics;
using ImGuiNET;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private readonly HashSet<string> _selection = new(StringComparer.Ordinal);
    private string? _selectionAnchor;
    private Vector2? _selectionBox;

    private void toggleSelection(string path)
    {
        if (!_selection.Remove(path)) { _selection.Add(path); _selected = path; }
        else if (_selected == path) _selected = _selection.LastOrDefault();
        _selectionAnchor = path;
        _selectedAsset = null;
    }

    private void hierarchySelection(IReadOnlyList<LumenInstanceInfo> clips, string path)
    {
        var io = ImGui.GetIO();
        if (io.KeyShift && _selectionAnchor is { } anchor)
        {
            var visible = new List<string>();
            int? closed = null;
            foreach (var clip in clips)
            {
                if (_filter.Length > 0)
                {
                    if (clip.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)) visible.Add(clip.Path);
                    continue;
                }
                if (closed is { } level && clip.Level > level) continue;
                closed = null;
                visible.Add(clip.Path);
                if (_collapsed.Contains(clip.Path) || clip.Level >= 2 && !_open.Contains(clip.Path)) closed = clip.Level;
            }
            var from = visible.IndexOf(anchor);
            var to = visible.IndexOf(path);
            if (from >= 0 && to >= 0)
            {
                if (!io.KeyCtrl) _selection.Clear();
                foreach (var item in visible.Skip(Math.Min(from, to)).Take(Math.Abs(from - to) + 1)) _selection.Add(item);
                _selected = path;
                _selectedAsset = null;
                return;
            }
        }
        if (io.KeyCtrl) toggleSelection(path); else selectClip(path);
    }

    private void selectionBox(LumenPlayer player, IReadOnlyList<LumenInstanceInfo> clips, EffectViewport view, Vector2 point)
    {
        if (_selectionBox is not { } start) return;
        var min = Vector2.Min(start, point);
        var max = Vector2.Max(start, point);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(view.ToScreen(min), view.ToScreen(max), color(.3f, .6f, 1, .15f));
        draw.AddRect(view.ToScreen(min), view.ToScreen(max), Outline);
        if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left)) return;
        if (Vector2.Distance(view.ToScreen(start), view.ToScreen(point)) < 4)
        {
            var hit = player.InspectPick(point.X, point.Y);
            if (hit is not null) toggleSelection(hit);
        }
        else
        {
            foreach (var clip in clips)
                if (clip.Level > 0 && clip.Visible && !clip.HiddenByHost && clip.Multiply.Alpha > 0
                    && player.TryInspectBounds(clip.Path, out var box)
                    && box.X <= max.X && box.Y <= max.Y && box.X + box.Width >= min.X && box.Y + box.Height >= min.Y)
                {
                    _selection.Add(clip.Path);
                    _selected = clip.Path;
                }
            _selectedAsset = null;
        }
        _selectionBox = null;
    }
}
