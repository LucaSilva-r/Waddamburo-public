using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Flow;

/// <summary>What a button does outside the drum (the menus, the scores list, replays and practice).</summary>
internal enum GameAction
{
    Menu,
    Confirm,
    Search,
    Scores,
    Favourite,
    Notices,
    Restart,
    Leaderboard,
    ShinuchiBoard,
    Practise,
    Pause,
    LoopStart,
    LoopEnd,
    NoteJump,
    AfterPass,
}

/// <summary>
/// Each action's button: one of the buttons beside the drum (SdlInputBindings.Buttons, each doing several
/// things by where you are), whose key is its id; the player binds keys and controller buttons to it
/// (Settings > Drum Controls > Buttons). Escape is the menu too. The code checks actions through here and
/// the hints name the bound inputs from here, so a change shows everywhere.
/// </summary>
/// <remarks>
/// ponytail: the hints name the keyboard's input only (controller glyphs that follow the device in use
/// would replace <see cref="Name"/>).
/// </remarks>
internal static class GameActions
{
    private static readonly Dictionary<GameAction, SdlKeyboardKey> Keys = new()
    {
        [GameAction.Menu] = SdlKeyboardKey.Backspace,
        [GameAction.Confirm] = SdlKeyboardKey.Enter,
        [GameAction.Search] = SdlKeyboardKey.Tab,
        [GameAction.Scores] = SdlKeyboardKey.L,
        [GameAction.Favourite] = SdlKeyboardKey.P,
        [GameAction.Notices] = SdlKeyboardKey.E,
        [GameAction.Restart] = SdlKeyboardKey.Q,
        [GameAction.Leaderboard] = SdlKeyboardKey.Q,
        [GameAction.ShinuchiBoard] = SdlKeyboardKey.Tab,
        [GameAction.Practise] = SdlKeyboardKey.P,
        [GameAction.Pause] = SdlKeyboardKey.Enter,
        [GameAction.LoopStart] = SdlKeyboardKey.Q,
        [GameAction.LoopEnd] = SdlKeyboardKey.E,
        [GameAction.NoteJump] = SdlKeyboardKey.Tab,
        [GameAction.AfterPass] = SdlKeyboardKey.L,
    };

    /// <summary>What presses each button (ArcadeSettings.Buttons, in SdlInputBindings.Buttons order).</summary>
    public static IReadOnlyList<string> Buttons { get; set; } = new Game.Flow.ArcadeSettings().Buttons;

    public static SdlKeyboardKey Key(GameAction action) => Keys[action];

    // The inputs bound to the action's button.
    private static IEnumerable<SdlInput> bound(GameAction action) =>
        SdlInputBindings.Buttons.IndexOf(Keys[action]) is var index and >= 0 && index < Buttons.Count
            ? Buttons[index].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static token => SdlInput.TryParse(token, out var input) ? input : (SdlInput?)null).OfType<SdlInput>()
            : [];

    /// <summary>Down in a snapshot (a tick's pulses: pressed this tick; a frame's: held).</summary>
    public static bool Down(SdlKeyboardSnapshot keys, GameAction action) => keys.IsDown(Keys[action]);

    /// <summary>Pressed this frame (a display frame's presses).</summary>
    public static bool Pressed(SdlKeyboardSnapshot keys, GameAction action) =>
        keys.Presses.Any(press => press.Key == Keys[action]);

    /// <summary>The play/pause keys of replays and practice, as hints show them (Space works there too).</summary>
    public static string PauseKeys => $"{Name(GameAction.Pause)}/Space";

    /// <summary>Its keyboard input's name, as hints show it ("—" when none).</summary>
    public static string Name(GameAction action) =>
        bound(action).Where(static input => input.Kind == SdlInputKind.Key).Select(SdlApplication.Describe).FirstOrDefault() ?? "—";

    /// <summary>Its controller button's name (Xbox's by place), as the button guide shows it; the menu's is B / Start.</summary>
    public static string Gamepad(GameAction action) => action == GameAction.Menu ? "B / Start"
        : bound(action).Where(static input => input.Kind is SdlInputKind.PadButton or SdlInputKind.PadTrigger)
            .Select(SdlApplication.GamepadName).FirstOrDefault() ?? "—";
}
