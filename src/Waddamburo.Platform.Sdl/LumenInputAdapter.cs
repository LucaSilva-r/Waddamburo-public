using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Platform.Sdl;

public enum LumenInputMode
{
    AuthoredControls,
    PresentationOnly,
}

public static class LumenInputAdapter
{
    public static LumenInputSnapshot CreateSnapshot(
        SdlKeyboardSnapshot keyboard,
        LumenInputMode mode = LumenInputMode.AuthoredControls)
    {
        ArgumentNullException.ThrowIfNull(keyboard);
        // Native gameplay owns input and drives movies through semantic callbacks.
        // Forwarding menu keys as well can activate authored preview/debug handlers.
        if (mode == LumenInputMode.PresentationOnly)
            return LumenInputSnapshot.Empty;
        if (mode != LumenInputMode.AuthoredControls)
            throw new ArgumentOutOfRangeException(nameof(mode));
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
