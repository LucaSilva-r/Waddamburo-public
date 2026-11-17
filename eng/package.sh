#!/usr/bin/env bash
# Builds the release artifacts on a Linux x64 host:
#   out/package/Waddamburo.exe              one self-contained Windows file (native libraries
#                                            self-extract on first run)
#   out/package/Waddamburo-x86_64.AppImage  the Linux build
#   out/package/licenses.zip                every license and notice both releases need
#   out/package/Waddamburo.apk              the Android app (target android: needs the Android SDK, NDK and JDK)
#   out/package/native-{win,linux}-x64.zip  the native libraries alone, for building from source
#                                            without a C toolchain (eng/fetch-native.sh / .ps1)
# Both are dropped into the game's USRDIR and find the game next to themselves.
# Needs: .NET 10 SDK, CMake, Ninja, make, gcc, mingw-w64 gcc (x86_64-w64-mingw32-gcc).
# On Windows it runs in an MSYS2 MINGW64 shell and builds the Windows release only (BUILDING.md).
#
# usage: eng/package.sh [linux|windows|android]   (default: linux and windows; windows on Windows)
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

fetch() { # url file sha256
    [[ -f $2 ]] || curl -fsSL -o "$2" "$1"
    echo "$3  $2" | sha256sum --check --status || { echo "error: checksum mismatch: $2" >&2; exit 1; }
}

native() { # preset stage-dir rid: build the preset, zip its libraries for eng/fetch-native
    (cd native && cmake --preset "$1" && cmake --build --preset "$1")
    rm -f "$out/native-$3.zip"
    (cd "$2" && cmake -E tar cf "$out/native-$3.zip" --format=zip $(ls waddamburo_*.dll libwaddamburo_*.so bgfx*.dll libbgfx*.so 2>/dev/null))
    echo "$out/native-$3.zip"
}

licenses() { # native-build-dir out-dir: every notice the releases need, beside Waddamburo's own
    local dir=$2
    rm -rf "$dir" && mkdir -p "$dir"
    cp LICENSE NOTICE.md THIRD_PARTY_NOTICES.md eng/packaging/licenses/* "$dir/"
    cp src/Waddamburo.App/Upscale/Real-ESRGAN.LICENSE.txt "$dir/"
    cp native/texture/third_party/bc7enc/LICENSE "$dir/bc7enc.LICENSE.txt"
    cp -r "$1"/ffmpeg/prefix/licenses/* "$1"/vgmstream/licenses/* "$1"/freetype/prefix/licenses/* "$1"/bgfx/prefix/licenses/* "$dir/"
    local dotnet_root
    dotnet_root=$(dirname "$(readlink -f "$(command -v dotnet)")")
    mkdir -p "$dir/dotnet" && cp "$dotnet_root/LICENSE.txt" "$dotnet_root/ThirdPartyNotices.txt" "$dir/dotnet/"
    # NuGet packages carry a license expression, not a file: list each one the app ships.
    python3 - src/Waddamburo.App/packages.lock.json "${NUGET_PACKAGES:-$HOME/.nuget/packages}" > "$dir/nuget-packages.txt" <<'PY'
import json, re, sys
from pathlib import Path
lock, cache = json.load(open(sys.argv[1])), Path(sys.argv[2])
packages = {(name, entry["resolved"]) for target in lock["dependencies"].values()
            for name, entry in target.items() if entry.get("type") != "Project"}
print("NuGet packages linked into Waddamburo (id, version, license):")
for name, version in sorted(packages, key=lambda p: p[0].lower()):
    spec = next((cache / name.lower() / version).glob("*.nuspec"), None)
    text = spec.read_text(encoding="utf-8-sig") if spec else ""
    license = re.search(r"<license[^>]*>([^<]+)</license>", text) or re.search(r"<licenseUrl>([^<]+)</licenseUrl>", text)
    print(f"{name} {version}: {license.group(1) if license else 'see https://www.nuget.org/packages/' + name}")
PY
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
    windows)
        native windows-package "$root/out/build/native/win-x64/windows-package/stage/Release/bin" win-x64
        # ImGui.NET supplies cimgui.dll as a NuGet runtime asset. VerifyImGuiBundle in
        # the App project checks it enters the same self-extracting bundle as SDL/bgfx.
        publish win-x64 "$root/out/build/native/win-x64/windows-package/stage/Release/bin" "$out/win-x64" \
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
            -p:EnableCompressionInSingleFile=true
        cp "$out/win-x64/Waddamburo.App.exe" "$out/Waddamburo.exe"
        echo "$out/Waddamburo.exe"
        ;;
    linux)
        native linux-package "$root/out/build/native/linux-x64/linux-package/stage/Release/lib" linux-x64
        tool=out/downloads/appimagetool-$appimagetool_version-x86_64.AppImage
        runtime=out/downloads/appimage-runtime-$runtime_version-x86_64
        fetch "https://github.com/AppImage/appimagetool/releases/download/$appimagetool_version/appimagetool-x86_64.AppImage" \
            "$tool" "$appimagetool_sha256"
        fetch "https://github.com/AppImage/type2-runtime/releases/download/$runtime_version/runtime-x86_64" \
            "$runtime" "$runtime_sha256"
        chmod +x "$tool"
        # ponytail: plain self-contained publish inside the AppDir; the AppImage is already one file.
        appdir=$out/Waddamburo.AppDir
        publish linux-x64 "$root/out/build/native/linux-x64/linux-package/stage/Release/lib" "$appdir/usr/bin"
        [[ -f "$appdir/usr/bin/libcimgui.so" ]] || { echo "error: missing ImGui native runtime" >&2; exit 1; }
        cp eng/packaging/waddamburo.png "$appdir/waddamburo.png"
        licenses out/build/native/linux-x64/linux-package "$appdir/usr/share/licenses/waddamburo"
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
        ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$tool" --no-appstream --runtime-file "$runtime" \
            "$appdir" "$out/Waddamburo-x86_64.AppImage" >/dev/null
        echo "$out/Waddamburo-x86_64.AppImage"
        echo "$out/licenses.zip"
        ;;
    android)
        # Needs an Android SDK (ANDROID_HOME, default ~/Android/Sdk) with an NDK, JDK 17 or 21 (JAVA_HOME) and the
        # .NET android workload (docs/development/android.md).
        native android-package "$root/out/build/native/android-arm64/android-package/stage/Release/lib" android-arm64
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
        rm -rf "$out/android"
        dotnet publish src/Waddamburo.Android -c Release -f net10.0-android -o "$out/android" \
            -p:AndroidSdkDirectory="${ANDROID_HOME:-$HOME/Android/Sdk}" "${java[@]}" \
            -p:WaddamburoAndroidNativeDir="$root/out/build/native/android-arm64/android-package/stage/Release/lib" \
            -p:Version="$version" -p:ApplicationDisplayVersion="${version%%-*}" -p:ApplicationVersion="$code" \
            "${app[@]}" "${signing[@]}"
        apk=$(ls "$out/android"/*-Signed.apk | head -1)
        cp "$apk" "$out/Waddamburo.apk"
        echo "$out/Waddamburo.apk"
        ;;
    *)
        echo "error: unknown target: $target" >&2
        exit 2
        ;;
    esac
done
