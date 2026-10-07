using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SDL;
using static SDL.SDL3;

namespace Bgfx
{
#pragma warning disable CS8981 // the generated bindings' class name
    public static partial class bgfx
    {
        // libbgfx.so / bgfx.dll / libbgfx.dylib next to the executable.
        private const string DllName = "bgfx";
    }
#pragma warning restore CS8981
}

namespace Waddamburo.Platform.Sdl.Rendering
{
    using Bgfx;

    /// <summary>bgfx plumbing shared by the compositor and the Don renderer.</summary>
    internal static unsafe class BgfxSupport
    {
        private const string ShaderResourcePrefix = "Waddamburo.Shaders.";

        public static readonly bgfx.TextureHandle InvalidTexture = new() { idx = ushort.MaxValue };
        public static readonly bgfx.FrameBufferHandle BackBuffer = new() { idx = ushort.MaxValue };

        /// <summary>BGFX_STATE_BLEND_FUNC_SEPARATE.</summary>
        public static ulong BlendSeparate(bgfx.StateFlags sourceRgb, bgfx.StateFlags destinationRgb,
            bgfx.StateFlags sourceAlpha, bgfx.StateFlags destinationAlpha) =>
            (ulong)sourceRgb | ((ulong)destinationRgb << 4) | (((ulong)sourceAlpha | ((ulong)destinationAlpha << 4)) << 8);

        /// <summary>0xRRGGBBAA as bgfx clears take it.</summary>
        public static uint PackRgba(RenderColor color) =>
            ((uint)(Math.Clamp(color.Red, 0, 1) * 255 + 0.5f) << 24)
            | ((uint)(Math.Clamp(color.Green, 0, 1) * 255 + 0.5f) << 16)
            | ((uint)(Math.Clamp(color.Blue, 0, 1) * 255 + 0.5f) << 8)
            | (uint)(Math.Clamp(color.Alpha, 0, 1) * 255 + 0.5f);

        public static bgfx.Memory* Copy(ReadOnlySpan<byte> bytes)
        {
            fixed (byte* data = bytes)
                return bgfx.copy(data, checked((uint)bytes.Length));
        }

        public static bgfx.TextureHandle CreateRgba8(uint width, uint height, ReadOnlySpan<byte> pixels, ulong flags = 0)
        {
            if (width > ushort.MaxValue || height > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(width), "Texture is too large.");
            // Created without data so video frames can update it in place.
            var texture = bgfx.create_texture_2d((ushort)width, (ushort)height, false, 1, bgfx.TextureFormat.RGBA8, flags, null, 0);
            if (!texture.Valid)
                throw new InvalidOperationException($"bgfx could not create a {width}x{height} texture.");
            if (!pixels.IsEmpty)
                bgfx.update_texture_2d(texture, 0, 0, 0, 0, (ushort)width, (ushort)height, Copy(pixels), ushort.MaxValue);
            return texture;
        }

        /// <summary>A BC7 texture from its blocks (sizes multiples of 4, one byte per pixel).</summary>
        public static bgfx.TextureHandle CreateBc7(uint width, uint height, ReadOnlySpan<byte> blocks)
        {
            if (width > ushort.MaxValue || height > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(width), "Texture is too large.");
            var texture = bgfx.create_texture_2d((ushort)width, (ushort)height, false, 1, bgfx.TextureFormat.BC7, 0, Copy(blocks), 0);
            if (!texture.Valid)
                throw new InvalidOperationException($"bgfx could not create a {width}x{height} BC7 texture.");
            return texture;
        }

        /// <summary>Whether this backend draws BC7 textures (natively, or decoded by bgfx).</summary>
        public static bool SupportsBc7() =>
            (bgfx.get_caps()->formats[(int)bgfx.TextureFormat.BC7]
                & (uint)(bgfx.CapsFormatFlags.Texture2d | bgfx.CapsFormatFlags.Texture2dEmulated)) != 0;

        /// <summary>First depth format this backend renders to (AMD Vulkan has no D24, GLES may lack D32F).</summary>
        public static bgfx.TextureFormat DepthFormat()
        {
            var caps = bgfx.get_caps();
            foreach (var format in new[] { bgfx.TextureFormat.D24, bgfx.TextureFormat.D32F, bgfx.TextureFormat.D16 })
                if ((caps->formats[(int)format] & (uint)bgfx.CapsFormatFlags.TextureFramebuffer) != 0)
                    return format;
            throw new PlatformNotSupportedException("The renderer has no depth format to render to.");
        }

        public static bgfx.ProgramHandle LoadProgram(string vertex, string fragment)
        {
            var program = bgfx.create_program(LoadShader(vertex), LoadShader(fragment), true);
            return program.Valid ? program : throw new InvalidOperationException($"bgfx could not link {vertex}/{fragment}.");
        }

        private static bgfx.ShaderHandle LoadShader(string name)
        {
            var token = bgfx.get_renderer_type() switch
            {
                bgfx.RendererType.Vulkan => "spirv",
                bgfx.RendererType.OpenGL => "glsl",
                bgfx.RendererType.OpenGLES => "essl",
                bgfx.RendererType.Metal => "metal",
                bgfx.RendererType.Direct3D11 => "dx11",
                var other => throw new PlatformNotSupportedException($"No packaged shaders for bgfx renderer {other}."),
            };
            var resource = $"{ShaderResourcePrefix}{name}.{token}.bin";
            using var stream = typeof(BgfxSupport).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Packaged shader '{resource}' is missing. Run eng/build-shaders.sh.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var shader = bgfx.create_shader(Copy(memory.GetBuffer().AsSpan(0, (int)memory.Length)));
            return shader.Valid ? shader : throw new InvalidOperationException($"bgfx rejected shader '{resource}'.");
        }

        /// <summary>BGFX_RESET_* for init and every resize: vsync unless WADDAMBURO_PRESENT_MODE uncaps it.</summary>
        public static uint ResetFlags { get; private set; }

        // One frame in the driver's queue: DXGI's default of three let vsync add ~50 ms of display lag
        // (Present returns at once until the queue fills, so our own pacing never waits).
        public const byte MaxFrameLatency = 1;

        /// <summary>Turns vsync on or off (the next present resets the swap chain); the diagnostic env var still wins.</summary>
        public static void SetVsync(bool vsync) =>
            ResetFlags = vsync && Environment.GetEnvironmentVariable("WADDAMBURO_PRESENT_MODE") is not ("immediate" or "mailbox")
                ? (uint)bgfx.ResetFlags.Vsync : 0;

        private static Thread? s_renderThread;
        private static readonly Lock s_pendingLock = new();
        private static int s_pendingFrames;

        /// <summary>
        /// Initializes bgfx on the SDL window, rendering on a dedicated thread so the calling (API)
        /// thread can keep polling input while a frame presents. WADDAMBURO_RENDERER (vulkan, opengl,
        /// gles, d3d11, metal) forces a backend for diagnostics; otherwise bgfx picks the default.
        /// </summary>
        // ponytail: macOS needs rendering on the main thread; there the roles must swap.
        public static void Initialize(SDL_Window* window, uint width, uint height, bool debug)
        {
            using var started = new ManualResetEventSlim();
            s_renderThread = new Thread(() => renderLoop(started)) { IsBackground = true, Name = "bgfx render" };
            s_renderThread.Start();
            started.Wait();

            var init = new bgfx.Init();
            bgfx.init_ctor(&init);
            init.type = Environment.GetEnvironmentVariable("WADDAMBURO_RENDERER")?.ToLowerInvariant() switch
            {
                "vulkan" => bgfx.RendererType.Vulkan,
                "opengl" or "gl" => bgfx.RendererType.OpenGL,
                "gles" or "opengles" => bgfx.RendererType.OpenGLES,
                "d3d11" => bgfx.RendererType.Direct3D11,
                "metal" => bgfx.RendererType.Metal,
                _ => bgfx.RendererType.Count,
            };
            // Diagnostic: WADDAMBURO_PRESENT_MODE=immediate or mailbox renders uncapped (no vsync).
            ResetFlags = Environment.GetEnvironmentVariable("WADDAMBURO_PRESENT_MODE") is "immediate" or "mailbox"
                ? 0 : (uint)bgfx.ResetFlags.Vsync;
            init.debug = (byte)(debug ? 1 : 0);
            init.callback = (IntPtr)Callbacks.Interface;
            init.swapChain.width = width;
            init.swapChain.height = height;
            init.swapChain.formatDepthStencil = bgfx.TextureFormat.D24S8;
            init.swapChain.maxFrameLatency = MaxFrameLatency;
            init.reset = ResetFlags;
            fillWindow(window, &init);
            if (!bgfx.init(&init))
            {
                bgfx.shutdown();
                s_renderThread.Join();
                throw new InvalidOperationException("bgfx could not initialize a renderer.");
            }
            // Diagnostic: the hitch trace reports GPU time per view, which needs bgfx's profiler.
            if (Environment.GetEnvironmentVariable("WADDAMBURO_HITCH_TRACE") == "1")
                bgfx.set_debug((uint)bgfx.DebugFlags.Profiler, default, 0);
        }

        private static void renderLoop(ManualResetEventSlim started)
        {
            // The first call before init makes this thread bgfx's render thread.
            bgfx.render_frame(0);
            started.Set();
            while (true)
            {
                var result = bgfx.render_frame(-1);
                if (result == bgfx.RenderFrame.Exiting)
                    return;
                if (result == bgfx.RenderFrame.NoContext)
                {
                    Thread.Sleep(1);
                    continue;
                }
                if (result == bgfx.RenderFrame.Render)
                    lock (s_pendingLock)
                        if (s_pendingFrames > 0)
                            s_pendingFrames--;
            }
        }

        /// <summary>Hands the frame to the render thread; waits only while the previous one still renders.</summary>
        public static void Frame()
        {
            lock (s_pendingLock)
                s_pendingFrames++;
            _ = bgfx.frame(0);
        }

        /// <summary>
        /// Whether the next frame should start. With vsync: only once the render thread has presented,
        /// so input keeps being polled instead of blocking in <see cref="Frame"/> for a refresh.
        /// Uncapped: one frame may still be rendering, so building the next one overlaps it.
        /// </summary>
        public static bool RenderThreadReady
        {
            get
            {
                lock (s_pendingLock)
                    return s_pendingFrames <= ((ResetFlags & (uint)bgfx.ResetFlags.Vsync) != 0 ? 0 : 1);
            }
        }

        public static void Shutdown()
        {
            bgfx.shutdown();
            s_renderThread?.Join();
            s_renderThread = null;
        }

        private static void fillWindow(SDL_Window* window, bgfx.Init* init)
        {
            var properties = SDL_GetWindowProperties(window);
            if (OperatingSystem.IsWindows())
                init->swapChain.nwh = (void*)SDL_GetPointerProperty(properties, SDL_PROP_WINDOW_WIN32_HWND_POINTER, IntPtr.Zero);
            else if (OperatingSystem.IsMacOS())
                init->swapChain.nwh = (void*)SDL_GetPointerProperty(properties, SDL_PROP_WINDOW_COCOA_WINDOW_POINTER, IntPtr.Zero);
            else if (SDL_GetCurrentVideoDriver() == "wayland")
            {
                init->swapChain.ndt = (void*)SDL_GetPointerProperty(properties, SDL_PROP_WINDOW_WAYLAND_DISPLAY_POINTER, IntPtr.Zero);
                init->swapChain.nwh = (void*)SDL_GetPointerProperty(properties, SDL_PROP_WINDOW_WAYLAND_SURFACE_POINTER, IntPtr.Zero);
                init->platformData.type = bgfx.NativeWindowHandleType.Wayland;
            }
            else
            {
                init->swapChain.ndt = (void*)SDL_GetPointerProperty(properties, SDL_PROP_WINDOW_X11_DISPLAY_POINTER, IntPtr.Zero);
                init->swapChain.nwh = (void*)(nint)SDL_GetNumberProperty(properties, SDL_PROP_WINDOW_X11_WINDOW_NUMBER, 0);
            }
            if (init->swapChain.nwh is null)
                throw new InvalidOperationException($"No native window handle for bgfx: {SDL_GetError()}");
        }

        public static string RendererName => Marshal.PtrToStringAnsi(bgfx.get_renderer_name(bgfx.get_renderer_type())) ?? "unknown";

        /// <summary>bgfx's callback interface: fatal errors and screenshot delivery.</summary>
        internal static class Callbacks
        {
            public static readonly nint* Interface = create();

            private static RenderCapture? s_capture;

            /// <summary>The latest screenshot, tightly packed top-down RGBA8 (written on the render thread).</summary>
            public static RenderCapture? Capture
            {
                get => Volatile.Read(ref s_capture);
                set => Volatile.Write(ref s_capture, value);
            }

            private static nint* create()
            {
                var vtable = (nint*)NativeMemory.AllocZeroed(12, (nuint)sizeof(nint));
                vtable[0] = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, ushort, int, byte*, void>)&fatal;
                vtable[1] = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, ushort, byte*, nint, void>)&trace;
                vtable[2] = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, uint, byte*, ushort, void>)&profilerBegin;
                vtable[3] = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, uint, byte*, ushort, void>)&profilerBegin;
                vtable[4] = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&profilerEnd;
                vtable[5] = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, uint>)&cacheReadSize;
                vtable[6] = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, void*, uint, byte>)&cacheRead;
                vtable[7] = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, void*, uint, void>)&cacheWrite;
                vtable[8] = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, uint, uint, uint, bgfx.TextureFormat, byte*, uint, byte, void>)&screenShot;
                vtable[9] = (nint)(delegate* unmanaged[Cdecl]<nint, uint, uint, uint, bgfx.TextureFormat, byte, void>)&captureBegin;
                vtable[10] = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&profilerEnd;
                vtable[11] = (nint)(delegate* unmanaged[Cdecl]<nint, void*, uint, void>)&cacheWrite2;
                var instance = (nint*)NativeMemory.AllocZeroed(1, (nuint)sizeof(nint));
                instance[0] = (nint)vtable;
                return instance;
            }

            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
            private static void fatal(nint self, byte* file, ushort line, int code, byte* message)
            {
                var text = Marshal.PtrToStringUTF8((nint)message);
                Console.Error.WriteLine($"bgfx fatal {code} at {Marshal.PtrToStringUTF8((nint)file)}:{line}: {text}");
                // Code 0 is a debug check; the others leave bgfx unusable.
                if (code != 0)
                    Environment.FailFast($"bgfx fatal error {code}: {text}");
            }

            // ponytail: bgfx's printf-style trace is dropped (va_list is not portable from C#).
            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
            private static void trace(nint self, byte* file, ushort line, byte* format, nint arguments) { }

            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
            private static void profilerBegin(nint self, byte* name, uint abgr, byte* file, ushort line) { }

            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
            private static void profilerEnd(nint self) { }

            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
            private static uint cacheReadSize(nint self, ulong id) => 0;

            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
            private static byte cacheRead(nint self, ulong id, void* data, uint size) => 0;

            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
            private static void cacheWrite(nint self, ulong id, void* data, uint size) { }

            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
            private static void cacheWrite2(nint self, void* data, uint size) { }

            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
            private static void captureBegin(nint self, uint width, uint height, uint pitch, bgfx.TextureFormat format, byte yFlip) { }

            [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
            private static void screenShot(nint self, byte* file, uint width, uint height, uint pitch,
                bgfx.TextureFormat format, byte* data, uint size, byte yFlip)
            {
                var pixels = new byte[checked((int)(width * height * 4))];
                var rowBytes = (int)width * 4;
                for (var row = 0; row < height; row++)
                {
                    var source = yFlip != 0 ? height - 1 - row : row;
                    new ReadOnlySpan<byte>(data + source * pitch, rowBytes).CopyTo(pixels.AsSpan(row * rowBytes, rowBytes));
                }
                if (format == bgfx.TextureFormat.BGRA8)
                    for (var offset = 0; offset < pixels.Length; offset += 4)
                        (pixels[offset], pixels[offset + 2]) = (pixels[offset + 2], pixels[offset]);
                Capture = new RenderCapture(width, height, [.. pixels]);
            }
        }
    }
}
