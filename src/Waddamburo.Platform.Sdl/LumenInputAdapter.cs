using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Platform.Sdl;

public static class LumenInputAdapter
{
    public static LumenInputSnapshot CreateSnapshot(SdlKeyboardSnapshot keyboard)
    {
        ArgumentNullException.ThrowIfNull(keyboard);
        return new LumenInputSnapshot(keyboard.PressedKeys.Select(key => (int)key));
    }
}
