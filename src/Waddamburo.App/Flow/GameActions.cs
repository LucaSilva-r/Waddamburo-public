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
/// Each action's key: one keyboard layout, the Tatacon's (a Tatacon is a keyboard), which a gamepad gets
/// by place (SdlApplication's layout). Escape is the menu too. The code checks actions through here and
/// the hints name the keys from here, so a change shows everywhere.
/// </summary>
/// <remarks>
/// ponytail: fixed; the settings could expose them. The hints name the keyboard's key only (controller
/// glyphs that follow the device in use would replace <see cref="Name"/>).
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

    public static SdlKeyboardKey Key(GameAction action) => Keys[action];

    /// <summary>Down in a snapshot (a tick's pulses: pressed this tick; a frame's: held).</summary>
    public static bool Down(SdlKeyboardSnapshot keys, GameAction action) => keys.IsDown(Keys[action]);

    /// <summary>Pressed this frame (a display frame's presses).</summary>
    public static bool Pressed(SdlKeyboardSnapshot keys, GameAction action) =>
        keys.Presses.Any(press => press.Key == Keys[action]);

    /// <summary>The key's name, as hints show it.</summary>
    public static string Name(GameAction action) => Keys[action].ToString();

    /// <summary>The gamepad's button for it (Xbox names by place), as the button guide shows it.</summary>
    public static string Gamepad(GameAction action) => SdlApplication.GamepadName(Keys[action]) ?? "—";
}
