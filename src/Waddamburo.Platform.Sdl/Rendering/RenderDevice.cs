using System.Runtime.InteropServices;
using Bgfx;
using ImGuiNET;
using SDL;
using static SDL.SDL3;

namespace Waddamburo.Platform.Sdl.Rendering;

public readonly record struct RgbaTextureUpload(uint Width, uint Height, ReadOnlyMemory<byte> Pixels);

/// <summary>Owns bgfx resources and frame submission for one window.</summary>
internal sealed unsafe class RenderDevice : IDisposable
{
    // Prepasses (the Don targets) own views 0..ClearView-1 and run first; bgfx executes views in id order.
    internal const ushort ClearView = 16;
    private const ushort MainView = 17;
    private const ushort PresentView = 18; // the stage drawn at RenderHeight, scaled into the window
    private const ushort ImGuiView = 19; // the effects editor's panels, over the whole window

    private readonly SDL_Window* _window;
    private readonly Dictionary<uint, bgfx.TextureHandle> _textures = [];
    // Upscaled copies drawn in place of their originals (same id).
    private readonly Dictionary<uint, bgfx.TextureHandle> _replacements = [];
    private readonly HashSet<uint> _borrowedTextures = [];
    private readonly HashSet<uint> _drawnBorrowed = [];

    /// <summary>The frame being presented draws this prepass texture.</summary>
    internal bool Draws(RenderTextureId texture) => _drawnBorrowed.Contains(texture.Value);
    private readonly List<IGpuRenderPrepass> _prepasses = [];
    private readonly bgfx.ProgramHandle _quadProgram;
    private readonly bgfx.ProgramHandle _maskProgram;
    // The frosted backdrop: its own program (absent where its shader isn't packaged) and quarter-size targets.
    private readonly bgfx.ProgramHandle? _blurProgram;
    private readonly bgfx.UniformHandle _blurUniform;
    private Backdrop? _backdrop;
    private const ushort BackdropView = 11; // 11 draws the scene small, 12-15 blur it (the Don prepasses use 0-3)
    private readonly bgfx.UniformHandle _textureSampler;
    private bgfx.VertexLayout _layout;
    private uint _width;
    private uint _height;
    private uint _resetFlags = BgfxSupport.ResetFlags;
    private byte _frameLatency = BgfxSupport.MaxFrameLatency;
    private uint _nextTextureId = 1;
    private bool _disposed;

    public RenderDevice(SDL_Window* window, uint width, uint height)
    {
        _window = window;
        _width = width;
        _height = height;
        _quadProgram = BgfxSupport.LoadProgram("vs_quad", "fs_quad");
        _maskProgram = BgfxSupport.LoadProgram("vs_quad", "fs_mask");
        _textureSampler = bgfx.create_uniform("s_texture", bgfx.UniformType.Sampler, 1);
        _blurUniform = bgfx.create_uniform("u_blur", bgfx.UniformType.Vec4, 1);
        try
        {
            _blurProgram = BgfxSupport.LoadProgram("vs_quad", "fs_blur");
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"Warning RENDER_BLUR: no frosted backdrop ({exception.Message}).");
        }
        fixed (bgfx.VertexLayout* layout = &_layout)
        {
            bgfx.vertex_layout_begin(layout, bgfx.RendererType.Noop);
            bgfx.vertex_layout_add(layout, bgfx.Attrib.Position, 2, bgfx.AttribType.Float, false, false);
            bgfx.vertex_layout_add(layout, bgfx.Attrib.TexCoord0, 2, bgfx.AttribType.Float, false, false);
            bgfx.vertex_layout_add(layout, bgfx.Attrib.Color0, 4, bgfx.AttribType.Float, false, false);
            bgfx.vertex_layout_add(layout, bgfx.Attrib.Color1, 4, bgfx.AttribType.Float, false, false);
            bgfx.vertex_layout_end(layout);
        }
        bgfx.set_view_mode(MainView, bgfx.ViewMode.Sequential);
    }

    public RenderTextureId UploadRgba8(uint width, uint height, ReadOnlySpan<byte> pixels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        if ((ulong)pixels.Length != checked((ulong)width * height * 4))
            throw new ArgumentException("RGBA8 data must contain exactly width * height * 4 bytes.", nameof(pixels));
        var id = new RenderTextureId(_nextTextureId++);
        _textures.Add(id.Value, BgfxSupport.CreateRgba8(width, height, pixels));
        return id;
    }

    public RenderTextureId[] UploadRgba8Batch(IReadOnlyList<RgbaTextureUpload> uploads)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        var ids = new RenderTextureId[uploads.Count];
        for (var index = 0; index < ids.Length; index++)
            ids[index] = UploadRgba8(uploads[index].Width, uploads[index].Height, uploads[index].Pixels.Span);
        return ids;
    }

    /// <summary>Replaces the pixels of an owned texture of the same size (streamed video frames).</summary>
    public void UpdateRgba8(RenderTextureId id, uint width, uint height, ReadOnlySpan<byte> pixels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_borrowedTextures.Contains(id.Value) || !_textures.TryGetValue(id.Value, out var texture))
            throw new ArgumentException($"Texture {id.Value} is not owned by this render device.", nameof(id));
        if ((ulong)pixels.Length != checked((ulong)width * height * 4))
            throw new ArgumentException("RGBA8 data must contain exactly width * height * 4 bytes.", nameof(pixels));
        bgfx.update_texture_2d(texture, 0, 0, 0, 0, (ushort)width, (ushort)height, BgfxSupport.Copy(pixels), ushort.MaxValue);
    }

    /// <summary>
    /// Swaps an owned texture for new pixels of any size under the same id (UVs are normalised, so
    /// an upscaled copy drops in); false when the texture is gone. The original stays for
    /// <see cref="ShowReplacements"/> off (a comparison toggle).
    /// </summary>
    public bool TryReplaceRgba8(RenderTextureId id, uint width, uint height, ReadOnlySpan<byte> pixels)
    {
        if ((ulong)pixels.Length != checked((ulong)width * height * 4))
            throw new ArgumentException("RGBA8 data must contain exactly width * height * 4 bytes.", nameof(pixels));
        if (!replaceable(id))
            return false;
        replace(id, BgfxSupport.CreateRgba8(width, height, pixels));
        return true;
    }

    /// <summary>As <see cref="TryReplaceRgba8"/> with BC7 blocks.</summary>
    public bool TryReplaceBc7(RenderTextureId id, uint width, uint height, ReadOnlySpan<byte> blocks)
    {
        checkBc7(width, height, blocks);
        if (!replaceable(id))
            return false;
        replace(id, BgfxSupport.CreateBc7(width, height, blocks));
        return true;
    }

    private bool replaceable(RenderTextureId id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return !_borrowedTextures.Contains(id.Value) && _textures.ContainsKey(id.Value);
    }

    private void replace(RenderTextureId id, bgfx.TextureHandle texture)
    {
        if (_replacements.Remove(id.Value, out var previous))
            bgfx.destroy_texture(previous);
        _replacements[id.Value] = texture;
    }

    /// <summary>Whether BC7 textures can be drawn (natively, or decoded by bgfx).</summary>
    public bool SupportsBc7 { get; } = BgfxSupport.SupportsBc7();

    /// <summary>A frame's backdrop can be drawn frosted (<see cref="RenderFrame.BlurredQuads"/>).</summary>
    public bool BlursBackdrop => _blurProgram is not null;

    public RenderTextureId UploadBc7(uint width, uint height, ReadOnlySpan<byte> blocks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        checkBc7(width, height, blocks);
        var id = new RenderTextureId(_nextTextureId++);
        _textures.Add(id.Value, BgfxSupport.CreateBc7(width, height, blocks));
        return id;
    }

    /// <summary>The block formats drawn natively (BC1, BC3 and BC7 on every desktop GPU).</summary>
    public bool Supports(TextureBlockFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return BgfxSupport.Supports(format);
    }

    /// <summary>A BC1 (8 bytes per 4x4 block) or BC3/BC7 (16) texture, sizes multiples of 4.</summary>
    public RenderTextureId UploadBlocks(TextureBlockFormat format, uint width, uint height, ReadOnlySpan<byte> blocks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width == 0 || height == 0 || width % 4 != 0 || height % 4 != 0
            || (ulong)blocks.Length != (ulong)width / 4 * (height / 4) * (format == TextureBlockFormat.Bc1 ? 8u : 16u))
            throw new ArgumentException($"{format} textures need sizes that are multiples of 4 and whole blocks.", nameof(blocks));
        var id = new RenderTextureId(_nextTextureId++);
        _textures.Add(id.Value, BgfxSupport.CreateBlocks(format, width, height, blocks));
        return id;
    }

    private static void checkBc7(uint width, uint height, ReadOnlySpan<byte> blocks)
    {
        if (width == 0 || height == 0 || width % 4 != 0 || height % 4 != 0 || (ulong)blocks.Length != (ulong)width * height)
            throw new ArgumentException("BC7 textures need sizes that are multiples of 4 and one byte per pixel.", nameof(blocks));
    }

    /// <summary>Draw replaced textures (true) or their originals.</summary>
    public bool ShowReplacements { get; set; } = true;

    private bgfx.TextureHandle drawn(uint id) =>
        ShowReplacements && _replacements.TryGetValue(id, out var replacement) ? replacement : _textures[id];

    public void ReleaseTexture(RenderTextureId id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_borrowedTextures.Contains(id.Value))
            throw new ArgumentException($"Texture {id.Value} is owned by a render prepass.", nameof(id));
        if (!_textures.Remove(id.Value, out var texture))
            throw new ArgumentException($"Texture {id.Value} is not owned by this render device.", nameof(id));
        bgfx.destroy_texture(texture);
        if (_replacements.Remove(id.Value, out var replacement))
            bgfx.destroy_texture(replacement);
    }

    internal RenderTextureId RegisterBorrowedTexture(bgfx.TextureHandle texture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = new RenderTextureId(_nextTextureId++);
        _textures.Add(id.Value, texture);
        _borrowedTextures.Add(id.Value);
        return id;
    }

    /// <summary>Points a borrowed id at a new texture, so frames built earlier stay valid.</summary>
    internal void ReplaceBorrowedTexture(RenderTextureId id, bgfx.TextureHandle texture)
    {
        if (!_borrowedTextures.Contains(id.Value))
            throw new ArgumentException($"Texture {id.Value} is not borrowed by this render device.", nameof(id));
        _textures[id.Value] = texture;
    }

    internal void UnregisterBorrowedTexture(RenderTextureId id)
    {
        if (!_borrowedTextures.Remove(id.Value) || !_textures.Remove(id.Value))
            throw new ArgumentException($"Texture {id.Value} is not borrowed by this render device.", nameof(id));
    }

    internal void AddPrepass(IGpuRenderPrepass prepass) => _prepasses.Add(prepass ?? throw new ArgumentNullException(nameof(prepass)));

    internal void RemovePrepass(IGpuRenderPrepass prepass)
    {
        if (!_prepasses.Remove(prepass))
            throw new ArgumentException("Render prepass is not registered.", nameof(prepass));
    }

    /// <summary>
    /// Without blocking, whether the next <see cref="Present"/> can start: false while the render
    /// thread still presents the previous frame (vsync), so the caller can keep polling input.
    /// </summary>
    public bool TryAcquireSwapchain()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return BgfxSupport.RenderThreadReady;
    }

    internal ImGuiLayer? ImGuiLayer { get; set; }

    public RenderViewport StageViewport { get; private set; }

    public RenderRectangle? StageRegion { get; set; }

    /// <summary>
    /// With <see cref="StageRegion"/>: where the stage goes (normalized window coordinates, may reach past the region),
    /// the whole region drawn as a canvas around it (the layout editor shows what lies outside the stage).
    /// </summary>
    public RenderRectangle? StageBox { get; set; }

    // The stage's place inside the main view (offset, then scale, in view units): identity but for a StageBox.
    private (float X, float Y, float Width, float Height) _map = (0, 0, 1, 1);

    public RenderCapture? Present(RenderFrame frame, bool capture = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        validateFrame(frame);

        int pixelWidth, pixelHeight;
        if (!SDL_GetWindowSizeInPixels(_window, &pixelWidth, &pixelHeight))
            throw new InvalidOperationException($"Failed to query the window pixel size: {SDL_GetError()}");
        var width = (uint)Math.Max(1, pixelWidth);
        var height = (uint)Math.Max(1, pixelHeight);
        if (width != _width || height != _height || _resetFlags != BgfxSupport.ResetFlags || _frameLatency != BgfxSupport.MaxFrameLatency)
        {
            _frameLatency = BgfxSupport.MaxFrameLatency;
            _width = width;
            _height = height;
            _resetFlags = BgfxSupport.ResetFlags;
            var swapChain = new bgfx.SwapChain
            {
                width = width,
                height = height,
                formatColor = bgfx.TextureFormat.Count,
                formatDepthStencil = bgfx.TextureFormat.D24S8,
                depth = BgfxSupport.InvalidTexture,
                maxFrameLatency = BgfxSupport.MaxFrameLatency,
            };
            bgfx.reset(BgfxSupport.ResetFlags, &swapChain);
        }

        // A prepass draws only what this frame shows (an off-screen Don costs ~5 ms on integrated GPUs).
        _drawnBorrowed.Clear();
        foreach (var quad in frame.QuadSpan)
            if (_borrowedTextures.Contains(quad.Texture.Value))
                _drawnBorrowed.Add(quad.Texture.Value);
        // The whole window (letterbox bars included) takes the clear colour; the stage draws inside.
        bgfx.set_view_rect(ClearView, 0, 0, (ushort)width, (ushort)height, 0, 1);
        bgfx.set_view_clear(ClearView, (ushort)(bgfx.ClearFlags.Color | bgfx.ClearFlags.Stencil),
            BgfxSupport.PackRgba(frame.ClearColor), 1, 0);
        bgfx.touch(ClearView);
        RenderViewport viewport;
        if (StageRegion is { } region)
        {
            var x = (int)Math.Clamp(region.X * width, 0, width - 1);
            var y = (int)Math.Clamp(region.Y * height, 0, height - 1);
            var w = (uint)Math.Clamp(region.Width * width, 1, width - x);
            var h = (uint)Math.Clamp(region.Height * height, 1, height - y);
            if (StageBox is { } box)
            {
                viewport = new RenderViewport(x, y, (int)w, (int)h);
                _map = ((box.X * width - x) / w, (box.Y * height - y) / h, box.Width * width / w, box.Height * height / h);
            }
            else
            {
                var fitted = stage169(frame) ? RenderViewport.AspectFit(w, h, stageAspect()) : frame.ResolveViewport(w, h);
                viewport = fitted with { X = fitted.X + x, Y = fitted.Y + y };
            }
        }
        else viewport = letterbox(stage169(frame) ? RenderViewport.AspectFit(width, height, stageAspect()) : frame.ResolveViewport(width, height), width, height);
        StageViewport = viewport;
        // Below the window's own resolution (RenderHeight), the stage draws into a target of that size, scaled up at
        // the end (needs the blur program, which also copies); the Don prepasses then draw at that size too.
        var scaled = RenderHeight > 0 && viewport.Height > RenderHeight && _blurProgram is not null;
        var stage = scaled ? stageTarget(viewport) : viewport;
        var ratio = (double)stage.Height / viewport.Height;
        foreach (var prepass in _prepasses)
            prepass.Record((uint)Math.Max(1, Math.Round(width * ratio)), (uint)Math.Max(1, Math.Round(height * ratio)));
        if (scaled)
        {
            bgfx.set_view_frame_buffer(MainView, _stage!.Buffer);
            bgfx.set_view_clear(MainView, (ushort)(bgfx.ClearFlags.Color | bgfx.ClearFlags.Stencil),
                BgfxSupport.PackRgba(frame.ClearColor), 1, 0);
        }
        else
        {
            bgfx.set_view_frame_buffer(MainView, BgfxSupport.BackBuffer);
            bgfx.set_view_clear(MainView, (ushort)bgfx.ClearFlags.None, 0, 1, 0);
        }
        bgfx.set_view_rect(MainView, (short)stage.X, (short)stage.Y, (ushort)stage.Width, (ushort)stage.Height, 0, 1);
        bgfx.set_view_scissor(MainView, (ushort)stage.X, (ushort)stage.Y, (ushort)stage.Width, (ushort)stage.Height);
        bgfx.touch(MainView);
        var quads = frame.QuadSpan;
        if (frame.BlurredQuads > 0 && _blurProgram is { } blur)
        {
            var backdrop = Math.Min(frame.BlurredQuads, quads.Length);
            frosted(quads[..backdrop], blur, stage, frame.BackdropBrightness);
            quads = quads[backdrop..];
        }
        submitQuads(MainView, quads);
        _map = (0, 0, 1, 1);
        if (scaled)
        {
            bgfx.set_view_rect(PresentView, (short)viewport.X, (short)viewport.Y, (ushort)viewport.Width, (ushort)viewport.Height, 0, 1);
            bgfx.set_view_scissor(PresentView, (ushort)viewport.X, (ushort)viewport.Y, (ushort)viewport.Width, (ushort)viewport.Height);
            bgfx.set_view_mode(PresentView, bgfx.ViewMode.Sequential);
            fullscreen(PresentView, _stage!.Color, _blurProgram!.Value, 0, 0, 1, blend: false);
        }

        if (ImGuiLayer is { } gui)
            submitImGui(gui.EndFrame());

        if (!capture)
        {
            BgfxSupport.Frame();
            return null;
        }
        BgfxSupport.Callbacks.Capture = null;
        bgfx.request_screen_shot(BgfxSupport.BackBuffer, "capture");
        // The request is served while a later frame renders.
        for (var attempt = 0; attempt < 4 && BgfxSupport.Callbacks.Capture is null; attempt++)
            BgfxSupport.Frame();
        return BgfxSupport.Callbacks.Capture
            ?? throw new InvalidOperationException("bgfx did not deliver the screenshot.");
    }

    /// <summary>
    /// The stage's resolution in lines when lower than the window's (0: the window's own): the stage is drawn at it and
    /// scaled up once (the basic quality, for weak GPUs; the stage is authored at 720).
    /// </summary>
    public int RenderHeight { get; set; }

    private StageTarget? _stage;

    // The target the stage draws into below the window's resolution: the viewport's shape at RenderHeight lines.
    private RenderViewport stageTarget(RenderViewport viewport)
    {
        var size = ((ushort)Math.Max(1, Math.Round(viewport.Width * (double)RenderHeight / viewport.Height)), (ushort)RenderHeight);
        if (_stage is not { } stage || stage.Size != size)
        {
            _stage?.Dispose();
            _stage = StageTarget.Create(size);
        }
        return new RenderViewport(0, 0, size.Item1, size.Item2);
    }

    // Colour (sampled by the scaling pass) and a depth-stencil for the stage's masks.
    private sealed class StageTarget : IDisposable
    {
        public (ushort Width, ushort Height) Size { get; private init; }
        public bgfx.FrameBufferHandle Buffer;
        public bgfx.TextureHandle Color;

        public static StageTarget Create((ushort Width, ushort Height) size)
        {
            var (width, height) = size;
            var target = new StageTarget { Size = size };
            target.Color = bgfx.create_texture_2d(width, height, false, 1, bgfx.TextureFormat.RGBA8,
                (ulong)bgfx.TextureFlags.Rt | (ulong)(bgfx.SamplerFlags.UClamp | bgfx.SamplerFlags.VClamp), null, 0);
            var attachments = stackalloc bgfx.TextureHandle[2];
            attachments[0] = target.Color;
            var stencil = (bgfx.get_caps()->formats[(int)bgfx.TextureFormat.D24S8] & (uint)bgfx.CapsFormatFlags.TextureFramebuffer) != 0;
            if (stencil)
                attachments[1] = bgfx.create_texture_2d(width, height, false, 1, bgfx.TextureFormat.D24S8, (ulong)bgfx.TextureFlags.RtWriteOnly, null, 0);
            target.Buffer = bgfx.create_frame_buffer_from_handles((byte)(stencil ? 2 : 1), attachments, true);
            return target;
        }

        public void Dispose() => bgfx.destroy_frame_buffer(Buffer);
    }

    /// <summary>The whole 16:9 stage squeezed into a 4:3 viewport (a 4:3 layout's songs, and the editor previewing one).</summary>
    public bool Squeeze43 { get; set; }

    // How far the stage is into its 4:3 shape (0 16:9, 1 4:3), eased towards Squeeze43 so a switch slides, not jumps.
    private float _squeezed;
    private long _squeezedAt;
    private const float SqueezeSeconds = .35f;

    // The 16:9 stage's shape this frame, in the slide between 16:9 and 4:3.
    private double stageAspect()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        // A frame's step is capped at 1/30 s: a stall (the results loading as the shutter closes) must not skip the slide.
        var step = _squeezedAt == 0 ? 1 : (float)Math.Min(System.Diagnostics.Stopwatch.GetElapsedTime(_squeezedAt, now).TotalSeconds, 1 / 30d) / SqueezeSeconds;
        _squeezedAt = now;
        _squeezed = Squeeze43 ? Math.Min(1, _squeezed + step) : Math.Max(0, _squeezed - step);
        var eased = _squeezed * _squeezed * (3 - 2 * _squeezed); // smoothstep
        return 16d / 9 + (4d / 3 - 16d / 9) * eased;
    }

    /// <summary>osu!-style letterbox: the stage at this fraction of its size, placed in the free space (0-1, 0.5 = centred).</summary>
    public (float Size, float X, float Y) Letterbox { get; set; } = (1, .5f, .5f);

    // Only the 16:9 stage is squeezed (frames without an aspect, such as tools, draw as they are).
    private static bool stage169(RenderFrame frame) => frame.ContentAspectRatio is { } aspect && Math.Abs(aspect - 16d / 9) < .01;

    private RenderViewport letterbox(RenderViewport fitted, uint width, uint height)
    {
        var (size, x, y) = Letterbox;
        if (size >= 1)
            return fitted;
        var w = Math.Max(1, (int)(fitted.Width * size));
        var h = Math.Max(1, (int)(fitted.Height * size));
        return new RenderViewport((int)((width - w) * x), (int)((height - h) * y), w, h);
    }

    // The backdrop drawn at a quarter of the stage's size, blurred twice each way (a wide, cheap Gaussian: a few
    // hundred thousand pixels per pass), then stretched over the stage at the given brightness.
    private void frosted(ReadOnlySpan<RenderQuad> quads, bgfx.ProgramHandle blur, RenderViewport viewport, float brightness)
    {
        var size = ((ushort)Math.Max(1, viewport.Width / 4), (ushort)Math.Max(1, viewport.Height / 4));
        if (_backdrop is not { } backdrop || backdrop.Size != size)
        {
            _backdrop?.Dispose();
            _backdrop = backdrop = Backdrop.Create(size);
        }
        var (width, height) = size;
        bgfx.set_view_frame_buffer(BackdropView, backdrop.Scene);
        bgfx.set_view_rect(BackdropView, 0, 0, width, height, 0, 1);
        bgfx.set_view_scissor(BackdropView, 0, 0, width, height);
        bgfx.set_view_clear(BackdropView, (ushort)(bgfx.ClearFlags.Color | bgfx.ClearFlags.Stencil), 0x000000ff, 1, 0);
        bgfx.set_view_mode(BackdropView, bgfx.ViewMode.Sequential);
        bgfx.touch(BackdropView);
        submitQuads(BackdropView, quads);
        // Scene -> A (across) -> B (down) -> A (across, wider) -> B (down, wider).
        (bgfx.TextureHandle From, bgfx.FrameBufferHandle To, float X, float Y)[] passes =
        [
            // Taps no more than ~3 texels apart, so the wide pass leaves no gaps (wider ones streak).
            (backdrop.SceneColor, backdrop.A, 1f / width, 0), (backdrop.AColor, backdrop.B, 0, 1f / height),
            (backdrop.BColor, backdrop.A, 2f / width, 0), (backdrop.AColor, backdrop.B, 0, 2f / height),
        ];
        for (var pass = 0; pass < passes.Length; pass++)
        {
            var view = (ushort)(BackdropView + 1 + pass);
            bgfx.set_view_frame_buffer(view, passes[pass].To);
            bgfx.set_view_rect(view, 0, 0, width, height, 0, 1);
            bgfx.set_view_scissor(view, 0, 0, width, height);
            fullscreen(view, passes[pass].From, blur, passes[pass].X, passes[pass].Y, 1, blend: false);
        }
        fullscreen(MainView, backdrop.BColor, blur, 0, 0, brightness, blend: true);
    }

    // A quad over the whole view sampling a render target (flipped where targets start at the bottom left).
    private void fullscreen(ushort view, bgfx.TextureHandle texture, bgfx.ProgramHandle program, float x, float y, float brightness, bool blend)
    {
        bgfx.TransientVertexBuffer buffer;
        fixed (bgfx.VertexLayout* layout = &_layout)
        {
            if (bgfx.get_avail_transient_vertex_buffer(6, layout) < 6)
                return;
            bgfx.alloc_transient_vertex_buffer(&buffer, 6, layout);
        }
        var flip = bgfx.get_caps()->originBottomLeft != 0;
        var (top, bottom) = flip ? (1f, 0f) : (0f, 1f);
        var tint = new RenderColor(brightness, brightness, brightness, 1);
        var vertices = new Span<QuadVertex>(buffer.data, 6);
        vertices[0] = vertex(new(0, 0, 0, top), tint, RenderColor.Transparent);
        vertices[1] = vertex(new(1, 0, 1, top), tint, RenderColor.Transparent);
        vertices[2] = vertex(new(1, 1, 1, bottom), tint, RenderColor.Transparent);
        vertices[3] = vertices[0];
        vertices[4] = vertices[2];
        vertices[5] = vertex(new(0, 1, 0, bottom), tint, RenderColor.Transparent);
        var state = (ulong)(bgfx.StateFlags.WriteRgb | bgfx.StateFlags.WriteA);
        if (blend)
            state |= BgfxSupport.BlendSeparate(bgfx.StateFlags.BlendOne, bgfx.StateFlags.BlendInvSrcAlpha,
                bgfx.StateFlags.BlendOne, bgfx.StateFlags.BlendInvSrcAlpha);
        bgfx.set_state(state, 0);
        var step = stackalloc float[4] { x, y, 0, 0 };
        bgfx.set_uniform(_blurUniform, step, 1);
        bgfx.set_texture(0, _textureSampler, texture, (uint)(bgfx.SamplerFlags.UClamp | bgfx.SamplerFlags.VClamp));
        bgfx.set_transient_vertex_buffer(0, &buffer, 0, 6);
        bgfx.submit(view, program, 0, (byte)bgfx.DiscardFlags.All);
    }

    // The backdrop's targets: the scene (colour, with a stencil for its masks where the backend has one) and two to
    // blur between.
    private sealed class Backdrop : IDisposable
    {
        public (ushort Width, ushort Height) Size { get; private init; }
        public bgfx.FrameBufferHandle Scene, A, B;
        public bgfx.TextureHandle SceneColor, AColor, BColor;

        public static Backdrop Create((ushort Width, ushort Height) size)
        {
            var (width, height) = size;
            const ulong target = (ulong)bgfx.TextureFlags.Rt | (ulong)(bgfx.SamplerFlags.UClamp | bgfx.SamplerFlags.VClamp);
            bgfx.TextureHandle color() => bgfx.create_texture_2d(width, height, false, 1, bgfx.TextureFormat.RGBA8, target, null, 0);
            var backdrop = new Backdrop { Size = size, SceneColor = color(), AColor = color(), BColor = color() };
            var caps = bgfx.get_caps();
            var scene = stackalloc bgfx.TextureHandle[2];
            scene[0] = backdrop.SceneColor;
            var stencil = (caps->formats[(int)bgfx.TextureFormat.D24S8] & (uint)bgfx.CapsFormatFlags.TextureFramebuffer) != 0;
            if (stencil)
                scene[1] = bgfx.create_texture_2d(width, height, false, 1, bgfx.TextureFormat.D24S8, (ulong)bgfx.TextureFlags.RtWriteOnly, null, 0);
            backdrop.Scene = bgfx.create_frame_buffer_from_handles((byte)(stencil ? 2 : 1), scene, true);
            var a = backdrop.AColor;
            backdrop.A = bgfx.create_frame_buffer_from_handles(1, &a, true);
            var b = backdrop.BColor;
            backdrop.B = bgfx.create_frame_buffer_from_handles(1, &b, true);
            return backdrop;
        }

        public void Dispose()
        {
            bgfx.destroy_frame_buffer(Scene);
            bgfx.destroy_frame_buffer(A);
            bgfx.destroy_frame_buffer(B);
        }
    }

    // ImGui shares the straight-alpha quad shader; it premultiplies its output for BlendOne.
    private void submitImGui(ImDrawDataPtr data)
    {
        if (data.NativePtr == null || data.TotalVtxCount == 0 || data.DisplaySize.X <= 0 || data.DisplaySize.Y <= 0)
            return;
        const ushort view = ImGuiView;
        bgfx.set_view_frame_buffer(view, BgfxSupport.BackBuffer);
        bgfx.set_view_rect(view, 0, 0, (ushort)_width, (ushort)_height, 0, 1);
        bgfx.set_view_scissor(view, 0, 0, (ushort)_width, (ushort)_height);
        bgfx.set_view_clear(view, (ushort)bgfx.ClearFlags.None, 0, 1, 0);
        bgfx.set_view_mode(view, bgfx.ViewMode.Sequential);
        for (var listIndex = 0; listIndex < data.CmdListsCount; listIndex++)
        {
            var list = data.CmdLists[listIndex];
            var vertexCount = (uint)list.VtxBuffer.Size;
            var indexCount = (uint)list.IdxBuffer.Size;
            bgfx.TransientVertexBuffer vb;
            bgfx.TransientIndexBuffer ib;
            fixed (bgfx.VertexLayout* layout = &_layout)
            {
                if (bgfx.get_avail_transient_vertex_buffer(vertexCount, layout) < vertexCount
                    || bgfx.get_avail_transient_index_buffer(indexCount, false) < indexCount)
                    return;
                bgfx.alloc_transient_vertex_buffer(&vb, vertexCount, layout);
                bgfx.alloc_transient_index_buffer(&ib, indexCount, false);
            }
            var vertices = new Span<QuadVertex>(vb.data, (int)vertexCount);
            for (var i = 0; i < vertices.Length; i++)
            {
                var v = list.VtxBuffer[i];
                var position = (v.pos - data.DisplayPos) / data.DisplaySize;
                var c = v.col;
                vertices[i] = new QuadVertex(position.X, position.Y, v.uv.X, v.uv.Y,
                    (c & 255) / 255f, ((c >> 8) & 255) / 255f, ((c >> 16) & 255) / 255f, (c >> 24) / 255f,
                    0, 0, 0, 0);
            }
            new ReadOnlySpan<ushort>((void*)list.IdxBuffer.Data, (int)indexCount)
                .CopyTo(new Span<ushort>(ib.data, (int)indexCount));
            for (var commandIndex = 0; commandIndex < list.CmdBuffer.Size; commandIndex++)
            {
                var command = list.CmdBuffer[commandIndex];
                // The reset-state sentinel needs no action: each submission sets every state it uses.
                if (command.UserCallback != 0)
                {
                    if (command.UserCallback != -8)
                        throw new NotSupportedException("Custom ImGui draw callbacks are not supported.");
                    continue;
                }
                var clip = command.ClipRect;
                var scale = data.FramebufferScale;
                var x = (int)Math.Clamp(MathF.Floor((clip.X - data.DisplayPos.X) * scale.X), 0, _width);
                var y = (int)Math.Clamp(MathF.Floor((clip.Y - data.DisplayPos.Y) * scale.Y), 0, _height);
                var right = (int)Math.Clamp(MathF.Ceiling((clip.Z - data.DisplayPos.X) * scale.X), 0, _width);
                var bottom = (int)Math.Clamp(MathF.Ceiling((clip.W - data.DisplayPos.Y) * scale.Y), 0, _height);
                if (right <= x || bottom <= y || command.ElemCount == 0) continue;
                var id = (uint)command.TextureId;
                if (!_textures.ContainsKey(id)) continue;
                bgfx.set_scissor((ushort)x, (ushort)y, (ushort)(right - x), (ushort)(bottom - y));
                bgfx.set_stencil(0, 0);
                bgfx.set_state((ulong)(bgfx.StateFlags.WriteRgb | bgfx.StateFlags.WriteA)
                    | BgfxSupport.BlendSeparate(bgfx.StateFlags.BlendOne, bgfx.StateFlags.BlendInvSrcAlpha,
                        bgfx.StateFlags.BlendOne, bgfx.StateFlags.BlendInvSrcAlpha), 0);
                bgfx.set_texture(0, _textureSampler, drawn(id), (uint)(bgfx.SamplerFlags.UClamp | bgfx.SamplerFlags.VClamp));
                bgfx.set_transient_vertex_buffer(0, &vb, command.VtxOffset, vertexCount - command.VtxOffset);
                bgfx.set_transient_index_buffer(&ib, command.IdxOffset, command.ElemCount);
                bgfx.submit(view, _quadProgram, 0, (byte)bgfx.DiscardFlags.All);
            }
        }
    }

    private void submitQuads(ushort view, ReadOnlySpan<RenderQuad> quads)
    {
        if (quads.IsEmpty)
            return;
        var vertexCount = (uint)quads.Length * 6;
        bgfx.TransientVertexBuffer buffer;
        fixed (bgfx.VertexLayout* layout = &_layout)
        {
            // ponytail: frames past the transient buffer size draw only what fits; raise
            // Init.limits.maxTransientVbSize if a scene ever gets there.
            vertexCount = Math.Min(vertexCount, bgfx.get_avail_transient_vertex_buffer(vertexCount, layout) / 6 * 6);
            if (vertexCount == 0)
                return;
            bgfx.alloc_transient_vertex_buffer(&buffer, vertexCount, layout);
        }
        var vertices = new Span<QuadVertex>(buffer.data, (int)vertexCount);
        var quadCount = (int)vertexCount / 6;
        for (var index = 0; index < quadCount; index++)
        {
            var quad = quads[index];
            var multiply = quad.MultiplyColor;
            var add = quad.AddColor;
            var corners = vertices.Slice(index * 6, 6);
            corners[0] = vertex(quad.TopLeft, multiply, add);
            corners[1] = vertex(quad.TopRight, multiply, add);
            corners[2] = vertex(quad.BottomRight, multiply, add);
            corners[3] = corners[0];
            corners[4] = corners[2];
            corners[5] = vertex(quad.BottomLeft, multiply, add);
        }

        // One draw per run of quads that share texture, sampling and blend/stencil state.
        var start = 0;
        for (var index = 1; index <= quadCount; index++)
        {
            if (index < quadCount && sameBatch(quads[start], quads[index]))
                continue;
            var first = quads[start];
            bgfx.set_state(stateFor(first), 0);
            var stencilOperation = first.MaskOperation switch
            {
                RenderMaskOperation.Push => bgfx.StencilFlags.OpPassZIncrsat,
                RenderMaskOperation.Pop => bgfx.StencilFlags.OpPassZDecrsat,
                _ => bgfx.StencilFlags.OpPassZKeep,
            };
            var stencil = (uint)(bgfx.StencilFlags.TestEqual | bgfx.StencilFlags.OpFailSKeep | bgfx.StencilFlags.OpFailZKeep | stencilOperation)
                | first.MaskDepth | (0xffu << (int)bgfx.StencilFlags.FuncRmaskShift);
            bgfx.set_stencil(stencil, (uint)bgfx.StencilFlags.None);
            bgfx.set_texture(0, _textureSampler, drawn(first.Texture.Value), samplerFor(first));
            bgfx.set_transient_vertex_buffer(0, &buffer, (uint)start * 6, (uint)(index - start) * 6);
            bgfx.submit(view, first.MaskOperation == RenderMaskOperation.Draw ? _quadProgram : _maskProgram, 0, (byte)bgfx.DiscardFlags.All);
            start = index;
        }
    }

    private static bool sameBatch(in RenderQuad a, in RenderQuad b) =>
        a.Texture == b.Texture && a.Blend == b.Blend && a.MaskOperation == b.MaskOperation
        && a.MaskDepth == b.MaskDepth && a.Sampling == b.Sampling && tiles(a) == tiles(b);

    private static ulong stateFor(in RenderQuad quad)
    {
        if (quad.MaskOperation != RenderMaskOperation.Draw)
            return 0; // stencil only: no colour writes
        // Premultiplied source: normal s + d(1 - sa), add s + d, screen s + d(1 - s), multiply sd + d(1 - sa).
        var source = quad.Blend == RenderBlend.Multiply ? bgfx.StateFlags.BlendDstColor : bgfx.StateFlags.BlendOne;
        var destination = quad.Blend switch
        {
            RenderBlend.Add => bgfx.StateFlags.BlendOne,
            RenderBlend.Screen => bgfx.StateFlags.BlendInvSrcColor,
            _ => bgfx.StateFlags.BlendInvSrcAlpha,
        };
        return (ulong)(bgfx.StateFlags.WriteRgb | bgfx.StateFlags.WriteA)
            | BgfxSupport.BlendSeparate(source, destination, bgfx.StateFlags.BlendOne, bgfx.StateFlags.BlendInvSrcAlpha);
    }

    private static uint samplerFor(in RenderQuad quad)
    {
        var flags = quad.Sampling is RenderSampling.Nearest
            ? bgfx.SamplerFlags.MinPoint | bgfx.SamplerFlags.MagPoint | bgfx.SamplerFlags.MipPoint
            : bgfx.SamplerFlags.None;
        // Authored shapes tile a texture by giving UVs beyond 0..1; everything else clamps so
        // atlas edges do not bleed.
        if (!tiles(quad))
            flags |= bgfx.SamplerFlags.UClamp | bgfx.SamplerFlags.VClamp;
        return (uint)flags;
    }

    private static bool tiles(in RenderQuad quad)
    {
        static bool outside(RenderVertex vertex) =>
            vertex.U is < -0.001f or > 1.001f || vertex.V is < -0.001f or > 1.001f;
        return outside(quad.TopLeft) || outside(quad.TopRight) || outside(quad.BottomRight) || outside(quad.BottomLeft);
    }

    private QuadVertex vertex(RenderVertex corner, RenderColor multiply, RenderColor add) => new(
        _map.X + corner.X * _map.Width, _map.Y + corner.Y * _map.Height, corner.U, corner.V,
        multiply.Red, multiply.Green, multiply.Blue, multiply.Alpha,
        add.Red, add.Green, add.Blue, add.Alpha);

    private void validateFrame(RenderFrame frame)
    {
        validateColor(frame.ClearColor, nameof(frame.ClearColor));
        foreach (var quad in frame.QuadSpan)
        {
            if (!_textures.ContainsKey(quad.Texture.Value))
                throw new ArgumentException($"Render frame references unknown texture {quad.Texture.Value}.", nameof(frame));
            if (!Enum.IsDefined(quad.Sampling))
                throw new ArgumentException("Render frame contains an unknown sampling mode.", nameof(frame));
            if (!Enum.IsDefined(quad.MaskOperation)
                || (quad.MaskOperation == RenderMaskOperation.Push && quad.MaskDepth == byte.MaxValue)
                || (quad.MaskOperation == RenderMaskOperation.Pop && quad.MaskDepth == 0))
                throw new ArgumentException("Render frame contains an invalid stencil operation.", nameof(frame));
            if (!Enum.IsDefined(quad.Blend))
                throw new ArgumentException("Render frame contains an unknown blend mode.", nameof(frame));
            validateVertex(quad.TopLeft, nameof(quad.TopLeft));
            validateVertex(quad.TopRight, nameof(quad.TopRight));
            validateVertex(quad.BottomRight, nameof(quad.BottomRight));
            validateVertex(quad.BottomLeft, nameof(quad.BottomLeft));
            validateColor(quad.MultiplyColor, nameof(quad.MultiplyColor));
            validateColor(quad.AddColor, nameof(quad.AddColor));
        }
    }

    private static void validateVertex(RenderVertex vertex, string name)
    {
        if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y)
            || !float.IsFinite(vertex.U) || !float.IsFinite(vertex.V))
            throw new ArgumentException($"{name} must contain finite values.");
    }

    private static void validateColor(RenderColor color, string name)
    {
        if (!float.IsFinite(color.Red) || !float.IsFinite(color.Green)
            || !float.IsFinite(color.Blue) || !float.IsFinite(color.Alpha))
            throw new ArgumentException($"{name} must contain finite values.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        foreach (var (id, texture) in _textures)
            if (!_borrowedTextures.Contains(id))
                bgfx.destroy_texture(texture);
        _textures.Clear();
        foreach (var replacement in _replacements.Values)
            bgfx.destroy_texture(replacement);
        _replacements.Clear();
        _borrowedTextures.Clear();
        _prepasses.Clear();
        bgfx.destroy_program(_quadProgram);
        bgfx.destroy_program(_maskProgram);
        if (_blurProgram is { } blurProgram)
            bgfx.destroy_program(blurProgram);
        bgfx.destroy_uniform(_blurUniform);
        _backdrop?.Dispose();
        _stage?.Dispose();
        bgfx.destroy_uniform(_textureSampler);
        _disposed = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct QuadVertex(
        float X, float Y, float U, float V,
        float MultiplyRed, float MultiplyGreen, float MultiplyBlue, float MultiplyAlpha,
        float AddRed, float AddGreen, float AddBlue, float AddAlpha);
}

internal interface IGpuRenderPrepass
{
    /// <summary>Submits off-screen views; width/height are the window size in pixels.</summary>
    void Record(uint width, uint height);
}
