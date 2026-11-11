using ImGuiNET;

namespace Waddamburo.Editor;

/// <summary>Project-authored editor styling using the existing Dear ImGui widgets.</summary>
internal static class EditorTheme
{
    public static void Apply()
    {
        var style = ImGui.GetStyle();
        style.WindowPadding = new(10, 8);
        style.FramePadding = new(7, 4);
        style.ItemSpacing = new(7, 5);
        style.ItemInnerSpacing = new(6, 4);
        style.WindowRounding = 5; style.ChildRounding = 4; style.FrameRounding = 4;
        style.PopupRounding = 6; style.GrabRounding = 4; style.ScrollbarRounding = 8;
        style.WindowBorderSize = 1; style.FrameBorderSize = 0;
        style.ScrollbarSize = 12;
        style.Colors[(int)ImGuiCol.Text] = new(.86f, .87f, .89f, 1);
        style.Colors[(int)ImGuiCol.TextDisabled] = new(.52f, .55f, .59f, 1);
        style.Colors[(int)ImGuiCol.WindowBg] = new(.145f, .155f, .17f, 1);
        style.Colors[(int)ImGuiCol.ChildBg] = new(.12f, .13f, .145f, 1);
        style.Colors[(int)ImGuiCol.PopupBg] = new(.18f, .19f, .21f, .99f);
        style.Colors[(int)ImGuiCol.Border] = new(.25f, .27f, .3f, .7f);
        style.Colors[(int)ImGuiCol.TitleBg] = new(.115f, .125f, .14f, 1);
        style.Colors[(int)ImGuiCol.TitleBgActive] = new(.19f, .22f, .26f, 1);
        style.Colors[(int)ImGuiCol.FrameBg] = new(.23f, .25f, .28f, 1);
        style.Colors[(int)ImGuiCol.FrameBgHovered] = new(.30f, .33f, .37f, 1);
        style.Colors[(int)ImGuiCol.FrameBgActive] = new(.33f, .37f, .42f, 1);
        style.Colors[(int)ImGuiCol.Button] = new(.25f, .28f, .32f, 1);
        style.Colors[(int)ImGuiCol.ButtonHovered] = new(.34f, .39f, .45f, 1);
        style.Colors[(int)ImGuiCol.ButtonActive] = new(.37f, .46f, .56f, 1);
        style.Colors[(int)ImGuiCol.Header] = new(.25f, .33f, .42f, .8f);
        style.Colors[(int)ImGuiCol.HeaderHovered] = new(.31f, .39f, .48f, 1);
        style.Colors[(int)ImGuiCol.HeaderActive] = new(.35f, .44f, .54f, 1);
        style.Colors[(int)ImGuiCol.CheckMark] = new(.65f, .78f, .91f, 1);
        style.Colors[(int)ImGuiCol.SliderGrab] = new(.51f, .64f, .77f, 1);
        style.Colors[(int)ImGuiCol.SliderGrabActive] = new(.7f, .81f, .92f, 1);
        style.Colors[(int)ImGuiCol.Separator] = new(.27f, .29f, .32f, 1);
    }
}
