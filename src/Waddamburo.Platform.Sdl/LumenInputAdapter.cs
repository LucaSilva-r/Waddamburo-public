using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Platform.Sdl;

public static class LumenInputAdapter
{
    public static LumenInputSnapshot CreateSnapshot(SdlKeyboardSnapshot keyboard)
    {
        ArgumentNullException.ThrowIfNull(keyboard);
        return new LumenInputSnapshot(keyboard.PressedKeys.SelectMany(mapKey));
    }

    private static IEnumerable<int> mapKey(SdlKeyboardKey key) => key switch
    {
        // Physical Taiko layout -> authored per-player left/right/decide polling.
        SdlKeyboardKey.D => [(int)SdlKeyboardKey.A],
        SdlKeyboardKey.K => [(int)SdlKeyboardKey.S],
        SdlKeyboardKey.F or SdlKeyboardKey.J => [(int)SdlKeyboardKey.Z],
        SdlKeyboardKey.Z => [(int)SdlKeyboardKey.D],
        SdlKeyboardKey.V => [(int)SdlKeyboardKey.F],
        SdlKeyboardKey.X or SdlKeyboardKey.C => [(int)SdlKeyboardKey.C],
        _ => [(int)key],
    };
}
