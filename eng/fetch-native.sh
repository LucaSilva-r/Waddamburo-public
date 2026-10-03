#!/usr/bin/env bash
# Downloads a release's native libraries (FFmpeg, vgmstream, FreeType, bgfx; built by eng/package.sh)
# into the folder dotnet build takes them from (Directory.Build.props), so the game builds and runs
# from source with only the .NET SDK. Native code changed since that release? Build the
# linux-package preset instead (BUILDING.md).
# usage: eng/fetch-native.sh [vX.Y.Z]   (default: the latest release)
set -euo pipefail

cd "$(dirname "$0")/.."
releases=https://github.com/LucaSilva-r/Waddamburo-public/releases
if [[ ${1:-latest} == latest ]]; then url=$releases/latest/download/native-linux-x64.zip
else url=$releases/download/$1/native-linux-x64.zip; fi
dir=out/build/native/linux-x64/linux-package/stage/Release/lib
zip=out/downloads/native-linux-x64.zip

mkdir -p "$dir" out/downloads
curl -fSL -o "$zip" "$url"
python3 -m zipfile -e "$zip" "$dir"
echo "Native libraries in $dir; now: dotnet run --project src/Waddamburo.App -- /path/to/USRDIR"
