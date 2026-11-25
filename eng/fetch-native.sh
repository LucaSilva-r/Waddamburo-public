#!/usr/bin/env bash
# Downloads a release's native libraries (FFmpeg, vgmstream, FreeType, bgfx; built by eng/package.sh)
# into the folder dotnet build takes them from (Directory.Build.props), so the game builds and runs
# from source with only the .NET SDK. Native code changed since that release? Build the
# linux-package preset instead (BUILDING.md).
# usage: eng/fetch-native.sh [vX.Y.Z]   (default: the latest release)
set -euo pipefail

cd "$(dirname "$0")/.."
releases=https://github.com/LucaSilva-r/Waddamburo-public/releases
if [[ ${1:-latest} == latest ]]; then base=$releases/latest/download; else base=$releases/download/$1; fi
rid=$(if [[ $(uname -m) == aarch64 ]]; then echo linux-arm64; else echo linux-x64; fi)
dir=out/build/native/$rid/linux-package/stage/Release/lib
zip=out/downloads/native-$rid.zip
bundle=out/downloads/Waddamburo.sources-and-licenses.zip

mkdir -p "$dir" out/downloads
# The release keeps native-linux-x64.zip inside Waddamburo.sources-and-licenses.zip; up to v0.32.x it was on its own.
if curl -fSL -o "$bundle" "$base/Waddamburo.sources-and-licenses.zip"; then
    python3 -c 'import sys, zipfile; open(sys.argv[2], "wb").write(zipfile.ZipFile(sys.argv[1]).read(sys.argv[3]))' \
        "$bundle" "$zip" "native-$rid.zip"
else
    curl -fSL -o "$zip" "$base/native-$rid.zip"
fi
python3 -m zipfile -e "$zip" "$dir"
echo "Native libraries in $dir; now: dotnet run --project src/Waddamburo.App -- /path/to/USRDIR"
