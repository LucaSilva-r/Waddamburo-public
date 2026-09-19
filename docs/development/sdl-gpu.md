# SDL and SDL_GPU lifecycle

`Waddamburo.Platform.Sdl.SdlApplication` owns SDL video initialization, one window,
one SDL_GPU device, the window/device claim, and event polling. Its internal
`RenderDevice` owns shaders, pipelines, samplers, textures, uploads, command
submission, and presentation. All operations stay on the thread that created the
application, and shutdown waits for idle before releasing GPU resources.

The first 2D path consumes an immutable `RenderFrame`. A frame contains a clear
color and ordered four-corner textured quads; public texture IDs keep SDL pointers
out of frame state. Positions and UVs are normalized, both nearest and linear
sampling are available, color multiply/add is explicit, source RGBA is treated as
straight alpha, and the fragment shader emits premultiplied alpha for one/source-
alpha blending. The sample app uploads a synthetic checkerboard through this same
path. A temporarily unavailable swapchain image is valid and is not replaced with
an invented render target.

Lumen owns its renderer-independent `LumenRenderSnapshot`: logical stage size,
ordered quad vertices, texture indices, colors, and sampling intent. The SDL adapter
normalizes those coordinates and resolves texture indices to opaque GPU IDs. This
keeps mutable display state and SDL resources on opposite sides of one small,
testable conversion boundary.

Run it interactively with:

```sh
dotnet run --project src/Waddamburo.App
```

Use `--frames=N` to render a bounded number of frames for local smoke tests. This is
a diagnostic option, not a gameplay timing mechanism. The successful Linux x64
baseline uses SDL's Vulkan GPU backend.

The platform adapter references `ppy.SDL3-CS` at the centrally pinned version. Its
NuGet package provides the platform-native SDL shared library. Shader bytecode is
embedded in the platform assembly; no compiler is loaded or executed at runtime.

Run `eng/build-shaders.sh` on Linux to reproduce the checked-in SPIR-V. Run
`eng/build-shaders.ps1` on Windows to reproduce SPIR-V and generate DXIL. Both
scripts download version-pinned official archives into ignored `out/` paths and
reject checksum mismatches. Vulkan uses a vertex uniform buffer at set 1/binding 0
and one combined image sampler at set 2/binding 0. D3D12 uses `b0, space1` plus
`t0/s0, space2`, matching SDL_GPU's documented resource order.
