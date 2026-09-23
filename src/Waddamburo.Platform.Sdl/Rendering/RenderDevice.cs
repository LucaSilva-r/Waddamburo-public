using System.Runtime.InteropServices;
using SDL;
using static SDL.SDL3;

namespace Waddamburo.Platform.Sdl.Rendering;

/// <summary>Owns SDL_GPU resources and command submission for one claimed window.</summary>
internal sealed unsafe class RenderDevice : IDisposable
{
    private const string ShaderResourcePrefix = "Waddamburo.Shaders.";

    private readonly SDL_GPUDevice* _device;
    private readonly SDL_Window* _window;
    private readonly Dictionary<uint, nint> _textures = [];
    private readonly HashSet<uint> _borrowedTextures = [];
    private readonly List<IGpuRenderPrepass> _prepasses = [];
    private SDL_GPUGraphicsPipeline* _normalQuadPipeline;
    private SDL_GPUGraphicsPipeline* _addQuadPipeline;
    private SDL_GPUGraphicsPipeline* _screenQuadPipeline;
    private SDL_GPUGraphicsPipeline* _pushMaskPipeline;
    private SDL_GPUGraphicsPipeline* _popMaskPipeline;
    private SDL_GPUTexture* _stencil;
    private uint _stencilWidth;
    private uint _stencilHeight;
    private readonly SDL_GPUTextureFormat _stencilFormat;
    private SDL_GPUSampler* _nearestSampler;
    private SDL_GPUSampler* _linearSampler;
    private SDL_GPUSampler* _nearestRepeatSampler;
    private SDL_GPUSampler* _linearRepeatSampler;
    private uint _nextTextureId = 1;
    private bool _disposed;

    public RenderDevice(SDL_GPUDevice* device, SDL_Window* window)
    {
        if (device is null)
            throw new ArgumentNullException(nameof(device));
        if (window is null)
            throw new ArgumentNullException(nameof(window));
        _device = device;
        _window = window;
        _stencilFormat = SDL_GPUTextureSupportsFormat(device, SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_D24_UNORM_S8_UINT,
            SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D, SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET)
            ? SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_D24_UNORM_S8_UINT
            : SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_D32_FLOAT_S8_UINT;

        try
        {
            _nearestSampler = createSampler(SDL_GPUFilter.SDL_GPU_FILTER_NEAREST);
            _linearSampler = createSampler(SDL_GPUFilter.SDL_GPU_FILTER_LINEAR);
            _nearestRepeatSampler = createSampler(SDL_GPUFilter.SDL_GPU_FILTER_NEAREST, repeat: true);
            _linearRepeatSampler = createSampler(SDL_GPUFilter.SDL_GPU_FILTER_LINEAR, repeat: true);
            _normalQuadPipeline = createQuadPipeline(RenderBlend.Normal);
            _addQuadPipeline = createQuadPipeline(RenderBlend.Add);
            _screenQuadPipeline = createQuadPipeline(RenderBlend.Screen);
            _pushMaskPipeline = createQuadPipeline(RenderBlend.Normal, RenderMaskOperation.Push);
            _popMaskPipeline = createQuadPipeline(RenderBlend.Normal, RenderMaskOperation.Pop);
        }
        catch
        {
            releaseResources();
            throw;
        }
    }

    public RenderTextureId UploadRgba8(uint width, uint height, ReadOnlySpan<byte> pixels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);

        var expectedLength = checked((ulong)width * height * 4);
        if (expectedLength > uint.MaxValue || (ulong)pixels.Length != expectedLength)
            throw new ArgumentException("RGBA8 data must contain exactly width * height * 4 bytes.", nameof(pixels));

        var textureInfo = new SDL_GPUTextureCreateInfo
        {
            type = SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D,
            format = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
            usage = SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER,
            width = width,
            height = height,
            layer_count_or_depth = 1,
            num_levels = 1,
            sample_count = SDL_GPUSampleCount.SDL_GPU_SAMPLECOUNT_1,
        };
        var texture = SDL_CreateGPUTexture(_device, &textureInfo);
        if (texture is null)
            throw sdlFailure("create an RGBA texture");

        try
        {
            uploadInto(texture, width, height, pixels);
            var id = new RenderTextureId(_nextTextureId++);
            _textures.Add(id.Value, (nint)texture);
            texture = null;
            return id;
        }
        finally
        {
            if (texture is not null)
                SDL_ReleaseGPUTexture(_device, texture);
        }
    }

    /// <summary>Replaces the pixels of an owned texture of the same size (streamed video frames).</summary>
    public void UpdateRgba8(RenderTextureId id, uint width, uint height, ReadOnlySpan<byte> pixels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_borrowedTextures.Contains(id.Value) || !_textures.TryGetValue(id.Value, out var texture))
            throw new ArgumentException($"Texture {id.Value} is not owned by this render device.", nameof(id));
        if ((ulong)pixels.Length != checked((ulong)width * height * 4))
            throw new ArgumentException("RGBA8 data must contain exactly width * height * 4 bytes.", nameof(pixels));
        uploadInto((SDL_GPUTexture*)texture, width, height, pixels);
    }

    private void uploadInto(SDL_GPUTexture* texture, uint width, uint height, ReadOnlySpan<byte> pixels)
    {
        SDL_GPUTransferBuffer* transferBuffer = null;
        try
        {
            var transferInfo = new SDL_GPUTransferBufferCreateInfo
            {
                usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
                size = (uint)pixels.Length,
            };
            transferBuffer = SDL_CreateGPUTransferBuffer(_device, &transferInfo);
            if (transferBuffer is null)
                throw sdlFailure("create a texture upload buffer");

            var mapped = SDL_MapGPUTransferBuffer(_device, transferBuffer, false);
            if (mapped == 0)
                throw sdlFailure("map a texture upload buffer");
            pixels.CopyTo(new Span<byte>((void*)mapped, pixels.Length));
            SDL_UnmapGPUTransferBuffer(_device, transferBuffer);

            var commandBuffer = SDL_AcquireGPUCommandBuffer(_device);
            if (commandBuffer is null)
                throw sdlFailure("acquire a texture upload command buffer");
            var copyPass = SDL_BeginGPUCopyPass(commandBuffer);
            if (copyPass is null)
            {
                SDL_CancelGPUCommandBuffer(commandBuffer);
                throw sdlFailure("begin a texture upload pass");
            }

            var source = new SDL_GPUTextureTransferInfo
            {
                transfer_buffer = transferBuffer,
                pixels_per_row = width,
                rows_per_layer = height,
            };
            var destination = new SDL_GPUTextureRegion
            {
                texture = texture,
                w = width,
                h = height,
                d = 1,
            };
            SDL_UploadToGPUTexture(copyPass, &source, &destination, false);
            SDL_EndGPUCopyPass(copyPass);
            if (!SDL_SubmitGPUCommandBuffer(commandBuffer))
                throw sdlFailure("submit a texture upload");
        }
        finally
        {
            if (transferBuffer is not null)
                SDL_ReleaseGPUTransferBuffer(_device, transferBuffer);
        }
    }

    public void ReleaseTexture(RenderTextureId id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_borrowedTextures.Contains(id.Value))
            throw new ArgumentException($"Texture {id.Value} is owned by a render prepass.", nameof(id));
        if (!_textures.Remove(id.Value, out var texture))
            throw new ArgumentException($"Texture {id.Value} is not owned by this render device.", nameof(id));
        SDL_ReleaseGPUTexture(_device, (SDL_GPUTexture*)texture);
    }

    internal RenderTextureId RegisterBorrowedTexture(nint texture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (texture == 0)
            throw new ArgumentNullException(nameof(texture));
        var id = new RenderTextureId(_nextTextureId++);
        _textures.Add(id.Value, texture);
        _borrowedTextures.Add(id.Value);
        return id;
    }

    /// <summary>Points a borrowed id at a new texture, so frames built earlier stay valid.</summary>
    internal void ReplaceBorrowedTexture(RenderTextureId id, nint texture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (texture == 0)
            throw new ArgumentNullException(nameof(texture));
        if (!_borrowedTextures.Contains(id.Value))
            throw new ArgumentException($"Texture {id.Value} is not borrowed by this render device.", nameof(id));
        _textures[id.Value] = texture;
    }

    internal void UnregisterBorrowedTexture(RenderTextureId id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_borrowedTextures.Remove(id.Value) || !_textures.Remove(id.Value))
            throw new ArgumentException($"Texture {id.Value} is not borrowed by this render device.", nameof(id));
    }

    internal void AddPrepass(IGpuRenderPrepass prepass)
    {
        ArgumentNullException.ThrowIfNull(prepass);
        _prepasses.Add(prepass);
    }

    internal void RemovePrepass(IGpuRenderPrepass prepass)
    {
        if (!_prepasses.Remove(prepass))
            throw new ArgumentException("Render prepass is not registered.", nameof(prepass));
    }

    public RenderCapture? Present(RenderFrame frame, bool capture = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        validateFrame(frame);

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

        SDL_GPUTransferBuffer* captureBuffer = null;
        var swapchainFormat = SDL_GetGPUSwapchainTextureFormat(_device, _window);
        if (swapchainTexture is null && capture)
        {
            SDL_CancelGPUCommandBuffer(commandBuffer);
            throw new InvalidOperationException("The swapchain has no image available for screenshot capture.");
        }
        if (swapchainTexture is not null)
        {
            foreach (var prepass in _prepasses)
                prepass.Record(commandBuffer, width, height);
            var clear = frame.ClearColor;
            var target = new SDL_GPUColorTargetInfo
            {
                texture = swapchainTexture,
                clear_color = new SDL_FColor { r = clear.Red, g = clear.Green, b = clear.Blue, a = clear.Alpha },
                load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_CLEAR,
                store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_STORE,
            };
            ensureStencil(width, height);
            var stencilTarget = new SDL_GPUDepthStencilTargetInfo
            {
                texture = _stencil,
                load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_DONT_CARE,
                store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_DONT_CARE,
                stencil_load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_CLEAR,
                stencil_store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_DONT_CARE,
                clear_stencil = 0,
                cycle = true,
            };
            var renderPass = SDL_BeginGPURenderPass(commandBuffer, &target, 1, &stencilTarget);
            if (renderPass is null)
            {
                SDL_CancelGPUCommandBuffer(commandBuffer);
                throw sdlFailure("begin the GPU render pass");
            }

            var resolvedViewport = frame.ResolveViewport(width, height);
            var viewport = new SDL_GPUViewport
            {
                x = resolvedViewport.X,
                y = resolvedViewport.Y,
                w = resolvedViewport.Width,
                h = resolvedViewport.Height,
                min_depth = 0,
                max_depth = 1,
            };
            var scissor = new SDL_Rect
            {
                x = resolvedViewport.X,
                y = resolvedViewport.Y,
                w = resolvedViewport.Width,
                h = resolvedViewport.Height,
            };
            SDL_SetGPUViewport(renderPass, &viewport);
            SDL_SetGPUScissor(renderPass, &scissor);
            SDL_GPUGraphicsPipeline* boundPipeline = null;
            foreach (var quad in frame.Quads)
            {
                var pipeline = quad.MaskOperation switch
                {
                    RenderMaskOperation.Push => _pushMaskPipeline,
                    RenderMaskOperation.Pop => _popMaskPipeline,
                    _ => quad.Blend switch
                    {
                        RenderBlend.Add => _addQuadPipeline,
                        RenderBlend.Screen => _screenQuadPipeline,
                        _ => _normalQuadPipeline,
                    },
                };
                if (pipeline != boundPipeline)
                {
                    SDL_BindGPUGraphicsPipeline(renderPass, pipeline);
                    boundPipeline = pipeline;
                }
                SDL_SetGPUStencilReference(renderPass, quad.MaskDepth);
                drawQuad(commandBuffer, renderPass, quad);
            }
            SDL_EndGPURenderPass(renderPass);

            if (capture)
            {
                var captureSize = checked((ulong)width * height * 4);
                if (captureSize > uint.MaxValue)
                {
                    SDL_CancelGPUCommandBuffer(commandBuffer);
                    throw new InvalidOperationException("The swapchain image is too large to capture.");
                }
                var transferInfo = new SDL_GPUTransferBufferCreateInfo
                {
                    usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_DOWNLOAD,
                    size = (uint)captureSize,
                };
                captureBuffer = SDL_CreateGPUTransferBuffer(_device, &transferInfo);
                if (captureBuffer is null)
                {
                    SDL_CancelGPUCommandBuffer(commandBuffer);
                    throw sdlFailure("create a screenshot download buffer");
                }

                var copyPass = SDL_BeginGPUCopyPass(commandBuffer);
                if (copyPass is null)
                {
                    SDL_CancelGPUCommandBuffer(commandBuffer);
                    SDL_ReleaseGPUTransferBuffer(_device, captureBuffer);
                    throw sdlFailure("begin a screenshot download pass");
                }
                var source = new SDL_GPUTextureRegion
                {
                    texture = swapchainTexture,
                    w = width,
                    h = height,
                    d = 1,
                };
                var destination = new SDL_GPUTextureTransferInfo
                {
                    transfer_buffer = captureBuffer,
                    pixels_per_row = width,
                    rows_per_layer = height,
                };
                SDL_DownloadFromGPUTexture(copyPass, &source, &destination);
                SDL_EndGPUCopyPass(copyPass);
            }
        }

        if (captureBuffer is null)
        {
            if (!SDL_SubmitGPUCommandBuffer(commandBuffer))
                throw sdlFailure("submit the GPU command buffer");
            return null;
        }

        SDL_GPUFence* fence = SDL_SubmitGPUCommandBufferAndAcquireFence(commandBuffer);
        if (fence is null)
        {
            SDL_ReleaseGPUTransferBuffer(_device, captureBuffer);
            throw sdlFailure("submit the screenshot command buffer");
        }
        try
        {
            if (!SDL_WaitForGPUFences(_device, true, &fence, 1))
                throw sdlFailure("wait for the screenshot download");
            var mapped = SDL_MapGPUTransferBuffer(_device, captureBuffer, false);
            if (mapped == 0)
                throw sdlFailure("map the screenshot download buffer");
            try
            {
                var pixels = new byte[checked((int)((ulong)width * height * 4))];
                new ReadOnlySpan<byte>((void*)mapped, pixels.Length).CopyTo(pixels);
                normalizeCaptureChannels(pixels, swapchainFormat);
                return new RenderCapture(width, height, [.. pixels]);
            }
            finally
            {
                SDL_UnmapGPUTransferBuffer(_device, captureBuffer);
            }
        }
        finally
        {
            SDL_ReleaseGPUFence(_device, fence);
            SDL_ReleaseGPUTransferBuffer(_device, captureBuffer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        SDL_WaitForGPUIdle(_device);
        releaseResources();
        _disposed = true;
    }

    private void drawQuad(SDL_GPUCommandBuffer* commandBuffer, SDL_GPURenderPass* renderPass, RenderQuad quad)
    {
        var textureAddress = _textures[quad.Texture.Value];

        var multiply = quad.MultiplyColor;
        var add = quad.AddColor;
        var uniforms = new QuadUniforms
        {
            TopLeft = convert(quad.TopLeft),
            TopRight = convert(quad.TopRight),
            BottomRight = convert(quad.BottomRight),
            BottomLeft = convert(quad.BottomLeft),
            MultiplyColor = new Float4(multiply.Red, multiply.Green, multiply.Blue, multiply.Alpha),
            AddColor = new Float4(add.Red, add.Green, add.Blue, add.Alpha),
        };
        SDL_PushGPUVertexUniformData(commandBuffer, 0, (nint)(&uniforms), (uint)sizeof(QuadUniforms));

        var binding = new SDL_GPUTextureSamplerBinding
        {
            texture = (SDL_GPUTexture*)textureAddress,
            // Authored shapes tile a texture by giving UVs beyond 0..1; everything else clamps so
            // atlas edges do not bleed.
            sampler = tiles(quad)
                ? quad.Sampling is RenderSampling.Nearest ? _nearestRepeatSampler : _linearRepeatSampler
                : quad.Sampling is RenderSampling.Nearest ? _nearestSampler : _linearSampler,
        };
        SDL_BindGPUFragmentSamplers(renderPass, 0, &binding, 1);
        SDL_DrawGPUPrimitives(renderPass, 6, 1, 0, 0);
    }

    private void ensureStencil(uint width, uint height)
    {
        if (_stencil is not null && _stencilWidth == width && _stencilHeight == height) return;
        var info = new SDL_GPUTextureCreateInfo
        {
            type = SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D,
            format = _stencilFormat,
            usage = SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET,
            width = width, height = height, layer_count_or_depth = 1, num_levels = 1,
            sample_count = SDL_GPUSampleCount.SDL_GPU_SAMPLECOUNT_1,
        };
        var texture = SDL_CreateGPUTexture(_device, &info);
        if (texture is null) throw sdlFailure("create the stencil target");
        if (_stencil is not null) SDL_ReleaseGPUTexture(_device, _stencil);
        _stencil = texture;
        _stencilWidth = width;
        _stencilHeight = height;
    }

    private SDL_GPUGraphicsPipeline* createQuadPipeline(RenderBlend blend,
        RenderMaskOperation maskOperation = RenderMaskOperation.Draw)
    {
        var formats = SDL_GetGPUShaderFormats(_device);
        var format = (formats & SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV) != 0
            ? SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV
            : (formats & SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL) != 0
                ? SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL
                : throw new PlatformNotSupportedException($"SDL_GPU selected unsupported shader formats: {formats}.");
        var extension = format is SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV ? "spv" : "dxil";

        var vertexShader = createShader($"quad.vert.{extension}", format, SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_VERTEX, 0, 1);
        SDL_GPUShader* fragmentShader = null;
        try
        {
            var fragmentName = maskOperation == RenderMaskOperation.Draw ? "quad" : "mask";
            fragmentShader = createShader($"{fragmentName}.frag.{extension}", format, SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_FRAGMENT, 1, 0);
            var blendState = new SDL_GPUColorTargetBlendState
            {
                src_color_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE,
                // Premultiplied source: screen is s + d(1 - s).
                dst_color_blendfactor = blend switch
                {
                    RenderBlend.Add => SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE,
                    RenderBlend.Screen => SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_COLOR,
                    _ => SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA,
                },
                color_blend_op = SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD,
                src_alpha_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE,
                dst_alpha_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA,
                alpha_blend_op = SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD,
                enable_blend = maskOperation == RenderMaskOperation.Draw,
                enable_color_write_mask = maskOperation != RenderMaskOperation.Draw,
                color_write_mask = 0,
            };
            var targetDescription = new SDL_GPUColorTargetDescription
            {
                format = SDL_GetGPUSwapchainTextureFormat(_device, _window),
                blend_state = blendState,
            };
            var stencilState = new SDL_GPUStencilOpState
            {
                compare_op = SDL_GPUCompareOp.SDL_GPU_COMPAREOP_EQUAL,
                fail_op = SDL_GPUStencilOp.SDL_GPU_STENCILOP_KEEP,
                depth_fail_op = SDL_GPUStencilOp.SDL_GPU_STENCILOP_KEEP,
                pass_op = maskOperation switch
                {
                    RenderMaskOperation.Push => SDL_GPUStencilOp.SDL_GPU_STENCILOP_INCREMENT_AND_CLAMP,
                    RenderMaskOperation.Pop => SDL_GPUStencilOp.SDL_GPU_STENCILOP_DECREMENT_AND_CLAMP,
                    _ => SDL_GPUStencilOp.SDL_GPU_STENCILOP_KEEP,
                },
            };
            var pipelineInfo = new SDL_GPUGraphicsPipelineCreateInfo
            {
                vertex_shader = vertexShader,
                fragment_shader = fragmentShader,
                primitive_type = SDL_GPUPrimitiveType.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST,
                rasterizer_state = new SDL_GPURasterizerState
                {
                    fill_mode = SDL_GPUFillMode.SDL_GPU_FILLMODE_FILL,
                    cull_mode = SDL_GPUCullMode.SDL_GPU_CULLMODE_NONE,
                    front_face = SDL_GPUFrontFace.SDL_GPU_FRONTFACE_COUNTER_CLOCKWISE,
                    enable_depth_clip = true,
                },
                depth_stencil_state = new SDL_GPUDepthStencilState
                {
                    enable_stencil_test = true,
                    compare_mask = byte.MaxValue,
                    write_mask = maskOperation == RenderMaskOperation.Draw ? (byte)0 : byte.MaxValue,
                    front_stencil_state = stencilState,
                    back_stencil_state = stencilState,
                },
                multisample_state = new SDL_GPUMultisampleState
                {
                    sample_count = SDL_GPUSampleCount.SDL_GPU_SAMPLECOUNT_1,
                },
                target_info = new SDL_GPUGraphicsPipelineTargetInfo
                {
                    color_target_descriptions = &targetDescription,
                    num_color_targets = 1,
                    has_depth_stencil_target = true,
                    depth_stencil_format = _stencilFormat,
                },
            };
            var pipeline = SDL_CreateGPUGraphicsPipeline(_device, &pipelineInfo);
            return pipeline is null ? throw sdlFailure("create the textured quad pipeline") : pipeline;
        }
        finally
        {
            SDL_ReleaseGPUShader(_device, vertexShader);
            if (fragmentShader is not null)
                SDL_ReleaseGPUShader(_device, fragmentShader);
        }
    }

    private SDL_GPUShader* createShader(
        string resourceName,
        SDL_GPUShaderFormat format,
        SDL_GPUShaderStage stage,
        uint samplerCount,
        uint uniformBufferCount)
    {
        var assembly = typeof(RenderDevice).Assembly;
        using var stream = assembly.GetManifestResourceStream(ShaderResourcePrefix + resourceName)
            ?? throw new InvalidOperationException(
                $"Packaged shader '{resourceName}' is missing. Run the platform shader build script before building.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var code = memory.ToArray();
        ReadOnlySpan<byte> entryPoint = "main\0"u8;
        fixed (byte* codePointer = code)
        fixed (byte* entryPointPointer = entryPoint)
        {
            var shaderInfo = new SDL_GPUShaderCreateInfo
            {
                code_size = (nuint)code.Length,
                code = codePointer,
                entrypoint = entryPointPointer,
                format = format,
                stage = stage,
                num_samplers = samplerCount,
                num_uniform_buffers = uniformBufferCount,
            };
            var shader = SDL_CreateGPUShader(_device, &shaderInfo);
            return shader is null ? throw sdlFailure($"create packaged shader '{resourceName}'") : shader;
        }
    }

    private static bool tiles(RenderQuad quad)
    {
        static bool outside(RenderVertex vertex) =>
            vertex.U is < -0.001f or > 1.001f || vertex.V is < -0.001f or > 1.001f;
        return outside(quad.TopLeft) || outside(quad.TopRight) || outside(quad.BottomRight) || outside(quad.BottomLeft);
    }

    private SDL_GPUSampler* createSampler(SDL_GPUFilter filter, bool repeat = false)
    {
        var address = repeat
            ? SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_REPEAT
            : SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE;
        var samplerInfo = new SDL_GPUSamplerCreateInfo
        {
            min_filter = filter,
            mag_filter = filter,
            mipmap_mode = SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_NEAREST,
            address_mode_u = address,
            address_mode_v = address,
            address_mode_w = address,
        };
        var sampler = SDL_CreateGPUSampler(_device, &samplerInfo);
        return sampler is null ? throw sdlFailure($"create a {filter} sampler") : sampler;
    }

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

    private static Float4 convert(RenderVertex vertex) => new(vertex.X, vertex.Y, vertex.U, vertex.V);

    private static void normalizeCaptureChannels(Span<byte> pixels, SDL_GPUTextureFormat format)
    {
        if (format is SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM
            or SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM_SRGB)
        {
            return;
        }
        if (format is not (SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM
            or SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM_SRGB))
        {
            throw new PlatformNotSupportedException($"Screenshot capture does not support swapchain format {format}.");
        }
        for (var offset = 0; offset < pixels.Length; offset += 4)
            (pixels[offset], pixels[offset + 2]) = (pixels[offset + 2], pixels[offset]);
    }

    private static void validateVertex(RenderVertex vertex, string name)
    {
        if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y)
            || !float.IsFinite(vertex.U) || !float.IsFinite(vertex.V))
        {
            throw new ArgumentException($"{name} must contain finite values.");
        }
    }

    private static void validateColor(RenderColor color, string name)
    {
        if (!float.IsFinite(color.Red) || !float.IsFinite(color.Green)
            || !float.IsFinite(color.Blue) || !float.IsFinite(color.Alpha))
        {
            throw new ArgumentException($"{name} must contain finite values.");
        }
    }

    private void releaseResources()
    {
        foreach (var (id, texture) in _textures)
            if (!_borrowedTextures.Contains(id))
                SDL_ReleaseGPUTexture(_device, (SDL_GPUTexture*)texture);
        _textures.Clear();
        _borrowedTextures.Clear();
        _prepasses.Clear();
        if (_stencil is not null)
        {
            SDL_ReleaseGPUTexture(_device, _stencil);
            _stencil = null;
        }
        if (_pushMaskPipeline is not null)
        {
            SDL_ReleaseGPUGraphicsPipeline(_device, _pushMaskPipeline);
            _pushMaskPipeline = null;
        }
        if (_popMaskPipeline is not null)
        {
            SDL_ReleaseGPUGraphicsPipeline(_device, _popMaskPipeline);
            _popMaskPipeline = null;
        }
        if (_addQuadPipeline is not null)
        {
            SDL_ReleaseGPUGraphicsPipeline(_device, _addQuadPipeline);
            _addQuadPipeline = null;
        }
        if (_screenQuadPipeline is not null)
        {
            SDL_ReleaseGPUGraphicsPipeline(_device, _screenQuadPipeline);
            _screenQuadPipeline = null;
        }
        if (_normalQuadPipeline is not null)
        {
            SDL_ReleaseGPUGraphicsPipeline(_device, _normalQuadPipeline);
            _normalQuadPipeline = null;
        }
        if (_linearSampler is not null)
        {
            SDL_ReleaseGPUSampler(_device, _linearSampler);
            _linearSampler = null;
        }
        foreach (var repeat in new[] { (nint)_linearRepeatSampler, (nint)_nearestRepeatSampler })
            if (repeat != 0)
                SDL_ReleaseGPUSampler(_device, (SDL_GPUSampler*)repeat);
        _linearRepeatSampler = null;
        _nearestRepeatSampler = null;
        if (_nearestSampler is not null)
        {
            SDL_ReleaseGPUSampler(_device, _nearestSampler);
            _nearestSampler = null;
        }
    }

    private static InvalidOperationException sdlFailure(string operation) =>
        new($"Failed to {operation}: {SDL_GetError()}");

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Float4(float X, float Y, float Z, float W);

    [StructLayout(LayoutKind.Sequential)]
    private struct QuadUniforms
    {
        public Float4 TopLeft;
        public Float4 TopRight;
        public Float4 BottomRight;
        public Float4 BottomLeft;
        public Float4 MultiplyColor;
        public Float4 AddColor;
    }
}

internal unsafe interface IGpuRenderPrepass
{
    /// <summary>Records off-screen work; width/height are the swapchain size in pixels.</summary>
    void Record(SDL_GPUCommandBuffer* commandBuffer, uint width, uint height);
}
