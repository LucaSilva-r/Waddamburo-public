# Pinned FFmpeg build

Waddamburo builds FFmpeg 8.1.2 from the official source archive. CMake verifies
the archive against SHA-256
`464beb5e7bf0c311e68b45ae2f04e9cc2af88851abb4082231742a74d97b524c`
before extraction. Downloads, extracted sources, objects, and installed libraries
remain below ignored `out/` directories.

The configuration builds dynamic `avutil`, `avcodec`, `avformat`, and
`swresample` libraries. It disables automatic dependency discovery, static
libraries, programs, devices, filters, scaling, networking, GPL components,
nonfree components, and version-3-only components. No external codec library is
linked.

The allowlist supports the product's declared inputs:

| Kind | Enabled components |
| --- | --- |
| Decoders | ATRAC3plus; MP3; Vorbis; Opus; FLAC; unsigned 8-bit, signed 16/24/32-bit, and float 32-bit little-endian PCM |
| Demuxers | WAV, MP3, Ogg, FLAC |
| Parsers | MPEG audio, Vorbis, Opus, FLAC |
| Protocols | Local files only; managed-memory inputs use Waddamburo's AVIO callbacks |

Build the Linux dependency from the repository root with:

```sh
cd native
cmake --preset linux-ffmpeg
cmake --build --preset linux-ffmpeg
```

The installed prefix is
`../out/build/native/linux-x64/linux-ffmpeg/ffmpeg/prefix` relative to `native`.
The build preset finishes by auditing the generated FFmpeg configuration against
the exact component allowlist and checking the shared libraries and compliance
files.

The Windows preset expects a Visual Studio x64 developer environment plus Bash
and Make from MSYS2. It selects FFmpeg's MSVC toolchain and produces DLLs rather
than MinGW static archives:

```sh
cd native
cmake --preset windows-ffmpeg
cmake --build --preset windows-ffmpeg
```

Windows remains an unvalidated recipe until its CI job lands. Do not publish its
artifacts based only on successful Linux validation.

The exact source URL, hash, component allowlist, link mode, and license-copy step
live in `native/ffmpeg/CMakeLists.txt`. Any version or option change requires a
fresh upstream-license review, lockstep notice update, and review of FFmpeg's
generated `config.h` and `ffbuild/config.mak`.

Upstream references:

- <https://ffmpeg.org/download.html>
- <https://ffmpeg.org/legal.html>
- <https://ffmpeg.org/doxygen/8.1/md_LICENSE.html>
