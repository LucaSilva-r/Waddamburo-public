# Pinned FFmpeg build

Waddamburo builds FFmpeg 8.1.2 from the official source archive. CMake verifies
the archive against SHA-256
`464beb5e7bf0c311e68b45ae2f04e9cc2af88851abb4082231742a74d97b524c`
before extraction. Downloads, extracted sources, objects, and installed libraries
remain below ignored `out/` directories.

The developer configuration builds shared `avutil`, `avcodec`, `avformat`,
`swresample`, and `swscale` libraries. Release presets link the same components
statically into the media library. Both configurations disable automatic
dependency discovery, programs, devices, filters, networking, GPL components,
nonfree components, and version-3-only components. The one external codec library
is libopus 1.5.2 (BSD-3-Clause, SHA-256
`65c1d2f78b9f2fb20082c38cbe47c951ad5839345876e46941612ee87f9a7ce1`, matching xiph's
SHA256SUMS), built static with PIC into the same prefix and linked into `avcodec`: it
re-encodes vgmstream-cli's WAV output as Opus for the decoded-audio cache
(`waddamburo_media_transcode_opus`, ABI 1.4). FFmpeg's own Opus encoder is not used: its
psychoacoustic queue overflows and drops audio on songs that start with silence.

The allowlist supports the product's declared inputs:

| Kind | Enabled components |
| --- | --- |
| Decoders | ATRAC3plus; H.264 (attract movies); MP3; Vorbis; Opus; FLAC; unsigned 8-bit, signed 16/24/32-bit, and float 32-bit little-endian PCM |
| Demuxers | WAV, MP3, Ogg, FLAC; MPEG program stream (PAMF movies) and raw H.264 (its stream probe) |
| Parsers | MPEG audio, Vorbis, Opus, FLAC, H.264 |
| Scaling | `swscale` converts decoded YUV movie frames to RGBA |
| Protocols | Local files only; managed-memory inputs use Waddamburo's AVIO callbacks |
| Encoders | libopus (the decoded-audio cache) |
| Muxers | Ogg (the decoded-audio cache) |

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

## Relinking a release

Waddamburo uses FFmpeg under the LGPL 2.1 or later. The release page provides
the tagged Waddamburo source, `ffmpeg-source.zip` with the exact unmodified
FFmpeg and Opus sources, and `ffmpeg-relink-<platform>.zip` with the original
configure options and the vgmstream inputs used by the media library.

Extract the Waddamburo source and run the following from its root on a Linux
host. Use the `linux-x64`, `win-x64`, or `android-arm64` archive for the target.
The Windows preset needs mingw-w64. The Android preset uses NDK 29.0.14206865
and targets API 26. Set `ANDROID_NDK_HOME` to the NDK's directory if needed.

```sh
mkdir -p out/downloads out/relink
unzip /path/to/ffmpeg-source.zip -d out/downloads
# Use the archive and preset for your platform.
unzip /path/to/ffmpeg-relink-android-arm64.zip -d out/relink
tar -xf out/downloads/ffmpeg-8.1.2.tar.xz -C out/relink
# Modify the extracted FFmpeg sources here.
cmake -S native --preset android-package -B out/relink/build \
  -DWADDAMBURO_FFMPEG_SOURCE_DIR="$PWD/out/relink/ffmpeg-8.1.2" \
  -DWADDAMBURO_RELINK_VGMSTREAM_DIR="$PWD/out/relink/vgmstream"
cmake --build out/relink/build --target waddamburo_media
```

For Linux, use `linux-package`, extract the AppImage with
`./Waddamburo-x86_64.AppImage --appimage-extract`, copy the rebuilt
`stage/Release/lib/libwaddamburo_media.so` into `squashfs-root/usr/bin/`, and run
`squashfs-root/AppRun`.

For Windows, use `windows-package`. Extract `native-win-x64.zip` into a directory
and replace its `waddamburo_media.dll` with the one under `stage/Release/bin`.
From the matching Waddamburo source, run
`dotnet publish src/Waddamburo.App -c Release -r win-x64 --self-contained -p:WaddamburoNativeDir=/absolute/path/to/that/directory -o out/relink/windows`
and use `Waddamburo.App.exe` in that publish folder.

For Android, replace the media library in a copy of the downloaded APK, align
it, and sign it with your own key. Run these commands from the matching source
root with Android SDK Build Tools 36.0.0. Omit the signature removal command if
the downloaded APK has no `META-INF` signature entries.

```sh
relink_dir="$PWD/out/relink"
build_tools="$ANDROID_HOME/build-tools/36.0.0"
cp /path/to/Waddamburo.apk "$relink_dir/modified.apk"
zip -d "$relink_dir/modified.apk" 'META-INF/*.SF' 'META-INF/*.RSA' \
  'META-INF/*.DSA' 'META-INF/*.EC' 'META-INF/MANIFEST.MF'
mkdir -p "$relink_dir/apk/lib/arm64-v8a"
cp "$relink_dir/build/stage/Release/lib/libwaddamburo_media.so" "$relink_dir/apk/lib/arm64-v8a/"
(cd "$relink_dir/apk" && zip -0 "$relink_dir/modified.apk" lib/arm64-v8a/libwaddamburo_media.so)
"$build_tools/zipalign" -P 16 -f 4 "$relink_dir/modified.apk" "$relink_dir/aligned.apk"
keytool -genkeypair -keystore "$relink_dir/relink.jks" -alias relink -keyalg RSA -keysize 2048 -validity 10000
"$build_tools/apksigner" sign --ks "$relink_dir/relink.jks" --ks-key-alias relink \
  --out "$relink_dir/Waddamburo-relinked.apk" "$relink_dir/aligned.apk"
"$build_tools/apksigner" verify "$relink_dir/Waddamburo-relinked.apk"
```

Install the signed APK on a test phone or emulator. Android only accepts
in-place updates signed with the same key. Back up your app data before
uninstalling an existing copy signed with a different key.

## vgmstream backend and Nijiiro audio

The game's nuSound2 `.nub` banks (sound effects and voices are cues inside them) and
NUS3BANK/BNSF files use a vgmstream backend. `native/vgmstream/` downloads pinned
vgmstream source and builds it as a static library with G.719 disabled: the G.719
reference code has no identified open-source redistribution grant, so Waddamburo never
builds or ships it. G.722.1 Annex C (Siren 14) is also disabled pending review
of the decoder's provenance. To build the Linux decoder with both backends:

```sh
cd native
cmake --preset linux-ffmpeg
cmake --build --preset linux-ffmpeg
cmake --preset linux-audio-complete
cmake --build --preset linux-audio-complete
ctest --preset linux-audio-complete
```

Release packages (`eng/package.sh`, see [native-builds.md](native-builds.md)) always
include this backend. Nijiiro BNSF/IS22 audio is G.719, so the bundled decoder rejects
it; the managed decoder then runs a user-supplied `vgmstream-cli` (an official
vgmstream release, placed next to the Waddamburo executable, in a `vgmstream` folder
beside it, or on `PATH`), decodes the file once to a WAV in the user cache
(`$XDG_CACHE_HOME` or `~/.cache`, `%LOCALAPPDATA%` on Windows, under
`Waddamburo/vgmstream`), and plays that. In the background the WAV is re-encoded as
160 kbit/s Opus in Ogg (about a tenth of the size) and removed; later plays use the
Opus file. Loop points are dropped with the WAV (these banks are songs, played once).
Without `vgmstream-cli` those files report the backend error and a one-time hint, and
the settings' Nijiiro row says the music needs it.

With `linux-audio-complete`, file inputs ending in `.nub`, `.nus3bank`,
`.nus3audio`, `.bnsf`, `.spsis14`, `.spsis22`, or `.idsp` are routed to
vgmstream. A single-stream `.nub` that vgmstream does not recognize falls back to
the bounded embedded-RIFF scanner and FFmpeg; this covers ATRAC-family NUBs without
weakening selected-cue routing. Decoder ABI 1.1 accepts a one-based source-stream index (zero retains
the default/first stream), allowing individual cues in multi-stream banks to be
decoded without extracting them. Callback-backed inputs continue through FFmpeg
and therefore do not support these formats yet. The ABI regression constructs a
silent synthetic BNSF/IS22 file; no commercial audio is stored in the repository.

The Windows preset expects a Visual Studio x64 developer environment plus Bash,
Make, and `cmp` (from `diffutils`) from MSYS2. It selects FFmpeg's MSVC toolchain and produces DLLs rather
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
