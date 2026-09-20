#!/usr/bin/env bash

set -euo pipefail

configuration=Debug
build_managed=true
build_native=true
build_ffmpeg=false
build_nijiro_audio=false

usage() {
  cat <<'EOF'
Usage: ./eng/bootstrap.sh [options]

Build and test Waddamburo with the same commands and output layout used by CI.

Options:
  --configuration <Debug|Release|Sanitize>
  --managed-only       Skip the native build.
  --native-only        Skip the managed build.
  --with-ffmpeg        Build and audit the pinned minimal FFmpeg dependency.
  --with-nijiro-audio  Build local-only vgmstream/G.719 support; requires
                       WADDAMBURO_G719_SOURCE_DIR.
  -h, --help           Show this help.
EOF
}

while (($# > 0)); do
  case "$1" in
    --configuration)
      if (($# < 2)); then
        echo "error: --configuration requires a value" >&2
        exit 2
      fi
      configuration=$2
      shift 2
      ;;
    --managed-only)
      build_native=false
      shift
      ;;
    --native-only)
      build_managed=false
      shift
      ;;
    --with-ffmpeg)
      build_ffmpeg=true
      shift
      ;;
    --with-nijiro-audio)
      build_ffmpeg=true
      build_nijiro_audio=true
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "error: unknown option: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

case "$configuration" in
  Debug|Release|Sanitize) ;;
  *)
    echo "error: configuration must be Debug, Release, or Sanitize" >&2
    exit 2
    ;;
esac

if [[ "$build_managed" == false && "$build_native" == false ]]; then
  echo "error: --managed-only and --native-only cannot be combined" >&2
  exit 2
fi

if [[ "$build_ffmpeg" == true && "$build_native" == false ]]; then
  echo "error: --with-ffmpeg cannot be combined with --managed-only" >&2
  exit 2
fi

repository_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repository_root"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "error: required command '$1' was not found" >&2
    exit 1
  fi
}

if [[ "$(uname -s)" != Linux ]]; then
  echo "error: bootstrap.sh supports Linux; use bootstrap.ps1 on Windows" >&2
  exit 1
fi

case "$(uname -m)" in
  x86_64|amd64) ;;
  *)
    echo "error: Phase 2 supports only Linux x64, found $(uname -m)" >&2
    exit 1
    ;;
esac

if [[ "$build_managed" == true ]]; then
  require_command dotnet
  dotnet_version=$(dotnet --version)
  if [[ "$dotnet_version" != 10.* ]]; then
    echo "error: .NET SDK 10 is required, found $dotnet_version" >&2
    exit 1
  fi

  managed_configuration=$configuration
  if [[ "$configuration" == Sanitize ]]; then
    managed_configuration=Debug
  fi

  dotnet restore Waddamburo.slnx --locked-mode
  CI=true dotnet build Waddamburo.slnx \
    --configuration "$managed_configuration" --no-restore
  CI=true dotnet test Waddamburo.slnx \
    --configuration "$managed_configuration" --no-build --no-restore
fi

if [[ "$build_native" == true ]]; then
  require_command cmake
  require_command ninja
  require_command cc

  native_configuration=${configuration,,}
  native_preset="linux-$native_configuration"
  pushd native >/dev/null
  cmake --preset "$native_preset"
  cmake --build --preset "$native_preset"
  ctest --preset "$native_preset"

  if [[ "$build_ffmpeg" == true ]]; then
    require_command make
    cmake --preset linux-ffmpeg
    cmake --build --preset linux-ffmpeg
    cmake --preset linux-audio-pinned
    cmake --build --preset linux-audio-pinned
    ctest --preset linux-audio-pinned
  fi
  if [[ "$build_nijiro_audio" == true ]]; then
    if [[ -z "${WADDAMBURO_G719_SOURCE_DIR:-}" ]]; then
      echo "error: --with-nijiro-audio requires WADDAMBURO_G719_SOURCE_DIR" >&2
      exit 2
    fi
    cmake --preset linux-vgmstream-g719
    cmake --build --preset linux-vgmstream-g719
    cmake --preset linux-audio-complete
    cmake --build --preset linux-audio-complete
    ctest --preset linux-audio-complete
  fi
  popd >/dev/null
fi

echo "Bootstrap completed successfully ($configuration)."
