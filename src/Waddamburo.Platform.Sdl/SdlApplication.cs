using SDL;
using static SDL.SDL3;

namespace Waddamburo.Platform.Sdl;

public readonly record struct SdlClearColor(float Red, float Green, float Blue, float Alpha)
{
    public static SdlClearColor WaddamburoBlue { get; } = new(0.035f, 0.075f, 0.14f, 1f);
}

/// <summary>Owns SDL video, one window, and one SDL_GPU device on the creating thread.</summary>
public sealed unsafe class SdlApplication : IDisposable
{
    private readonly int _ownerThreadId;
    private SDL_Window* _window;
    private SDL_GPUDevice* _device;
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
            GpuDriver = SDL_GetGPUDeviceDriver(_device) ?? "unknown";
        }
        catch
        {
            disposeNativeResources();
            throw;
        }
    }

    public string GpuDriver { get; } = string.Empty;

    public int Run(int? frameLimit = null, SdlClearColor? clearColor = null)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frameLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(frameLimit));

        var color = clearColor ?? SdlClearColor.WaddamburoBlue;
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
            renderClearFrame(color);
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

    private void renderClearFrame(SdlClearColor color)
    {
        var commandBuffer = SDL_AcquireGPUCommandBuffer(_device);
        if (commandBuffer is null)
            throw sdlFailure("acquire a GPU command buffer");

        SDL_GPUTexture* swapchainTexture = null;
        uint width = 0;
        uint height = 0;
        if (!SDL_WaitAndAcquireGPUSwapchainTexture(commandBuffer, _window, &swapchainTexture, &width, &height))
        {
            SDL_CancelGPUCommandBuffer(commandBuffer);
            throw sdlFailure("acquire the swapchain texture");
        }

        if (swapchainTexture is not null)
        {
            var target = new SDL_GPUColorTargetInfo
            {
                texture = swapchainTexture,
                clear_color = new SDL_FColor
                {
                    r = color.Red,
                    g = color.Green,
                    b = color.Blue,
                    a = color.Alpha,
                },
                load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_CLEAR,
                store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_STORE,
            };
            var renderPass = SDL_BeginGPURenderPass(commandBuffer, &target, 1, null);
            if (renderPass is null)
            {
                SDL_CancelGPUCommandBuffer(commandBuffer);
                throw sdlFailure("begin the GPU render pass");
            }
            SDL_EndGPURenderPass(renderPass);
        }

        if (!SDL_SubmitGPUCommandBuffer(commandBuffer))
            throw sdlFailure("submit the GPU command buffer");
    }

    private void disposeNativeResources()
    {
        if (_device is not null)
        {
            SDL_WaitForGPUIdle(_device);
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
