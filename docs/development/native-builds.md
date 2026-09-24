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
does not discover, embed, cache, or redistribute fonts. Release builds must pin
and stage an exact reviewed FreeType version before enabling this option.
