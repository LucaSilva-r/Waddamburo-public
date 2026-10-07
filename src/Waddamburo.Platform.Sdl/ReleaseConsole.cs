using System.Runtime.InteropServices;
using SDL;

namespace Waddamburo.Platform.Sdl;

/// <summary>
/// Windows release builds are GUI programs, so they start without a console. The log is held
/// until config.cfg says whether to show it (<c>console = true</c> opens a console window with
/// everything logged so far); a fatal error then still reaches the user as a message box.
/// Builds started with a console (dotnet run, other platforms) or with their output redirected to a
/// file (<c>Waddamburo.exe &gt; log.txt</c>) are left alone.
/// </summary>
public static partial class ReleaseConsole
{
    private static StringWriter? _held;

    public static void Begin()
    {
        if (!OperatingSystem.IsWindows() || GetConsoleWindow() != 0 || Console.IsOutputRedirected)
            return;
        _held = new StringWriter();
        Console.SetOut(_held);
        Console.SetError(_held);
    }

    public static void Show(bool show)
    {
        if (_held is null)
            return;
        var log = _held.ToString();
        _held = null;
        if (show && AllocConsole())
        {
            var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            output.Write(log);
            Console.SetOut(output);
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        else
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
        }
    }

    /// <summary>Reports a fatal error: its message on the console when there is one, otherwise all of it in a message box.</summary>
    public static unsafe void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        // Diagnostic: WADDAMBURO_PROFILE=1 prints the stack too.
        Console.Error.WriteLine(Environment.GetEnvironmentVariable("WADDAMBURO_PROFILE") == "1" ? exception.ToString() : exception.Message);
        if (!OperatingSystem.IsWindows() || GetConsoleWindow() != 0)
            return;
        SDL3.SDL_ShowSimpleMessageBox(SDL_MessageBoxFlags.SDL_MESSAGEBOX_ERROR, "Waddamburo", exception.ToString(), null);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetConsoleWindow();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();
}
