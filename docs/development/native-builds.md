# Native build configurations

Native dependencies and Waddamburo's native wrappers use checked-in CMake presets.
The build configuration is always an explicit preset; environment variables must
not alter compiled product behavior.

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
