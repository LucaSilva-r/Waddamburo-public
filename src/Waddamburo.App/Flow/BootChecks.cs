using Waddamburo.App.Home;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Flow;

/// <summary>The window was closed while the game loaded (or on its error screen): the game ends there.</summary>
internal sealed class BootClosedException() : Exception("The window was closed while the game loaded.");

/// <summary>
/// The startup's check screen, as a cabinet's (its system checks in its service menu's look): one line per part of
/// the game as it loads, "_" blinking while it does, then OK (green) or NG (red); the version at the bottom. The
/// window shows it from the first moment, and the slow parts that need no GPU load in the background
/// (<see cref="Background{T}"/>) while it keeps drawing. A part that fails stops the game on an error screen (its
/// code and name, then what went wrong and where the crash log is) instead of crashing, until the window is closed.
/// </summary>
internal sealed class BootChecks : IDisposable
{
    /// <summary>In a <see cref="BootClosedException"/>'s data when the game stopped on its error screen (exit code 1).</summary>
    public const string FailedKey = "boot-failed";

    private sealed class Check(string name)
    {
        public string Name { get; } = name;
        public bool? Ok { get; set; } // null: under way
        public string? Why { get; set; } // NG's reason, after it
        public int? Percent { get; set; } // how far it is, when it says
    }

    private readonly SdlApplication _application;
    private readonly string _fontPath;
    private readonly OverlayMenu _menu;
    private readonly List<Check> _checks = [];
    private readonly (string Text, RenderColor Colour)[] _footer;

    // Nobody at the window (a scripted run): a failure is thrown on, not waited on.
    private readonly bool _interactive;

    public BootChecks(SdlApplication application, string fontPath, bool interactive = true)
    {
        _interactive = interactive;
        _application = application;
        _fontPath = fontPath;
        _menu = new OverlayMenu(application, fontPath);
        // Waddamburo's own name and version only: the boot screen names no game or release.
        _footer = [(OverlayMenu.upper(Hosting.SelfUpdate.WindowTitle), OverlayMenu.Cyan)];
        _menu.Open(new OverlayPage(static () => "", () => [.. _checks.Select(check => new OverlayRow(Shown(check.Name), Value: () => check.Ok switch
            {
                true => "{OK}",
                false => check.Why is null ? "{NG}" : "{NG}  " + OverlayMenu.upper(check.Why),
                // The cabinet's cursor: an underscore blinking where the result will be.
                null => (check.Percent is { } percent ? $"{percent}% " : "") + (Environment.TickCount64 / 300 % 2 == 0 ? "{_}" : ""),
            }, Colour: () => check.Ok == false ? OverlayMenu.Red : check.Ok == true ? OverlayMenu.Green : OverlayMenu.White))],
            Legend: false, Footer: () => _footer), home: false);
    }

    /// <summary>The next part starts loading: the one before it is OK, this one shows its cursor (drawn at once).</summary>
    public void Begin(string name)
    {
        Done();
        _checks.Add(new Check(name));
        Console.WriteLine($"Boot: {name}.");
        present();
        // Diagnostic: WADDAMBURO_BOOT_FAIL=<check> (SONG LIBRARY, MOVIES ...) fails that check, to see the error screen.
        if (Environment.GetEnvironmentVariable("WADDAMBURO_BOOT_FAIL") == name)
            throw new InvalidOperationException($"WADDAMBURO_BOOT_FAIL asked {name} to fail.");
    }

    /// <summary>How far the part under way is, 0-100 (any thread: the screen reads it on its next frame).</summary>
    public void Progress(int percent)
    {
        if (_checks is [.., { Ok: null } last])
            last.Percent = percent;
    }

    /// <summary>The part under way is OK.</summary>
    public void Done()
    {
        if (_checks is [.., { Ok: null } last])
            last.Ok = true;
    }

    /// <summary>
    /// The last part is NG but not fatal (the game goes on without it): <paramref name="why"/> shows after it, briefly;
    /// <paramref name="details"/> go to the log.
    /// </summary>
    public void Warn(string why, IEnumerable<string>? details = null)
    {
        if (_checks is [.., var last])
        {
            last.Ok = false;
            last.Why = why;
        }
        Console.Error.WriteLine($"Warning BOOT: {why}");
        foreach (var detail in details ?? [])
            Console.Error.WriteLine($"Warning BOOT:   {detail}");
        present();
    }

    /// <summary>The part named runs on a background thread while the screen keeps drawing; its result once done.</summary>
    public T Background<T>(string name, Func<T> work)
    {
        Begin(name);
        return Await(work);
    }

    /// <summary>The part under way goes on on a background thread while the screen keeps drawing; its result once done.</summary>
    public T Await<T>(Func<T> work)
    {
        var task = Task.Run(work);
        while (!task.IsCompleted)
        {
            present();
            Thread.Sleep(15);
        }
        var result = task.GetAwaiter().GetResult(); // a failure is thrown here, on the loading thread
        Done();
        present();
        return result;
    }

    /// <summary>
    /// The part under way failed: NG, then the error screen (<see cref="CrashScreen"/>) until the window is closed or
    /// Escape is pressed (the cabinet waits for its operator). Then the exception to throw: a
    /// <see cref="BootClosedException"/>, or in a scripted run (nobody at the window) the failure itself.
    /// </summary>
    public Exception Fail(Exception exception)
    {
        var failed = _checks.FindIndex(static check => check.Ok is null);
        if (failed >= 0)
            _checks[failed].Ok = false;
        var name = failed >= 0 ? _checks[failed].Name : "SYSTEM";
        name = Shown(name);
        Console.Error.WriteLine($"Boot failed at {name}: {exception}");
        if (!_interactive)
            return exception;
        var log = ReleaseConsole.WriteCrashFile(exception);
        // Its code: the part's place in the startup, as the cabinet numbers its errors (22-2 ...).
        CrashScreen.Show(_application, _fontPath, $"{Math.Max(failed, 0) + 1:00}-1", name, exception, log, _footer);
        var closed = new BootClosedException();
        closed.Data[FailedKey] = true;
        return closed;
    }

    /// <summary>A part's name (English, as logged and as WADDAMBURO_BOOT_FAIL names it) in the game's language.</summary>
    public static string Shown(string name) => OverlayMenu.upper(Strings.T(name switch
    {
        "NETWORK SERVICE" => "service.boot.network_service",
        "SONG LIBRARY" => "service.boot.song_library",
        "SONG CATALOG" => "service.boot.song_catalog",
        "I/O" => "service.boot.io",
        "DON-CHAN" => "service.boot.don_chan",
        "AUDIO DEVICE" => "service.boot.audio_device",
        "SOUND DATA" => "service.boot.sound_data",
        "GRAPHICS" => "service.boot.graphics",
        "GAMEPLAY DATA" => "service.boot.gameplay_data",
        "MOVIES" => "service.boot.movies",
        "SYSTEM SETUP" => "service.boot.system_setup",
        "STARTUP" => "service.boot.startup",
        "GAME" => "service.boot.game",
        _ => "service.boot.system",
    }));

    private void present()
    {
        if (!_application.PumpAndPresent(_menu.Frame(), out _))
            throw new BootClosedException();
    }

    public void Dispose() => _menu.Dispose();
}
