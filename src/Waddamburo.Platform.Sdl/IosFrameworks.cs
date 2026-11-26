using System.Runtime.InteropServices;

namespace Waddamburo.Platform.Sdl;

/// <summary>iOS: SDL ships as a framework (Frameworks/SDL3.framework/SDL3), which the runtime's library lookup doesn't
/// search; call <see cref="Register"/> before the first SDL call.</summary>
public static class IosFrameworks
{
    public static void Register() =>
        NativeLibrary.SetDllImportResolver(typeof(SDL.SDL3).Assembly, static (name, assembly, path) =>
            name == "SDL3" ? NativeLibrary.Load("@rpath/SDL3.framework/SDL3", assembly, path) : IntPtr.Zero);
}
