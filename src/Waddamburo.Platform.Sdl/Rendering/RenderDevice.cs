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
    private SDL_GPUGraphicsPipeline* _quadPipeline;
    private SDL_GPUSampler* _nearestSampler;
    private SDL_GPUSampler* _linearSampler;
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

        try
        {
            _nearestSampler = createSampler(SDL_GPUFilter.SDL_GPU_FILTER_NEAREST);
            _linearSampler = createSampler(SDL_GPUFilter.SDL_GPU_FILTER_LINEAR);
            _quadPipeline = createQuadPipeline();
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

        SDL_GPUTransferBuffer* transferBuffer = null;
        try
        {
            var transferInfo = new SDL_GPUTransferBufferCreateInfo
            {
                usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
                size = (uint)expectedLength,
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

            var id = new RenderTextureId(_nextTextureId++);
            _textures.Add(id.Value, (nint)texture);
            texture = null;
            return id;
        }
        finally
        {
            if (transferBuffer is not null)
                SDL_ReleaseGPUTransferBuffer(_device, transferBuffer);
            if (texture is not null)
                SDL_ReleaseGPUTexture(_device, texture);
        }
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
            var clear = frame.ClearColor;
            var target = new SDL_GPUColorTargetInfo
            {
                texture = swapchainTexture,
                clear_color = new SDL_FColor { r = clear.Red, g = clear.Green, b = clear.Blue, a = clear.Alpha },
                load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_CLEAR,
                store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_STORE,
            };
            var renderPass = SDL_BeginGPURenderPass(commandBuffer, &target, 1, null);
            if (renderPass is null)
            {
                SDL_CancelGPUCommandBuffer(commandBuffer);
                throw sdlFailure("begin the GPU render pass");
            }

            SDL_BindGPUGraphicsPipeline(renderPass, _quadPipeline);
            foreach (var quad in frame.Quads)
                drawQuad(commandBuffer, renderPass, quad);
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
            sampler = quad.Sampling is RenderSampling.Nearest ? _nearestSampler : _linearSampler,
        };
        SDL_BindGPUFragmentSamplers(renderPass, 0, &binding, 1);
        SDL_DrawGPUPrimitives(renderPass, 6, 1, 0, 0);
    }

    private SDL_GPUGraphicsPipeline* createQuadPipeline()
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
            fragmentShader = createShader($"quad.frag.{extension}", format, SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_FRAGMENT, 1, 0);
            var blendState = new SDL_GPUColorTargetBlendState
            {
                src_color_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE,
                dst_color_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA,
                color_blend_op = SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD,
                src_alpha_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE,
                dst_alpha_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA,
                alpha_blend_op = SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD,
                enable_blend = true,
            };
            var targetDescription = new SDL_GPUColorTargetDescription
            {
                format = SDL_GetGPUSwapchainTextureFormat(_device, _window),
                blend_state = blendState,
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
                multisample_state = new SDL_GPUMultisampleState
                {
                    sample_count = SDL_GPUSampleCount.SDL_GPU_SAMPLECOUNT_1,
                },
                target_info = new SDL_GPUGraphicsPipelineTargetInfo
                {
                    color_target_descriptions = &targetDescription,
                    num_color_targets = 1,
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

    private SDL_GPUSampler* createSampler(SDL_GPUFilter filter)
    {
        var samplerInfo = new SDL_GPUSamplerCreateInfo
        {
            min_filter = filter,
            mag_filter = filter,
            mipmap_mode = SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_NEAREST,
            address_mode_u = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
            address_mode_v = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
            address_mode_w = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
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
        foreach (var texture in _textures.Values)
            SDL_ReleaseGPUTexture(_device, (SDL_GPUTexture*)texture);
        _textures.Clear();
        if (_quadPipeline is not null)
        {
            SDL_ReleaseGPUGraphicsPipeline(_device, _quadPipeline);
            _quadPipeline = null;
        }
        if (_linearSampler is not null)
        {
            SDL_ReleaseGPUSampler(_device, _linearSampler);
            _linearSampler = null;
        }
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
