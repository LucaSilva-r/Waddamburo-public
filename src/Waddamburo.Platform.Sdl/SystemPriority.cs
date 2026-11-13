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
                return SetPriorityClass(GetCurrentProcess(), AboveNormalPriorityClass)
                    ? "above-normal priority" : $"priority class refused ({Marshal.GetLastPInvokeError()})";
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(nint process, uint priorityClass);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    private static extern nint AvSetMmThreadCharacteristicsW(string task, ref uint taskIndex);
}
