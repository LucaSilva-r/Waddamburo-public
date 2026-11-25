using SDL;
using Waddamburo.Platform.Sdl.Rendering;
using static SDL.SDL3;

namespace Waddamburo.Platform.Sdl;

/// <summary>Owns SDL video, one window, and the bgfx renderer on the creating thread.</summary>
/// <remarks>Split by concern: Display (stage and display modes), Textures, Input, Dialogs, Loop (Run and its frame).</remarks>
public sealed unsafe partial class SdlApplication : IDisposable
{
    private readonly int _ownerThreadId;
    private SDL_Window* _window;
    private RenderDevice? _renderer;
    private bool _bgfxInitialized;
    private bool _sdlInitialized;
    private bool _disposed;

    /// <summary>
    /// Android's UI thread, before the window's surface is destroyed (the app goes to the background): stops rendering
    /// and waits for the frames in flight. The game renders again once SDL reports the app back in the foreground.
    /// </summary>
    public static void SurfaceDestroying() => BgfxSupport.SurfaceDestroying();

    private static int s_surfaceReady;

    /// <summary>Android's UI thread, once a (new) surface is ready: the game thread renders again, onto it. (SDL's
    /// DID_ENTER_FOREGROUND never reached the event queue, which left the screen black.)</summary>
    public static void SurfaceReady() => Interlocked.Exchange(ref s_surfaceReady, 1);

    /// <summary>Opens a web page in the system's browser (false when it can't).</summary>
    public static bool OpenUrl(Uri url) => SDL_OpenURL(url.AbsoluteUri);

    /// <summary>SDL's clock (nanoseconds since start), the time base of <see cref="TouchHits"/>.</summary>
    public static ulong TicksNs => SDL_GetTicksNS();

    public SdlApplication(
        string title,
        int width,
        int height,
        bool debugGpu = false,
        bool resizable = true,
        bool highPixelDensity = true,
        bool fullscreen = false,
        bool showCursor = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _ownerThreadId = Environment.CurrentManagedThreadId;

        try
        {
            // Linux: the app id is the Wayland app_id / X11 class, which desktops match to waddamburo.desktop.
            SDL_SetAppMetadata("Waddamburo", null, "waddamburo");
            // Fingers are the touch drum's: a tap must not also arrive as a mouse click (a don in menus).
            SDL_SetHint(SDL_HINT_TOUCH_MOUSE_EVENTS, "0");
            // Landscape only (either way up): SDL otherwise lets a resizable window follow the phone into portrait.
            if (PlatformInfo.IsMobile)
                SDL_SetHint(SDL_HINT_ORIENTATIONS, "LandscapeLeft LandscapeRight");
            if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
                throw sdlFailure("initialize SDL video");
            _sdlInitialized = true;
            // A Steam Controller without the Steam client stays in its keyboard-and-mouse mode (B is Escape): then SDL
            // drives it itself, as a controller. With Steam running, Steam's virtual controller is the one to use
            // (SDL opening the real one too would play every press twice).
            if (PlatformInfo.IsMobile || !steamRunning())
                SDL_SetHint("SDL_JOYSTICK_HIDAPI_STEAM", "1");
            // Controllers are optional: without the subsystem the keyboard still plays.
            if (!SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_GAMEPAD))
                Console.Error.WriteLine($"Controllers unavailable: {SDL_GetError()}");

            var windowFlags = highPixelDensity
                ? SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY
                : 0;
            if (resizable)
                windowFlags |= SDL_WindowFlags.SDL_WINDOW_RESIZABLE;
            if (fullscreen || PlatformInfo.IsMobile) // a phone's window is the screen (immersive: no system bars)
                windowFlags |= SDL_WindowFlags.SDL_WINDOW_FULLSCREEN;
            _window = SDL_CreateWindow(
                title,
                width,
                height,
                windowFlags);
            if (_window is null)
                throw sdlFailure("create the window");
            setIcon(_window);
            // The game is played with drums and keys: no pointer over the window (F4 inspect shows it).
            if (showCursor) SDL_ShowCursor(); else SDL_HideCursor();

            int pixelWidth, pixelHeight;
            if (!SDL_GetWindowSizeInPixels(_window, &pixelWidth, &pixelHeight))
                throw sdlFailure("query the window pixel size");
            if (SystemPriority.PreferHighPerformanceGpu() is { } gpu)
                Console.WriteLine($"{gpu}.");
            BgfxSupport.Initialize(_window, (uint)pixelWidth, (uint)pixelHeight, debugGpu);
            _bgfxInitialized = true;
            _renderer = new RenderDevice(_window, (uint)pixelWidth, (uint)pixelHeight);
            GpuDriver = BgfxSupport.RendererName;
        }
        catch
        {
            disposeNativeResources();
            throw;
        }
    }

    // ponytail: the client's process by name, checked once at start; Steam started later keeps both controllers.
    private static bool steamRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("steam").Length > 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
        {
            return true; // unknown: leave the controller to Steam
        }
    }

    // Windows takes the icon from the .exe; Linux windows have none of their own (desktops showed a letter). Not on
    // macOS: SDL would set it as the Dock's, over the bundle's own icon.
    private static void setIcon(SDL_Window* window)
    {
        if (OperatingSystem.IsMacOS())
            return;
        using var resource = typeof(SdlApplication).Assembly.GetManifestResourceStream("Waddamburo.Icon.png");
        if (resource is null)
            return;
        var png = new byte[resource.Length];
        resource.ReadExactly(png);
        fixed (byte* bytes = png)
        {
            var stream = SDL_IOFromConstMem((nint)bytes, (nuint)png.Length);
            var icon = stream == null ? null : SDL_LoadPNG_IO(stream, true);
            if (icon == null)
                return;
            SDL_SetWindowIcon(window, icon);
            SDL_DestroySurface(icon);
        }
    }

    public string GpuDriver { get; } = string.Empty;

    /// <summary>Attaches one UI layer; the application owns its native lifetime as a fallback.</summary>
    public ImGuiLayer AttachImGui(Action draw)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(draw);
        if (_renderer!.ImGuiLayer is not null)
            throw new InvalidOperationException("An ImGui layer is already attached.");
        var cursorWasVisible = (bool)SDL_CursorVisible();
        var textWasActive = (bool)SDL_TextInputActive(_window);
        var layer = new ImGuiLayer(_renderer, draw, () =>
        {
            _renderer.ImGuiLayer = null;
            _heldInputs.Clear();
            _pressedKeys.Clear();
            if (!textWasActive) StopTextInput();
            if (!cursorWasVisible) SDL_HideCursor();
        }, SDL_GetWindowPixelDensity(_window));
        _renderer.ImGuiLayer = layer;
        _heldInputs.Clear();
        _pressedKeys.Clear();
        SDL_ShowCursor();
        if (!textWasActive) StartTextInput();
        return layer;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        ensureOwnerThread();
        disposeNativeResources();
        _disposed = true;
    }

    private void disposeNativeResources()
    {
        _midi.Dispose();
        foreach (var (_, pad) in _pads)
            if (pad != IntPtr.Zero)
                SDL_CloseGamepad((SDL_Gamepad*)pad);
        Array.Clear(_pads);
        _renderer?.ImGuiLayer?.Dispose();
        _renderer?.Dispose();
        _renderer = null;
        if (_bgfxInitialized)
        {
            BgfxSupport.Shutdown();
            _bgfxInitialized = false;
        }
        if (_window is not null)
        {
            SDL_DestroyWindow(_window);
            _window = null;
        }
        if (_sdlInitialized)
        {
            SDL_Quit();
            _sdlInitialized = false;
        }
    }

    private void ensureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("SDL window and GPU operations must run on the creating thread.");
    }

    private static InvalidOperationException sdlFailure(string operation)
    {
        var error = SDL_GetError();
        return new InvalidOperationException($"Failed to {operation}: {error}");
    }
}
