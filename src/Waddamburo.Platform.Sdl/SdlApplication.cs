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

    public SdlApplication(string title, int width, int height, bool debugGpu = false)
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

            _window = SDL_CreateWindow(
                title,
                width,
                height,
                SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY);
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

    public RenderTextureId UploadRgba8(uint width, uint height, ReadOnlySpan<byte> pixels)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer!.UploadRgba8(width, height, pixels);
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
        Action<RenderCapture>? captureFinalFrame = null)
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
                            _pressedKeys.Add(mapped);
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
            var keyboard = new SdlKeyboardSnapshot(_pressedKeys);
            var update = clock.AddElapsed(elapsed, () => simulationTick(keyboard), remainingTicks);
            simulationTicks += update.ExecutedTicks;
            droppedTicks += update.DroppedTicks;

            var reachedTickLimit = tickLimit is int requestedTicks && simulationTicks >= requestedTicks;
            var reachedFrameLimit = frameLimit is int requestedFrames && renderedFrames + 1 >= requestedFrames;
            var shouldCapture = captureFinalFrame is not null && (reachedTickLimit || reachedFrameLimit);
            var interpolationFraction = reachedTickLimit ? 1d : clock.InterpolationFraction;
            var capture = _renderer!.Present(createFrame(interpolationFraction), shouldCapture);
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
