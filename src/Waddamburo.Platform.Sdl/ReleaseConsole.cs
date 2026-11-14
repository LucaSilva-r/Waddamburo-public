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

    /// <summary>
    /// From now on every line logged also goes, timestamped, to <see cref="CrashFolder"/>/latest.log (the lines so far
    /// first); the last session's becomes previous.log. For players to send when something went wrong without a crash.
    /// Another game already writing there (two on one PC) keeps the file: this one then logs nowhere.
    /// </summary>
    public static void LogToFile()
    {
        try
        {
            Directory.CreateDirectory(CrashFolder);
            var latest = Path.Combine(CrashFolder, "latest.log");
            if (File.Exists(latest))
                File.Move(latest, Path.Combine(CrashFolder, "previous.log"), overwrite: true);
            // Not flushed per line: a background thread writes the queued lines (Tee.Drain); a flush per line cost the
            // game thread 5-8 ms on a low-end Windows PC (antivirus on every write) and showed as the note lane jumping.
            var file = new StreamWriter(new FileStream(latest, FileMode.Create, FileAccess.Write, FileShare.Read));
            file.WriteLine($"Waddamburo {Version ?? "unknown"} started at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}, "
                + $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, .NET {Environment.Version}");
            Tee.LogTo(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"No log file: {exception.Message}");
        }
    }

    private static string? Version => System.Reflection.Assembly.GetEntryAssembly()?
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;

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
    /// Opens a console whatever the settings say, for a tool whose whole point is what it prints. A Windows GUI program
    /// has no standard handles at all, which .NET reports as redirected, so only real ones count as a redirect here.
    /// </summary>
    public static void OpenForTool()
    {
        if (!OperatingSystem.IsWindows() || GetConsoleWindow() != 0 || _held is null && GetStdHandle(StandardOutput) is not (0 or -1))
        {
            Show(true);
            return;
        }
        var log = _held?.ToString() ?? "";
        _held = null;
        if (!AllocConsole())
            return;
        var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        output.Write(log);
        _out.Inner = output;
        _error.Inner = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
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
        Tee.Drain();
        try
        {
            Directory.CreateDirectory(CrashFolder);
            var path = Path.Combine(CrashFolder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var version = Version;
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
        private static StreamWriter? _file;
        public TextWriter Inner { get; set; } = inner;
        public override System.Text.Encoding Encoding => Inner.Encoding;

        public static string Recent()
        {
            lock (Lines)
                return string.Join("\n", Lines) + (Line.Length > 0 ? "\n" + Line : "");
        }

        public static void LogTo(StreamWriter file)
        {
            lock (Lines)
            {
                lock (FileGate)
                {
                    foreach (var line in Lines)
                        file.WriteLine(line);
                    file.Flush();
                    _file = file;
                }
            }
            new Thread(static () =>
            {
                while (true)
                {
                    Thread.Sleep(250);
                    Drain();
                }
            }) { IsBackground = true, Name = "Log file" }.Start();
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => Drain();
        }

        private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Pending = new();
        private static readonly object FileGate = new();

        /// <summary>Writes the queued lines to the log file now (the background thread, a crash, the exit).</summary>
        public static void Drain()
        {
            lock (FileGate)
            {
                if (_file is null || Pending.IsEmpty)
                    return;
                try
                {
                    while (Pending.TryDequeue(out var line))
                        _file.WriteLine(line);
                    _file.Flush();
                }
                catch (IOException)
                {
                    _file = null; // ponytail: a full disk stops the log file, not the game
                }
            }
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
                if (_file is not null)
                    Pending.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {Line}");
                Line.Clear();
                if (Lines.Count > RecentLines)
                    Lines.Dequeue();
            }
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetConsoleWindow();

    private const int StandardOutput = -11;

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();
}
