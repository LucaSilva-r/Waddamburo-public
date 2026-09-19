# SDL and SDL_GPU lifecycle

`Waddamburo.Platform.Sdl.SdlApplication` owns SDL video initialization, one window,
one SDL_GPU device, the window/device claim, event polling, command submission, and
shutdown. All of those operations must remain on the thread that created the
application.

The initial rendering path acquires the swapchain texture, starts an empty render
pass with a clear load operation, presents the submitted command buffer, and handles
a temporarily unavailable swapchain image without inventing a render target. It
does not require shaders. Shutdown waits for the GPU to become idle, releases the
window claim, then destroys the device, window, and SDL subsystems in that order.

Run it interactively with:

```sh
dotnet run --project src/Waddamburo.App
```

Use `--frames=N` to render a bounded number of frames for local smoke tests. This is
a diagnostic option, not a gameplay timing mechanism. The successful Linux x64
baseline uses SDL's Vulkan GPU backend.

The platform adapter references `ppy.SDL3-CS` at the centrally pinned version. Its
NuGet package provides the platform-native SDL shared library; no system SDL install
or runtime shader compiler is used by this clear-only path. Shader packaging becomes
required when the textured quad pipeline is introduced.
