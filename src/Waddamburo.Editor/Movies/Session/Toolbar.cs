using ImGuiNET;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private bool _scriptsEnabled = true;
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
                : _document is null ? "movie package" : "movie package: changes are saved as you make them");
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
                    Paused = !_scriptsEnabled;
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Restarts the preview. Enabling scripts resumes playback; invoke startup callbacks again if needed. Disable for timeline-only art editing.");
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
}
