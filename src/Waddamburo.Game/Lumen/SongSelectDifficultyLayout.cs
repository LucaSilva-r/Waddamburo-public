using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

public sealed partial class SongSelectHostBinding
{
    private void layoutDifficultySelect(LumenPlayer player)
    {
        const string root = "main/courseSelect_/";
        if (!player.TryGetInstanceTransform("main/courseSelect_", out var parent)) return;
        var scale = Math.Abs(parent.M11);
        if (scale < .001f) return;
        for (var index = 0; index < 4; index++)
            offset(root + $"board_course{index}", DifficultySelectLayout.ChartX, DifficultySelectLayout.ChartY);
        for (var index = 0; index < 3; index++)
            offset(root + $"board_setting{index}", DifficultySelectLayout.SettingsX, DifficultySelectLayout.SettingsY);
        if (player.TryGetInstanceLocalTransform(root + "board_course0", out var first)
            && player.TryGetInstanceLocalTransform(root + "board_setting2", out var settings))
            foreach (var side in _sides)
            {
                var path = root + $"cursor_{side + 1}p_";
                if (!player.TryGetInstanceLocalTransform(path, out var cursor)) continue;
                // Follow the authored cursor tween across the new gap. Switching
                // offsets as soon as the logical type changed caused a small jump.
                // Both native cursors use the same local origin. A second-player
                // subtraction incorrectly left P2 between Settings and Easy.
                var blend = Math.Clamp((cursor.X - settings.X) / (first.X - settings.X), 0, 1);
                var x = DifficultySelectLayout.SettingsX + blend * (DifficultySelectLayout.ChartX - DifficultySelectLayout.SettingsX);
                var y = DifficultySelectLayout.SettingsY + blend * (DifficultySelectLayout.ChartY - DifficultySelectLayout.SettingsY);
                offset(path, x, y);
            }
        if (player.TryGetInstanceLocalTransform("main/coursePanel_/wd_course_header", out var header)
            && player.TryGetInstanceTransform("main/coursePanel_", out var panel) && Math.Abs(panel.M22) > .001f)
            player.TrySetInstanceLayoutPosition("main/coursePanel_/wd_course_header", header.X,
                header.Y + DifficultySelectLayout.TitleY / Math.Abs(panel.M22));
        // Keep native title lettering; use the free space above the lowered panel.
        player.TrySetInstanceLayoutTransform("main/coursePanel_/wd_course_header/wd_course_title_mask",
            new((DifficultySelectLayout.TitleRight - DifficultySelectLayout.TitleLeft) / 640, 0, 0,
                DifficultySelectLayout.TitleHeight / 720f,
                (DifficultySelectLayout.TitleLeft + DifficultySelectLayout.TitleRight) / 2 - 640, DifficultySelectLayout.TitleHeight / 2f - 24));

        void offset(string path, float x, float y)
        {
            if (player.TryGetInstanceLocalTransform(path, out var authored))
                player.TrySetInstanceLayoutPosition(path, authored.X + x / scale, authored.Y + y / scale);
        }
    }
}
