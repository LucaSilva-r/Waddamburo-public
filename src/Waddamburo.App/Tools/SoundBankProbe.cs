using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Tools;

// Compatibility entry point for callers of the former sound-only diagnostic.
internal static class SoundBankProbe
{
    public static void Run(string path, int windowWidth, int windowHeight,
        int? frames = null, int? ticks = null, string? screenshot = null, SdlKeyboardTimeline? inputTimeline = null) =>
        FilePreview.Run(path, windowWidth, windowHeight, frames, ticks, screenshot, inputTimeline);
}
