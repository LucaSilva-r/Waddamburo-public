using Waddamburo.Game.Gameplay;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Gameplay;

/// <summary>The shared battle simulation's presses delivered to the displayed AI lane.</summary>
internal sealed class AiDrummer(AiBattleSimulation simulation, Func<IReadOnlyList<TaikoReplayInput>> inputs)
{
    public AiBattleSimulation Simulation { get; } = simulation;

    // Called after the player's lane accepts this frame's inputs and before the AI lane advances.
    public SdlKeyboardSnapshot Apply(SdlKeyboardSnapshot keyboard, TimeSpan chartTime)
    {
        var presses = Simulation.Advance(inputs(), chartTime).Select(input => new SdlKeyPress(input.Action switch
        {
            TaikoInputAction.LeftDon => SdlKeyboardKey.X,
            TaikoInputAction.RightDon => SdlKeyboardKey.C,
            TaikoInputAction.LeftKa => SdlKeyboardKey.Z,
            _ => SdlKeyboardKey.V,
        }, keyboard.Timestamp - (chartTime - input.Time))).ToArray();
        return new SdlKeyboardSnapshot([], presses, keyboard.Timestamp);
    }
}
