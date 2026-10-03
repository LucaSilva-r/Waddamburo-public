using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Gameplay;

/// <summary>
/// The player's lane board: score, combo, drum and course display. Blue and later draw all of it in one
/// movie (lane_obi) driven by callbacks; earlier releases split it into lane_obi_don_1p, taiko, score,
/// taiko_combo_number and icon_course, which the board call is routed to here.
/// </summary>
public sealed class TaikoBoard
{
    private readonly Func<string, LumenHostValue[], bool> _call;

    private TaikoBoard(Func<string, LumenHostValue[], bool> call) => _call = call;

    /// <summary>Calls a board callback; false when the movie that should take it failed.</summary>
    public bool Call(string name, params LumenHostValue[] arguments) => _call(name, arguments);

    /// <summary>Blue and later: the single lane_obi movie.</summary>
    public static TaikoBoard Merged(LumenPlayer laneObi) =>
        new((name, arguments) => laneObi.TryInvokeOptionalCallback("Gameplay board", name, arguments));

    /// <summary>
    /// Before Blue: score and combo numbers have their own movies' callbacks, the drum plays its hit
    /// child's "on" label (left_don, right_katsu, ...) and the course icon jumps to the course's label.
    /// Entry type, play side and Waiwai have no counterpart there.
    /// </summary>
    public static TaikoBoard Split(LumenPlayer lane, LumenPlayer drum, LumenPlayer score, LumenPlayer combo,
        LumenPlayer course)
    {
        return new((name, arguments) => name switch
        {
            "SetScore" => score.TryInvokeCallback("SetCount", arguments),
            "SetComboCount" => combo.TryInvokeCallback("SetComboCount", arguments),
            "SetTaikoHit" => drum.TryGotoLabel(arguments[0].AsString(), "on"),
            "SetCourse" => course.TryGotoLabel("", arguments[0].AsString(), play: false),
            _ => true,
        });
    }
}
