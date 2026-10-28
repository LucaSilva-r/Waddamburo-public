namespace Waddamburo.Platform.Sdl.Rendering;

/// <summary>A single, thread-affine GPU viewport hosted by an existing Windows HWND.</summary>
public sealed unsafe class EmbeddedRenderer : IDisposable
{
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private RenderDevice? _device;

    public EmbeddedRenderer(nint windowHandle, int width, int height)
    {
        BgfxSupport.Initialize(windowHandle, (uint)Math.Max(1, width), (uint)Math.Max(1, height), false);
        Driver = BgfxSupport.RendererName;
        try
        {
            BgfxSupport.SetVsync(false);
            _device = new RenderDevice(null, (uint)Math.Max(1, width), (uint)Math.Max(1, height));
            Resize(width, height);
        }
        catch { BgfxSupport.Shutdown(); throw; }
    }

    public string Driver { get; }
    public bool Ready => device.TryAcquireSwapchain();
    private RenderDevice device
    {
        get
        {
            if (_owner != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Use the viewport on its creating thread.");
            return _device ?? throw new ObjectDisposedException(nameof(EmbeddedRenderer));
        }
    }
    public RenderTextureId Upload(uint width, uint height, ReadOnlySpan<byte> rgba) => device.UploadRgba8(width, height, rgba);
    public void Release(RenderTextureId texture) => device.ReleaseTexture(texture);
    public void Resize(int width, int height) => device.ResizeEmbedded((uint)Math.Max(1, width), (uint)Math.Max(1, height));
    public RenderCapture? Present(RenderFrame frame, bool capture = false) => device.Present(frame, capture);
    public SdlDonRenderer CreateDonRenderer(string assetRoot) => new(device, assetRoot);

    public void Dispose()
    {
        if (_device is null) return;
        device.Dispose();
        _device = null;
        BgfxSupport.Shutdown();
    }
}
