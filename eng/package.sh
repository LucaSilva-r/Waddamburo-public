#!/usr/bin/env bash
# Builds the release artifacts on a Linux x64 host:
#   out/package/Waddamburo.exe              one self-contained Windows file (native libraries
#                                            self-extract on first run)
#   out/package/Waddamburo-x86_64.AppImage  the Linux build
# Both are dropped into the game's USRDIR and find the game next to themselves.
# Needs: .NET 10 SDK, CMake, Ninja, make, gcc, mingw-w64 gcc (x86_64-w64-mingw32-gcc).
#
# usage: eng/package.sh [linux|windows]   (default: both)
set -euo pipefail

cd "$(dirname "$0")/.."
root=$PWD
out=$root/out/package
targets=${1:-linux windows}

appimagetool_version=1.9.1
appimagetool_sha256=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0
runtime_version=20251108
runtime_sha256=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d

fetch() { # url file sha256
    [[ -f $2 ]] || curl -fsSL -o "$2" "$1"
    echo "$3  $2" | sha256sum --check --status || { echo "error: checksum mismatch: $2" >&2; exit 1; }
}

native() { # preset
    (cd native && cmake --preset "$1" >/dev/null && cmake --build --preset "$1" >/dev/null)
}

publish() { # rid native-dir out-dir extra-args...
    local rid=$1 native_dir=$2 dir=$3; shift 3
    rm -rf "$dir"
    dotnet publish src/Waddamburo.App -c Release -r "$rid" --self-contained \
        -p:WaddamburoNativeDir="$native_dir" -p:DebugType=none -p:GenerateDocumentationFile=false \
        -o "$dir" "$@" >/dev/null
}

mkdir -p "$out" out/downloads
for target in $targets; do
    case $target in
    windows)
        native windows-package
        publish win-x64 "$root/out/build/native/win-x64/windows-package/stage/Release/bin" "$out/win-x64" \
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
            -p:EnableCompressionInSingleFile=true
        cp "$out/win-x64/Waddamburo.App.exe" "$out/Waddamburo.exe"
        echo "$out/Waddamburo.exe"
        ;;
    linux)
        native linux-package
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
        cp eng/packaging/waddamburo.png "$appdir/waddamburo.png"
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
        ;;
    *)
        echo "error: unknown target: $target" >&2
        exit 2
        ;;
    esac
done
