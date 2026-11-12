using ImGuiNET;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    // Bottom-panel tabs share one dock region. Each feature draws its own contents.
    private void bottom(EditorPanel rect, LumenPlayer player, IReadOnlyList<LumenInstanceInfo> clips)
    {
        if (panel("##movie bottom", rect, ImGuiWindowFlags.NoTitleBar))
        {
            if (ImGui.BeginTabBar("##bottom tabs"))
            {
                if (_document is { ReadOnly: false } && ImGui.BeginTabItem("To do"))
                {
                    todo();
                    ImGui.EndTabItem();
                }
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
                // Scripts, what the movie asks of the engine, what the game calls on it, and what came of it: one place.
                // Diagnostic: WADDAMBURO_EDITOR_TAB=Scripts opens on that tab, for a screenshot run.
                // (The only call that takes flags also draws a close button, so it is used for those first frames alone.)
                var open = true;
                if (ImGui.GetFrameCount() < 3 && Environment.GetEnvironmentVariable("WADDAMBURO_EDITOR_TAB") == "Scripts"
                    ? ImGui.BeginTabItem("Scripts", ref open, ImGuiTabItemFlags.SetSelected) : ImGui.BeginTabItem("Scripts"))
                {
                    host(new(rect.X, rect.Y, rect.Width, rect.Height), player);
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }
        ImGui.End();
    }
}
