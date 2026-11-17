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
# arm64 native libraries: bgfx (GLES 3 + Vulkan), media (FFmpeg, vgmstream), text (FreeType), texture
cd native
cmake --preset android-package
cmake --build --preset android-package
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

`eng/package.sh android` builds `out/package/Waddamburo.apk` (and `native-android-arm64.zip`); the Release workflow
runs it beside Windows and Linux and attaches the APK to the release, `Waddamburo-nightly.apk` to a nightly. A nightly is
an app of its own (`org.waddamburo.game.nightly`, "Waddamburo Nightly") beside the stable one. The versionCode, which
Android requires to grow with each update, is `X*1000000 + Y*10000 + Z*100` for X.Y.Z, +50 for its nightly.

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
compared, language packs change them), the setup screen shows the phone's name and a 4-digit code: Waddamburo on a PC
sends Green to it over USB tethering (~25 MB/s on a Redmi 8) or Wi-Fi (~3 MB/s) through the receiver
(`Sync/PhoneReceiver`, port 8380, UDP discovery on the same port), only what the phone lacks; a file takes its name only
once complete, so a copy resumes. **Start anyway** appears once the boot's essentials are in. A complete copy is
remembered (`files/green-verified`). Or the player copies USRDIR's contents into `Green/` over USB.

Songs travel the same way, as rows and content-addressed blobs in `Songs/` (`Boot.ReceivedSongsFolder`): see
[song-sync-plan.md](song-sync-plan.md). `Arcade/`, `Wii/`, `Wii U/` stay for releases copied by hand.

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
