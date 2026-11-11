using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SDL;
using static SDL.SDL3;

namespace Waddamburo.Platform.Sdl;

public sealed unsafe partial class SdlApplication
{
    /// <summary>Asynchronous system folder picker; completion returns through the same SDL dialog callback.</summary>
    public Task<string?> PickFolderAsync(string? start = null)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = new FileDialogRequest("Folder", "*", start);
        var handle = GCHandle.Alloc(request);
        SDL_ShowOpenFolderDialog(&filePicked, GCHandle.ToIntPtr(handle), _window, (byte*)request.Start, false);
        return request.Completion.Task;
    }

    /// <summary>System file picker. SDL may complete it on another thread; native filter data lives until then.</summary>
    public Task<string?> PickFileAsync(bool save, string description, string extension, string? start = null)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = new FileDialogRequest(description, extension, start);
        var handle = GCHandle.Alloc(request);
        if (save) SDL_ShowSaveFileDialog(&filePicked, GCHandle.ToIntPtr(handle), _window, request.Filter, 1, (byte*)request.Start);
        else SDL_ShowOpenFileDialog(&filePicked, GCHandle.ToIntPtr(handle), _window, request.Filter, 1, (byte*)request.Start, false);
        return request.Completion.Task;
    }

    private sealed class FileDialogRequest
    {
        public readonly TaskCompletionSource<string?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly nint _name, _pattern;
        public readonly nint Start;
        public readonly SDL_DialogFileFilter* Filter;
        public FileDialogRequest(string name, string extension, string? start)
        {
            _name = Marshal.StringToCoTaskMemUTF8(name); _pattern = Marshal.StringToCoTaskMemUTF8(extension);
            Start = start is null ? 0 : Marshal.StringToCoTaskMemUTF8(start);
            Filter = (SDL_DialogFileFilter*)Marshal.AllocHGlobal(sizeof(SDL_DialogFileFilter));
            *Filter = new() { name = (byte*)_name, pattern = (byte*)_pattern };
        }
        public void Free()
        {
            Marshal.FreeCoTaskMem(_name); Marshal.FreeCoTaskMem(_pattern); Marshal.FreeCoTaskMem(Start);
            Marshal.FreeHGlobal((nint)Filter);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void filePicked(nint userdata, byte** list, int filter)
    {
        var handle = GCHandle.FromIntPtr(userdata);
        var request = (FileDialogRequest)handle.Target!;
        var path = list is null || *list is null ? null : Marshal.PtrToStringUTF8((nint)(*list));
        var error = list is null ? SDL_GetError() : null;
        request.Free(); handle.Free();
        if (error is not null) request.Completion.TrySetException(new IOException(error));
        else request.Completion.TrySetResult(path);
    }
}
