using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Waddamburo.Platform.Sdl;

/// <summary>
/// A session's performance report for us to read (players send the file): the machine, then one line per second
/// (frame rate and frame-time percentiles, process and system CPU/RAM, GC), the long frames, the scene
/// switches, and a summary per scene with the memory trend. Plain text, tab-separated where it is a table, so
/// it skims in an editor and parses with a few lines of script. The file is rewritten every 30 s (a crash or a
/// killed leak run keeps what came before) and once more at the end.
/// </summary>
public sealed class PerformanceRecorder : IDisposable
{
    /// <summary>What the frames being recorded show (the shell sets it on each scene switch).</summary>
    public static string Scene { get; set; } = "-";

    /// <summary>The recorder of this session, if one runs (scene switches report to it).</summary>
    public static PerformanceRecorder? Current { get; private set; }

    // A frame interval this long is a hitch (under 40 fps), listed on its own.
    private static readonly TimeSpan HitchInterval = TimeSpan.FromMilliseconds(25);
    private const int MaxHitches = 5000;
    private static readonly TimeSpan WriteEvery = TimeSpan.FromSeconds(30);

    private readonly string _path;
    private readonly List<string> _header = [];
    private readonly List<string> _seconds = [];
    private readonly List<string> _hitches = [];
    private readonly List<string> _events = [];
    private readonly List<double> _intervals = new(1024);
    private readonly Dictionary<string, SceneTotals> _scenes = new(StringComparer.Ordinal);
    private readonly List<(double Minutes, double RssMiB, double ManagedMiB)> _memory = [];
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly object _writeLock = new();
    private long _secondStart;
    private long _lastWrite;
    private double _maxUpdate, _maxRender;
    private int _ticks, _droppedHitches, _missed;
    private TimeSpan _cpuAt;
    private (ulong Idle, ulong Total) _systemCpuAt;
    private (int Gen0, int Gen1, int Gen2, TimeSpan Pause) _gcAt;
    private (int Gen0, int Gen1, int Gen2, TimeSpan Pause) _gcFrame;
    private double _peakRss;
    private Task _writing = Task.CompletedTask;

    private sealed class SceneTotals
    {
        public double Seconds, Frames, Hitches, MaxIntervalMs, Missed;
        public List<double> Lows { get; } = []; // each second's 1% low fps
    }

    /// <param name="path">The report file.</param>
    /// <param name="title">What was recorded (a free session, the benchmark route).</param>
    /// <param name="details">Lines about the run (version, renderer, display, settings).</param>
    public PerformanceRecorder(string path, string title, IEnumerable<string> details)
    {
        _path = path;
        _header.Add($"Waddamburo performance report: {title}");
        _header.Add($"started\t{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        _header.AddRange(details);
        _header.AddRange(SystemInfo.Describe());
        _secondStart = _lastWrite = _start;
        _cpuAt = _process.TotalProcessorTime;
        _systemCpuAt = SystemInfo.CpuTimes();
        _gcAt = _gcFrame = gc();
        Current = this;
    }

    /// <summary>One rendered frame: the interval since the last one and the update and render time inside it.</summary>
    /// <param name="interval">Time since the previous frame.</param>
    /// <param name="update">Time spent updating the game this frame.</param>
    /// <param name="render">Time spent rendering this frame.</param>
    /// <param name="ticks">Simulation ticks run this frame.</param>
    /// <param name="gpu">The GPU figure for the log line, read only when a line is written.</param>
    /// <param name="missedRefresh">The frame took long enough to cost a screen refresh (the notes jump forward).</param>
    public void Record(TimeSpan interval, TimeSpan update, TimeSpan render, int ticks, Func<string>? gpu = null, bool missedRefresh = false)
    {
        if (missedRefresh)
            _missed++;
        var now = Stopwatch.GetTimestamp();
        var ms = interval.TotalMilliseconds;
        _intervals.Add(ms);
        _maxUpdate = Math.Max(_maxUpdate, update.TotalMilliseconds);
        _maxRender = Math.Max(_maxRender, render.TotalMilliseconds);
        _ticks += ticks;
        var gcNow = gc();
        if (interval >= HitchInterval)
        {
            if (_hitches.Count < MaxHitches)
                _hitches.Add(string.Join('\t', clock(now), Scene, f(ms), f(update.TotalMilliseconds), f(render.TotalMilliseconds), ticks,
                    $"{gcNow.Gen0 - _gcFrame.Gen0}/{gcNow.Gen1 - _gcFrame.Gen1}/{gcNow.Gen2 - _gcFrame.Gen2}",
                    f((gcNow.Pause - _gcFrame.Pause).TotalMilliseconds), gpu?.Invoke() ?? ""));
            else
                _droppedHitches++;
        }
        _gcFrame = gcNow;
        if (Stopwatch.GetElapsedTime(_secondStart, now) >= TimeSpan.FromSeconds(1))
            closeSecond(now);
        if (Stopwatch.GetElapsedTime(_lastWrite, now) >= WriteEvery && _writing.IsCompleted)
        {
            _lastWrite = now;
            var text = Render(final: false);
            _writing = Task.Run(() => write(text));
        }
    }

    /// <summary>Something that happened (a scene switch with its timings, focus lost); listed with its time.</summary>
    public void Event(string text) => _events.Add($"{clock(Stopwatch.GetTimestamp())}\t{Scene}\t{text}");

    private void closeSecond(long now)
    {
        var wall = Stopwatch.GetElapsedTime(_secondStart, now).TotalSeconds;
        _secondStart = now;
        var sorted = _intervals.ToArray();
        _intervals.Clear();
        Array.Sort(sorted);
        double pct(double p) => sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling((sorted.Length - 1) * p)];
        var fps = sorted.Length / wall;
        var low = pct(.99) > 0 ? 1000 / pct(.99) : 0;
        var cpu = _process.TotalProcessorTime;
        var processCpu = (cpu - _cpuAt).TotalSeconds / wall / Environment.ProcessorCount * 100;
        _cpuAt = cpu;
        var system = SystemInfo.CpuTimes();
        var systemCpu = system.Total > _systemCpuAt.Total
            ? 100 * (1 - (double)(system.Idle - _systemCpuAt.Idle) / (system.Total - _systemCpuAt.Total)) : double.NaN;
        _systemCpuAt = system;
        _process.Refresh();
        var rss = _process.WorkingSet64 / 1048576d;
        _peakRss = Math.Max(_peakRss, rss);
        var managed = GC.GetTotalMemory(false) / 1048576d;
        var (ramUsed, ramTotal) = SystemInfo.Memory();
        var gcNow = gc();
        var minutes = Stopwatch.GetElapsedTime(_start, now).TotalMinutes;
        _memory.Add((minutes, rss, managed));
        _seconds.Add(string.Join('\t', clock(now), Scene, f(fps, 0), f(low, 0), f(pct(.5)), f(pct(.99)), f(sorted.Length == 0 ? 0 : sorted[^1]),
            f(_maxUpdate), f(_maxRender), _ticks, f(processCpu, 0), f(systemCpu, 0), f(rss, 0), f(managed, 0),
            f(ramUsed, 0) + "/" + f(ramTotal, 0), $"{gcNow.Gen0 - _gcAt.Gen0}/{gcNow.Gen1 - _gcAt.Gen1}/{gcNow.Gen2 - _gcAt.Gen2}",
            f((gcNow.Pause - _gcAt.Pause).TotalMilliseconds), _missed));
        _gcAt = gcNow;
        if (!_scenes.TryGetValue(Scene, out var totals))
            _scenes[Scene] = totals = new SceneTotals();
        totals.Seconds += wall;
        totals.Frames += sorted.Length;
        totals.Hitches += sorted.Count(static ms => ms >= HitchInterval.TotalMilliseconds);
        totals.Missed += _missed;
        _missed = 0;
        totals.MaxIntervalMs = Math.Max(totals.MaxIntervalMs, sorted.Length == 0 ? 0 : sorted[^1]);
        totals.Lows.Add(low);
        (_maxUpdate, _maxRender, _ticks) = (0, 0, 0);
    }

    /// <summary>The report as it stands.</summary>
    public string Render(bool final)
    {
        var text = new StringBuilder();
        foreach (var line in _header)
            text.AppendLine(line);
        text.AppendLine(CultureInfo.InvariantCulture, $"recorded\t{Stopwatch.GetElapsedTime(_start).TotalMinutes:F1} min{(final ? "" : " (still running)")}");
        text.AppendLine();
        text.AppendLine("== Summary by scene (fps = frames / seconds; low = worst 1% of frames as fps, median over the scene's seconds)");
        text.AppendLine("scene\tseconds\tfps\tlow1%\tworst-second-low\tmax-frame-ms\thitches>=25ms\tmissed-refreshes\tmissed/min");
        foreach (var (scene, t) in _scenes.OrderByDescending(static pair => pair.Value.Seconds))
        {
            var lows = t.Lows.Order().ToArray();
            text.AppendLine(string.Join('\t', scene, f(t.Seconds, 0), f(t.Frames / Math.Max(t.Seconds, 1e-9), 0),
                f(lows.Length == 0 ? 0 : lows[lows.Length / 2], 0), f(lows.Length == 0 ? 0 : lows[0], 0), f(t.MaxIntervalMs), t.Hitches,
                t.Missed, f(t.Missed / Math.Max(t.Seconds / 60, 1e-9), 1)));
        }
        text.AppendLine();
        text.AppendLine("== Memory");
        if (_memory.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"RSS first/last/peak\t{f(_memory[0].RssMiB, 0)}/{f(_memory[^1].RssMiB, 0)}/{f(_peakRss, 0)} MiB");
            text.AppendLine(CultureInfo.InvariantCulture, $"managed first/last/max\t{f(_memory[0].ManagedMiB, 0)}/{f(_memory[^1].ManagedMiB, 0)}/{f(_memory.Max(static m => m.ManagedMiB), 0)} MiB");
            // A leak shows as a steady climb: the slope of each 5-minute window's lowest RSS (after its collections).
            var floors = _memory.GroupBy(static m => (int)(m.Minutes / 5)).Select(static g => (Minutes: g.Key * 5d, RssMiB: g.Min(static m => m.RssMiB))).ToList();
            text.AppendLine(CultureInfo.InvariantCulture, $"RSS floor per 5 min\t{string.Join(' ', floors.Select(static w => f(w.RssMiB, 0)))} MiB");
            // The first window is the warm-up (every scene loading once), not a trend.
            if (floors.Count >= 4)
                text.AppendLine(CultureInfo.InvariantCulture, $"RSS floor trend\t{f(slope(floors[1..]) * 60, 0)} MiB/hour (5-min floors after the first; near 0 = no leak)");
        }
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"== Hitches: frames >= {HitchInterval.TotalMilliseconds:F0} ms ({_hitches.Count}{(_droppedHitches > 0 ? $", {_droppedHitches} more not listed" : "")})");
        text.AppendLine("time\tscene\tframe-ms\tupdate-ms\trender-ms\tticks\tGC0/1/2\tGC-pause-ms\tgpu");
        foreach (var line in _hitches)
            text.AppendLine(line);
        text.AppendLine();
        text.AppendLine("== Events");
        foreach (var line in _events)
            text.AppendLine(line);
        text.AppendLine();
        text.AppendLine("== Seconds (cpu% = share of all cores; ram = system used/total MiB; rss/managed = Waddamburo's MiB)");
        text.AppendLine("time\tscene\tfps\tlow1%\tp50-ms\tp99-ms\tmax-ms\tmax-update-ms\tmax-render-ms\tticks\tproc-cpu%\tsys-cpu%\trss\tmanaged\tram\tGC0/1/2\tGC-pause-ms\tmissed");
        foreach (var line in _seconds)
            text.AppendLine(line);
        return text.ToString();
    }

    private void write(string text)
    {
        lock (_writeLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path + ".tmp", text);
                File.Move(_path + ".tmp", _path, overwrite: true);
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine($"Performance report not written: {exception.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (Current == this)
            Current = null;
        _writing.Wait();
        write(Render(final: true));
        Console.WriteLine($"Performance report: {_path}");
        _process.Dispose();
    }

    private static double slope(List<(double Minutes, double RssMiB)> points)
    {
        var (mx, my) = (points.Average(static p => p.Minutes), points.Average(static p => p.RssMiB));
        var den = points.Sum(p => (p.Minutes - mx) * (p.Minutes - mx));
        return den == 0 ? 0 : points.Sum(p => (p.Minutes - mx) * (p.RssMiB - my)) / den;
    }

    private string clock(long now) => Stopwatch.GetElapsedTime(_start, now).ToString(@"hh\:mm\:ss\.f", CultureInfo.InvariantCulture);

    private static (int Gen0, int Gen1, int Gen2, TimeSpan Pause) gc() =>
        (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration());

    private static string f(double value, int decimals = 1) =>
        double.IsFinite(value) ? value.ToString("F" + decimals, CultureInfo.InvariantCulture) : "-";
}

/// <summary>The machine: its parts for the report's header, and system-wide CPU and RAM use.</summary>
internal static class SystemInfo
{
    public static IEnumerable<string> Describe()
    {
        yield return $"os\t{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}";
        yield return $"cpu\t{cpuName()} ({Environment.ProcessorCount} threads)";
        yield return $"ram\t{f(Memory().TotalMiB / 1024)} GiB";
        foreach (var gpu in gpuNames())
            yield return $"gpu (system)\t{gpu}";
    }

    /// <summary>Idle and total CPU time of the whole system so far, in any unit (0, 0 where unknown).</summary>
    public static (ulong Idle, ulong Total) CpuTimes()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // "cpu  user nice system idle iowait irq softirq steal ..."
                var fields = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries)[1..]
                    .Select(static value => ulong.Parse(value, CultureInfo.InvariantCulture)).ToArray();
                return (fields[3] + (fields.Length > 4 ? fields[4] : 0), fields.Take(8).Aggregate(0UL, static (a, b) => a + b));
            }
            if (OperatingSystem.IsWindows() && GetSystemTimes(out var idle, out var kernel, out var user))
                return (idle, kernel + user); // kernel time includes idle
        }
        catch (Exception exception) when (exception is IOException or FormatException or UnauthorizedAccessException) { }
        return (0, 0);
    }

    /// <summary>System RAM in use and installed, MiB (NaN where unknown).</summary>
    public static (double UsedMiB, double TotalMiB) Memory()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var info = File.ReadLines("/proc/meminfo").Select(static line => line.Split(':'))
                    .ToDictionary(static parts => parts[0], static parts => double.Parse(parts[1].Trim().Split(' ')[0], CultureInfo.InvariantCulture) / 1024);
                return (info["MemTotal"] - info["MemAvailable"], info["MemTotal"]);
            }
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                if (GlobalMemoryStatusEx(ref status))
                    return ((status.TotalPhys - status.AvailPhys) / 1048576d, status.TotalPhys / 1048576d);
            }
        }
        catch (Exception exception) when (exception is IOException or FormatException or KeyNotFoundException or UnauthorizedAccessException) { }
        return (double.NaN, double.NaN);
    }

    private static string cpuName()
    {
        try
        {
            if (OperatingSystem.IsLinux())
                return File.ReadLines("/proc/cpuinfo").FirstOrDefault(static line => line.StartsWith("model name", StringComparison.Ordinal))
                    ?.Split(':', 2)[1].Trim() ?? "unknown";
            if (OperatingSystem.IsWindows())
                return Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    "ProcessorNameString", null) as string ?? "unknown";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return "unknown";
    }

    // Every display adapter the system lists; the one the game renders with is in the renderer line.
    private static List<string> gpuNames()
    {
        var names = new List<string>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var adapters = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                foreach (var key in adapters?.GetSubKeyNames().Where(static name => name.All(char.IsDigit)) ?? [])
                {
                    using var adapter = adapters!.OpenSubKey(key);
                    if (adapter?.GetValue("DriverDesc") is string name)
                        names.Add($"{name}, driver {adapter.GetValue("DriverVersion")}");
                }
            }
            else if (OperatingSystem.IsLinux() && Directory.Exists("/sys/class/drm"))
                foreach (var card in Directory.EnumerateDirectories("/sys/class/drm", "card?"))
                {
                    var device = Path.Combine(card, "device");
                    string read(string file) => File.Exists(Path.Combine(device, file)) ? File.ReadAllText(Path.Combine(device, file)).Trim() : "?";
                    var driver = Directory.Exists(Path.Combine(device, "driver"))
                        ? Path.GetFileName(new DirectoryInfo(Path.Combine(device, "driver")).ResolveLinkTarget(true)?.FullName ?? "?") : "?";
                    names.Add($"{Path.GetFileName(card)} {Vendor(read("vendor"))} {read("vendor")}:{read("device")}, driver {driver}");
                }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return names;
    }

    /// <summary>A PCI vendor id's name (0x10de or 4318).</summary>
    public static string Vendor(string id) => (id.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToInt32(id, 16) : int.TryParse(id, out var number) ? number : -1) switch
    {
        0x10de => "NVIDIA",
        0x1002 => "AMD",
        0x8086 => "Intel",
        0x5143 => "Qualcomm",
        0x106b => "Apple",
        _ => "",
    };

    private static string f(double value) => double.IsFinite(value) ? value.ToString("F1", CultureInfo.InvariantCulture) : "?";

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
