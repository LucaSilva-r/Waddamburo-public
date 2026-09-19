using SDL;
using Waddamburo.Platform.Sdl.Rendering;
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
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (frameLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(frameLimit));
        if (captureFinalFrame is not null && frameLimit is null)
            throw new ArgumentException("A final-frame capture requires a bounded frame count.", nameof(frameLimit));

        var renderedFrames = 0;
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
            }

            if (!running)
                break;
            var shouldCapture = captureFinalFrame is not null && renderedFrames + 1 == frameLimit;
            var capture = _renderer!.Present(frame, shouldCapture);
            if (capture is not null)
                captureFinalFrame!(capture);
            renderedFrames++;
        }
        return renderedFrames;
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
