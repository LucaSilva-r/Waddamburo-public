# Waddamburo

Waddamburo is an independent, MIT-licensed C# game engine for compatible Taiko
presentation and rhythm-game data.

This is a bring-your-own-assets project. The repository and its releases do not
include a game executable, firmware, original game assets, commercial fonts,
extracted media, or generated decompilations. Users must supply files they are
entitled to use from their own local installation.

## Status

The public implementation started from a clean repository. Its .NET 10 solution,
project boundaries, central build policy, dependency pins, lock files, native build
presets, checksum-pinned minimal FFmpeg build, versioned media C ABI, architecture
tests, bounded asset parsers, semantic LMB definitions, a small display-list runtime,
and an SDL_GPU textured-quad path are in place; the decoder backend and game
functionality are not implemented yet. New product code is written here without
copying the private proof of concept or importing executable-derived source.

The initial targets are Linux x64 and Windows x64 on .NET 10. The intended product
is a local game plus reusable libraries for asset formats, animation, rendering,
audio, input, catalogues, and persistence.

## Project boundaries

- Product code may use public specifications, independently recorded asset-format
  facts, and black-box behavioral requirements.
- Runtime addresses, lifted code, original content, and content-reproducing research
  artifacts are prohibited.
- Tests committed here use synthetic fixtures.
- Local inputs and generated observations remain outside this repository.

See the [evidence policy](docs/legal/evidence-policy.md),
[provenance policy](docs/legal/provenance.md), and
[release-content policy](docs/legal/release-content-policy.md).

Build and test the current scaffold with the developer bootstrap:

```sh
./eng/bootstrap.sh --configuration Debug
```

On Windows x64, run `./eng/bootstrap.ps1 -Configuration Debug` from a Visual
Studio x64 developer PowerShell.

Run the current SDL_GPU visual smoke test until the window is closed:

```sh
dotnet run --project src/Waddamburo.App
```

For a bounded automated smoke test, pass `--frames=N`. Use `--ticks=N` to stop on
an exact authored 60 Hz simulation tick; display frames and simulation ticks are
independent. With no asset options the app renders a synthetic nearest-sampled
checkerboard. `--window-size=WIDTHxHEIGHT` selects a diagnostic startup size; Lumen
frames preserve their logical-stage aspect ratio in the drawable pixel surface.

Capture the final bounded frame directly from the SDL_GPU swapchain with
`--screenshot=/path/to/frame.png` (or `.bmp`). If neither `--frames` nor `--ticks` is
present, a screenshot run renders one frame and exits. With `--ticks`, it captures
the exact requested simulation state.

The asset-backed vertical slice can open frame zero of a user-supplied DDP movie
without copying that archive into the repository:

```sh
dotnet run --project src/Waddamburo.App -- \
  --archive=/path/to/archive.ddp --movie=movie_name \
  --seek-frame=30 \
  --screenshot=/tmp/movie.png
```

This validates DDP/LMB/NUT structures, decodes and uploads textures, builds the
initial Lumen display list, advances it at 60 Hz, and presents immutable render
snapshots. `--seek-frame=N` restores a single movie through its nearest F105
seek-state snapshot and ordinary-frame replay. Deferred AVM actions, special blend
modes, and native fill surfaces are diagnosed explicitly; this is not yet a full
compatibility viewer.

The diagnostic viewer selects the native-game script branch through an explicit
per-player host binding and prints any authored `ExternalInterface` callback names
registered by the movie. It does not silently implement the corresponding game
services; missing native methods remain structured runtime diagnostics.

Live SDL keyboard state is delivered to each Lumen tick through Flash-compatible
key codes. Letters, digits, arrow keys, Enter, Escape, Space, and Backspace are
supported; focus loss clears every held key. The Taiko keyboard layout is P1
`D`/`F`/`J`/`K` and P2 `Z`/`X`/`C`/`V`. Either center (`don`) confirms. The left
and right rims (`ka`) navigate in their corresponding direction.

Bounded probes can add one-tick physical-key pulses with repeatable
`--press=KEY@TICK[,KEY@TICK...]` options. Tick numbers are one-based, and scripted
keys are combined with any live keys held during that tick.

For a deterministic single-movie probe, invoke registered callbacks after optional
seek and before ticking with repeatable `--invoke=` options. Arguments are separated
by `|` and explicitly typed as `b:true`, `n:1.5`, `s:text`, `null`, or `undefined`:

```sh
dotnet run --project src/Waddamburo.App -- \
  --archive=/path/to/archive.ddp --movie=movie_name \
  '--invoke=SetPlayer|n:1' --press=F@30,K@120 \
  --ticks=180 --window-size=1280x720 --screenshot=/tmp/callback.png
```

Screenshot runs use the requested dimensions as fixed framebuffer pixels, disabling
interactive resize and display-density scaling for byte-repeatable local probes.

An unbounded single-movie run also starts the interactive Lumen debugger. It prints
exported callback signatures and the first nine root labels. Press keyboard `1`–`9`
to jump to the corresponding labelled state, or enter commands in the launching
terminal:

```text
callbacks
labels
state 3
invoke SetPlayer|n:0|b:true|b:false|b:false
```

Callback values use the same typed syntax as `--invoke`. These controls are a
diagnostic surface and can deliberately bypass normal game flow.

Compose several independently loaded Lumen movies on the 1280x720 stage with a
scene description and a user-owned asset root:

```sh
dotnet run --project src/Waddamburo.App -- \
  --scene=/path/to/scene.txt \
  --asset-root=/path/to/lumendata/packed \
  --ticks=30 \
  --screenshot=/tmp/scene.png
```

Scene lines are ordered bottom to top and use the bounded format below. Archive
paths must be relative to `--asset-root`; blank lines and `#` comments are allowed.

```text
archive/packeddata.ddp movie/movie.lm
archive/packeddata.ddp movie/movie.lm x y
archive/packeddata.ddp movie/movie.lm x y scale
```

Each child has an independent display-list player and texture namespace. This
static composition slice does not yet implement script-driven `MovieClipLoader`.

The game-flow diagnostic can exercise the configured Green Entry-to-Song-Select
handoff using user-owned archives below one explicit asset root:

```sh
dotnet run --project src/Waddamburo.App -- \
  --entry-song-select \
  --asset-root=/path/to/lumendata/packed \
  --tja-root=/path/to/TJA \
  --font=/path/to/user-owned-font.ttf \
  --play-jingle=/path/to/user-owned-entry-jingle.nub \
  --press=F@30,F@240,F@380,K@700,D@850,F@1000,K@1200 \
  --ticks=1500 \
  --window-size=1280x720 \
  --screenshot=/tmp/entry-to-song-select.png
```

This initializes Entry through its exported callbacks, receives its authored scene
request, resolves that request in the app-owned scene catalog, loads a fresh Song
Select movie and host, scans the read-only TJA provider, opens a category, scrolls
away from and back to its return card, closes it, and scrolls the rebuilt category
carousel. It also renders requested song titles with the user-supplied font. Build
the optional native text adapter first;
the font, TJA files, audio, Lumen archives, and framebuffer output are never copied
into the repository. The relative archive/movie IDs and numeric request mapping are
composition data; neither the AVM runtime nor generic scene loader contains
Entry- or Song-Select-specific behavior.

`--play-jingle` is optional. It decodes a bounded short clip and plays it through the
menu-sound mixer bus; no referenced audio is copied into the repository.

Native build policy and preset commands are documented in
[docs/development/native-builds.md](docs/development/native-builds.md).
The source-neutral song/category catalog and provider contract are documented in
[docs/development/song-catalog.md](docs/development/song-catalog.md).
Binary parser limits and failure behavior are documented in
[docs/development/parser-safety.md](docs/development/parser-safety.md).
The first format implementation, the Green-profile DDP archive index, is described
in [docs/development/ddp-archives.md](docs/development/ddp-archives.md).
NTP3 texture-pack validation and reference BC decoding are described in
[docs/development/nut-textures.md](docs/development/nut-textures.md).
The lossless LMB container boundary is documented in
[docs/development/lmb-records.md](docs/development/lmb-records.md).
The initial display-list runtime is documented in
[docs/development/lumen-runtime.md](docs/development/lumen-runtime.md).
The current SDL and SDL_GPU lifecycle is documented in
[docs/development/sdl-gpu.md](docs/development/sdl-gpu.md).

## Legal

Waddamburo is not affiliated with, authorized by, sponsored by, or endorsed by
Bandai Namco Entertainment Inc. or its affiliates. “Taiko no Tatsujin” and related
names and marks are the property of their respective owners and are referenced only
to describe compatibility.

Waddamburo source is licensed under the [MIT License](LICENSE). Dependencies retain
their own licenses and will be recorded in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
