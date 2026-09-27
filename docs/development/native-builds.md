# Native build configurations

Native dependencies and Waddamburo's native wrappers use checked-in CMake presets.
The build configuration is always an explicit preset; environment variables must
not alter compiled product behavior.

## Developer bootstrap

The supported bootstrap commands validate the host architecture and required
tools, restore locked managed dependencies, build with CI warning policy, run the
managed and native tests, and use the same `bin/`, `obj/`, and `out/build/` layout
as CI:

```sh
# Linux x64
./eng/bootstrap.sh --configuration Debug
```

```powershell
# Windows x64, from a Visual Studio x64 developer PowerShell
./eng/bootstrap.ps1 -Configuration Debug
```

Use `Release` for release-policy validation or `Sanitize` for the native sanitizer
configuration (the managed build remains `Debug`). Pass `--with-ffmpeg` on Linux
or `-WithFFmpeg` on Windows to also build and audit the checksum-pinned minimal
FFmpeg libraries. That opt-in build downloads source into `out/downloads/` and can
take substantially longer than the default bootstrap.

The required tools are:

| Host | Required tools |
| --- | --- |
| Linux x64 | .NET 10 SDK, CMake 3.25 or newer, Ninja, and a C17/C++20 GCC or Clang toolchain |
| Windows x64 | .NET 10 SDK, CMake 3.25 or newer, Ninja, and Visual Studio 2022 C/C++ build tools in an x64 developer shell |
| FFmpeg opt-in | `make`; Windows additionally requires MSYS2 `bash` and `cmp` (from `diffutils`) |
| Text rasterizer opt-in | FreeType 2.13 or newer development package discoverable by CMake |

The scripts fail immediately for unsupported hosts or missing tools. They do not
install SDKs, mutate user-level configuration, or consult product behavior flags
from the environment.

Run presets from the `native` directory:

```sh
cd native
cmake --preset linux-debug
cmake --build --preset linux-debug
ctest --preset linux-debug
```

Linux developers and CI may replace `linux-debug` with `linux-release` or
`linux-sanitize`. Windows developers use the corresponding `windows-debug`,
`windows-release`, or `windows-sanitize` preset from a Visual Studio developer
shell with Ninja available.

The configurations have these purposes:

| Configuration | Optimization and diagnostics | Intended use |
| --- | --- | --- |
| `Debug` | Toolchain debug defaults, symbols, strict warnings | Local development and native ABI tests |
| `Release` | Toolchain release optimization, strict warnings | Packaging and artifact smoke tests |
| `Sanitize` | Debug information, low optimization, strict warnings, AddressSanitizer; UndefinedBehaviorSanitizer on Clang/GCC | Linux CI and focused native diagnostics |

Every project-authored native target must call
`waddamburo_configure_native_target(<target>)`. This applies the shared language,
warning, sanitizer, runtime-library, and staging policy. Dependency projects built
through `ExternalProject` or their own build systems must receive an equivalent
explicit configuration rather than inheriting untracked flags from the shell.

Generated files are written below `out/build/native/<rid>/<preset>/`; no native
build output belongs in source control.

The first project-authored native target is the versioned
[media C ABI](../../native/media/README.md). Its decoder entry points deliberately
report that the backend is unavailable until the pinned FFmpeg build is connected.
The separate [FFmpeg dependency procedure](ffmpeg.md) downloads and builds only
the reviewed codec/demuxer allowlist.

The optional title rasterizer is enabled with
`-DWADDAMBURO_BUILD_TEXT=ON`. It exposes only a small project-owned C ABI and
links dynamically to a system FreeType 2.13-or-newer development installation.
It accepts an explicit user-owned font path and returns premultiplied RGBA8; it
does not discover, embed, cache, or redistribute fonts. Release packages link the
pinned FreeType below statically instead.

## Release packages

`eng/package.sh` builds both releases on a Linux x64 host (it additionally needs
`make` and the mingw-w64 GCC cross compiler, `x86_64-w64-mingw32-gcc`):

```sh
./eng/package.sh            # both; or: ./eng/package.sh linux|windows
```

The `linux-package` and `windows-package` presets (`WADDAMBURO_PACKAGE=ON`; the
Windows one cross-compiles with `native/cmake/mingw-w64.cmake`) build the pinned FFmpeg,
vgmstream (G.719 disabled) and FreeType 2.14.2 as static libraries and fold them into
`waddamburo_media` and `waddamburo_text`. The result depends only on the C runtime and
system libraries (`libc`/`libm`; `kernel32`/`msvcrt`/`bcrypt`), and exports only the
project C ABI. The script then publishes the app self-contained with those two
libraries:

- `out/package/Waddamburo.exe`: a single-file publish; .NET extracts the native
  libraries (SDL3, SQLite and ours) on first run.
- `out/package/Waddamburo-x86_64.AppImage`: a plain self-contained publish in an
  AppDir, packed with a pinned appimagetool and the static type-2 runtime (no FUSE 2
  needed on the target). Build it on the oldest glibc the release should support.

DXIL shaders cannot be compiled on Linux, so the compiled shaders are committed;
rerun `eng/build-shaders.ps1` on Windows (and `.sh` for SPIR-V) after changing a
shader source.
