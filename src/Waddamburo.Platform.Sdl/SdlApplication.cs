using System.Diagnostics;
using SDL;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.Platform.Sdl.Timing;
using static SDL.SDL3;

namespace Waddamburo.Platform.Sdl;

/// <summary>Owns SDL video, one window, and one SDL_GPU device on the creating thread.</summary>
public sealed unsafe class SdlApplication : IDisposable
{
    private readonly int _ownerThreadId;
    private SDL_Window* _window;
    private SDL_GPUDevice* _device;
    private RenderDevice? _renderer;
    private bool _windowClaimed;
    private bool _sdlInitialized;
    private bool _disposed;
    private readonly HashSet<SdlKeyboardKey> _pressedKeys = [];

    public SdlApplication(
        string title,
        int width,
        int height,
        bool debugGpu = false,
        bool resizable = true,
        bool highPixelDensity = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _ownerThreadId = Environment.CurrentManagedThreadId;

        try
        {
            if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
                throw sdlFailure("initialize SDL video");
            _sdlInitialized = true;

            var windowFlags = highPixelDensity
                ? SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY
                : 0;
            if (resizable)
                windowFlags |= SDL_WindowFlags.SDL_WINDOW_RESIZABLE;
            _window = SDL_CreateWindow(
                title,
                width,
                height,
                windowFlags);
            if (_window is null)
                throw sdlFailure("create the window");

            _device = SDL_CreateGPUDevice(
                SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV | SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL,
                debugGpu,
                (byte*)null);
            if (_device is null)
                throw sdlFailure("create the GPU device");

            if (!SDL_ClaimWindowForGPUDevice(_device, _window))
                throw sdlFailure("claim the window for the GPU device");
            _windowClaimed = true;
            _renderer = new RenderDevice(_device, _window);
            GpuDriver = SDL_GetGPUDeviceDriver(_device) ?? "unknown";
        }
        catch
        {
            disposeNativeResources();
            throw;
        }
    }

    public string GpuDriver { get; } = string.Empty;

    public (int Width, int Height) GetPixelSize()
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        int width;
        int height;
        if (!SDL_GetWindowSizeInPixels(_window, &width, &height))
            throw sdlFailure("query the window pixel size");
        return (width, height);
    }

    public RenderTextureId UploadRgba8(uint width, uint height, ReadOnlySpan<byte> pixels)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer!.UploadRgba8(width, height, pixels);
    }

    public void UpdateRgba8(RenderTextureId texture, uint width, uint height, ReadOnlySpan<byte> pixels)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _renderer!.UpdateRgba8(texture, width, height, pixels);
    }

    public void ReleaseTexture(RenderTextureId texture)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _renderer!.ReleaseTexture(texture);
    }

    public SdlDonRenderer CreateDonRenderer(string assetRoot)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new SdlDonRenderer(_device, _renderer!, assetRoot);
    }

    public int Run(
        RenderFrame frame,
        int? frameLimit = null,
        Action<RenderCapture>? captureFinalFrame = null)
        => Run(
            _ => frame,
            static () => { },
            frameLimit,
            tickLimit: null,
            captureFinalFrame).RenderedFrames;

    public SdlRunResult Run(
        Func<double, RenderFrame> createFrame,
        Action simulationTick,
        int? frameLimit = null,
        int? tickLimit = null,
        Action<RenderCapture>? captureFinalFrame = null)
    {
        ArgumentNullException.ThrowIfNull(simulationTick);
        return Run(
            createFrame,
            _ => simulationTick(),
            frameLimit,
            tickLimit,
            captureFinalFrame);
    }

    public SdlRunResult Run(
        Func<double, RenderFrame> createFrame,
        Action<SdlKeyboardSnapshot> simulationTick,
        int? frameLimit = null,
        int? tickLimit = null,
        Action<RenderCapture>? captureFinalFrame = null,
        Action<SdlKeyboardSnapshot>? updateFrame = null)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(createFrame);
        ArgumentNullException.ThrowIfNull(simulationTick);
        if (frameLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(frameLimit));
        if (tickLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(tickLimit));
        if (captureFinalFrame is not null && frameLimit is null && tickLimit is null)
            throw new ArgumentException("A final-frame capture requires a frame or tick limit.");

        var renderedFrames = 0;
        var simulationTicks = 0;
        var droppedTicks = 0;
        var clock = new FixedStepAccumulator();
        var previousTimestamp = Stopwatch.GetTimestamp();
        var running = true;
        var pendingPresses = new List<SdlKeyPress>();
        var hitchTrace = Environment.GetEnvironmentVariable("WADDAMBURO_HITCH_TRACE") == "1";
        while (running && (frameLimit is null || renderedFrames < frameLimit))
        {
            SDL_Event currentEvent;
            while (SDL_PollEvent(&currentEvent))
            {
                if (currentEvent.type is (uint)SDL_EventType.SDL_EVENT_QUIT
                    or (uint)SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED)
                {
                    running = false;
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST)
                    _pressedKeys.Clear();
                else if (currentEvent.type is (uint)SDL_EventType.SDL_EVENT_KEY_DOWN
                    or (uint)SDL_EventType.SDL_EVENT_KEY_UP)
                {
                    var key = mapKey(currentEvent.key.key);
                    if (key is SdlKeyboardKey mapped)
                    {
                        if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_KEY_DOWN)
                        {
                            if (!_pressedKeys.Contains(mapped))
                                pendingPresses.Add(new SdlKeyPress(mapped,
                                    TimeSpan.FromTicks(checked((long)(currentEvent.key.timestamp / 100)))));
                            _pressedKeys.Add(mapped);
                        }
                        else
                            _pressedKeys.Remove(mapped);
                    }
                }
            }

            if (!running)
                break;

            var timestamp = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(previousTimestamp, timestamp);
            previousTimestamp = timestamp;
            var remainingTicks = tickLimit is int limit ? limit - simulationTicks : int.MaxValue;
            var eventTime = TimeSpan.FromTicks(checked((long)(SDL_GetTicksNS() / 100)));
            var keyboard = new SdlKeyboardSnapshot(_pressedKeys, pendingPresses, eventTime);
            updateFrame?.Invoke(keyboard);
            var delivered = updateFrame is not null;
            var updateStart = Stopwatch.GetTimestamp();
            var update = clock.AddElapsed(elapsed, () =>
            {
                simulationTick(delivered ? new SdlKeyboardSnapshot(_pressedKeys, timestamp: eventTime) : keyboard);
                delivered = true;
            }, remainingTicks);
            if (delivered)
                pendingPresses.Clear();
            simulationTicks += update.ExecutedTicks;
            droppedTicks += update.DroppedTicks;

            var reachedTickLimit = tickLimit is int requestedTicks && simulationTicks >= requestedTicks;
            var reachedFrameLimit = frameLimit is int requestedFrames && renderedFrames + 1 >= requestedFrames;
            var shouldCapture = captureFinalFrame is not null && (reachedTickLimit || reachedFrameLimit);
            var interpolationFraction = reachedTickLimit ? 1d : clock.InterpolationFraction;
            var renderStart = Stopwatch.GetTimestamp();
            var capture = _renderer!.Present(createFrame(interpolationFraction), shouldCapture);
            // Diagnostic: WADDAMBURO_HITCH_TRACE=1 reports which part of a slow frame took the time.
            var updateTime = Stopwatch.GetElapsedTime(updateStart, renderStart);
            var renderTime = Stopwatch.GetElapsedTime(renderStart);
            if (hitchTrace && updateTime + renderTime > TimeSpan.FromMilliseconds(40))
                Console.Error.WriteLine($"Frame hitch at tick {simulationTicks}: update {updateTime.TotalMilliseconds:F0} ms "
                    + $"({update.ExecutedTicks} ticks), render {renderTime.TotalMilliseconds:F0} ms.");
            if (capture is not null)
                captureFinalFrame!(capture);
            renderedFrames++;
            if (reachedTickLimit)
                break;
        }
        return new SdlRunResult(renderedFrames, simulationTicks, droppedTicks, clock.InterpolationFraction);
    }

    private static SdlKeyboardKey? mapKey(SDL_Keycode key) => key switch
    {
        SDL_Keycode.SDLK_BACKSPACE => SdlKeyboardKey.Backspace,
        SDL_Keycode.SDLK_RETURN => SdlKeyboardKey.Enter,
        SDL_Keycode.SDLK_ESCAPE => SdlKeyboardKey.Escape,
        SDL_Keycode.SDLK_SPACE => SdlKeyboardKey.Space,
        SDL_Keycode.SDLK_F2 => SdlKeyboardKey.F2,
        SDL_Keycode.SDLK_LEFT => SdlKeyboardKey.Left,
        SDL_Keycode.SDLK_UP => SdlKeyboardKey.Up,
        SDL_Keycode.SDLK_RIGHT => SdlKeyboardKey.Right,
        SDL_Keycode.SDLK_DOWN => SdlKeyboardKey.Down,
        >= SDL_Keycode.SDLK_0 and <= SDL_Keycode.SDLK_9 =>
            SdlKeyboardKey.Digit0 + (int)(key - SDL_Keycode.SDLK_0),
        >= SDL_Keycode.SDLK_A and <= SDL_Keycode.SDLK_Z =>
            SdlKeyboardKey.A + (int)(key - SDL_Keycode.SDLK_A),
        _ => null,
    };

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
        if (_device is not null)
        {
            _renderer?.Dispose();
            _renderer = null;
            if (_windowClaimed && _window is not null)
                SDL_ReleaseWindowFromGPUDevice(_device, _window);
            SDL_DestroyGPUDevice(_device);
            _device = null;
            _windowClaimed = false;
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

public readonly record struct SdlRunResult(
    int RenderedFrames,
    int SimulationTicks,
    int DroppedTicks,
    double InterpolationFraction);
