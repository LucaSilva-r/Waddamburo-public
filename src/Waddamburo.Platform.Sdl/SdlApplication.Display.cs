using System.Diagnostics;
using SDL;
using Waddamburo.Platform.Sdl.Rendering;
using static SDL.SDL3;

namespace Waddamburo.Platform.Sdl;

// The stage in the window, fullscreen and display modes.
public sealed unsafe partial class SdlApplication
{
    /// <summary>The last presented stage rectangle, in framebuffer pixels.</summary>
    public RenderViewport StageViewport => _renderer!.StageViewport;

    /// <summary>Places the stage inside the window; restored by the editor when it closes.</summary>
    public (float Size, float X, float Y) Letterbox
    {
        get => _renderer!.Letterbox;
        set { ensureOwnerThread(); _renderer!.Letterbox = value; }
    }


    /// <summary>The 16:9 stage drawn squeezed into a 4:3 picture (a 4:3 layout's songs).</summary>
    public bool Squeeze43
    {
        get => _renderer!.Squeeze43;
        set { ensureOwnerThread(); _renderer!.Squeeze43 = value; }
    }

    /// <summary>Within <see cref="StageRegion"/>, where the stage goes (normalized window coordinates; null: fitted).</summary>
    public RenderRectangle? StageBox
    {
        get => _renderer!.StageBox;
        set
        {
            ensureOwnerThread();
            if (value is { } r && (!float.IsFinite(r.X) || !float.IsFinite(r.Y) || !float.IsFinite(r.Width)
                || !float.IsFinite(r.Height) || r.Width <= 0 || r.Height <= 0))
                throw new ArgumentOutOfRangeException(nameof(value));
            _renderer!.StageBox = value;
        }
    }


    /// <summary>Optional normalized region containing the aspect-fitted stage (editor panels surround it).</summary>
    public RenderRectangle? StageRegion
    {
        get => _renderer!.StageRegion;
        set
        {
            ensureOwnerThread();
            if (value is { } r && (!float.IsFinite(r.X) || !float.IsFinite(r.Y) || !float.IsFinite(r.Width)
                || !float.IsFinite(r.Height) || r.X < 0 || r.Y < 0 || r.Width <= 0 || r.Height <= 0
                || r.X + r.Width > 1.00001f || r.Y + r.Height > 1.00001f))
                throw new ArgumentOutOfRangeException(nameof(value));
            _renderer!.StageRegion = value;
        }
    }

    private DisplaySettings? _display;

    // Stopwatch ticks between frame starts under the frame limit; 0 = none.
    private long _frameInterval;
    private long _nextFrameAt;

    /// <summary>
    /// Applies vsync, the fullscreen kind and mode, and the letterbox. The window only enters or leaves
    /// fullscreen when that setting itself changed (F11 toggles it apart from the settings).
    /// </summary>
    public void ApplyDisplay(DisplaySettings display)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        BgfxSupport.SetVsync(display.Vsync);
        BgfxSupport.SetFrameLatency(display.FrameLatency);
        _frameInterval = display.FpsCap > 0 ? Stopwatch.Frequency / display.FpsCap : 0;
        _renderer!.Letterbox = (display.LetterboxSize, display.LetterboxX, display.LetterboxY);
        _renderer.RenderHeight = display.RenderHeight;
        if (_display is { } previous && previous.Fullscreen == display.Fullscreen && previous.Exclusive == display.Exclusive
            && previous.Width == display.Width && previous.Height == display.Height && previous.RefreshRate == display.RefreshRate)
        {
            _display = display;
            return;
        }
        _display = display;
        SDL_DisplayMode mode;
        var (width, height) = display.Width > 0 ? (display.Width, display.Height) : desktop();
        var refresh = display.RefreshRate > 0 ? display.RefreshRate
            : FullscreenModes().Where(m => (m.Width, m.Height) == (width, height)).Select(static m => m.RefreshRate).DefaultIfEmpty(0).Max();
        var exclusive = display.Exclusive && SDL_GetClosestFullscreenDisplayMode(SDL_GetDisplayForWindow(_window),
            width, height, refresh, true, &mode);
        // Null: borderless (the desktop's mode).
        if (!SDL_SetWindowFullscreenMode(_window, exclusive ? &mode : null))
            Console.Error.WriteLine($"Fullscreen mode not applied: {SDL_GetError()}");
        SDL_SetWindowFullscreen(_window, display.Fullscreen || PlatformInfo.IsMobile);
    }

    /// <summary>What the game renders with and on, for a performance report.</summary>
    public IReadOnlyList<string> DescribeRendering()
    {
        if (_renderer is null)
            return [];
        var caps = Bgfx.bgfx.get_caps();
        var renderer = $"renderer\t{BgfxSupport.RendererName}, GPU {SystemInfo.Vendor(caps->vendorId.ToString(System.Globalization.CultureInfo.InvariantCulture))} "
            + $"{caps->vendorId:x4}:{caps->deviceId:x4}";
        var stats = Bgfx.bgfx.get_stats();
        var mode = SDL_GetCurrentDisplayMode(SDL_GetDisplayForWindow(_window));
        return [renderer, $"display\tbackbuffer {stats->width}x{stats->height}, display mode "
            + (mode is null ? "?" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{mode->w}x{mode->h} @ {mode->refresh_rate:F1} Hz"))];
    }

    // Read every frame only while the hitch trace is on; SDL keeps the mode, so the call is cheap.
    private TimeSpan refreshInterval()
    {
        var mode = SDL_GetCurrentDisplayMode(SDL_GetDisplayForWindow(_window));
        return TimeSpan.FromSeconds(1 / (mode is null || mode->refresh_rate <= 0 ? 60f : mode->refresh_rate));
    }

    private (int Width, int Height) desktop()
    {
        var mode = SDL_GetDesktopDisplayMode(SDL_GetDisplayForWindow(_window));
        return mode is null ? (1920, 1080) : (mode->w, mode->h);
    }

    /// <summary>The exclusive fullscreen modes of the window's display, largest and fastest first.</summary>
    public IReadOnlyList<(int Width, int Height, int RefreshRate)> FullscreenModes()
    {
        ensureOwnerThread();
        int count;
        var modes = SDL_GetFullscreenDisplayModes(SDL_GetDisplayForWindow(_window), &count);
        if (modes is null)
            return [];
        var list = new List<(int, int, int)>();
        for (var index = 0; index < count; index++)
            list.Add((modes[index]->w, modes[index]->h, (int)Math.Round(modes[index]->refresh_rate)));
        SDL_free(modes);
        return [.. list.Distinct()];
    }

    /// <summary>
    /// The height the stage is drawn at in fullscreen: the exclusive mode's or the desktop's, times the
    /// letterbox (what the upscaled textures are sized for).
    /// </summary>
    public int StageHeight(DisplaySettings display)
    {
        var (width, height) = display.Exclusive && display.Width > 0 ? (display.Width, display.Height) : desktop();
        return (int)(Math.Min(height, width * 9 / 16) * display.LetterboxSize);
    }

    // Once a frame: title textures asked for it per quad.
    private (int Width, int Height)? _pixelSize;

    public (int Width, int Height) GetPixelSize()
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pixelSize is { } size)
            return size;
        int width;
        int height;
        if (!SDL_GetWindowSizeInPixels(_window, &width, &height))
            throw sdlFailure("query the window pixel size");
        return (_pixelSize = (width, height)).Value;
    }
}

/// <summary>
/// The display settings <see cref="SdlApplication.ApplyDisplay"/> takes. Width/Height 0: the desktop's; RefreshRate 0: the
/// highest; FpsCap 0: none; RenderHeight: the stage drawn at this many lines and scaled up (0: the window's own);
/// FrameLatency: frames queued with vsync (1-3).
/// </summary>
public readonly record struct DisplaySettings(bool Vsync, bool Fullscreen, bool Exclusive, int Width, int Height, int RefreshRate,
    float LetterboxSize, float LetterboxX, float LetterboxY, int FpsCap = 0, int RenderHeight = 0, int FrameLatency = 1);
