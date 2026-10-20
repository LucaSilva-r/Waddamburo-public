# Building and contributing to Waddamburo

[Back to the README](README.md)

This guide covers Windows and Linux builds, tests, release packages, and code or
translation contributions. To download and play Waddamburo, use the
[setup instructions in the README](README.md#getting-started).

Building the simulator and running its managed tests do not require original game
data. Playing a build requires your own legitimately obtained compatible game data;
the optional original font setup is explained in the README. Keep game data and
commercial fonts outside the source checkout.

## Get the source

Install Git, then clone the repository:

```sh
git clone https://github.com/LucaSilva-r/Waddamburo-public.git
cd Waddamburo-public
```

Run the commands below from this checkout unless a step changes the directory.

Waddamburo is a .NET 10 app plus four native libraries: `waddamburo_media` (FFmpeg and
vgmstream: music, sounds, movies), `waddamburo_text` (FreeType: song titles),
`waddamburo_texture` and `bgfx`. Without them the game runs silent or not at all.

There are two ways to get the native libraries:

- **Download them** from the latest release with `eng/fetch-native`. This avoids
  installing a native compiler. Use this unless you change code under `native/`.
- **Build them** with the `linux-package` / `windows-package` CMake preset. This needs a C
  toolchain and takes a while the first time.

Both put the libraries in `out/build/native/<rid>/<preset>/stage/Release/`, and from there
`dotnet build` / `dotnet run` copy them next to the app (`Directory.Build.props`).

## 1. Install the .NET SDK

`global.json` pins the SDK to **exactly 10.0.201** (`rollForward: disable`). With any other
SDK version every `dotnet` command fails with "A compatible .NET SDK was not found".
Install it next to any others:

```powershell
# Windows (PowerShell)
New-Item -ItemType Directory -Force out/tools | Out-Null
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile out/tools/dotnet-install.ps1
powershell -ExecutionPolicy Bypass -File out/tools/dotnet-install.ps1 -Version 10.0.201 -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
```

```sh
# Linux
mkdir -p out/tools
curl -fsSL https://dot.net/v1/dotnet-install.sh -o out/tools/dotnet-install.sh
bash out/tools/dotnet-install.sh --version 10.0.201 --install-dir "$HOME/.dotnet"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
```

These environment variables apply to the current terminal. Set them again in a new
terminal, or add them to your shell profile. Alternatively, use the 10.0.201 SDK
installer from <https://dotnet.microsoft.com/download/dotnet/10.0>.

Before continuing, `dotnet --version` in the repository folder must print `10.0.201`.

## 2. Get the native libraries and run

```powershell
# Windows (PowerShell)
powershell -ExecutionPolicy Bypass -File eng/fetch-native.ps1
dotnet run --project src/Waddamburo.App -c Release -- "C:\path\to\game\USRDIR"
```

```sh
# Linux (needs curl and python3)
./eng/fetch-native.sh
dotnet run --project src/Waddamburo.App -c Release -- "/path/to/game/USRDIR"
```

Replace the example path with your own game folder containing `data`. Visual Studio,
Rider or VS Code can open `Waddamburo.slnx` and run `Waddamburo.App` once the libraries
are in place; pass the same game-data path as a launch argument.

For a build without launching the game:

```sh
dotnet restore Waddamburo.slnx --locked-mode
dotnet build Waddamburo.slnx -c Release --no-restore
```

The downloaded libraries belong to the latest release (`-Release vX.Y.Z` / an `vX.Y.Z`
argument picks another). If the native code changed since then, the game reports that the
media library or the text rasterizer "has an incompatible ABI": build the libraries
yourself (below). Their licenses are in `licenses.zip` on the same release.

If the game says "The FFmpeg decoder backend is not built" or "built without FFmpeg", it
is using libraries without FFmpeg: run the fetch script (or the package preset), then
`dotnet build src/Waddamburo.App` again.

## 3. Building the native libraries yourself

### Linux

CMake must be 3.25 or newer (Ubuntu 22.04's is too old).

```sh
# Ubuntu / Debian
sudo apt install build-essential cmake ninja-build make pkg-config curl python3 \
    libx11-dev libgl-dev libwayland-dev
# Fedora
sudo dnf install gcc gcc-c++ cmake ninja-build make pkgconf curl python3 \
    libX11-devel mesa-libGL-devel wayland-devel

cd native
cmake --preset linux-package
cmake --build --preset linux-package
cd ..
```

### Windows (MSYS2)

The Windows libraries are built with mingw-w64, the same compiler as the releases, in
[MSYS2](https://www.msys2.org/). This release-library preset uses MinGW rather than MSVC.

1. Install MSYS2 (installer from msys2.org, or `winget install MSYS2.MSYS2`).
2. Open **MSYS2 MINGW64** from the Start menu (not UCRT64 or MSYS) and install the tools:

   ```sh
   pacman -S --needed make mingw-w64-x86_64-gcc mingw-w64-x86_64-cmake \
       mingw-w64-x86_64-ninja mingw-w64-x86_64-pkgconf
   ```

3. In the same MINGW64 shell, build in your checkout:

   ```sh
   cd /c/path/to/Waddamburo-public/native
   cmake --preset windows-package
   cmake --build --preset windows-package
   ```

   FFmpeg and bgfx are built from source; the first build takes about 15-30 minutes.
4. Back in PowerShell, return to the checkout root and run
   `dotnet run --project src/Waddamburo.App -c Release -- "C:\path\to\game\USRDIR"`.

Run the `cmake --build` line again after changing native code.

## Release files (`eng/package.sh`)

`eng/package.sh` builds what is published on the
[Releases](https://github.com/LucaSilva-r/Waddamburo-public/releases) page: a single
`Waddamburo.exe` / AppImage to drop into the game's `USRDIR`. It runs on Linux x64 (and
cross-compiles the Windows build there) or on Windows in MSYS2.

**Windows:** set up MSYS2 as in [Windows (MSYS2)](#windows-msys2), then use the MINGW64
shell. If you installed the SDK with the PowerShell script above, add its location
to this shell's path too:

```sh
cd /c/path/to/Waddamburo-public
export PATH="$(cygpath -u "$LOCALAPPDATA")/Microsoft/dotnet:$PATH"
dotnet --version            # must report 10.0.201
./eng/package.sh windows    # out/package/Waddamburo.exe and native-win-x64.zip
cp out/package/Waddamburo.exe /c/path/to/game/USRDIR/
```

**Linux:**

```sh
./eng/package.sh            # both
./eng/package.sh linux      # Waddamburo-x86_64.AppImage, licenses.zip, native-linux-x64.zip
./eng/package.sh windows    # Waddamburo.exe, native-win-x64.zip
```

The results are in `out/package/`. On Linux it needs the [Linux tools](#linux) and the .NET
SDK, and for `windows` also the mingw-w64 cross compiler (`mingw-w64` on Ubuntu 24.04 or
newer, `mingw64-gcc mingw64-gcc-c++` on Fedora). `licenses.zip` is only made by the Linux
target.

What it does:

1. Builds the `linux-package` / `windows-package` native preset and zips its libraries
   (`native-<rid>.zip`, what `eng/fetch-native` downloads).
2. Publishes `Waddamburo.App` self-contained with those libraries. The Windows build is one
   `.exe` that unpacks its native libraries on first run; the Linux build is packed into an
   AppImage with a pinned, checksum-verified `appimagetool`.
3. The Linux target collects the required licenses and notices into `licenses.zip`.
   Include these notices when distributing the binaries.

The version comes from a `vX.Y.Z` git tag on the current commit, or from
`WADDAMBURO_VERSION`. Anything else builds `0.0.0-dev`, which never updates itself (so a
self-built exe stays as built until you replace it). Pushing
a `vX.Y.Z` tag runs the script on GitHub Actions (`.github/workflows/release.yml`) and
drafts a release with all these files. See
[native builds](docs/development/native-builds.md#release-packages) for details.

Song Select's Classic/Vertical and Linear/Curved choices ship with the normal app.
The portable `Waddamburo.MenuLayouts` generator derives and caches the vertical movie
from the user's original assets at runtime; no editor or prebuilt private overlay is
needed in a release.

## Tests (`eng/bootstrap`)

### Managed tests

These tests use synthetic fixtures and run without original game data or downloaded
native libraries. From the checkout root:

```sh
dotnet restore Waddamburo.slnx --locked-mode
dotnet build Waddamburo.slnx -c Release --no-restore -p:CI=true
dotnet test Waddamburo.slnx -c Release --no-build --no-restore
```

These commands work on Windows and Linux. `CI=true` enables warnings as errors
and locked dependency restore for the build.

### Managed and native tests

The bootstrap scripts restore, build and test the managed solution, then build and test the
native libraries with the `*-debug` preset:

```sh
./eng/bootstrap.sh --configuration Debug          # Linux
```

```powershell
./eng/bootstrap.ps1 -Configuration Debug          # Windows, Visual Studio x64 developer PowerShell
```

The Windows script needs the Visual Studio C/C++ tools, CMake and Ninja (all included with
Visual Studio). The debug native libraries are for the tests only: they have no FFmpeg,
vgmstream, FreeType or bgfx, so the game cannot use them. Neither can it use
`-WithFFmpeg` on Windows (an untested MSVC recipe). To play, use step 2 or 3 above.

More detail: [native builds](docs/development/native-builds.md),
[FFmpeg](docs/development/ffmpeg.md), [diagnostics](docs/development/diagnostics.md).

## Contributing

Keep changes focused and describe what changes for the player and how you checked
it. Run the managed checks above for code changes; also run the relevant native
tests when changing `native/`. UI and gameplay changes need a manual check with
your own data. Keep that data, screenshots, caches and accounts out of commits.

### Contributing a translation

The simulator's own text lives in `src/Waddamburo.App/Languages/<code>.json`, one
file per language:

1. Copy `en.json` to the new language's code and translate the values. Keep the
   `{0}`-style placeholders.
2. Rebuild. The language appears in **Settings → Language**.

`LanguageFileTests` checks that no keys or placeholders are missing.

### Project rules

Waddamburo is an independent simulator. Do not add original game code, assets,
commercial fonts, extracted data or credentials. Tests use synthetic data.
See the [evidence policy](docs/legal/evidence-policy.md),
[provenance](docs/legal/provenance.md) and
[release content policy](docs/legal/release-content-policy.md).

### Further reading

- [Native builds](docs/development/native-builds.md)
- [Diagnostics and developer runs](docs/development/diagnostics.md)
- [App flow](docs/development/app-flow.md)
- [Development documentation](docs/development)
