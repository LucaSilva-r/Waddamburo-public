using System.Numerics;
using ImGuiNET;
using Waddamburo.Catalog;

namespace Waddamburo.Editor;

public sealed partial class EffectsEditorSession
{
    private ChartReference _reference = null!;
    private int _noteBranch;
    private static readonly string[] BranchNames = ["Normal", "Expert", "Master"];

    private void ChartControls()
    {
        ImGui.SameLine(); ImGui.SetNextItemWidth(125);
        ImGui.Combo("Snap", ref _snapChoice, SnapNames, SnapNames.Length);
        if (_reference.HasBranches)
        {
            ImGui.SameLine(); ImGui.SetNextItemWidth(100);
            if (ImGui.Combo("Note branch", ref _noteBranch, BranchNames, BranchNames.Length))
                _reference = new(_sourceChart, _preview.BeatMap, (ChartBranch)_noteBranch);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Chooses timeline notes, timing markers and note snapping. Gameplay keeps its practice route; effect tracks are shared.");
        }
        if (_snapChoice == Snaps.Length && _reference.SnapBeats.IsEmpty)
        { ImGui.SameLine(); ImGui.TextDisabled("No notes to snap to"); }
    }

    private void ChartRuler(ImDrawListPtr draw, Vector2 ruler, float width, bool hovered)
    {
        var mouse = ImGui.GetIO().MousePos;
        var last = float.NegativeInfinity;
        foreach (var measure in _reference.Measures)
        {
            var x = X(measure.Beat, ruler, width);
            if (x < ruler.X || x > ruler.X + width) continue;
            draw.AddLine(new(x, ruler.Y), new(x, ruler.Y + 40), Bar, 1.5f);
            if (x - last >= 55) { draw.AddText(new(x + 3, ruler.Y + 2), Accent, $"M{measure.Number}"); last = x; }
            if (hovered && Math.Abs(mouse.X - x) < 5)
                ImGui.SetTooltip($"Measure {measure.Number} | Beat {F(measure.Beat)}{(measure.Visible ? "" : " | hidden game bar")}");
        }
        GridLines(draw, ruler + new Vector2(0, 20), new(width, 20), true);
    }

    private void ChartLanes(Vector2 origin, float labelWidth, float width)
    {
        var draw = ImGui.GetWindowDrawList();
        var plot = origin + new Vector2(labelWidth, 0);
        draw.AddText(origin + new Vector2(6, 2), Muted, "Tempo / meter");
        ImGui.SetCursorScreenPos(plot);
        ImGui.InvisibleButton("Timing markers", new(width, 22));
        var hovered = ImGui.IsItemHovered();
        Navigate(plot, width, hovered);
        draw.PushClipRect(plot, plot + new Vector2(width, 22), true);
        var last = float.NegativeInfinity;
        foreach (var marker in _reference.Timing)
        {
            var x = X(marker.Beat, plot, width);
            if (x < plot.X || x > plot.X + width) continue;
            draw.AddLine(new(x, plot.Y), new(x, plot.Y + 22), Color(.76f, .64f, .45f));
            if (x >= last)
            {
                draw.AddText(new(x + 4, plot.Y + 3), Text, marker.Label);
                last = x + ImGui.CalcTextSize(marker.Label).X + 16;
            }
            if (hovered && Math.Abs(ImGui.GetIO().MousePos.X - x) < 7)
                ImGui.SetTooltip($"{marker.Label}\nBeat {F(marker.Beat)}");
        }
        draw.PopClipRect();
        var notes = plot + new Vector2(0, 23);
        draw.AddText(origin + new Vector2(6, 27), Muted, "Chart notes" + (_reference.HasBranches ? " / " + BranchNames[_noteBranch] : ""));
        ImGui.SetCursorScreenPos(notes);
        ImGui.InvisibleButton("Chart note lane", new(width, 28), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle);
        hovered = ImGui.IsItemHovered(); Navigate(notes, width, hovered);
        draw.AddRectFilled(notes, notes + new Vector2(width, 28), Background);
        draw.PushClipRect(notes, notes + new Vector2(width, 28), true);
        GridLines(draw, notes, new(width, 28), false);
        ReferenceNote? hit = null;
        foreach (var note in _reference.Notes)
        {
            if (note.EndBeat < _view.Start || note.Beat > _view.Start + _view.Span) continue;
            var x = X(note.Beat, notes, width); var end = X(note.EndBeat, notes, width);
            var center = new Vector2(x, notes.Y + 14);
            var color = note.IsLong ? Color(.92f, .72f, .3f) : note.Ka ? Color(.35f, .69f, .87f) : Color(.91f, .42f, .38f);
            if (note.IsLong)
            {
                draw.AddRectFilled(new(x, center.Y - 4), new(end, center.Y + 4), color, 3);
                draw.AddLine(new(end, center.Y - 7), new(end, center.Y + 7), color, 2);
            }
            draw.AddCircleFilled(center, note.Strong ? 6 : 4.5f, color);
            if (hovered && Math.Abs(ImGui.GetIO().MousePos.X - x) < 7) hit = note;
        }
        DrawPlayhead(draw, notes, new(width, 28));
        draw.PopClipRect();
        if (hit is { } selected)
            ImGui.SetTooltip($"{selected.Label} | Beat {F(selected.Beat)}{(selected.IsLong ? " - " + F(selected.EndBeat) : "")} | {selected.Time.TotalSeconds:0.###} s\nClick to seek. Snap: Chart notes aligns edits to this route's notes and long-note ends.");
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            SeekBeat(hit?.Beat ?? _view.BeatAt((ImGui.GetIO().MousePos.X - notes.X) / width));
    }
}
