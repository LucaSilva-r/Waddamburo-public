using System.Runtime.InteropServices;

namespace Waddamburo.Platform.Sdl;

/// <summary>
/// Asks the system to favour the game. Linux: Feral GameMode, when installed (performance CPU governor, raised
/// priority, GPU performance mode), through its client library as games do. Windows: an above-normal priority class,
/// and MMCSS "Games" scheduling for the threads that pace frames (<see cref="FavourThread"/>). Each is a request: when
/// it is refused or missing, the game runs as before.
/// </summary>
public static class SystemPriority
{
    /// <summary>Once at start, on any thread; returns what was granted, for the log.</summary>
    public static string Request()
    {
        try
        {
            if (OperatingSystem.IsLinux())
                return requestGameMode();
            if (OperatingSystem.IsWindows())
            {
                var granted = new List<string>
                {
                    SetPriorityClass(GetCurrentProcess(), AboveNormalPriorityClass)
                        ? "above-normal priority" : $"priority class refused ({Marshal.GetLastPInvokeError()})",
                };
                // Windows 11 may run a process it judges background-like on efficiency cores at low clocks (EcoQoS),
                // and lets a minimised one's timers go coarse: neither for the game.
                // The timer flag is Windows 11's: Windows 10 refuses the whole call over it, so it goes again without.
                var throttling = new PowerThrottlingState { Version = 1, ControlMask = ExecutionSpeed | IgnoreTimerResolution, StateMask = 0 };
                var size = Marshal.SizeOf<PowerThrottlingState>();
                var unthrottled = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref throttling, size);
                if (!unthrottled)
                {
                    throttling.ControlMask = ExecutionSpeed;
                    unthrottled = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref throttling, size);
                }
                granted.Add(unthrottled ? "no power throttling" : $"power throttling kept ({Marshal.GetLastPInvokeError()})");
                // 1 ms timer resolution: Sleep(1) in the frame cap's pacing sleeps ~1 ms instead of up to 15.6.
                if (timeBeginPeriod(1) == 0)
                    granted.Add("1 ms timers");
                return string.Join(", ", granted);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return "unavailable";
        }
        return "unchanged";
    }

    /// <summary>
    /// Windows: schedules the calling thread under MMCSS's "Games" task (it lives until the thread ends). Elsewhere a no-op.
    /// </summary>
    public static void FavourThread()
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            uint task = 0;
            _ = AvSetMmThreadCharacteristicsW("Games", ref task); // 0 = refused: the thread keeps its normal scheduling
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// Windows: the high-performance GPU for this exe (a laptop's discrete one), as Settings > Display > Graphics sets it,
    /// unless the player chose one there. DXGI reads it when it creates its factory: call it before the renderer starts.
    /// </summary>
    public static string? PreferHighPerformanceGpu()
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe)
            return null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            if (key.GetValue(exe) is string chosen)
                return $"GPU preference kept ({chosen.TrimEnd(';')})";
            key.SetValue(exe, "GpuPreference=2;");
            return "GPU preference: high performance";
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return $"GPU preference not set ({exception.Message})";
        }
    }

    // libgamemode's client entry points (gamemode_client.h dlopens the same library and calls these).
    private static unsafe string requestGameMode()
    {
        if (!NativeLibrary.TryLoad("libgamemode.so.0", out var library))
            return "GameMode not installed";
        if (!NativeLibrary.TryGetExport(library, "real_gamemode_request_start", out var start))
            return "GameMode library without request_start";
        if (((delegate* unmanaged<int>)start)() == 0)
            return "GameMode on";
        var reason = NativeLibrary.TryGetExport(library, "real_gamemode_error_string", out var error)
            ? Marshal.PtrToStringUTF8(((delegate* unmanaged<nint>)error)()) : null;
        return $"GameMode refused ({reason ?? "no reason"})";
    }

    private const uint AboveNormalPriorityClass = 0x8000;
    private const int ProcessPowerThrottling = 4; // PROCESS_INFORMATION_CLASS.ProcessPowerThrottling
    private const uint ExecutionSpeed = 0x1, IgnoreTimerResolution = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version, ControlMask, StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(nint process, int informationClass, ref PowerThrottlingState information, int size);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(nint process, uint priorityClass);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    private static extern nint AvSetMmThreadCharacteristicsW(string task, ref uint taskIndex);
}
