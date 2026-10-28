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
        LumenInputMode mode = LumenInputMode.AuthoredControls,
        int primarySide = 0)
    {
        ArgumentNullException.ThrowIfNull(keyboard);
        // Native gameplay owns input and drives movies through semantic callbacks.
        // Forwarding menu keys as well can activate authored preview/debug handlers.
        if (mode == LumenInputMode.PresentationOnly)
            return LumenInputSnapshot.Empty;
        if (mode != LumenInputMode.AuthoredControls)
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (primarySide is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(primarySide));
        return new LumenInputSnapshot(keyboard.PressedKeys.SelectMany(key => mapKey(key, primarySide)));
    }

    private static IEnumerable<int> mapKey(SdlKeyboardKey key, int primarySide) => key switch
    {
        // Physical Taiko layout -> authored per-player left/right/decide polling.
        SdlKeyboardKey.D => [(int)SdlKeyboardKey.A],
        SdlKeyboardKey.K => [(int)SdlKeyboardKey.S],
        SdlKeyboardKey.F or SdlKeyboardKey.J => [(int)SdlKeyboardKey.Z],
        SdlKeyboardKey.Z => [(int)SdlKeyboardKey.D],
        SdlKeyboardKey.V => [(int)SdlKeyboardKey.F],
        SdlKeyboardKey.X or SdlKeyboardKey.C => [(int)SdlKeyboardKey.C],
        // A wheel notch (menus only) is the active drum's left/right ka (profiles, difficulties),
        // and stays a wheel notch for song select's own list scrolling.
        SdlKeyboardKey.WheelUp => [primarySide == 0 ? 'A' : 'D', (int)key],
        SdlKeyboardKey.WheelDown => [primarySide == 0 ? 'S' : 'F', (int)key],
        _ => [(int)key],
    };
}
