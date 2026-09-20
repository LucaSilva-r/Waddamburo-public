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
cmake --preset linux-audio-pinned
cmake --build --preset linux-audio-pinned
ctest --preset linux-audio-pinned
```

The installed prefix is
`../out/build/native/linux-x64/linux-ffmpeg/ffmpeg/prefix` relative to `native`.
The build preset finishes by auditing the generated FFmpeg configuration against
the exact component allowlist and checking the shared libraries and compliance
files.

`linux-audio-pinned` links `waddamburo_media` against that prefix and runs a
synthetic decode test. Developers with compatible system FFmpeg headers may use
`linux-audio` for faster iteration; release and compatibility conclusions must use
the pinned preset.

For a local Linux playback smoke test, build the managed app and put the media
library and its four pinned FFmpeg shared libraries on the loader path:

```sh
LD_LIBRARY_PATH=out/build/native/linux-x64/linux-audio-pinned/stage/Release/lib:\
out/build/native/linux-x64/linux-ffmpeg/ffmpeg/prefix/lib \
  dotnet run --project src/Waddamburo.App -- --play-audio=/path/to/song.ogg
```

The same entry point accepts MP3, Ogg/Vorbis, Opus, FLAC, WAV, and Green NUB files.
It is diagnostic scaffolding for the streaming decoder, not the final BGM/preview/
sound-effect mixer. SDL device ownership and queue semantics are documented in
[`sdl-audio.md`](sdl-audio.md).

## Local Nijiro backend

Nijiro NUS3BANK files containing IDSP or BNSF/IS22 payloads use an optional
vgmstream backend. This backend is deliberately excluded from normal and release
builds because its G.719 implementation has no identified open-source redistribution
grant. Waddamburo downloads pinned vgmstream source, but never downloads G.719 source;
a developer must obtain it independently and point the build at a local checkout.

The currently reviewed local checkout is `libg719_decode` commit
`da90ad8a676876c6c47889bcea6a753f9bbf7a73`. To build the complete Linux decoder:

```sh
export WADDAMBURO_G719_SOURCE_DIR=/path/to/libg719_decode
cd native
cmake --preset linux-ffmpeg
cmake --build --preset linux-ffmpeg
cmake --preset linux-vgmstream-g719
cmake --build --preset linux-vgmstream-g719
cmake --preset linux-audio-complete
cmake --build --preset linux-audio-complete
ctest --preset linux-audio-complete
```

Equivalently, run this from the repository root to build and test all three
native layers:

```sh
WADDAMBURO_G719_SOURCE_DIR=/path/to/libg719_decode \
  ./eng/bootstrap.sh --native-only --configuration Release --with-nijiro-audio
```

The vgmstream prefix includes
`NON-REDISTRIBUTABLE-G719.txt`. Do not copy its library into a release, CI artifact,
public download, or package. This is a Linux-only developer recipe; the ordinary
FFmpeg backend remains the distributable path.

With `linux-audio-complete`, file inputs ending in `.nus3bank`, `.nus3audio`,
`.bnsf`, `.spsis14`, `.spsis22`, or `.idsp` are routed to vgmstream. A NUS3BANK
opens its default/first stream. Callback-backed inputs continue through FFmpeg and
therefore do not support these formats yet. The ABI regression constructs a silent
synthetic BNSF/IS22 file; no commercial audio is stored in the repository.

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
