using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Lumen;

public sealed partial class SongSelectHostBinding
{
    private SongChartCarousel? _cursorChoices;
    private readonly HashSet<int> _animatedChartConfirmations = [];

    private void bindChartCursorStyles()
    {
        requirePlayer().TryWrapScriptMethod("_global.CourseSelect.prototype", "UpdateCursor", (_, original) =>
        {
            if (!ChartCarouselReady) return original();
            updateChartCursorStyles();
            return LumenHostValue.Undefined;
        });
    }

    private void updateChartCursorStyles()
    {
        if (!ChartCarouselReady || ChartCarousel is not { } choices) return;
        var player = requirePlayer();
        if (!ReferenceEquals(_cursorChoices, choices))
        {
            _cursorChoices = choices;
            _animatedChartConfirmations.Clear();
        }
        // The authored four-course controller thinks two different extra Oni charts are the same
        // course. Split only matching choices; settings use their real native indices.
        // A fixed player's cursor keeps its decided animation, as in UpdateCursor.
        var fixedState = player.ReadScriptValue("_global.CourseCursor.STATE_COURSE_DECIDED");
        bool isFixed(int side) => choices.IsConfirmed(side) && fixedState is double state
            && Equals(player.ReadScriptValue($"main.courseScene.courseSelect.cursor.{side}.state"), state);
        var two = choices.Sides.Length == 2;
        var sharedChart = two && choices.Sides.All(side => !choices.IsUtility(side))
            && choices.Selected(0) == choices.Selected(1);
        var sharedUtility = two && choices.Sides.All(choices.IsUtility)
            && player.ReadScriptValue("main.courseScene.courseSelect.cursor.0.currentIndex") is double index
            && Equals(player.ReadScriptValue("main.courseScene.courseSelect.cursor.1.currentIndex"), index);
        var duet = two && !choices.Sides.Any(isFixed) && (sharedChart || sharedUtility);
        var type = player.ReadScriptValue(duet ? "_global.CourseCursor.INDEX_DUET" : "_global.CourseCursor.INDEX_SOLO");
        foreach (var side in choices.Sides)
        {
            var cursor = $"main.courseScene.courseSelect.cursor.{side}";
            if (!isFixed(side) && type is double desired
                && player.ReadScriptValue(cursor + ".typeIndex") is double actual && actual != desired)
                player.TryCallScriptMethod(cursor, "SetDuet", [LumenHostValue.FromBoolean(duet)]);
            player.TryCallScriptMethod(cursor, "SetOption", [LumenHostValue.FromBoolean(choices.IsUtility(side))]);
            if (!choices.IsConfirmed(side) && _animatedChartConfirmations.Contains(side))
            {
                // Custom confirms do not enter the native controller's WAIT state. An undo owns
                // their cancellation too, including a decide tween that is still latched as busy.
                player.TryWriteScriptValue(cursor + ".isTween", false);
                if (player.TryCallScriptMethod(cursor, "Tween_Cancel", []))
                {
                    player.TrySetInstanceVisibility(side == 0 ? "main/courseSelect_/cursor_1p_" : "main/courseSelect_/cursor_2p_", true);
                    _animatedChartConfirmations.Remove(side);
                }
                continue;
            }
            if (player.ReadScriptValue(cursor + ".isTween") is true) continue;
            if (choices.IsConfirmed(side) && !_animatedChartConfirmations.Contains(side))
            {
                if (player.TryCallScriptMethod(cursor, "Tween_Decide", [LumenHostValue.FromBoolean(false)]))
                    _animatedChartConfirmations.Add(side);
            }
        }
    }

    public void UndoChartConfirmations()
    {
        if (ChartCarousel is not { } choices) return;
        foreach (var side in choices.Sides.Where(choices.IsConfirmed)) choices.UndoConfirm(side);
        updateChartCursorStyles();
    }
}
