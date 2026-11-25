#!/usr/bin/env bash
# Builds the release artifacts on a Linux x64 host:
#   out/package/Waddamburo.exe              one self-contained Windows file (native libraries
#                                            self-extract on first run)
#   out/package/Waddamburo-x86_64.AppImage  the Linux build (Waddamburo-aarch64.AppImage on an arm64 host)
#   out/package/licenses.zip                release licenses and notices
#   out/package/licenses-win-x64.zip        notices from the Windows .NET runtime pack
#   out/package/Waddamburo.apk              the Android app (target android: needs the Android SDK, NDK and JDK)
#   out/package/Waddamburo.nro              the Switch homebrew (target switch: platforms/switch/README.md), with
#                                            licenses-switch.zip and ffmpeg-source-switch.zip (GPL: its patched FFmpeg)
#   out/package/ffmpeg-source.zip           pinned FFmpeg and Opus source archives (from the Linux job)
#   out/package/ffmpeg-relink-*.zip         platform-specific configure options and vgmstream link inputs
#   out/package/native-{win,linux}-x64.zip  the native libraries alone, for building from source
#                                            without a C toolchain (eng/fetch-native.sh / .ps1)
# Both are dropped into the game's USRDIR and find the game next to themselves.
# Needs: .NET 10 SDK, CMake, Ninja, make, gcc, mingw-w64 gcc (x86_64-w64-mingw32-gcc).
# On Windows it runs in an MSYS2 MINGW64 shell and builds the Windows release only (BUILDING.md).
#
# usage: eng/package.sh [linux|windows|windows-arm64|macos|android|switch]   (default: linux and windows; windows on Windows)
set -euo pipefail

cd "$(dirname "$0")/.."
case $(uname -s) in
MINGW* | MSYS*) msys=true ;;
*) msys=false ;;
esac
# MSYS2: dotnet is a Windows program and needs C:/... paths, not /c/...
root=$(if $msys; then pwd -W; else pwd; fi)
out=$root/out/package
targets=${1:-$(if $msys; then echo windows; else echo linux windows; fi)}
# Release version from a vX.Y.Z tag on HEAD; untagged builds are development builds, which never self-update.
version=${WADDAMBURO_VERSION:-$(git describe --tags --exact-match --match 'v[0-9]*' 2>/dev/null | sed 's/^v//' || true)}
version=${version:-0.0.0-dev}

appimagetool_version=1.9.1
appimagetool_sha256=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0
runtime_version=20251108
runtime_sha256=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d
appimagetool_sha256_aarch64=f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158
runtime_sha256_aarch64=00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444

# macOS has shasum, not sha256sum.
command -v sha256sum >/dev/null || sha256sum() { shasum -a 256 "$@"; }

fetch() { # url file sha256
    [[ -f $2 ]] || curl -fsSL -o "$2" "$1"
    echo "$3  $2" | sha256sum --check --status || { echo "error: checksum mismatch: $2" >&2; exit 1; }
}

native() { # preset stage-dir rid: build the preset and package its libraries and FFmpeg relink inputs
    (cd native && cmake --preset "$1" && cmake --build --preset "$1")
    rm -f "$out/native-$3.zip"
    (cd "$2" && cmake -E tar cf "$out/native-$3.zip" --format=zip $(ls waddamburo_*.dll libwaddamburo_*.so libwaddamburo_*.dylib bgfx*.dll libbgfx*.so libbgfx*.dylib 2>/dev/null))
    echo "$out/native-$3.zip"

    (cd native && cmake --build --preset "$1" --target waddamburo_ffmpeg_relink)
    local build_dir=${2%/stage/*}
    (cd "$build_dir/ffmpeg-relink" && cmake -E tar cf "$out/ffmpeg-relink-$3.zip" --format=zip FFmpeg vgmstream)
    echo "$out/ffmpeg-relink-$3.zip"
}

ffmpeg_source() { # build-dir: publish the same sources that the pinned build verified
    local manifest=$root/$1/ffmpeg-source-archives.txt
    local archives=() name
    while read -r _ name; do
        archives+=("$name")
    done < "$manifest"
    (cd out/downloads && sha256sum --check "$manifest" &&
        cmake -E tar cf "$out/ffmpeg-source.zip" --format=zip "${archives[@]}")
    echo "$out/ffmpeg-source.zip"
}

nuget_inventory() { # packages.lock.json: the NuGet packages in it with their licenses
    python3 - "$1" "${NUGET_PACKAGES:-$HOME/.nuget/packages}" <<'PY'
import json, re, sys
from pathlib import Path
lock, cache = json.load(open(sys.argv[1])), Path(sys.argv[2])
packages = {(name, entry["resolved"]) for target in lock["dependencies"].values()
            for name, entry in target.items() if entry.get("type") != "Project"}
print("NuGet dependency inventory, including build tools (id, version, license):")
for name, version in sorted(packages, key=lambda p: p[0].lower()):
    spec = next((cache / name.lower() / version).glob("*.nuspec"), None)
    text = spec.read_text(encoding="utf-8-sig") if spec else ""
    license = re.search(r"<license[^>]*>([^<]+)</license>", text) or re.search(r"<licenseUrl>([^<]+)</licenseUrl>", text)
    print(f"{name} {version}: {license.group(1) if license else 'see https://www.nuget.org/packages/' + name}")
PY
}

licenses() { # native-build-dir out-dir [project]: collect Waddamburo's and third-party notices
    local dir=$2
    rm -rf "$dir" && mkdir -p "$dir"
    cp LICENSE NOTICE.md THIRD_PARTY_NOTICES.md eng/packaging/licenses/* "$dir/"
    cp src/Waddamburo.App/Upscale/Real-ESRGAN.LICENSE.txt "$dir/"
    cp src/Waddamburo.Game/Gameplay/Data/soul-gauge-rates.LICENSE.txt "$dir/"
    cp native/texture/third_party/bc7enc/LICENSE "$dir/bc7enc.LICENSE.txt"
    cp -r "$1"/ffmpeg/prefix/licenses/* "$1"/vgmstream/licenses/* "$1"/freetype/prefix/licenses/* "$1"/bgfx/prefix/licenses/* \
        "$1"/bungee/licenses/* "$dir/"
    local dotnet_root
    dotnet_root=$(dirname "$(readlink -f "$(command -v dotnet)")")
    mkdir -p "$dir/dotnet" && cp "$dotnet_root/LICENSE.txt" "$dotnet_root/ThirdPartyNotices.txt" "$dir/dotnet/"
    nuget_inventory "src/${3:-Waddamburo.App}/packages.lock.json" > "$dir/nuget-packages.txt"
}

runtime_licenses() { # project rid license-dir [MSBuild arguments]
    local project=$1 rid=$2 dir=$3; shift 3
    dotnet msbuild "$project" -nologo -t:ResolveFrameworkReferences \
        -getItem:ResolvedFrameworkReference,ResolvedRuntimePack \
        -p:RuntimeIdentifier="$rid" -p:SelfContained=true "$@" |
        python3 -c '
import json, shutil, sys
from pathlib import Path

destination = Path(sys.argv[1])
items = json.load(sys.stdin)["Items"]
frameworks = {item["Identity"] for item in items["ResolvedFrameworkReference"]}
packs = [pack for pack in items["ResolvedRuntimePack"] if pack["FrameworkName"] in frameworks]
if not packs:
    raise SystemExit("No resolved runtime packs for license collection")
for pack in packs:
    folder = destination / pack["NuGetPackageId"]
    folder.mkdir(parents=True, exist_ok=True)
    for name in ("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
        shutil.copy2(Path(pack["PackageDirectory"]) / name, folder / name)
' "$dir"
}

publish() { # rid native-dir out-dir extra-args...
    local rid=$1 native_dir=$2 dir=$3; shift 3
    rm -rf "$dir"
    # ReadyToRun: precompiled, so a method's first call (the first hit, combo voice, Go-Go) is not JIT-compiled
    # inside a frame (10-40 ms hitches on a low-end PC early in a song).
    dotnet publish src/Waddamburo.App -c Release -r "$rid" --self-contained -p:PublishReadyToRun=true \
        -p:WaddamburoNativeDir="$native_dir" -p:Version="$version" -p:DebugType=none -p:PublishDocumentationFile=false -p:PublishReferencesDocumentationFiles=false \
        -o "$dir" "$@"
}

mkdir -p "$out" out/downloads
for target in $targets; do
    case $target in
    windows | windows-arm64)
        # windows-arm64: llvm-mingw (aarch64-w64-mingw32-clang on PATH), Waddamburo-arm64.exe.
        if [[ $target == windows-arm64 ]]; then
            rid=win-arm64 preset=windows-arm64-package exe=Waddamburo-arm64.exe
        else
            rid=win-x64 preset=windows-package exe=Waddamburo.exe
        fi
        native $preset "$root/out/build/native/$rid/windows-package/stage/Release/bin" $rid
        # ImGui.NET supplies cimgui.dll as a NuGet runtime asset. VerifyImGuiBundle in
        # the App project checks it enters the same self-extracting bundle as SDL/bgfx.
        publish $rid "$root/out/build/native/$rid/windows-package/stage/Release/bin" "$out/$rid" \
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
            -p:EnableCompressionInSingleFile=true
        cp "$out/$rid/Waddamburo.App.exe" "$out/$exe"
        win_notices=$out/windows-notices-$rid
        rm -rf "$win_notices"
        runtime_licenses src/Waddamburo.App $rid "$win_notices/dotnet"
        (cd "$win_notices" && cmake -E tar cf "$out/licenses-$rid.zip" --format=zip dotnet)
        echo "$out/$exe"
        ;;
    linux)
        # The host's arch: linux-arm64 builds on an arm64 machine (the release's arm64 runner), no cross toolchain.
        if [[ $(uname -m) == aarch64 ]]; then
            arch=aarch64 rid=linux-arm64 preset=linux-arm64-package
            appimagetool_sha256=$appimagetool_sha256_aarch64 runtime_sha256=$runtime_sha256_aarch64
        else
            arch=x86_64 rid=linux-x64 preset=linux-package
        fi
        build=out/build/native/$rid/linux-package
        native $preset "$root/$build/stage/Release/lib" $rid
        ffmpeg_source $build
        tool=out/downloads/appimagetool-$appimagetool_version-$arch.AppImage
        runtime=out/downloads/appimage-runtime-$runtime_version-$arch
        fetch "https://github.com/AppImage/appimagetool/releases/download/$appimagetool_version/appimagetool-$arch.AppImage" \
            "$tool" "$appimagetool_sha256"
        fetch "https://github.com/AppImage/type2-runtime/releases/download/$runtime_version/runtime-$arch" \
            "$runtime" "$runtime_sha256"
        chmod +x "$tool"
        # ponytail: plain self-contained publish inside the AppDir; the AppImage is already one file.
        appdir=$out/Waddamburo.AppDir
        publish $rid "$root/$build/stage/Release/lib" "$appdir/usr/bin"
        # ponytail: ImGui.NET ships no linux-arm64 cimgui, so the arm64 build has no editor overlays; build cimgui
        # in native/ if arm64 players need them.
        [[ $rid == linux-arm64 || -f "$appdir/usr/bin/libcimgui.so" ]] || { echo "error: missing ImGui native runtime" >&2; exit 1; }
        cp eng/packaging/waddamburo.png "$appdir/waddamburo.png"
        licenses $build "$appdir/usr/share/licenses/waddamburo"
        runtime_licenses src/Waddamburo.App $rid "$appdir/usr/share/licenses/waddamburo/dotnet"
        (cd "$appdir/usr/share/licenses" && rm -f "$out/licenses.zip" \
            && python3 -m zipfile -c "$out/licenses.zip" waddamburo/*)
        cat > "$appdir/waddamburo.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Waddamburo
Exec=Waddamburo.App
Icon=waddamburo
Categories=Game;
EOF
        cat > "$appdir/AppRun" <<'EOF'
#!/bin/sh
exec "$(dirname "$(readlink -f "$0")")/usr/bin/Waddamburo.App" "$@"
EOF
        chmod +x "$appdir/AppRun"
        ARCH=$arch APPIMAGE_EXTRACT_AND_RUN=1 "$tool" --no-appstream --runtime-file "$runtime" \
            "$appdir" "$out/Waddamburo-$arch.AppImage" >/dev/null
        echo "$out/Waddamburo-$arch.AppImage"
        echo "$out/licenses.zip"
        ;;
    macos)
        # On a Mac (Command Line Tools, cmake, ninja, pkg-config): an ad-hoc signed Waddamburo.app, zipped.
        # ponytail: no self-update on macOS yet (SelfUpdate has no installed file there); players download the zip.
        build=out/build/native/osx-arm64/macos-package
        native macos-package "$root/$build/stage/Release/lib" osx-arm64
        app=$out/Waddamburo.app
        rm -rf "$app"
        publish osx-arm64 "$root/$build/stage/Release/lib" "$app/Contents/MacOS"
        mkdir -p "$app/Contents/Resources"
        licenses $build "$app/Contents/Resources/licenses"
        runtime_licenses src/Waddamburo.App osx-arm64 "$app/Contents/Resources/licenses/dotnet"
        iconset=$out/waddamburo.iconset
        rm -rf "$iconset" && mkdir -p "$iconset"
        for size in 16 32 128 256 512; do
            sips -z $size $size eng/packaging/waddamburo.png --out "$iconset/icon_${size}x${size}.png" >/dev/null
            sips -z $((size * 2)) $((size * 2)) eng/packaging/waddamburo.png --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
        done
        iconutil -c icns "$iconset" -o "$app/Contents/Resources/waddamburo.icns" && rm -rf "$iconset"
        cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Waddamburo</string>
    <key>CFBundleDisplayName</key><string>Waddamburo</string>
    <key>CFBundleIdentifier</key><string>org.waddamburo.Waddamburo</string>
    <key>CFBundleExecutable</key><string>Waddamburo.App</string>
    <key>CFBundleIconFile</key><string>waddamburo</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>${version%%[-+]*}</string>
    <key>CFBundleVersion</key><string>$version</string>
    <key>LSMinimumSystemVersion</key><string>14.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.music-games</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
        # Ad-hoc: arm64 macOS runs only signed code; without a Developer ID, Gatekeeper asks once (right-click, Open).
        codesign --force --deep --sign - "$app"
        rm -f "$out/Waddamburo-macos-arm64.zip"
        ditto -c -k --keepParent "$app" "$out/Waddamburo-macos-arm64.zip"
        echo "$out/Waddamburo-macos-arm64.zip"
        ;;
    android)
        # Needs an Android SDK (ANDROID_HOME, default ~/Android/Sdk) with an NDK, JDK 17 or 21 (JAVA_HOME) and the
        # .NET android workload (docs/development/android.md).
        android_arm64_build=$root/out/build/native/android-arm64/android-package
        android_x64_build=$root/out/build/native/android-x64/android-x64-package
        native android-package "$android_arm64_build/stage/Release/lib" android-arm64
        native android-x64-package "$android_x64_build/stage/Release/lib" android-x64
        android_notices=$out/android-notices
        licenses "$android_arm64_build" "$android_notices" Waddamburo.Android
        mv "$android_notices/FFmpeg/configure-options.txt" "$android_notices/FFmpeg/configure-options-android-arm64.txt"
        cp "$android_x64_build/ffmpeg/prefix/licenses/FFmpeg/configure-options.txt" \
            "$android_notices/FFmpeg/configure-options-android-x64.txt"
        # Android installs an update only over a lower versionCode: X.Y.Z -> X*1000000 + Y*10000 + Z*100, a nightly
        # (newer than its X.Y.Z, older than the next) +50; development builds 1.
        code=1
        if [[ $version =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)(-nightly)?$ ]]; then
            code=$(( BASH_REMATCH[1] * 1000000 + BASH_REMATCH[2] * 10000 + BASH_REMATCH[3] * 100 + (${#BASH_REMATCH[4]} > 0 ? 50 : 0) ))
        fi
        # A nightly is an app of its own beside the stable one (as on desktop, where it keeps its own folder).
        app=(); [[ $version == *-nightly ]] && app=(-p:ApplicationId=org.waddamburo.game.nightly -p:ApplicationTitle="Waddamburo Nightly")
        # Releases are signed with the release key (every update must carry the same one, or Android refuses it):
        # WADDAMBURO_ANDROID_KEYSTORE (a .jks file), _KEYSTORE_PASSWORD, _KEY_ALIAS. Without them, the debug key.
        signing=()
        if [[ -n ${WADDAMBURO_ANDROID_KEYSTORE:-} ]]; then
            signing=(-p:AndroidKeyStore=true -p:AndroidSigningKeyStore="$WADDAMBURO_ANDROID_KEYSTORE"
                -p:AndroidSigningStorePass=env:WADDAMBURO_ANDROID_KEYSTORE_PASSWORD
                -p:AndroidSigningKeyPass=env:WADDAMBURO_ANDROID_KEYSTORE_PASSWORD
                -p:AndroidSigningKeyAlias="${WADDAMBURO_ANDROID_KEY_ALIAS:-waddamburo}")
        fi
        java=(); [[ -n ${JAVA_HOME:-} ]] && java=(-p:JavaSdkDirectory="$JAVA_HOME")
        android_sdk=(-p:AndroidSdkDirectory="${ANDROID_HOME:-$HOME/Android/Sdk}" "${java[@]}")
        rm -rf "$out/android"
        dotnet restore src/Waddamburo.Android -p:Configuration=Release "${android_sdk[@]}"
        for rid in android-arm64 android-x64; do
            runtime_licenses src/Waddamburo.Android "$rid" "$android_notices/dotnet" \
                -p:Configuration=Release "${android_sdk[@]}"
        done
        dotnet publish src/Waddamburo.Android -c Release -f net10.0-android --no-restore -o "$out/android" \
            "${android_sdk[@]}" \
            -p:WaddamburoAndroidNativeDir="$android_arm64_build/stage/Release/lib" \
            -p:WaddamburoAndroidX64NativeDir="$android_x64_build/stage/Release/lib" \
            -p:WaddamburoAndroidNoticesDir="$android_notices" \
            -p:Version="$version" -p:ApplicationDisplayVersion="${version%%-*}" -p:ApplicationVersion="$code" \
            "${app[@]}" "${signing[@]}"
        apk=$(ls "$out/android"/*-Signed.apk | head -1)
        cp "$apk" "$out/Waddamburo.apk"
        echo "$out/Waddamburo.apk"
        ;;
    switch)
        # Needs Docker or the devkitA64 image (platforms/switch/build.sh).
        WADDAMBURO_VERSION=$version platforms/switch/build.sh all
        switch=$root/out/switch
        source "$switch/ext/versions.env"
        notices=$out/switch-notices
        rm -rf "$notices" && mkdir -p "$notices/FFmpeg" "$notices/vgmstream" "$notices/dotnet"
        cp LICENSE NOTICE.md THIRD_PARTY_NOTICES.md eng/packaging/licenses/* "$notices/"
        cp platforms/switch/THIRD_PARTY_NOTICES.md "$notices/THIRD_PARTY_NOTICES-switch.md"
        cp native/texture/third_party/bc7enc/LICENSE "$notices/bc7enc.LICENSE.txt"
        cp "$switch/ext/ffmpeg-$FFMPEG"/COPYING.GPLv2 "$switch/ext/ffmpeg-$FFMPEG"/LICENSE.md "$notices/FFmpeg/"
        grep -o -- '--[a-z-]*\(=[^ ]*\)\?' platforms/switch/scripts/natives.sh | sort -u > "$notices/FFmpeg/configure-options-switch.txt"
        cp "$switch/ext/vgmstream-$VGMSTREAM/COPYING" "$notices/vgmstream/"
        cp "$switch"/bgfx/licenses/* "$notices/"
        runtime_pack=$(ls -d "${NUGET_PACKAGES:-$HOME/.nuget/packages}"/microsoft.netcore.app.runtime.nativeaot.linux-arm64/* | sort -V | tail -1)
        cp "$runtime_pack/LICENSE.TXT" "$runtime_pack/THIRD-PARTY-NOTICES.TXT" "$notices/dotnet/"
        nuget_inventory src/Waddamburo.App/packages.lock.json > "$notices/nuget-packages.txt"
        rm -f "$out/licenses-switch.zip" && (cd "$notices" && cmake -E tar cf "$out/licenses-switch.zip" --format=zip .)
        echo "$out/licenses-switch.zip"
        # GPL (nvtegra): the Switch build's FFmpeg source as built, the release tarball and devkitPro's patch; the rest of
        # the corresponding source is this repository at the release's tag.
        rm -f "$out/ffmpeg-source-switch.zip" && (cd "$root/out/downloads" && cmake -E tar cf "$out/ffmpeg-source-switch.zip" \
            --format=zip "ffmpeg-${FFMPEG%-nvtegra}.tar.xz" "ffmpeg-${FFMPEG%-nvtegra}-switch.patch")
        echo "$out/ffmpeg-source-switch.zip"
        echo "$out/Waddamburo.nro"
        ;;
    *)
        echo "error: unknown target: $target" >&2
        exit 2
        ;;
    esac
done
