# Third-party components of the Switch build

Besides Waddamburo's own notices (`THIRD_PARTY_NOTICES.md`, FFmpeg, vgmstream, bgfx, bc7enc and the NuGet inventory in
this archive), `Waddamburo.nro` links these statically. Their full license texts are in the devkitPro packages (portlibs
under `/opt/devkitpro`) and at the projects below.

| Component | License | Source |
| --- | --- | --- |
| .NET NativeAOT runtime (Microsoft.NETCore.App.Runtime.NativeAOT.linux-arm64) | MIT | https://github.com/dotnet/runtime (notices in `dotnet/`) |
| libnx | ISC | https://github.com/switchbrew/libnx |
| newlib (devkitA64) | BSD-style licenses | https://sourceware.org/newlib/ |
| Mesa (EGL, GLES 2/3 over nouveau) | MIT | https://gitlab.freedesktop.org/mesa/mesa |
| libdrm_nouveau | MIT | https://gitlab.freedesktop.org/mesa/drm |
| FreeType | FreeType License (FTL) | https://freetype.org |
| HarfBuzz | MIT (Old MIT) | https://github.com/harfbuzz/harfbuzz |
| libpng | PNG Reference Library License v2 | http://www.libpng.org |
| zlib | zlib License | https://zlib.net |
| bzip2 | bzip2 License (BSD-style) | https://sourceware.org/bzip2/ |
| Opus | BSD 3-Clause | https://opus-codec.org |
| Mbed TLS (mbedcrypto) | Apache-2.0 | https://github.com/Mbed-TLS/mbedtls |
| SQLite | Public domain | https://sqlite.org |
| SDL 3 headers (the ABI `shim/sdl3nx.c` implements) | zlib License | https://github.com/libsdl-org/SDL |
| nx-hbloader 2.4.5 (the HOME-menu forwarder's program, `patches/hbloader`; embedded in the NSP Waddamburo makes) | ISC | https://github.com/switchbrew/nx-hbloader |

Portions of this software are copyright © The FreeType Project (www.freetype.org). All rights reserved.

`Waddamburo.nro` is distributed under the GNU GPL, version 2 or (at your option) any later version (`FFmpeg/COPYING.GPLv2`):
its FFmpeg is 7.1 with devkitPro's Switch patch, which adds averne's nvtegra hardware decoding (GPL-2.0-or-later), built
with `--enable-gpl`. Its complete corresponding source: `ffmpeg-source-switch.zip` next to the release (the FFmpeg
tarball and the patch, as `platforms/switch/build.sh` applies them) and the Waddamburo source at the release's tag
(Waddamburo itself is MIT; `platforms/switch/` builds the whole). Mbed TLS and FreeType are taken under their GPL
options (GPL-2.0-or-later), the other components' licenses above are GPL-compatible.
