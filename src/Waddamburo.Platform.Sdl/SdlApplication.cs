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

    public RenderTextureId[] UploadRgba8Batch(IReadOnlyList<RgbaTextureUpload> uploads)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer!.UploadRgba8Batch(uploads);
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
        Action<SdlKeyboardSnapshot>? updateFrame = null,
        Func<bool>? profileFrame = null,
        Action? togglePerformanceOverlay = null,
        Action<float, float>? pointerMoved = null,
        Func<bool>? performanceVisible = null,
        Action<SdlFrameMetrics>? performanceSample = null)
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
        using var frameProfile = Environment.GetEnvironmentVariable("WADDAMBURO_GAMEPLAY_FRAME_PROFILE") == "1"
            ? new FrameProfile() : null;
        var previousProfileEligible = false;
        var windowFocused = true;
        while (running && (frameLimit is null || renderedFrames < frameLimit))
        {
            var performanceActive = performanceVisible?.Invoke() == true;
            var inputStart = performanceActive ? Stopwatch.GetTimestamp() : 0;
            SDL_Event currentEvent;
            while (SDL_PollEvent(&currentEvent))
            {
                if (currentEvent.type is (uint)SDL_EventType.SDL_EVENT_QUIT
                    or (uint)SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED)
                {
                    running = false;
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST)
                {
                    _pressedKeys.Clear();
                    windowFocused = false;
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED)
                    windowFocused = true;
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_MOUSE_MOTION && pointerMoved is not null)
                {
                    int width, height;
                    if (SDL_GetWindowSize(_window, &width, &height) && width > 0 && height > 0)
                        pointerMoved(currentEvent.motion.x / width, currentEvent.motion.y / height);
                }
                else if (currentEvent.type is (uint)SDL_EventType.SDL_EVENT_KEY_DOWN
                    or (uint)SDL_EventType.SDL_EVENT_KEY_UP)
                {
                    var key = mapKey(currentEvent.key.key);
                    if (key is SdlKeyboardKey mapped)
                    {
                        if (mapped == SdlKeyboardKey.F5)
                        {
                            if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_KEY_DOWN && _pressedKeys.Add(mapped))
                                togglePerformanceOverlay?.Invoke();
                            else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_KEY_UP)
                                _pressedKeys.Remove(mapped);
                            continue;
                        }
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

            // Input runs apart from presentation, like osu!'s 1 kHz input thread: while vsync still
            // holds every swapchain image, drum presses are delivered (judged, hit sound played) at
            // once and SDL is polled again ~1 ms later, instead of waiting for the next frame.
            // Screenshot runs and minimised/hidden windows keep the blocking present.
            if (captureFinalFrame is null
                && (SDL_GetWindowFlags(_window) & (SDL_WindowFlags.SDL_WINDOW_MINIMIZED | SDL_WindowFlags.SDL_WINDOW_HIDDEN)) == 0
                && !_renderer!.TryAcquireSwapchain())
            {
                if (updateFrame is not null && pendingPresses.Count > 0)
                {
                    updateFrame(new SdlKeyboardSnapshot(_pressedKeys, pendingPresses,
                        TimeSpan.FromTicks(checked((long)(SDL_GetTicksNS() / 100)))));
                    pendingPresses.Clear();
                }
                Thread.Sleep(1);
                continue;
            }

            var profileEligibleAtStart = frameProfile is not null && windowFocused && (profileFrame?.Invoke() ?? true);
            var timestamp = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(previousTimestamp, timestamp);
            previousTimestamp = timestamp;
            var remainingTicks = tickLimit is int limit ? limit - simulationTicks : int.MaxValue;
            var eventTime = TimeSpan.FromTicks(checked((long)(SDL_GetTicksNS() / 100)));
            var keyboard = new SdlKeyboardSnapshot(_pressedKeys, pendingPresses, eventTime);
            updateFrame?.Invoke(keyboard);
            var inputTime = performanceActive ? Stopwatch.GetElapsedTime(inputStart) : TimeSpan.Zero;
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
            var profileEligibleAtEnd = frameProfile is not null && windowFocused && (profileFrame?.Invoke() ?? true);
            if (performanceActive)
                performanceSample?.Invoke(new SdlFrameMetrics(elapsed, inputTime, updateTime, renderTime, update.ExecutedTicks));
            if (frameProfile is not null)
            {
                if (previousProfileEligible && profileEligibleAtStart && profileEligibleAtEnd)
                    frameProfile.Record(elapsed, updateTime, renderTime, simulationTicks);
                else
                {
                    frameProfile.Report(simulationTicks);
                    frameProfile.Flush();
                }
            }
            previousProfileEligible = profileEligibleAtStart && profileEligibleAtEnd;
            if (hitchTrace && updateTime + renderTime > TimeSpan.FromMilliseconds(40))
                Console.Error.WriteLine($"Frame hitch at tick {simulationTicks}: update {updateTime.TotalMilliseconds:F0} ms "
                    + $"({update.ExecutedTicks} ticks), render {renderTime.TotalMilliseconds:F0} ms.");
            if (capture is not null)
                captureFinalFrame!(capture);
            renderedFrames++;
            if (reachedTickLimit)
                break;
        }
        frameProfile?.Report(simulationTicks);
        frameProfile?.Flush();
        return new SdlRunResult(renderedFrames, simulationTicks, droppedTicks, clock.InterpolationFraction);
    }

    private sealed class FrameProfile : IDisposable
    {
        private const int WindowFrames = 1920;
        private readonly double[] _frameTimes = new double[WindowFrames];
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly List<string> _reports = [];
        private int _frames;
        private double _maximumUpdateMs;
        private double _maximumRenderMs;
        private long _windowStart;
        private TimeSpan _cpuStart;
        private int _gen0Start;
        private int _gen1Start;
        private int _gen2Start;

        public void Record(TimeSpan interval, TimeSpan update, TimeSpan render, int tick)
        {
            if (_frames == 0)
            {
                _windowStart = Stopwatch.GetTimestamp();
                _cpuStart = _process.TotalProcessorTime;
                _gen0Start = GC.CollectionCount(0);
                _gen1Start = GC.CollectionCount(1);
                _gen2Start = GC.CollectionCount(2);
            }
            _frameTimes[_frames++] = interval.TotalMilliseconds;
            _maximumUpdateMs = Math.Max(_maximumUpdateMs, update.TotalMilliseconds);
            _maximumRenderMs = Math.Max(_maximumRenderMs, render.TotalMilliseconds);
            if (_frames == WindowFrames)
                Report(tick);
        }

        public void Report(int tick)
        {
            if (_frames == 0)
                return;
            var sorted = _frameTimes.AsSpan(0, _frames).ToArray();
            Array.Sort(sorted);
            var wallSeconds = Stopwatch.GetElapsedTime(_windowStart).TotalSeconds;
            var cpuSeconds = (_process.TotalProcessorTime - _cpuStart).TotalSeconds;
            var over2 = sorted.Count(static ms => ms > 1000d / 480);
            var over4 = sorted.Count(static ms => ms > 1000d / 240);
            var over8 = sorted.Count(static ms => ms > 1000d / 120);
            static double percentile(double[] values, double fraction) =>
                values[(int)Math.Ceiling((values.Length - 1) * fraction)];
            _reports.Add($"Gameplay frames to tick {tick}: {_frames} frames, "
                + $"FPS {_frames / wallSeconds:F0}, "
                + $"p50/p95/p99/max {percentile(sorted, .5):F2}/{percentile(sorted, .95):F2}/"
                + $"{percentile(sorted, .99):F2}/{sorted[^1]:F2} ms, "
                + $">2.08/>4.17/>8.33 ms {over2}/{over4}/{over8}, "
                + $"max update/render {_maximumUpdateMs:F2}/{_maximumRenderMs:F2} ms, "
                + $"CPU {cpuSeconds / wallSeconds * 100:F0}% of one core, "
                + $"RSS {_process.WorkingSet64 / 1048576d:F0} MiB, "
                + $"managed {GC.GetTotalMemory(false) / 1048576d:F0} MiB, "
                + $"GC {GC.CollectionCount(0) - _gen0Start}/"
                + $"{GC.CollectionCount(1) - _gen1Start}/"
                + $"{GC.CollectionCount(2) - _gen2Start}.");
            _frames = 0;
            _maximumUpdateMs = 0;
            _maximumRenderMs = 0;
        }

        public void Flush()
        {
            foreach (var report in _reports)
                Console.Error.WriteLine(report);
            _reports.Clear();
        }

        public void Dispose()
        {
            Flush();
            _process.Dispose();
        }
    }

    private static SdlKeyboardKey? mapKey(SDL_Keycode key) => key switch
    {
        SDL_Keycode.SDLK_BACKSPACE => SdlKeyboardKey.Backspace,
        SDL_Keycode.SDLK_RETURN => SdlKeyboardKey.Enter,
        SDL_Keycode.SDLK_ESCAPE => SdlKeyboardKey.Escape,
        SDL_Keycode.SDLK_SPACE => SdlKeyboardKey.Space,
        SDL_Keycode.SDLK_F1 => SdlKeyboardKey.F1,
        SDL_Keycode.SDLK_F2 => SdlKeyboardKey.F2,
        SDL_Keycode.SDLK_F5 => SdlKeyboardKey.F5,
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
