using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using SDL;
using Waddamburo.Formats.Don;
using Waddamburo.Formats.Nud;
using Waddamburo.Formats.Nut;
using static SDL.SDL3;

namespace Waddamburo.Platform.Sdl.Rendering;

/// <summary>Shared-device Don renderer. All methods must run on the owning SDL thread.</summary>
public sealed unsafe class SdlDonRenderer : IDisposable, IGpuRenderPrepass
{
    // Stage units covered by a target: menus use 600x600, gameplay the game's 448x256 slot.
    private (float Width, float Height) _targetSize = (600, 600);
    private (uint Width, uint Height) _targetPixels = (600, 600);
    private const int PaletteSize = 40;
    private const string ShaderPrefix = "Waddamburo.Shaders.";
    // Independently reconstructed camera from observed gameplay projection geometry.
    private static readonly Matrix4x4 GameplayCamera =
        Matrix4x4.CreateLookAt(new(-430, 22, 1045), new(6.6f, 15.6f, 0), Vector3.UnitY)
        * Matrix4x4.CreatePerspectiveFieldOfView(2 * MathF.PI / 180, 448f / 256, 1, 10000);
    private static readonly Matrix4x4 PlayerOneCamera = new(
        50.5310f, 0.4728f, 0.5763f, 0.4715f,
        0f, 57.2952f, -0.0214f, -0.0175f,
        27.0253f, -0.8839f, -1.0776f, -0.8817f,
        0.0008f, -866.3033f, 1675.5881f, 2280.0266f);

    private readonly SDL_GPUDevice* _device;
    private readonly RenderDevice _compositor;
    private readonly string _assetRoot;
    private readonly DonAnimationFile _bindAnimation;
    private readonly DonSkeleton _skeleton;
    private readonly ImmutableArray<Matrix4x4> _bindWorld;
    private readonly Dictionary<string, DonAnimationFile> _animations = new(StringComparer.Ordinal);
    private readonly List<GpuMesh> _meshes = [];
    private readonly Dictionary<uint, nint>[] _faceTextures = [[], []];
    private readonly List<nint> _ownedTextures = [];
    private readonly List<nint> _ownedBuffers = [];
    private readonly Dictionary<(bool Blend, SDL_GPUCullMode Cull), nint> _pipelines = [];
    private readonly Player[] _players = [new(), new()];
    private readonly float[] _pose = new float[DonSkeleton.CharacterValuesPerFrame];

    /// <summary>Display-rate position between the last two ticks (0 = previous, 1 = latest).</summary>
    public float Interpolation { get; set; } = 1;
    private readonly Target[] _targets = new Target[2];
    private readonly float[] _poseUniforms = new float[16 * (PaletteSize + 1)];
    private SDL_GPUShader* _vertexShader;
    private SDL_GPUShader* _fragmentShader;
    private SDL_GPUGraphicsPipeline* _postPipeline;
    private SDL_GPUSampler* _linearSampler;
    private SDL_GPUSampler* _nearestSampler;
    private SDL_GPUTexture* _whiteTexture;
    private bool _mirrorPlayerTwoCamera = true;
    private bool _gameplayCamera;
    private bool _disposed;

    internal SdlDonRenderer(SDL_GPUDevice* device, RenderDevice compositor, string assetRoot)
    {
        _device = device;
        _compositor = compositor;
        _assetRoot = Path.GetFullPath(assetRoot ?? throw new ArgumentNullException(nameof(assetRoot)));
        if (!Directory.Exists(_assetRoot))
            throw new DirectoryNotFoundException($"Don asset root does not exist: {_assetRoot}");

        try
        {
            _bindAnimation = readAnimation("ani/don_bind.bin");
            _skeleton = DonSkeleton.ForAnimation(_bindAnimation);
            _bindWorld = _skeleton.EvaluateWorld(_bindAnimation.GetFrame(0));
            _linearSampler = createSampler(SDL_GPUFilter.SDL_GPU_FILTER_LINEAR);
            _nearestSampler = createSampler(SDL_GPUFilter.SDL_GPU_FILTER_NEAREST);
            _vertexShader = createShader("don.vert", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_VERTEX, 0, 2);
            _fragmentShader = createShader("don.frag", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_FRAGMENT, 1, 1);
            _postPipeline = createPostPipeline();
            _whiteTexture = uploadRgba(1, 1, [255, 255, 255, 255]);
            loadModel("parts/body/body_000000.nud");
            loadModel("parts/head/head_000000.nud");
            loadFaces(0, "parts/paint/paint_000000.nut", replaceFaceColor: false);
            loadFaces(1, "parts/paint/paint_000000.nut", replaceFaceColor: true);
            for (var index = 0; index < _targets.Length; index++)
                _targets[index] = createTarget();
            var idle = loadMotion("don_select_loop");
            foreach (var player in _players)
                player.Set(idle, idle);
            _compositor.AddPrepass(this);
        }
        catch
        {
            foreach (var target in _targets)
                if (target.TextureId.Value != 0)
                    _compositor.UnregisterBorrowedTexture(target.TextureId);
            releaseResources();
            throw;
        }
    }

    public RenderTextureId GetTexture(int playerIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(playerIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(playerIndex, _targets.Length);
        return _targets[playerIndex].TextureId;
    }

    public void Reset(bool mirrorPlayerTwoCamera, bool gameplayCamera = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _mirrorPlayerTwoCamera = mirrorPlayerTwoCamera;
        _gameplayCamera = gameplayCamera;
        _targetSize = gameplayCamera ? (448, 256) : (600, 600);
        var idle = loadMotion("don_select_loop");
        foreach (var player in _players)
            player.Set(idle, idle);
    }

    public void SetMotion(int playerIndex, string? oneShot, string? loop)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(playerIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(playerIndex, _players.Length);
        var player = _players[playerIndex];
        var nextLoop = loop is null ? null : loadMotion(normalizeMotion(loop));
        if (oneShot is not null)
        {
            player.Set(loadMotion(normalizeMotion(oneShot)), nextLoop);
            return;
        }
        if (nextLoop is not null)
            player.Set(nextLoop, nextLoop);
    }

    public void SetIdle(int playerIndex, string loop)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(playerIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(playerIndex, _players.Length);
        _players[playerIndex].SetIdle(loadMotion(normalizeMotion(loop)));
    }

    public void Advance(double frames = 1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(frames) || frames < 0)
            throw new ArgumentOutOfRangeException(nameof(frames));
        foreach (var player in _players)
            player.Advance(frames);
    }

    void IGpuRenderPrepass.Record(SDL_GPUCommandBuffer* commandBuffer, uint width, uint height)
    {
        // Render at twice the window's stage scale and let the composite's downscale anti-alias.
        var scale = Math.Min(width / 1280f, height / 720f);
        uint pixels(float units) => (uint)Math.Clamp(MathF.Round(units * scale * 2 / 8) * 8, 64, 4096);
        resizeTargets((pixels(_targetSize.Width), pixels(_targetSize.Height)));
        for (var index = 0; index < _players.Length; index++)
            recordPlayer(commandBuffer, index);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _compositor.RemovePrepass(this);
        foreach (var target in _targets)
            if (target.TextureId.Value != 0)
                _compositor.UnregisterBorrowedTexture(target.TextureId);
        SDL_WaitForGPUIdle(_device);
        releaseResources();
        _disposed = true;
    }

    private void recordPlayer(SDL_GPUCommandBuffer* commandBuffer, int playerIndex)
    {
        var player = _players[playerIndex];
        var target = _targets[playerIndex];
        player.Sample(Interpolation, _pose, _skeleton.ExpressionOffset);
        ReadOnlySpan<float> frame = _pose;
        var animatedWorld = _skeleton.EvaluateWorld(frame);
        var matrices = MemoryMarshal.Cast<float, Matrix4x4>(_poseUniforms.AsSpan());
        var camera = _gameplayCamera ? GameplayCamera : PlayerOneCamera;
        var reflectedCamera = playerIndex == 1 && _mirrorPlayerTwoCamera;
        if (reflectedCamera)
        {
            camera.M11 = -camera.M11;
            camera.M21 = -camera.M21;
            camera.M31 = -camera.M31;
            camera.M41 = -camera.M41;
        }
        matrices[0] = camera;
        for (var index = 0; index < PaletteSize; index++)
        {
            if (index < animatedWorld.Length && Matrix4x4.Invert(_bindWorld[index], out var inverseBind))
                matrices[index + 1] = inverseBind * animatedWorld[index];
            else
                matrices[index + 1] = Matrix4x4.Identity;
        }

        var colorTarget = new SDL_GPUColorTargetInfo
        {
            texture = (SDL_GPUTexture*)target.Color,
            clear_color = new SDL_FColor(),
            load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_CLEAR,
            store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_STORE,
        };
        var depthTarget = new SDL_GPUDepthStencilTargetInfo
        {
            texture = (SDL_GPUTexture*)target.Depth,
            clear_depth = 1,
            load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_CLEAR,
            store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_DONT_CARE,
        };
        var pass = SDL_BeginGPURenderPass(commandBuffer, &colorTarget, 1, &depthTarget);
        if (pass is null)
            throw sdlFailure("begin the Don model pass");
        fixed (float* pose = _poseUniforms)
            SDL_PushGPUVertexUniformData(commandBuffer, 0, (nint)pose, checked((uint)(_poseUniforms.Length * sizeof(float))));

        var expression = _skeleton.GetExpression(frame);
        var face = _faceTextures[playerIndex].TryGetValue((uint)expression, out var faceAddress)
            ? (SDL_GPUTexture*)faceAddress
            : _whiteTexture;
        var binding = new SDL_GPUTextureSamplerBinding();
        foreach (var blended in new[] { false, true })
        {
            foreach (var mesh in _meshes)
            {
                foreach (var material in mesh.Materials)
                {
                    if ((material.SourceBlend != 0) != blended)
                        continue;
                    var kind = material.ShaderKind;
                    // Kind 8 is a head back-face pass, not the normal-mesh inverted hull.
                    // The silhouette post-pass supplies its visible outer edge.
                    if (kind == 8)
                        continue;
                    // Hull line: 3 stage pixels, offset per axis (clip units per stage pixel).
                    var outline = new Float4(kind is 3 or 8 ? 3f : 0, 2f / _targetSize.Width, 2f / _targetSize.Height, 0);
                    SDL_PushGPUVertexUniformData(commandBuffer, 1, (nint)(&outline), (uint)sizeof(Float4));
                    var cull = material.CullMode switch
                    {
                        0x405 => SDL_GPUCullMode.SDL_GPU_CULLMODE_BACK,
                        0x404 => SDL_GPUCullMode.SDL_GPU_CULLMODE_FRONT,
                        _ => SDL_GPUCullMode.SDL_GPU_CULLMODE_NONE,
                    };
                    // Reflecting the 2P camera reverses projected triangle winding. Preserve
                    // the authored visible side instead of rendering the mesh inside-out.
                    if (reflectedCamera)
                    {
                        cull = cull switch
                        {
                            SDL_GPUCullMode.SDL_GPU_CULLMODE_BACK => SDL_GPUCullMode.SDL_GPU_CULLMODE_FRONT,
                            SDL_GPUCullMode.SDL_GPU_CULLMODE_FRONT => SDL_GPUCullMode.SDL_GPU_CULLMODE_BACK,
                            _ => cull,
                        };
                    }
                    SDL_BindGPUGraphicsPipeline(pass, getModelPipeline(blended, cull));
                    var vertexBinding = new SDL_GPUBufferBinding { buffer = (SDL_GPUBuffer*)mesh.VertexBuffer };
                    SDL_BindGPUVertexBuffers(pass, 0, &vertexBinding, 1);
                    var indexBinding = new SDL_GPUBufferBinding { buffer = (SDL_GPUBuffer*)mesh.IndexBuffer };
                    SDL_BindGPUIndexBuffer(pass, &indexBinding, SDL_GPUIndexElementSize.SDL_GPU_INDEXELEMENTSIZE_16BIT);
                    var texture = kind == 2
                        ? face
                        : material.TextureIds.Length > 0
                        && mesh.Textures.TryGetValue(material.TextureIds[0], out var textureAddress)
                            ? (SDL_GPUTexture*)textureAddress
                            : _whiteTexture;
                    var materialUniforms = new MaterialUniforms
                    {
                        Parameters = new Float4(material.AlphaFunction != 0 || kind == 8
                            ? Math.Max(material.AlphaReference, (byte)6) / 255f
                            : 0, kind, 0, 0),
                        ReplaceRed = playerIndex == 0
                            ? color(0x6C, 0xC3, 0xC6)
                            : color(0xF9, 0x4C, 0x2C),
                        ReplaceGreen = playerIndex == 0
                            ? color(0xF9, 0x4C, 0x2C)
                            : color(0x6C, 0xC3, 0xC6),
                        ReplaceBlue = color(0xF8, 0xF0, 0xDC),
                    };
                    SDL_PushGPUFragmentUniformData(commandBuffer, 0, (nint)(&materialUniforms), (uint)sizeof(MaterialUniforms));
                    binding = new SDL_GPUTextureSamplerBinding
                    {
                        texture = texture,
                        sampler = kind == 1 ? _nearestSampler : _linearSampler,
                    };
                    SDL_BindGPUFragmentSamplers(pass, 0, &binding, 1);
                    SDL_DrawGPUIndexedPrimitives(pass, mesh.IndexCount, 1, 0, 0, 0);
                }
            }
        }
        SDL_EndGPURenderPass(pass);

        var finalTarget = new SDL_GPUColorTargetInfo
        {
            texture = (SDL_GPUTexture*)target.Final,
            load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_DONT_CARE,
            store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_STORE,
        };
        var post = SDL_BeginGPURenderPass(commandBuffer, &finalTarget, 1, null);
        if (post is null)
            throw sdlFailure("begin the Don outline pass");
        SDL_BindGPUGraphicsPipeline(post, _postPipeline);
        var postBinding = new SDL_GPUTextureSamplerBinding { texture = (SDL_GPUTexture*)target.Color, sampler = _linearSampler };
        SDL_BindGPUFragmentSamplers(post, 0, &postBinding, 1);
        var postUniform = new Float4(3f * _targetPixels.Width / _targetSize.Width,
            1f / _targetPixels.Width, 1f / _targetPixels.Height, 0);
        SDL_PushGPUFragmentUniformData(commandBuffer, 0, (nint)(&postUniform), (uint)sizeof(Float4));
        SDL_DrawGPUPrimitives(post, 3, 1, 0, 0);
        SDL_EndGPURenderPass(post);
    }

    private void loadModel(string relativePath)
    {
        var path = resolveAsset(relativePath);
        var model = NudFile.Parse(File.ReadAllBytes(path));
        var textures = uploadNut(Path.ChangeExtension(path, ".nut"));
        foreach (var item in model.Objects)
        {
            foreach (var polygon in item.Polygons)
            {
                if (polygon.TriangleIndices.IsEmpty)
                    continue;
                var vertices = polygon.Vertices.Select(static vertex => new GpuVertex(
                    vertex.Position,
                    vertex.Normal,
                    vertex.TextureCoordinate,
                    vertex.Color,
                    vertex.BoneWeights,
                    vertex.BoneIndices)).ToArray();
                _meshes.Add(new GpuMesh(
                    (nint)uploadBuffer(MemoryMarshal.AsBytes(vertices.AsSpan()), SDL_GPUBufferUsageFlags.SDL_GPU_BUFFERUSAGE_VERTEX),
                    (nint)uploadBuffer(MemoryMarshal.AsBytes(polygon.TriangleIndices.AsSpan()), SDL_GPUBufferUsageFlags.SDL_GPU_BUFFERUSAGE_INDEX),
                    checked((uint)polygon.TriangleIndices.Length),
                    polygon.Materials,
                    textures));
            }
        }
    }

    private void loadFaces(int playerIndex, string relativePath, bool replaceFaceColor)
    {
        var nut = NutFile.Parse(File.ReadAllBytes(resolveAsset(relativePath)));
        foreach (var texture in nut.Textures)
        {
            var pixels = NutTextureDecoder.DecodeRgba8(texture);
            if (replaceFaceColor)
                replaceRgb(pixels, 0xF9, 0x4C, 0x2C, 0x6C, 0xC3, 0xC6);
            var id = texture.GlobalId ?? checked((uint)texture.Index);
            _faceTextures[playerIndex][id] = (nint)uploadRgba(texture.Width, texture.Height, pixels);
        }
    }

    private static void replaceRgb(
        Span<byte> pixels,
        byte sourceRed,
        byte sourceGreen,
        byte sourceBlue,
        byte targetRed,
        byte targetGreen,
        byte targetBlue)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] != sourceRed
                || pixels[offset + 1] != sourceGreen
                || pixels[offset + 2] != sourceBlue)
            {
                continue;
            }
            pixels[offset] = targetRed;
            pixels[offset + 1] = targetGreen;
            pixels[offset + 2] = targetBlue;
        }
    }

    private Dictionary<uint, nint> uploadNut(string path)
    {
        var result = new Dictionary<uint, nint>();
        var nut = NutFile.Parse(File.ReadAllBytes(path));
        foreach (var texture in nut.Textures)
        {
            var pixels = NutTextureDecoder.DecodeRgba8(texture);
            result[texture.GlobalId ?? checked((uint)texture.Index)] = (nint)uploadRgba(texture.Width, texture.Height, pixels);
        }
        return result;
    }

    private DonAnimationFile loadMotion(string name)
    {
        if (_animations.TryGetValue(name, out var animation))
            return animation;
        animation = readAnimation($"ani/{name}.bin");
        if (animation.ValuesPerFrame != DonSkeleton.CharacterValuesPerFrame)
            throw new InvalidDataException($"Don motion '{name}' has stride {animation.ValuesPerFrame}, expected {DonSkeleton.CharacterValuesPerFrame}.");
        _animations.Add(name, animation);
        return animation;
    }

    private DonAnimationFile readAnimation(string relativePath) =>
        DonAnimationFile.Parse(File.ReadAllBytes(resolveAsset(relativePath)));

    private string resolveAsset(string relativePath)
    {
        var path = Path.GetFullPath(relativePath, _assetRoot);
        var relative = Path.GetRelativePath(_assetRoot, path);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidDataException($"Don asset path escapes its root: {relativePath}");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Don asset was not found: {relativePath}", path);
        return path;
    }

    private Target createTarget(RenderTextureId? reuse = null)
    {
        var colorTexture = createTexture(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
            SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
        var depthTexture = createTexture(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_D32_FLOAT,
            SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET);
        var finalTexture = createTexture(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
            SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER);
        if (reuse is { } id)
            _compositor.ReplaceBorrowedTexture(id, (nint)finalTexture);
        return new Target((nint)colorTexture, (nint)depthTexture, (nint)finalTexture,
            reuse ?? _compositor.RegisterBorrowedTexture((nint)finalTexture));
    }

    private void resizeTargets((uint Width, uint Height) pixels)
    {
        if (pixels == _targetPixels) return;
        _targetPixels = pixels;
        for (var index = 0; index < _targets.Length; index++)
        {
            var old = _targets[index];
            var fresh = createTarget(old.TextureId);
            _targets[index] = fresh;
            // SDL defers the release until submitted work no longer uses the textures.
            foreach (var texture in new[] { old.Color, old.Depth, old.Final })
            {
                _ownedTextures.Remove(texture);
                SDL_ReleaseGPUTexture(_device, (SDL_GPUTexture*)texture);
            }
        }
    }

    private SDL_GPUTexture* createTexture(SDL_GPUTextureFormat format, SDL_GPUTextureUsageFlags usage, uint width = 0, uint height = 0)
    {
        if (width == 0) (width, height) = _targetPixels;
        var info = new SDL_GPUTextureCreateInfo
        {
            type = SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D,
            format = format,
            usage = usage,
            width = width,
            height = height,
            layer_count_or_depth = 1,
            num_levels = 1,
            sample_count = SDL_GPUSampleCount.SDL_GPU_SAMPLECOUNT_1,
        };
        var texture = SDL_CreateGPUTexture(_device, &info);
        if (texture is null)
            throw sdlFailure("create a Don texture");
        _ownedTextures.Add((nint)texture);
        return texture;
    }

    private SDL_GPUTexture* uploadRgba(uint width, uint height, ReadOnlySpan<byte> pixels)
    {
        var texture = createTexture(SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
            SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER, width, height);
        uploadTexture(texture, width, height, pixels);
        return texture;
    }

    private void uploadTexture(SDL_GPUTexture* texture, uint width, uint height, ReadOnlySpan<byte> pixels)
    {
        var transferInfo = new SDL_GPUTransferBufferCreateInfo
        {
            usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
            size = checked((uint)pixels.Length),
        };
        var transfer = SDL_CreateGPUTransferBuffer(_device, &transferInfo);
        if (transfer is null)
            throw sdlFailure("create a Don texture upload buffer");
        try
        {
            var mapped = SDL_MapGPUTransferBuffer(_device, transfer, false);
            if (mapped == 0)
                throw sdlFailure("map a Don texture upload buffer");
            pixels.CopyTo(new Span<byte>((void*)mapped, pixels.Length));
            SDL_UnmapGPUTransferBuffer(_device, transfer);
            var command = SDL_AcquireGPUCommandBuffer(_device);
            var copy = SDL_BeginGPUCopyPass(command);
            var source = new SDL_GPUTextureTransferInfo { transfer_buffer = transfer, pixels_per_row = width, rows_per_layer = height };
            var destination = new SDL_GPUTextureRegion { texture = texture, w = width, h = height, d = 1 };
            SDL_UploadToGPUTexture(copy, &source, &destination, false);
            SDL_EndGPUCopyPass(copy);
            if (!SDL_SubmitGPUCommandBuffer(command))
                throw sdlFailure("submit a Don texture upload");
        }
        finally
        {
            SDL_ReleaseGPUTransferBuffer(_device, transfer);
        }
    }

    private SDL_GPUBuffer* uploadBuffer(ReadOnlySpan<byte> bytes, SDL_GPUBufferUsageFlags usage)
    {
        var info = new SDL_GPUBufferCreateInfo { usage = usage, size = checked((uint)bytes.Length) };
        var buffer = SDL_CreateGPUBuffer(_device, &info);
        if (buffer is null)
            throw sdlFailure("create a Don geometry buffer");
        _ownedBuffers.Add((nint)buffer);
        var transferInfo = new SDL_GPUTransferBufferCreateInfo { usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD, size = info.size };
        var transfer = SDL_CreateGPUTransferBuffer(_device, &transferInfo);
        if (transfer is null)
            throw sdlFailure("create a Don geometry upload buffer");
        try
        {
            var mapped = SDL_MapGPUTransferBuffer(_device, transfer, false);
            bytes.CopyTo(new Span<byte>((void*)mapped, bytes.Length));
            SDL_UnmapGPUTransferBuffer(_device, transfer);
            var command = SDL_AcquireGPUCommandBuffer(_device);
            var copy = SDL_BeginGPUCopyPass(command);
            var source = new SDL_GPUTransferBufferLocation { transfer_buffer = transfer };
            var destination = new SDL_GPUBufferRegion { buffer = buffer, size = info.size };
            SDL_UploadToGPUBuffer(copy, &source, &destination, false);
            SDL_EndGPUCopyPass(copy);
            if (!SDL_SubmitGPUCommandBuffer(command))
                throw sdlFailure("submit a Don geometry upload");
            return buffer;
        }
        finally
        {
            SDL_ReleaseGPUTransferBuffer(_device, transfer);
        }
    }

    private SDL_GPUGraphicsPipeline* getModelPipeline(bool blend, SDL_GPUCullMode cull)
    {
        if (_pipelines.TryGetValue((blend, cull), out var address))
            return (SDL_GPUGraphicsPipeline*)address;
        var attributes = stackalloc SDL_GPUVertexAttribute[6];
        var formats = stackalloc SDL_GPUVertexElementFormat[6]
        {
            SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3,
            SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3,
            SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2,
            SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4,
            SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4,
            SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4,
        };
        var offsets = stackalloc uint[6] { 0, 12, 24, 32, 48, 64 };
        for (var index = 0; index < 6; index++)
            attributes[index] = new SDL_GPUVertexAttribute { location = (uint)index, format = formats[index], offset = offsets[index] };
        var vertexDescription = new SDL_GPUVertexBufferDescription
        {
            slot = 0,
            pitch = (uint)sizeof(GpuVertex),
            input_rate = SDL_GPUVertexInputRate.SDL_GPU_VERTEXINPUTRATE_VERTEX,
        };
        var blendState = new SDL_GPUColorTargetBlendState();
        if (blend)
        {
            blendState = new SDL_GPUColorTargetBlendState
            {
                enable_blend = true,
                src_color_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_SRC_ALPHA,
                dst_color_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA,
                color_blend_op = SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD,
                src_alpha_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE,
                dst_alpha_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA,
                alpha_blend_op = SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD,
            };
        }
        var target = new SDL_GPUColorTargetDescription
        {
            format = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
            blend_state = blendState,
        };
        var info = new SDL_GPUGraphicsPipelineCreateInfo
        {
            vertex_shader = _vertexShader,
            fragment_shader = _fragmentShader,
            vertex_input_state = new SDL_GPUVertexInputState
            {
                vertex_buffer_descriptions = &vertexDescription,
                num_vertex_buffers = 1,
                vertex_attributes = attributes,
                num_vertex_attributes = 6,
            },
            primitive_type = SDL_GPUPrimitiveType.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST,
            rasterizer_state = new SDL_GPURasterizerState { cull_mode = cull, front_face = SDL_GPUFrontFace.SDL_GPU_FRONTFACE_COUNTER_CLOCKWISE },
            depth_stencil_state = new SDL_GPUDepthStencilState
            {
                enable_depth_test = !blend,
                enable_depth_write = !blend,
                compare_op = SDL_GPUCompareOp.SDL_GPU_COMPAREOP_LESS_OR_EQUAL,
            },
            target_info = new SDL_GPUGraphicsPipelineTargetInfo
            {
                color_target_descriptions = &target,
                num_color_targets = 1,
                has_depth_stencil_target = true,
                depth_stencil_format = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_D32_FLOAT,
            },
        };
        var pipeline = SDL_CreateGPUGraphicsPipeline(_device, &info);
        if (pipeline is null)
            throw sdlFailure("create a Don model pipeline");
        _pipelines.Add((blend, cull), (nint)pipeline);
        return pipeline;
    }

    private SDL_GPUGraphicsPipeline* createPostPipeline()
    {
        var vertex = createShader("don-post.vert", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_VERTEX, 0, 0);
        var fragment = createShader("don-post.frag", SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_FRAGMENT, 1, 1);
        try
        {
            var target = new SDL_GPUColorTargetDescription { format = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM };
            var info = new SDL_GPUGraphicsPipelineCreateInfo
            {
                vertex_shader = vertex,
                fragment_shader = fragment,
                primitive_type = SDL_GPUPrimitiveType.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST,
                rasterizer_state = new SDL_GPURasterizerState { cull_mode = SDL_GPUCullMode.SDL_GPU_CULLMODE_NONE },
                target_info = new SDL_GPUGraphicsPipelineTargetInfo { color_target_descriptions = &target, num_color_targets = 1 },
            };
            var pipeline = SDL_CreateGPUGraphicsPipeline(_device, &info);
            return pipeline is null ? throw sdlFailure("create the Don outline pipeline") : pipeline;
        }
        finally
        {
            SDL_ReleaseGPUShader(_device, vertex);
            SDL_ReleaseGPUShader(_device, fragment);
        }
    }

    private SDL_GPUShader* createShader(string name, SDL_GPUShaderStage stage, uint samplerCount, uint uniformCount)
    {
        var formats = SDL_GetGPUShaderFormats(_device);
        var format = (formats & SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV) != 0
            ? SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV
            : (formats & SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL) != 0
                ? SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL
                : throw new PlatformNotSupportedException($"SDL_GPU selected unsupported shader formats: {formats}.");
        var extension = format == SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV ? "spv" : "dxil";
        using var stream = typeof(SdlDonRenderer).Assembly.GetManifestResourceStream($"{ShaderPrefix}{name}.{extension}")
            ?? throw new InvalidOperationException($"Packaged Don shader '{name}.{extension}' is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var code = memory.ToArray();
        fixed (byte* codePointer = code)
        fixed (byte* entry = "main\0"u8)
        {
            var info = new SDL_GPUShaderCreateInfo
            {
                code = codePointer,
                code_size = (nuint)code.Length,
                entrypoint = entry,
                format = format,
                stage = stage,
                num_samplers = samplerCount,
                num_uniform_buffers = uniformCount,
            };
            var shader = SDL_CreateGPUShader(_device, &info);
            return shader is null ? throw sdlFailure($"create Don shader '{name}'") : shader;
        }
    }

    private SDL_GPUSampler* createSampler(SDL_GPUFilter filter)
    {
        var info = new SDL_GPUSamplerCreateInfo
        {
            min_filter = filter,
            mag_filter = filter,
            mipmap_mode = SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_NEAREST,
            address_mode_u = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_REPEAT,
            address_mode_v = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_REPEAT,
            address_mode_w = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_REPEAT,
        };
        var sampler = SDL_CreateGPUSampler(_device, &info);
        return sampler is null ? throw sdlFailure("create a Don sampler") : sampler;
    }

    private void releaseResources()
    {
        foreach (var pipeline in _pipelines.Values)
            SDL_ReleaseGPUGraphicsPipeline(_device, (SDL_GPUGraphicsPipeline*)pipeline);
        _pipelines.Clear();
        if (_postPipeline is not null) SDL_ReleaseGPUGraphicsPipeline(_device, _postPipeline);
        if (_fragmentShader is not null) SDL_ReleaseGPUShader(_device, _fragmentShader);
        if (_vertexShader is not null) SDL_ReleaseGPUShader(_device, _vertexShader);
        if (_nearestSampler is not null) SDL_ReleaseGPUSampler(_device, _nearestSampler);
        if (_linearSampler is not null) SDL_ReleaseGPUSampler(_device, _linearSampler);
        foreach (var buffer in _ownedBuffers) SDL_ReleaseGPUBuffer(_device, (SDL_GPUBuffer*)buffer);
        foreach (var texture in _ownedTextures) SDL_ReleaseGPUTexture(_device, (SDL_GPUTexture*)texture);
        _ownedBuffers.Clear();
        _ownedTextures.Clear();
    }

    private static string normalizeMotion(string value)
    {
        var normalized = value.ToLowerInvariant()
            .Replace("1p", "1P", StringComparison.Ordinal)
            .Replace("2p", "2P", StringComparison.Ordinal);
        return normalized == "don_siwng02" ? "don_swing02" : normalized;
    }

    private static Float4 color(byte red, byte green, byte blue) => new(red / 255f, green / 255f, blue / 255f, 1);

    private static InvalidOperationException sdlFailure(string operation) => new($"Failed to {operation}: {SDL_GetError()}");

    private sealed class Player
    {
        public DonAnimationFile Motion { get; private set; } = null!;
        public DonAnimationFile? Loop { get; private set; }
        private double _frame;
        private double _lastStep; // frames advanced by the latest tick; 0 after a cut

        public void Set(DonAnimationFile motion, DonAnimationFile? loop)
        {
            Motion = motion;
            Loop = loop;
            _frame = 0;
            _lastStep = 0;
        }

        public void SetIdle(DonAnimationFile loop)
        {
            if (Motion != Loop) Loop = loop; // a one-shot is running: it ends into the new idle
            else if (Motion != loop) Set(loop, loop);
        }

        public void Advance(double frames)
        {
            _lastStep = frames;
            _frame += frames;
            if (_frame < Motion.FrameCount)
                return;
            if (Loop is not null)
            {
                _frame = (_frame - Motion.FrameCount) % Loop.FrameCount;
                Motion = Loop;
                return;
            }
            // Holding the final frame: report only the distance actually moved.
            var last = Motion.FrameCount - 1;
            _lastStep = Math.Max(0, last - (_frame - frames));
            _frame = last;
        }

        /// <summary>
        /// Writes the pose one tick behind the simulation, moved forward by the display
        /// interpolation, blending the two nearest baked frames. The expression is not blended.
        /// </summary>
        // ponytail: right after a one-shot hands over to its loop the lagged position clamps
        // to the loop's first frame for the rest of that tick.
        public void Sample(float interpolation, float[] pose, int? expressionOffset)
        {
            var position = Math.Max(0, _frame - _lastStep * (1 - interpolation));
            var index = Math.Min((int)position, Motion.FrameCount - 1);
            var weight = (float)Math.Min(1, position - index);
            var current = Motion.GetFrame(index);
            var next = index + 1 < Motion.FrameCount ? Motion.GetFrame(index + 1)
                : Loop == Motion ? Motion.GetFrame(0)
                : current;
            for (var i = 0; i < pose.Length; i++)
            {
                var delta = next[i] - current[i];
                // Baked Euler angles can wrap by a full turn between frames.
                if (MathF.Abs(MathF.Abs(delta) - MathF.Tau) < 0.5f)
                    delta -= MathF.CopySign(MathF.Tau, delta);
                pose[i] = current[i] + delta * weight;
            }
            if (expressionOffset is int offset)
                pose[offset] = current[offset];
        }
    }

    private sealed class GpuMesh(
        nint vertexBuffer,
        nint indexBuffer,
        uint indexCount,
        ImmutableArray<NudMaterial> materials,
        Dictionary<uint, nint> textures)
    {
        public nint VertexBuffer { get; } = vertexBuffer;
        public nint IndexBuffer { get; } = indexBuffer;
        public uint IndexCount { get; } = indexCount;
        public ImmutableArray<NudMaterial> Materials { get; } = materials;
        public Dictionary<uint, nint> Textures { get; } = textures;
    }

    private readonly record struct Target(nint Color, nint Depth, nint Final, RenderTextureId TextureId);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GpuVertex(
        Vector3 Position,
        Vector3 Normal,
        Vector2 TextureCoordinate,
        Vector4 Color,
        Vector4 Weights,
        Vector4 BoneIndices);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Float4(float X, float Y, float Z, float W);

    [StructLayout(LayoutKind.Sequential)]
    private struct MaterialUniforms
    {
        public Float4 Parameters;
        public Float4 ReplaceRed;
        public Float4 ReplaceGreen;
        public Float4 ReplaceBlue;
    }
}
