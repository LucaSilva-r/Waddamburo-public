using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.Platform.Sdl;

// Textures: the renderer's uploads, replacements and releases, on the owner thread.
public sealed unsafe partial class SdlApplication
{
    // atlas: may share an atlas page: never drawn tiled (UVs past 0..1), never updated in place.
    public RenderTextureId UploadRgba8(uint width, uint height, ReadOnlySpan<byte> pixels, bool atlas = false)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer!.UploadRgba8(width, height, pixels, atlas);
    }

    /// <summary>Whether BC7 textures can be drawn (natively, or decoded by bgfx).</summary>
    public bool SupportsBc7 => _renderer?.SupportsBc7 ?? false;

    /// <summary>Overlays can blur what is under them (frosted glass); else they darken it.</summary>
    public bool BlursBackdrop => _renderer?.BlursBackdrop ?? false;

    public RenderTextureId UploadBc7(uint width, uint height, ReadOnlySpan<byte> blocks)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer!.UploadBc7(width, height, blocks);
    }

    /// <summary>Whether the GPU draws this block format natively (false before the renderer starts).</summary>
    public bool Supports(TextureBlockFormat format) => _renderer?.Supports(format) ?? false;

    public RenderTextureId UploadBlocks(TextureBlockFormat format, uint width, uint height, ReadOnlySpan<byte> blocks, bool atlas = false)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer!.UploadBlocks(format, width, height, blocks, atlas);
    }

    public bool TryReplaceBc7(RenderTextureId texture, uint width, uint height, ReadOnlySpan<byte> blocks)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer!.TryReplaceBc7(texture, width, height, blocks);
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

    /// <summary>F9: upscaled textures shown (true) or the originals, for comparison.</summary>
    public bool ShowUpscaled => _renderer?.ShowReplacements ?? true;

    public bool TryReplaceRgba8(RenderTextureId texture, uint width, uint height, ReadOnlySpan<byte> pixels)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer!.TryReplaceRgba8(texture, width, height, pixels);
    }

    // The last presented frame as bgfx saw it: GPU-bound, submit-bound or waiting on the swapchain.
    private string gpuStats()
    {
        if (_renderer is null)
            return "";
        var stats = Bgfx.bgfx.get_stats();
        double ms(long ticks, long frequency) => frequency == 0 ? 0 : ticks * 1000.0 / frequency;
        // Reports are read by scripts: decimal points whatever the player's locale.
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"; bgfx {stats->numDraw} draws, gpu {ms(stats->gpuTimeEnd - stats->gpuTimeBegin, stats->gpuTimerFreq):F1} ms, "
            + $"submit {ms(stats->cpuTimeEnd - stats->cpuTimeBegin, stats->cpuTimerFreq):F1} ms, "
            + $"wait render {ms(stats->waitRender, stats->cpuTimerFreq):F1} ms; "
            + $"{stats->width}x{stats->height}, textures {stats->textureMemoryUsed / 1048576}, targets {stats->rtMemoryUsed / 1048576} MiB; ")
            + string.Join(", ", Enumerable.Range(0, stats->numViews).Select(index =>
            {
                var view = stats->viewStats[index];
                return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"view {view.view} {ms(view.gpuTimeEnd - view.gpuTimeBegin, stats->gpuTimerFreq):F1} ms");
            }));
    }

    /// <summary>Live textures against the renderer's limit ("live/max").</summary>
    public int TextureCount => _renderer?.TextureCount ?? 0;

    public string TextureUsage => _renderer is null ? "" : BgfxSupport.TextureUsage;

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
        return new SdlDonRenderer(_renderer!, assetRoot);
    }

    /// <summary>A texture to draw whole frames into (the editor's scene thumbnails and previews).</summary>
    public RenderTextureId CreatePreview(int width, int height) =>
        (_renderer ?? throw new InvalidOperationException("The renderer is not ready.")).CreatePreview(checked((ushort)width), checked((ushort)height));

    /// <summary>Draws the frame into a preview with the next present; false when this present already draws its most.</summary>
    public bool DrawPreview(RenderTextureId preview, RenderFrame frame) => _renderer?.DrawPreview(preview, frame) ?? false;

    public void ReleasePreview(RenderTextureId preview) => _renderer?.ReleasePreview(preview);
}
