using System.Runtime.InteropServices;
using SDL;

namespace Waddamburo.Platform.Sdl;

/// <summary>
/// Windows release builds are GUI programs, so they start without a console. The log is held
/// until config.cfg says whether to show it (<c>console = true</c> opens a console window with
/// everything logged so far); a fatal error then still reaches the user as a message box.
/// Builds started with a console (dotnet run, other platforms) or with their output redirected to a
/// file (<c>Waddamburo.exe &gt; log.txt</c>) are left alone.
/// Every build keeps the last lines logged: a fatal error writes them, with its stack trace, to a
/// crash file in <see cref="CrashFolder"/>.
/// </summary>
public static partial class ReleaseConsole
{
    private const int RecentLines = 500;
    private static StringWriter? _held;
    private static readonly Tee _out = new(Console.Out), _error = new(Console.Error);

    /// <summary>Where crash files go: the game's waddamburo/logs once known, until then the user's app data.</summary>
    public static string CrashFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Waddamburo", "logs");

    public static void Begin()
    {
        Console.SetOut(_out);
        Console.SetError(_error);
        // A crash off the main thread (a worker, a finalizer) ends the process without reaching Fail.
        AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
                WriteCrashFile(exception);
        };
        if (!OperatingSystem.IsWindows() || GetConsoleWindow() != 0 || Console.IsOutputRedirected)
            return;
        _held = new StringWriter();
        _out.Inner = _error.Inner = _held;
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
            _out.Inner = output;
            _error.Inner = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        }
        else
            _out.Inner = _error.Inner = TextWriter.Null;
    }

    /// <summary>
    /// Reports a fatal error: its message (and the crash file) on the console when there is one,
    /// otherwise all of it in a message box.
    /// </summary>
    public static unsafe void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        // Diagnostic: WADDAMBURO_PROFILE=1 prints the stack too.
        Console.Error.WriteLine(Environment.GetEnvironmentVariable("WADDAMBURO_PROFILE") == "1" ? exception.ToString() : exception.Message);
        var file = WriteCrashFile(exception);
        if (file is not null)
            Console.Error.WriteLine($"Crash log: {file}");
        if (!OperatingSystem.IsWindows() || GetConsoleWindow() != 0)
            return;
        SDL3.SDL_ShowSimpleMessageBox(SDL_MessageBoxFlags.SDL_MESSAGEBOX_ERROR, "Waddamburo",
            file is null ? exception.ToString() : $"{exception}\n\nCrash log: {file}", null);
    }

    /// <summary>crash-&lt;time&gt;.txt: version, system, the exception with its stack, the last lines logged; null if it could not be written.</summary>
    public static string? WriteCrashFile(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(CrashFolder);
            var path = Path.Combine(CrashFolder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var version = System.Reflection.Assembly.GetEntryAssembly()?
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
            File.WriteAllText(path,
                $"Waddamburo {version ?? "unknown"} crashed at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n"
                + $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, .NET {Environment.Version}\n\n"
                + $"{exception}\n\nLast {RecentLines} lines logged:\n{Tee.Recent()}");
            return path;
        }
        catch (Exception)
        {
            return null; // ponytail: a crash while reporting a crash is not reported
        }
    }

    // Passes everything on to the current console (or the held log) and keeps the last lines, stdout
    // and stderr interleaved, for the crash file.
    private sealed class Tee(TextWriter inner) : TextWriter
    {
        private static readonly Queue<string> Lines = new();
        private static readonly System.Text.StringBuilder Line = new();
        public TextWriter Inner { get; set; } = inner;
        public override System.Text.Encoding Encoding => Inner.Encoding;

        public static string Recent()
        {
            lock (Lines)
                return string.Join("\n", Lines) + (Line.Length > 0 ? "\n" + Line : "");
        }

        public override void Write(char value)
        {
            Inner.Write(value);
            keep(value);
        }

        public override void Write(string? value)
        {
            Inner.Write(value);
            foreach (var character in value ?? "")
                keep(character);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            Inner.Write(buffer, index, count);
            for (var at = index; at < index + count; at++)
                keep(buffer[at]);
        }

        public override void Flush() => Inner.Flush();

        private static void keep(char character)
        {
            lock (Lines)
            {
                if (character == '\r')
                    return;
                if (character != '\n')
                {
                    Line.Append(character);
                    return;
                }
                Lines.Enqueue(Line.ToString());
                Line.Clear();
                if (Lines.Count > RecentLines)
                    Lines.Dequeue();
            }
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetConsoleWindow();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();
}
