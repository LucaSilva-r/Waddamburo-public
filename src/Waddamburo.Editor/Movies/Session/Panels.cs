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
                if (_document is { ReadOnly: false } && ImGui.BeginTabItem("Code"))
                {
                    code();
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Game calls"))
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
