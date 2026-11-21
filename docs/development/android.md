# Android

`src/Waddamburo.Android` is the Android app: an `SDLActivity` (SDL3-CS) whose `Main` runs the same boot as the desktop
executable (`Waddamburo.App.Boot.Run`). Everything the game does is shared; the Android project only sets up storage,
the surface's lifecycle and the setup screen. The project is not part of `Waddamburo.slnx` (desktop CI has no Android
workload); build it on its own.

## Toolchain

- .NET's Android workload: `dotnet workload install android`.
- Android SDK (cmdline-tools, `platforms;android-36`, `build-tools;36.0.0`, `platform-tools`) and an NDK (r29 tested).
  The CMake toolchain wrapper finds the NDK through `ANDROID_NDK_HOME`, `ANDROID_NDK_ROOT`, or the newest one in
  `$ANDROID_HOME/ndk` (default `~/Android/Sdk/ndk`).
- JDK 17 or 21 (the Android build tools do not support newer ones).

## Build

```sh
# Native libraries for arm64-v8a and x86_64: bgfx, media, text, and texture
cd native
cmake --preset android-package
cmake --build --preset android-package
cmake --preset android-x64-package
cmake --build --preset android-x64-package
cd ..

# the APK (Release: CoreCLR, framework assemblies trimmed)
dotnet build src/Waddamburo.Android -c Release \
  -p:AndroidSdkDirectory=$HOME/Android/Sdk -p:JavaSdkDirectory=/path/to/jdk-21
# -> src/Waddamburo.Android/bin/Release/net10.0-android/org.waddamburo.game-Signed.apk (debug-signed)
```

The app runs on **CoreCLR** (`UseMonoRuntime=false`), the same runtime as desktop; experimental on Android in .NET 10.
Mono is not used: even fully AOT compiled it ran the Lumen interpreter about 25 times slower than desktop (song select
~30 fps against ~50 on a Snapdragon 439).

`Directory.Build.targets` makes every other project RID-agnostic: the Android build's per-RID pass expects its
references built without an RID. Desktop publishes (`dotnet publish -r linux-x64`) produce the same files as before.

## Releases

`eng/package.sh android` builds one `out/package/Waddamburo.apk` for ARM64 and x86_64 Android devices, including
x86_64 Chromebooks with Android app support. It also builds the native library archives `native-android-arm64.zip`
and `native-android-x64.zip`. The Release workflow attaches `Waddamburo.apk` to stable releases and
`Waddamburo-nightly.apk` to nightlies. The nightly app installs alongside the stable one as
`org.waddamburo.game.nightly` ("Waddamburo Nightly"). Android requires the versionCode to grow with each update.
For X.Y.Z, it is `X*1000000 + Y*10000 + Z*100` with 50 added for the nightly.

Every release must be signed with the same key, or Android refuses to install it over the previous one. The workflow
takes it from two repository secrets: `ANDROID_KEYSTORE` (the .jks file, base64) and `ANDROID_KEYSTORE_PASSWORD`
(alias `waddamburo`); without them the APK is debug-signed (a development build). Keep the keystore and its password
somewhere safe: a lost key means players have to uninstall to take the next release.

## Storage

No storage permission. The game data and libraries live in the app's own storage, which a PC sees over USB (MTP) as
`Android/data/org.waddamburo.game/files/`:

| Folder | Holds |
|---|---|
| `Green/` | Green's USRDIR contents (or the USRDIR folder itself) |
| `Custom Songs/` | TJA library (`Boot.TjaFolder`) |
| `Songs/` | songs other devices sent (`Sync/SongStore`: `songs.json` + `blobs/`) |
| `Arcade/`, `Wii/`, `Wii U/` | one subfolder per release, added to `stock_folders` (`Boot.StockFolders`) |

Settings, scores and caches go to the app's private files (`Boot.AppHome`). Uninstalling asks whether to keep the data
(`hasFragileUserData`).

Until `Green/` holds every file of Green's list (`GreenData`, file names from `eng/green-data-list.py`; sizes are not
compared, language packs change them), the setup screen shows the phone's name and a 4-digit pairing code
(`GreenSetup`, `Sync/PairingListener`, port 8380). Once Waddamburo on a PC pairs with it (Settings › Devices › Add a device), the
phone mirrors the PC's Green library from it (`SyncClient`, port 8381) over USB tethering (~25 MB/s on a Redmi 8) or
Wi-Fi (~3 MB/s), only what it lacks; a file takes its name only once complete, so a copy resumes. **Start anyway**
appears once the boot's essentials are in. A complete copy is remembered (`files/green-verified`). Or the player copies
USRDIR's contents into `Green/` over USB.

On a phone the setup opens on a welcome asking where Green is. **I downloaded it on this phone** explains the import
(no need to extract), then opens Android's file picker for one zip, 7z or rar (no storage permission), and
`DeviceImport`/`GreenImport` take Green out of it on an import page (bar, entry being written or skipped): any folder
layout (each entry's path is matched from its `data/` segment against the list; zip through System.IO.Compression,
7z and rar through SharpCompress), arcade update packages inside it (`DATA.BIN`, `DATA00010.BIN` and on,
`Formats/Packages/ArcadePackage`, streamed, applied after loose files by name). **It is on my PC** is the pairing page. Only listed
files are written: the release's (`S111_00.md5`) and those its updates add (`S111_01`-`_13`, `+` lines in the list,
optional). Each file is written as `.import` and moved in when whole; one already there at its size is skipped, so a
retry resumes. PC test: `Waddamburo --import-green <Green folder> <sources...>`.

Afterwards select the paired PC in Settings › Devices to mirror its libraries into `Received/` (`ReceivedLibraries`): see
[song-sync-plan.md](song-sync-plan.md). `Arcade/`, `Wii/`, `Wii U/` stay for releases copied by hand.
The PC prepares Nijiiro music as Ogg Opus and transfers the library's configured keys to app-private storage.
Android plays these converted libraries using its bundled decoder.

## Lifecycle

Android destroys the window's surface on its UI thread when the app leaves the foreground. `GameSurface` (SDL's surface
view) stops frame submission and waits for the frames in flight before that happens (a frame drawn into a destroyed
surface hung an Adreno GPU), and reopens rendering once a surface is back; bgfx's EGL context is patched
(`native/bgfx/android-surface.cmake`) to make its surface on the new window. SDL's `DID_ENTER_FOREGROUND` never reached
the event queue, so nothing relies on it. The window is landscape only (`SDL_HINT_ORIENTATIONS`).

## Diagnostics

Release builds are debug-signed, not debuggable (no `run-as`). Environment switches go through the launch intent:

```sh
adb shell am start -n org.waddamburo.game/crc64724ce03bbe90aa91.WaddamburoActivity --es env "'WADDAMBURO_TICK_STATS=1;WADDAMBURO_HITCH_TRACE=1'"
adb logcat | grep DOTNET
```

Only `WADDAMBURO_*` names are taken. Useful ones: `WADDAMBURO_TICK_STATS=1` (game logic time and allocation per tick),
`WADDAMBURO_HITCH_TRACE=1` (slow frames, GPU time on Vulkan), `WADDAMBURO_RENDERER=vulkan` (GLES is the default).
Development builds start with the performance overlay on (phones have no F5).

Some devices (MIUI) refuse `adb install`: push the APK to `/sdcard/Download` and install it from the phone.
