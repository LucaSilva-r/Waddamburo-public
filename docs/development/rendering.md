# Rendering (SDL + bgfx)

`Waddamburo.Platform.Sdl.SdlApplication` owns SDL video initialization, one window,
and event polling. Drawing goes through [bgfx](https://github.com/bkaradzic/bgfx),
one renderer over Vulkan, Direct3D 11, Metal, and OpenGL ES. Its internal
`RenderDevice` owns programs, textures, uploads, and frame submission; the Don
renderer adds its off-screen views in front of the compositor. All bgfx calls stay
on the thread that created the application.

## Backends

bgfx picks the platform default (Vulkan on Linux, Direct3D on Windows, Metal on
macOS). `WADDAMBURO_RENDERER=vulkan|gles|d3d11|metal|opengl` forces one for
diagnostics; `renderer` in `config.cfg` does the same for players (`auto` by default,
the env var wins). The Linux library is built with only the GLES 3.0 and Vulkan
renderers: bgfx's desktop GL renderer requires GL 4.3, while Ivy Bridge-era GPUs
expose 4.2 at most and do expose ES 3.0 under Mesa. On Windows those GPUs use
Direct3D 11 (feature level 11_0). Current bgfx no longer supports GLES 2.

## Threading and input

The app runs bgfx's render thread itself (`BgfxSupport.Initialize`), so the main
thread only records frames. With vsync the next frame starts once the render thread
has presented; until then the main loop keeps polling SDL every ~1 ms and delivers
drum presses immediately, as with the earlier SDL_GPU backend.
`WADDAMBURO_PRESENT_MODE=immediate` (or `mailbox`) disables vsync; uncapped runs let
one frame render while the next is built.

## Frames

The 2D path consumes an immutable `RenderFrame`. A frame contains a clear
color and ordered four-corner textured quads; public texture IDs keep bgfx handles
out of frame state. Quads are written to one transient vertex buffer per frame and
drawn in a sequential view, one draw per run of quads that share texture, sampling,
blend, and stencil state. Positions and UVs are normalized, both nearest and linear
sampling are available, color multiply/add is explicit, source RGBA is treated as
straight alpha, and the fragment shader emits premultiplied alpha for one/source-
alpha blending. Ordered quads may also select additive or screen blending, which
keep the normal alpha equation. The sample app uploads a synthetic checkerboard
through this same path.

Lumen owns its renderer-independent `LumenRenderSnapshot`: logical stage size,
ordered quad vertices, texture indices, colors, blend, and sampling intent. The SDL adapter
normalizes those coordinates and resolves texture indices to opaque GPU IDs. This
keeps mutable display state and SDL resources on opposite sides of one small,
testable conversion boundary.

The standalone asset viewer frames small movies around their visible quad bounds
with a five-percent margin while preserving the logical stage aspect ratio. Stage
composition continues to use the unmodified 0..1280 by 0..720 view.

Run it interactively with:

```sh
dotnet run --project src/Waddamburo.App
```

Use `--frames=N` to render a bounded number of presentation frames for local smoke
tests. `--ticks=N` instead stops after an exact number of authored 60 Hz simulation
ticks. `SdlApplication` accumulates monotonic wall time independently of display
presentation, executes at most five catch-up ticks per display frame, and reports
discarded excess ticks explicitly. The successful Linux x64 baseline uses SDL's
Vulkan GPU backend.

SDL key-down/up events maintain a portable immutable keyboard snapshot. Every set
of catch-up ticks sees the snapshot captured after that presentation loop's event
poll, and window focus loss clears held state. `LumenInputAdapter` maps letters,
digits, arrows, Enter, Escape, Space, and Backspace to the standard key codes read
by AVM `Key.isDown`; SDL types do not cross into the Lumen runtime. Physical Taiko
keys map P1 `D/F/J/K` and P2 `Z/X/C/V` onto each movie's authored
left/right/decide polling, with either center hit acting as decide.

Lumen frames carry their logical-stage aspect ratio into the renderer. Every
presentation derives an integer-pixel viewport from the acquired swapchain image
and applies matching GPU viewport and scissor state, preserving the stage across
resize and high-DPI drawable sizes. Standalone movies use that same fixed stage
transform: animated elements outside the stage are clipped and never change the
camera or scale. `--window-size=WIDTHxHEIGHT` provides a bounded diagnostic startup
size for framebuffer checks. Screenshot runs use a non-resizable, logical-density
window so the requested pixel dimensions cannot vary with window-manager placement.

`--screenshot=PATH` requests bgfx's back-buffer screenshot for the final bounded
frame and writes a top-down RGBA capture as PNG or BMP. Without a frame or tick
bound, capture renders one frame. The request waits for the render thread by design
and is restricted to this explicit diagnostic path; normal presentation performs no
readback.

The platform adapter references `ppy.SDL3-CS` at the centrally pinned version. Its
NuGet package provides the platform-native SDL shared library. bgfx comes from the
pinned native build (`native/bgfx`, `-DWADDAMBURO_BUILD_BGFX=ON`), which also builds
its `shaderc` tool; the platform project copies the library next to the executable.
Shader bytecode is embedded in the platform assembly; no compiler is loaded or
executed at runtime.

The `.sc` shader sources live in `Shaders/Bgfx/`. Run `eng/build-shaders.sh` on Linux
to regenerate the Vulkan, GLES, desktop GL, and Metal binaries in `Shaders/Compiled/`,
and `eng/build-shaders.ps1` on Windows to also generate Direct3D 11 bytecode, which
needs Microsoft's compiler. Commit the regenerated binaries with the sources.
