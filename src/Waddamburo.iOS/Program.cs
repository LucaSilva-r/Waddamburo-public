using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static SDL.SDL3;

namespace Waddamburo.iOS;

/// <summary>
/// The game on iOS: SDL owns the app (UIApplicationMain), then calls <see cref="run"/> on the main thread, which boots the
/// game exactly as the desktop executable does (Waddamburo.App.Boot). Its files live in the app's Documents folder, which
/// the Files app shows (On My iPhone › Waddamburo) and a Mac or PC reaches over USB.
/// </summary>
public static unsafe class Program
{
    public static int Main(string[] args)
    {
        // SDL ships as a framework (Frameworks/SDL3.framework/SDL3), which the runtime's library lookup doesn't search.
        NativeLibrary.SetDllImportResolver(typeof(SDL.SDL3).Assembly, static (name, assembly, path) =>
            name == "SDL3" ? NativeLibrary.Load("@rpath/SDL3.framework/SDL3", assembly, path) : IntPtr.Zero);
        return SDL_RunApp(0, null, &run, IntPtr.Zero);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int run(int argc, byte** argv)
    {
        try
        {
            start();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Waddamburo stopped: {exception}");
        }
        return 0;
    }

    private static void start()
    {
        var library = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Environment.CurrentDirectory = library;
        // The caches (catalog, decoded audio, upscaled textures) in Library/Caches: the desktop default (~/.cache) is the
        // container's root, which iOS doesn't let the app write.
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Environment.GetFolderPath(Environment.SpecialFolder.InternetCache));
        var folder = new GameFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        App.Boot.AppHome = Path.Combine(library, "waddamburo");
        App.Boot.DeviceName = GameFolder.DeviceName;
        Platform.Sdl.ReleaseConsole.CrashFolder = Path.Combine(folder.Root, "Waddamburo", "logs");
        // Null: the setup screen was closed without Green.
        if (folder.Resolve() is not { } game)
            return;
        App.Boot.TjaFolder = Path.Combine(folder.Root, GameFolder.CustomSongs);
        App.Boot.UserHome = Path.Combine(folder.Root, "Waddamburo");
        string[] args = [$"--game-data={game}"];
        // Read again on a restart: a release imported since joins.
        do
            App.Boot.StockFolders = folder.ReleaseFolders();
        while (App.Boot.Run(args) == App.Boot.RestartExitCode && (args = App.Boot.RestartArguments) is not null);
    }
}
