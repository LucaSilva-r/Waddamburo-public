using Waddamburo.App.Home;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Flow;

/// <summary>
/// The fatal error screen, as a cabinet's (22-2 USBドングルエラー2 ...): whatever was going on stops and this takes the
/// window: the error's code and name, the red "an error has occurred", what went wrong and where the crash log is, the
/// version at the bottom; until the window is closed or Escape pressed. For a failure while the game loads
/// (<see cref="BootChecks"/>), one while it runs (<see cref="GameShell"/>), or one before its window opened (the game
/// folder or its settings): that opens a window of its own, drawn with the bundled font alone.
/// </summary>
internal static class CrashScreen
{
    /// <summary>
    /// Shows the screen in <paramref name="application"/>'s window until it is closed or Escape is pressed.
    /// <paramref name="code"/> and <paramref name="name"/>: the error's ("10-1", "MOVIES"); <paramref name="log"/>: the
    /// crash file written for it.
    /// </summary>
    public static void Show(SdlApplication application, string fontPath, string code, string name, Exception exception,
        string? log, (string Text, RenderColor Colour)[]? footer = null)
    {
        using var menu = new OverlayMenu(application, fontPath);
        var japanese = Strings.Language == "ja";
        menu.Open(new OverlayPage(() => $"{code} {name} ERROR", static () => [], Centred: true, Legend: false,
            Body: () =>
            [
                (japanese ? "エラーが発生しています。" : "An error has occurred.", OverlayMenu.Red),
                (exception.Message, OverlayMenu.White),
                (log is null ? "" : (japanese ? "ログ: " : "Log: ") + log, OverlayMenu.White),
                (japanese ? "ウィンドウを閉じるか、Escapeで終了します。" : "Close the window or press Escape to quit.", OverlayMenu.White),
            ], Footer: () => footer ?? [(OverlayMenu.upper(Hosting.SelfUpdate.WindowTitle), OverlayMenu.Cyan)]), home: false);
        while (application.PumpAndPresent(menu.Frame(), out var escape) && !escape)
            Thread.Sleep(15);
    }

    /// <summary>
    /// A failure before the game's window opened: a window of its own shows the screen, with the bundled service font
    /// (written to the user's app data, as the game folder may be what's wrong). False when even that can't be shown
    /// (no display, no GPU): the caller falls back to the console or a message box.
    /// </summary>
    public static bool ShowAlone(Exception exception, string? log)
    {
        try
        {
            var fonts = Cli.BundledFonts.Extract(Path.Combine(Path.GetDirectoryName(ReleaseConsole.CrashFolder)!, "fonts"));
            using var application = new SdlApplication(Hosting.SelfUpdate.WindowTitle, 1280, 720);
            Show(application, fonts.Service, "00-1", "STARTUP", exception, log);
            return true;
        }
        catch (Exception other) when (other is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"The error screen could not be shown: {other.Message}");
            return false;
        }
    }
}
