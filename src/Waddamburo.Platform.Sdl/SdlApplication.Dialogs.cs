using SDL;
using static SDL.SDL3;

namespace Waddamburo.Platform.Sdl;

// The system's folder and file pickers.
public sealed unsafe partial class SdlApplication
{
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> PickedFolders = new();

    /// <summary>Opens the system's folder picker; the chosen folder arrives through <see cref="TryTakePickedFolder"/>.</summary>
    public void PickFolder(string? start)
    {
        var location = start is null ? null : System.Text.Encoding.UTF8.GetBytes(start + "\0");
        fixed (byte* path = location)
            SDL_ShowOpenFolderDialog(&folderPicked, IntPtr.Zero, _window, path, false);
    }

    /// <summary>The folder picked since the last call (none when the picker was cancelled).</summary>
    public static bool TryTakePickedFolder([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? folder) =>
        PickedFolders.TryDequeue(out folder);

    // Called by SDL, possibly on another thread; an empty list = cancelled, null = failed.
    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void folderPicked(IntPtr userdata, byte** list, int filter)
    {
        if (list is not null && *list is not null
            && System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)(*list)) is { Length: > 0 } folder)
            PickedFolders.Enqueue(folder);
    }

    /// <summary>
    /// Opens the system's file picker (no window needed: a screen without an application can ask too) for files
    /// matching <paramref name="patterns"/> ("zip;7z;bin", SDL's form); <paramref name="picked"/> gets the chosen paths,
    /// none when cancelled, possibly on another thread.
    /// </summary>
    public static void PickFiles(string filterName, string patterns, bool many, Action<IReadOnlyList<string>> picked)
    {
        var name = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(filterName);
        var pattern = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(patterns);
        var filter = (SDL_DialogFileFilter*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)sizeof(SDL_DialogFileFilter));
        *filter = new SDL_DialogFileFilter { name = (byte*)name, pattern = (byte*)pattern };
        // The filter has to live until SDL calls back: freed there, with the handle carrying the callback.
        var state = System.Runtime.InteropServices.GCHandle.Alloc(new PickState(picked, name, pattern, (IntPtr)filter));
        SDL_ShowOpenFileDialog(&filesPicked, System.Runtime.InteropServices.GCHandle.ToIntPtr(state), null, filter, 1, (byte*)null, many);
    }

    private sealed record PickState(Action<IReadOnlyList<string>> Picked, IntPtr Name, IntPtr Pattern, IntPtr Filter);

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void filesPicked(IntPtr userdata, byte** list, int filter)
    {
        var handle = System.Runtime.InteropServices.GCHandle.FromIntPtr(userdata);
        var state = (PickState)handle.Target!;
        handle.Free();
        List<string> files = [];
        for (var entry = list; entry is not null && *entry is not null; entry++)
            if (System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)(*entry)) is { Length: > 0 } file)
                files.Add(file);
        System.Runtime.InteropServices.Marshal.FreeCoTaskMem(state.Name);
        System.Runtime.InteropServices.Marshal.FreeCoTaskMem(state.Pattern);
        System.Runtime.InteropServices.NativeMemory.Free((void*)state.Filter);
        try
        {
            state.Picked(files);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"File picker: {exception}");
        }
    }
}
