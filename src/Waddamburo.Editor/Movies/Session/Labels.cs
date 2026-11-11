using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private bool _showLabelTrack = true;
    private string _labelFilter = "";

    // Frame numbers have their own ruler. Names live in staggered, clickable flags below it.
    private float timelineLabels(LumenPlayer player, string path, JsonObject? labels, Vector2 origin, float width, float step)
    {
        var entries = (labels ?? []).Select(label => (Name: label.Key, Frame: (int)label.Value!)).OrderBy(label => label.Frame).ToArray();
        if (entries.Length == 0) return 0;
        void jump(int frame) { player.InspectGoto(path, frame, play: false); Paused = true; }
        ImGui.SetCursorScreenPos(origin + new Vector2(2, RowHeight + 1));
        if (ImGui.SmallButton($"Labels ({entries.Length})")) ImGui.OpenPopup("##timeline labels");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Browse and search every label. Click a label to jump to its frame.");
        if (ImGui.BeginPopup("##timeline labels"))
        {
            ImGui.Checkbox("Show label track", ref _showLabelTrack);
            ImGui.SetNextItemWidth(360);
            ImGui.InputTextWithHint("##label search", "Search labels", ref _labelFilter, 128);
            ImGui.BeginChild("##label list", new(400, 260));
            foreach (var label in entries.Where(label => label.Name.Contains(_labelFilter, StringComparison.OrdinalIgnoreCase)))
                if (ImGui.Selectable($"{label.Frame,5}   {label.Name}")) jump(label.Frame);
            ImGui.EndChild();
            ImGui.EndPopup();
        }
        if (!_showLabelTrack) return RowHeight;
        var placed = TimelineLabelLayout.Arrange(entries.Select(label => (label.Name, label.Frame, ImGui.CalcTextSize(label.Name).X + 12)), step, width);
        var namedRows = placed.Where(label => label.Row >= 0).Select(label => label.Row + 1).DefaultIfEmpty(0).Max();
        var overflow = placed.Where(label => label.Row < 0).ToArray();
        var rows = namedRows + (overflow.Length > 0 ? 1 : 0);
        var draw = ImGui.GetWindowDrawList();
        var track = origin + new Vector2(NameColumn, RowHeight);
        draw.AddRectFilled(track, track + new Vector2(width, rows * RowHeight), color(.12f, .13f, .15f));
        foreach (var label in placed.Where(label => label.Row >= 0))
        {
            var top = track + new Vector2(label.Left, label.Row * RowHeight + 2);
            var size = new Vector2(label.Width, RowHeight - 4);
            var anchor = track + new Vector2(label.Frame * step, label.Row * RowHeight);
            draw.AddLine(anchor, new(anchor.X, top.Y + size.Y), KeySelected);
            draw.AddRectFilled(top, top + size, color(.28f, .23f, .16f), 3);
            draw.PushClipRect(top, top + size, true);
            draw.AddText(top + new Vector2(6, 1), KeySelected, label.Name);
            draw.PopClipRect();
            ImGui.PushID(label.Name);
            ImGui.SetCursorScreenPos(top);
            if (ImGui.InvisibleButton("##label flag", size)) jump(label.Frame);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{label.Name}\nFrame {label.Frame}");
            if (ImGui.BeginPopupContextItem("##label actions"))
            {
                if (ImGui.MenuItem("Go to frame " + label.Frame)) jump(label.Frame);
                if (ImGui.MenuItem("Delete label", "", false, _document is { ReadOnly: false }))
                    attempt(() => _document!.Edit(_ => labels!.Remove(label.Name)));
                ImGui.EndPopup();
            }
            ImGui.PopID();
        }
        if (overflow.Length > 0)
        {
            var y = track.Y + namedRows * RowHeight;
            draw.AddText(new(origin.X + 4, y + 3), Muted, $"{overflow.Length} more in label list");
            foreach (var label in overflow)
            {
                var center = new Vector2(track.X + label.Frame * step, y + RowHeight / 2);
                draw.AddTriangleFilled(center + new Vector2(-4, -4), center + new Vector2(4, -4), center + new Vector2(0, 4), KeySelected);
            }
            ImGui.SetCursorScreenPos(new(track.X, y));
            ImGui.InvisibleButton("##overflow labels", new(width, RowHeight));
            if (ImGui.IsItemHovered())
            {
                var near = overflow.Where(label => Math.Abs(track.X + label.Frame * step - ImGui.GetIO().MousePos.X) <= 7).ToArray();
                if (near.Length > 0) ImGui.SetTooltip(string.Join("\n", near.Select(label => $"{label.Name} — frame {label.Frame}")));
            }
        }
        return rows * RowHeight;
    }
}
