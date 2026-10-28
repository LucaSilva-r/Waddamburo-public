using Waddamburo.Game.SongSelect;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Flow;

/// <summary>Routes each drum independently between custom charts and the native settings.</summary>
internal static class ChartCarouselControls
{
    public static SdlKeyboardSnapshot Route(SdlKeyboardSnapshot keys, SongChartCarousel choices,
        Func<int, bool> returnSelected, Action cancel, Action undo, Action<int> confirm)
    {
        if (keys.IsDown(SdlKeyboardKey.Backspace) || keys.IsDown(SdlKeyboardKey.Escape))
        {
            if (choices.Sides.Any(choices.IsConfirmed)) undo();
            else cancel();
            return SdlKeyboardSnapshot.Empty;
        }
        var consumed = new HashSet<SdlKeyboardKey>();
        foreach (var side in choices.Sides)
        {
            var primary = side == choices.Sides[0];
            var left = side == 0 ? SdlKeyboardKey.D : SdlKeyboardKey.Z;
            var right = side == 0 ? SdlKeyboardKey.K : SdlKeyboardKey.V;
            var don1 = side == 0 ? SdlKeyboardKey.F : SdlKeyboardKey.X;
            var don2 = side == 0 ? SdlKeyboardKey.J : SdlKeyboardKey.C;
            var decide = keys.IsDown(don1) || keys.IsDown(don2) || primary && keys.IsDown(SdlKeyboardKey.Enter);
            if (choices.IsUtility(side))
            {
                if (decide && returnSelected(side)) { cancel(); return SdlKeyboardSnapshot.Empty; }
                continue;
            }
            if (keys.IsDown(left) || primary && (keys.IsDown(SdlKeyboardKey.Left) || keys.IsDown(SdlKeyboardKey.WheelUp)))
            {
                if (choices.CanEnterUtilities(side))
                {
                    choices.EnterUtilities(side);
                    // Keep this drum's left rim for the native controller, while the other drum
                    // can still move or confirm a custom chart in this same input snapshot.
                    continue;
                }
                choices.Move(side, -1);
            }
            if (keys.IsDown(right) || primary && (keys.IsDown(SdlKeyboardKey.Right) || keys.IsDown(SdlKeyboardKey.WheelDown)))
                choices.Move(side, 1);
            if (primary && keys.IsDown(SdlKeyboardKey.PageUp)) choices.Move(side, -SongChartCarousel.VisibleCount);
            if (primary && keys.IsDown(SdlKeyboardKey.PageDown)) choices.Move(side, SongChartCarousel.VisibleCount);
            if (decide) confirm(side);
            consumed.UnionWith([left, right, don1, don2]);
            if (primary) consumed.UnionWith([SdlKeyboardKey.Left, SdlKeyboardKey.Right,
                SdlKeyboardKey.WheelUp, SdlKeyboardKey.WheelDown, SdlKeyboardKey.Enter,
                SdlKeyboardKey.PageUp, SdlKeyboardKey.PageDown]);
        }
        return new SdlKeyboardSnapshot(keys.PressedKeys.Where(key => !consumed.Contains(key)),
            keys.Presses.Where(press => !consumed.Contains(press.Key)), keys.Timestamp);
    }
}
