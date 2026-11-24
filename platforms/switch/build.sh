#!/usr/bin/env bash
# Builds Waddamburo for the Nintendo Switch (homebrew, libnx) -> out/package/Waddamburo.nro.
#
# The app is the same Waddamburo.App, compiled with NativeAOT for linux-arm64 (only ILC's object file is used: its own
# link is expected to fail) and linked against libnx through a glibc shim (shim/). The native libraries (FFmpeg,
# vgmstream, Waddamburo's own, SQLite, bgfx) are built for libnx from the same pinned sources as native/.
#
# Needs: .NET 10 SDK, curl, and either the devkitPro devkitA64 image (CI runs inside it: $DEVKITPRO is set) or Docker
# (the steps that need devkitA64 then run in the pinned image). See platforms/switch/README.md.
#
# usage: platforms/switch/build.sh [all|deps|natives|bgfx|app|link]   (default all; app also links)
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
out=$root/out/switch
step=${1:-all}
image=devkitpro/devkita64@sha256:1fc388c3a0d34bd2045a6dadcb1020e069d5f876a187fd705de14b4440c00282
version=${WADDAMBURO_VERSION:-$(git -C "$root" describe --tags --exact-match --match 'v[0-9]*' 2>/dev/null | sed 's/^v//' || true)}
version=${version:-0.0.0-dev}
mkdir -p "$out/ext" "$root/out/downloads" "$root/out/package"

# A script that needs devkitA64: directly inside the image, else in a container with the repository at the same path.
devkit() {
    if [[ -n ${DEVKITPRO:-} ]]; then
        ROOT=$root OUT=$out HERE=$here VERSION=$version bash "$@"
    else
        docker run --rm --user "$(id -u):$(id -g)" -v "$root:$root:z" -w "$root" \
            -e ROOT="$root" -e OUT="$out" -e HERE="$here" -e VERSION="$version" "$image" bash "$@"
    fi
}

# A pin from native/'s CMake files (the first quoted value after "set(NAME"), so both builds use the same sources.
pin() { grep -rhA1 "set($1\b" "$root/native" --include=CMakeLists.txt | grep -oE '"[^"]*"' | head -1 | tr -d '"'; }

fetch() { # url file sha256
    if [[ ! -f $2 ]]; then curl -fsSL -o "$2.part" "$1" && mv "$2.part" "$2"; fi
    echo "$3  $2" | sha256sum --check --status || { echo "error: checksum mismatch: $2" >&2; exit 1; }
}

deps() {
    local downloads=$root/out/downloads ext=$out/ext
    local vgm=$(pin WADDAMBURO_VGMSTREAM_REVISION) bgfx=$(pin WADDAMBURO_BGFX_VERSION)
    # FFmpeg: not native/'s version but devkitPro's Switch one, with its patch (averne's nvtegra: NVDEC hardware
    # decoding; GPL, so the Switch build is GPL-2.0-or-later as a whole).
    local ffmpeg=7.1 dkp=0e7866ba743b817e5a4f7f8268b94d4620202474
    fetch "https://ffmpeg.org/releases/ffmpeg-$ffmpeg.tar.xz" "$downloads/ffmpeg-$ffmpeg.tar.xz" \
        40973d44970dbc83ef302b0609f2e74982be2d85916dd2ee7472d30678a7abe6
    fetch "https://raw.githubusercontent.com/devkitPro/pacman-packages/$dkp/switch/ffmpeg/ffmpeg-$ffmpeg.patch" \
        "$downloads/ffmpeg-$ffmpeg-switch.patch" 1792380b992e3554a4abcddf0d7b395bfd8c118ac7c6e38c8f2fb0d39753a390
    fetch "https://github.com/vgmstream/vgmstream/archive/$vgm.tar.gz" "$downloads/vgmstream-$vgm.tar.gz" "$(pin WADDAMBURO_VGMSTREAM_SHA256)"
    fetch "https://github.com/bkaradzic/bgfx.cmake/releases/download/$bgfx/bgfx.cmake.$bgfx.tar.gz" \
        "$downloads/bgfx.cmake.$bgfx.tar.gz" "$(pin WADDAMBURO_BGFX_SHA256)"
    # SQLite as SQLitePCLRaw's e_sqlite3 (3.53.3), and the SDL headers ppy.SDL3-CS binds (sdl3nx.c implements that ABI).
    fetch https://www.sqlite.org/2026/sqlite-amalgamation-3530300.zip "$downloads/sqlite-amalgamation-3530300.zip" \
        646421e12aac110282ef8cc68f1a62d4bb15fc7b8f09da0b53e29ee690500431
    local sdl=a8591d943b7079b17fdd018dc04ec9c71dc94ae4
    fetch "https://github.com/libsdl-org/SDL/archive/$sdl.tar.gz" "$downloads/SDL-$sdl.tar.gz" \
        7009fddd4c608817435471dbb964da401bc7932dee0cbcfe5883df38e94b63e7

    # nx-hbloader 2.4.5, the program of the home-menu forwarder (patches/hbloader, scripts/link.sh, shim/forwarder.c).
    local hbl=82b95122c5ae8dc059bf23893ba7623c72c86773
    fetch "https://github.com/switchbrew/nx-hbloader/archive/$hbl.tar.gz" "$downloads/nx-hbloader-$hbl.tar.gz" \
        c0e6a7a485d3ef87a25de2fb564d71be97c71ed8161703436b4f089ea1c15c16

    if [[ ! -d $ext/ffmpeg-$ffmpeg-nvtegra ]]; then
        rm -rf "$ext/ffmpeg-$ffmpeg" && tar -xJf "$downloads/ffmpeg-$ffmpeg.tar.xz" -C "$ext"
        patch -s -p1 -d "$ext/ffmpeg-$ffmpeg" < "$downloads/ffmpeg-$ffmpeg-switch.patch"
        mv "$ext/ffmpeg-$ffmpeg" "$ext/ffmpeg-$ffmpeg-nvtegra"
    fi
    [[ -d $ext/vgmstream-$vgm ]] || tar -xzf "$downloads/vgmstream-$vgm.tar.gz" -C "$ext"
    [[ -d $ext/sqlite-amalgamation-3530300 ]] || (cd "$ext" && cmake -E tar xf "$downloads/sqlite-amalgamation-3530300.zip")
    [[ -d $ext/sdl3 ]] || { mkdir -p "$ext/sdl3" && tar -xzf "$downloads/SDL-$sdl.tar.gz" -C "$ext/sdl3" --strip-components=1 "SDL-$sdl/include"; }
    if [[ ! -d $ext/bgfx-src ]]; then
        mkdir -p "$ext/bgfx-src.part" && tar -xzf "$downloads/bgfx.cmake.$bgfx.tar.gz" -C "$ext/bgfx-src.part" --strip-components=1
        for patch in "$here"/patches/*.patch; do patch -s -p1 -d "$ext/bgfx-src.part" < "$patch"; done
        mv "$ext/bgfx-src.part" "$ext/bgfx-src"
    fi
    if [[ ! -d $ext/nx-hbloader ]]; then
        mkdir -p "$ext/nx-hbloader.part" && tar -xzf "$downloads/nx-hbloader-$hbl.tar.gz" -C "$ext/nx-hbloader.part" --strip-components=1
        patch -s -p1 -d "$ext/nx-hbloader.part" < "$here/patches/hbloader/forwarder.patch"
        mv "$ext/nx-hbloader.part" "$ext/nx-hbloader"
    fi
    echo "FFMPEG=$ffmpeg-nvtegra VGMSTREAM=$vgm" > "$ext/versions.env"
}

app() {
    # ILC's object for linux-arm64; switch.targets binds the natives statically and marks the build as the Switch's.
    # The cross link that follows needs a linux-arm64 toolchain and fails: expected, only the object is used.
    local log=$out/app-publish.log
    local object=$root/src/Waddamburo.App/obj/Release/net10.0/linux-arm64/native/Waddamburo.App.o
    rm -f "$object" # a failed compile must not leave the previous object in place
    dotnet publish "$root/src/Waddamburo.App" -r linux-arm64 -c Release -p:PublishAot=true -p:SelfContained=true \
        -p:InvariantGlobalization=true -p:Version="$version" -p:NuGetLockFilePath=obj/switch.lock.json -p:RestoreLockedMode=false \
        -p:CustomAfterMicrosoftCommonTargets="$here/switch.targets" > "$log" 2>&1 || true
    [[ -f $object ]] || { grep -E "error" "$log" | sort -u | head; echo "error: no ILC object" >&2; exit 1; }
    # The runtime's static libraries, from the NativeAOT runtime pack the publish used.
    local packs=${NUGET_PACKAGES:-$HOME/.nuget/packages}/microsoft.netcore.app.runtime.nativeaot.linux-arm64
    local runtime; runtime=$(ls -d "$packs"/*/runtimes/linux-arm64/native | sort -V | tail -1)
    rm -rf "$out/rt" && mkdir -p "$out/rt"
    cp "$object" "$out/rt/app.o"
    for lib in libbootstrapper.o libRuntime.WorkstationGC.a libSystem.Native.a libSystem.Globalization.Native.a \
        libSystem.IO.Compression.Native.a libSystem.Net.Security.Native.a libSystem.Security.Cryptography.Native.OpenSsl.a \
        libeventpipe-disabled.a libstandalonegc-disabled.a libaotminipal.a libbrotlienc.a libbrotlidec.a libbrotlicommon.a; do
        cp "$runtime/$lib" "$out/rt/"
    done
}

case $step in
deps) deps ;;
natives) devkit "$here/scripts/natives.sh" "${2:-all}" ;;
bgfx) devkit "$here/scripts/bgfx.sh" ;;
app) app && devkit "$here/scripts/link.sh" ;;
link) devkit "$here/scripts/link.sh" ;;
all)
    deps
    [[ -f $out/natives/prefix/lib/libwaddamburo_media.a ]] || devkit "$here/scripts/natives.sh" all
    [[ -f $out/bgfx/prefix/lib/libbgfx.a ]] || devkit "$here/scripts/bgfx.sh"
    app
    devkit "$here/scripts/link.sh"
    ;;
*) echo "usage: $0 [all|deps|natives|bgfx|app|link]" >&2; exit 2 ;;
esac
