using System.Runtime.InteropServices;
using Bgfx;
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

    private readonly SDL_Window* _window;
    private readonly Dictionary<uint, bgfx.TextureHandle> _textures = [];
    // Upscaled copies drawn in place of their originals (same id).
    private readonly Dictionary<uint, bgfx.TextureHandle> _replacements = [];
    private readonly HashSet<uint> _borrowedTextures = [];
    private readonly List<IGpuRenderPrepass> _prepasses = [];
    private readonly bgfx.ProgramHandle _quadProgram;
    private readonly bgfx.ProgramHandle _maskProgram;
    private readonly bgfx.UniformHandle _textureSampler;
    private bgfx.VertexLayout _layout;
    private uint _width;
    private uint _height;
    private uint _resetFlags = BgfxSupport.ResetFlags;
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

    public RenderTextureId UploadBc7(uint width, uint height, ReadOnlySpan<byte> blocks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        checkBc7(width, height, blocks);
        var id = new RenderTextureId(_nextTextureId++);
        _textures.Add(id.Value, BgfxSupport.CreateBc7(width, height, blocks));
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
        if (width != _width || height != _height || _resetFlags != BgfxSupport.ResetFlags)
        {
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
            };
            bgfx.reset(BgfxSupport.ResetFlags, &swapChain);
        }

        foreach (var prepass in _prepasses)
            prepass.Record(width, height);

        // The whole window (letterbox bars included) takes the clear colour; the stage draws inside.
        bgfx.set_view_rect(ClearView, 0, 0, (ushort)width, (ushort)height, 0, 1);
        bgfx.set_view_clear(ClearView, (ushort)(bgfx.ClearFlags.Color | bgfx.ClearFlags.Stencil),
            BgfxSupport.PackRgba(frame.ClearColor), 1, 0);
        bgfx.touch(ClearView);
        var viewport = letterbox(frame.ResolveViewport(width, height), width, height);
        bgfx.set_view_rect(MainView, (short)viewport.X, (short)viewport.Y, (ushort)viewport.Width, (ushort)viewport.Height, 0, 1);
        bgfx.set_view_scissor(MainView, (ushort)viewport.X, (ushort)viewport.Y, (ushort)viewport.Width, (ushort)viewport.Height);
        submitQuads(frame.Quads.AsSpan());

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

    /// <summary>osu!-style letterbox: the stage at this fraction of its size, placed in the free space (0-1, 0.5 = centred).</summary>
    public (float Size, float X, float Y) Letterbox { get; set; } = (1, .5f, .5f);

    private RenderViewport letterbox(RenderViewport fitted, uint width, uint height)
    {
        var (size, x, y) = Letterbox;
        if (size >= 1)
            return fitted;
        var w = Math.Max(1, (int)(fitted.Width * size));
        var h = Math.Max(1, (int)(fitted.Height * size));
        return new RenderViewport((int)((width - w) * x), (int)((height - h) * y), w, h);
    }

    private void submitQuads(ReadOnlySpan<RenderQuad> quads)
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
            bgfx.submit(MainView, first.MaskOperation == RenderMaskOperation.Draw ? _quadProgram : _maskProgram, 0, (byte)bgfx.DiscardFlags.All);
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
        // Premultiplied source: normal s + d(1 - sa), add s + d, screen s + d(1 - s).
        var destination = quad.Blend switch
        {
            RenderBlend.Add => bgfx.StateFlags.BlendOne,
            RenderBlend.Screen => bgfx.StateFlags.BlendInvSrcColor,
            _ => bgfx.StateFlags.BlendInvSrcAlpha,
        };
        return (ulong)(bgfx.StateFlags.WriteRgb | bgfx.StateFlags.WriteA)
            | BgfxSupport.BlendSeparate(bgfx.StateFlags.BlendOne, destination, bgfx.StateFlags.BlendOne, bgfx.StateFlags.BlendInvSrcAlpha);
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

    private static QuadVertex vertex(RenderVertex corner, RenderColor multiply, RenderColor add) => new(
        corner.X, corner.Y, corner.U, corner.V,
        multiply.Red, multiply.Green, multiply.Blue, multiply.Alpha,
        add.Red, add.Green, add.Blue, add.Alpha);

    private void validateFrame(RenderFrame frame)
    {
        validateColor(frame.ClearColor, nameof(frame.ClearColor));
        foreach (var quad in frame.Quads)
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
