using System.Diagnostics;

namespace Waddamburo.App.Presentation;

/// <summary>Opt-in phase timings for the first three seconds; no tracing in normal play.</summary>
internal readonly struct StartupWorkTrace : IDisposable
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("WADDAMBURO_STARTUP_TRACE") == "1";
    private readonly long _started;
    private readonly string? _phase;
    private readonly int _tick;

    private StartupWorkTrace(string phase, int tick)
    {
        _phase = phase; _tick = tick; _started = Stopwatch.GetTimestamp();
    }

    public static StartupWorkTrace Measure(string phase, int tick) =>
        Enabled && tick <= 180 ? new(phase, tick) : default;

    public void Dispose()
    {
        if (_phase is null) return;
        var elapsed = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        if (elapsed >= 3 || _tick <= 2)
            Console.Error.WriteLine(FormattableString.Invariant($"[startup] tick={_tick} {_phase}: {elapsed:F2} ms"));
    }
}
