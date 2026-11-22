# Diagnostics and developer runs

Command-line options for probing single movies, scenes and flows from source. Normal play
needs none of this: see the
[README](../../README.md).

Browse movies and audio from a user-supplied `USRDIR` (or any asset folder):

```sh
dotnet run --project src/Waddamburo.App -- --preview=/path/to/USRDIR
```

The preview scans DDP archives, NUB sound banks, and common audio files under the
selected folder and displays an expandable folder tree. Empty folders are omitted.
A bare `--preview` uses the normal game-data directory;
`--preview /path/to/USRDIR` also accepts it positionally. Click a folder or archive
(or press Enter) to expand/collapse it. Archives reveal their movies as children;
Selecting a movie or audio file with the keyboard starts playback automatically;
clicking a leaf row also starts it. Rows show each item's name, with the
selected item's relative path above the preview.

Up/Down selects rows; Right expands or enters a branch; Left/Backspace collapses
it or selects its parent. PageUp/PageDown moves a page. Mouse wheel scrolls through
rows without changing the selection; drag the scrollbar to move through a long
tree. Over the preview pane, the wheel steps movie frames or audio-bank cues. Escape closes the preview.

Space or the Pause button pauses/resumes playback; R or Replay restarts it. For movies,
click or drag the timeline to seek to an authored root frame, or use D/K and the frame
buttons to step backward/forward. Seeking pauses playback; Space resumes it. Dragging captures the mouse until
release and also ends on focus loss. The
viewer uses the same diagnostic host binding as `--archive`/`--movie`, so movies that
need game services may require the existing callback debugger for further inspection.

For NUB banks, D/K or the cue buttons select the previous/next cue. Type a
zero-based cue number and press Enter to play it directly (decoder stream = cue + 1).
Cues are decoded on demand, with buffered streaming playback; an unavailable cue or
unsupported file produces an on-screen error. Normal audio files play their
default stream.

`--preview` also accepts a single DDP, NUB, or audio file. The existing
`--probe-sound-bank=<file-or-folder>` opens this browser as well. `--frames`, `--ticks`,
`--window-size`, `--press`, and `--screenshot` are available for bounded preview checks.

An assembled gameplay preview requires a scene preset and gameplay state. The
existing `--scene` viewer composes static movie layers, with independent timelines
and the diagnostic host binding. For a complete enso preview, a future scene mode
should reuse `GameplaySceneComposition` for the selected skin, player configuration,
layout, and depth order, and `TaikoGameplayPresentation` for chart-driven notes,
gauges, score, Go-Go effects, and Don presentation. A synthetic idle/demo chart can
initialize a scene without choosing a song; a song preview can use a user-owned chart.
The scene scrubber should track elapsed song time and restore/replay gameplay events
with fixed skin choices and inputs, preserving independent movie timelines and
callbacks. This assembled scene mode is not implemented in the file browser yet.

Run the synthetic renderer smoke test until the window is closed:

```sh
dotnet run --project src/Waddamburo.App -- --smoke
```

For a bounded automated smoke test, pass `--frames=N`. Use `--ticks=N` to stop on
an exact authored 60 Hz simulation tick; display frames and simulation ticks are
independent. `--smoke` renders a synthetic nearest-sampled checkerboard without game
assets. `--window-size=WIDTHxHEIGHT` selects a diagnostic startup size; Lumen
frames preserve their logical-stage aspect ratio in the drawable pixel surface.

Capture the final bounded frame from the back buffer with
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
  --don-root=/path/to/don3d \
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

Release builds are one file per platform, `Waddamburo.exe` (Windows x64) and
`Waddamburo-x86_64.AppImage` (Linux x64), built by `eng/package.sh`. Drop the file
into the game's `USRDIR` and run it; nothing else needs installing. Nijiiro audio
(G.719) additionally needs [vgmstream-cli](https://github.com/vgmstream/vgmstream/releases)
next to it (or in a `vgmstream` folder beside it), which Waddamburo cannot ship.
Releases update themselves: at start they check GitHub for a newer release and, if
there is one, download it on an update screen and restart into it (`auto_update = false`
in `waddamburo/config.cfg` turns this off; without a connection the game just starts). The Windows release runs without a console window;
`console = true` shows the log. F11 switches between fullscreen and a window, and
`fullscreen = true` starts fullscreen.

For normal play from source, supply only the game's `USRDIR` directory. It resolves
`data/lumendata/packed`, the game's own songs under `data`, the optional
`custom_songs` TJA library, and `data/sound` from that root and boots
like the cabinet: the startup notice and logos, then the attract loop (logo, title,
caution screen, then one of the `data/movie/attract_cm_###.pam` commercials in turn). In free play, hit a drum key (D/F/J/K) during the attract loop to reach player Entry. The drum pads can be put on other keys (several per pad), controller buttons or MIDI notes in the Settings menu (Player 1/2 Controls) or in `config.cfg`; see [input](input.md). `fast_song_scroll` (Settings: Fast Song Scrolling) lets Song Select's list keep up with quick rim hits and the mouse wheel and skip ten songs with Page Up/Down or three quick rim hits. For a drum that sends double hits, `drum_debounce_ms` (Settings: Drum Debounce, off by default) makes every pad ignore a hit that soon after its last one; the menu shows the resulting roll limit.
Waddamburo keeps its own files together in a `waddamburo` folder in that root: `config.cfg`,
`accounts.json`, `scores.db` and `avatars`, plus `cache/upscaled` (upscaled textures) and
`cache/vgmstream` (decoded Nijiiro audio); files from older versions move there on first start.
Cabinet settings live in `waddamburo/config.cfg` (written with defaults on first run):
`free_play`, `credits_per_coin`, `credits_1p`, `credits_2p`, `songs_per_session`,
`fullscreen`, `console`, `auto_update` and `renderer` (`auto`, or `vulkan`/`gles`/`opengl`/`d3d11`/`metal` to force a GPU backend). Scores go to `server` (default `https://taikonline.com`; `server =` left empty plays offline). The file records its `config_version`; a
newer build appends the settings it added, with their defaults, keeping the user's edits. In coin
mode the coin button (F2, `button_coin`) inserts a coin and the service button (F3, `button_service`) gives a
service credit in place of the coins inserted; the first credit opens player Entry, and joining pays the
credits. At home Space skips the startup screens (a cabinet waits for them). In arcade mode Escape is the test
switch: the service menu (`Home/ServiceMenu.cs`) stops the game, clears its state and the credits, holds the
settings, and EXIT starts the attract again; its BOOKKEEPING counts live in `waddamburo/bookkeeping.cfg`.

The window opens first and shows the startup's check screen (`Flow/BootChecks.cs`, the service menu's look):
one line per part as it loads (`_` blinking, then OK or NG), the song library scanned on a background thread.
A part that throws stops the game on an error screen (its number and name, the message, the crash log's path)
until the window is closed or Escape pressed; the exit code is then 1. `WADDAMBURO_BOOT_FAIL=<check>` (e.g.
`MOVIES`, `SONG LIBRARY`) fails that check on purpose, to see the screen. The same screen (`Flow/CrashScreen.cs`)
takes the window when the game crashes while it runs (code 99-1; `WADDAMBURO_CRASH_AT=<tick>` crashes on purpose),
and opens a window of its own for an error before the game's (00-1: the game folder, its settings), drawn with the
bundled font from the user's app data. Scripted runs (a screenshot, a tick or frame limit) fail as before instead.

Every session also writes its whole log, timestamped, to `waddamburo/logs/latest.log` (the one before is kept as
`previous.log`): what to ask a player for when something went wrong without a crash, such as an online match that
dropped (the online lines name the route, direct or relayed, never an address). A second game on the same folder
leaves the file to the first.

F4 toggles inspect mode: a left click then prints every scene quad under the pointer, topmost
first, with its movie, runtime texture index (or native surface) and clip chain (character ids and
instance names up to `_level0`). Texture indices are the runtime ones, which can differ from an
offline texture dump's order. The click maps the window to the stage directly, so a letterboxed
window is off by the bars.

```sh
dotnet run --project src/Waddamburo.App -- "/path/to/game/USRDIR"
```

The equivalent explicit form is `--game-data=/path/to/game/USRDIR`. Without either, the
executable's own directory is used when it holds the game (a release dropped into
`USRDIR`), otherwise the working directory. A user-owned
TrueType/OpenType font placed in `data/font` is selected automatically, with
`font.ttf`, `font.otf`, and `font.ttc` preferred in that order. Otherwise the bundled
Mochiy Pop P One is used; the bundled Noto Sans CJK JP Black subset draws any character
the chosen font lacks. `--font=/path/to/font` remains the
highest-priority override. All granular content options below are retained for
diagnostic runs.

To test a scene without playing through the preceding screens, use
`--start-scene=` with the normal game-data argument: `boot`, `attract` (skips the startup notice), `entry`,
`song-select`, `ai-song-select`, `dani-select`, `result-fail`, `result-almost`, `result-clear`,
`dani-result-fail`, `dani-result-clear`, `dani-gameover`, `retry`, `gameover`, `waiwai-result`, `ai-remind`,
`waiwai-tutorial` or `ai-tutorial`. For example, the Revival drum-roll
challenge starts directly with:

```sh
dotnet run --project src/Waddamburo.App -- \
  --game-data="/path/to/game/USRDIR" --start-scene=retry
```

`result-fail` and `result-clear` use fixed diagnostic play results; pressing Escape
on the failed result advances to Revival. With `retry`, leave the drum idle to
hear its failure sequence, or play to hear its success sequence. The option also
works with the explicit `--entry-song-select` content arguments below. Normal
launches still begin at Entry.

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

This runs Entry, Song Select and a song from explicit content folders instead of a game
folder: the archives, TJA library, font and jingle are the user's own and are never copied
into the repository. How the scenes chain together is in [app flow](app-flow.md); timing and
judgement references are in [gameplay integration](gameplay-integration.md).
`--sound-root=/path/to/user-owned-sound-tree` additionally enables authored
nuSound2 bank/cue effects and voices; unresolved system/player request shapes are
logged rather than guessed.

## Environment switches

Every `WADDAMBURO_*` variable the game reads, from each project's `DiagnosticSwitches` class (read live, at the place
that uses it). A switch is on with `1` unless its line says otherwise.

| Variable | Does | Read in |
|---|---|---|
| `WADDAMBURO_AI_TRACE` | AI battle: each section's decisions and scores on the console. | App |
| `WADDAMBURO_BENCHMARK_ATTRACT` | --benchmark: seconds of attract loop before the song (default 180). | App |
| `WADDAMBURO_BOOT_FAIL` | Boot fails at the named check (BootChecks), to see its error screen. | App |
| `WADDAMBURO_CRASH_AT` | The game throws at this tick, to see its crash screen. | App |
| `WADDAMBURO_CUSTOM_TJA` | 1: the custom TJA library in arcade mode too. | App |
| `WADDAMBURO_DECODE_TEXTURES` | 1: scene textures decoded to RGBA instead of kept compressed. | App |
| `WADDAMBURO_DON_COSTUME` | Don-chan's costume at start (slot:id list). | App |
| `WADDAMBURO_DON_PUCHI` | This puchi chara beside player 1 at start. | App |
| `WADDAMBURO_DRUM_TRACE` | 1: every drum hit's judgement on the console. | App |
| `WADDAMBURO_DUMP_TREE` | The active scene's display lists printed at this tick. | App |
| `WADDAMBURO_FILL_PLACEHOLDERS` | Movie viewer: native fills shown as placeholders (name=index,...). | App |
| `WADDAMBURO_GAMEPLAY_FRAME_PROFILE` | 1: per-frame timings of gameplay frames to a file. | Platform.Sdl |
| `WADDAMBURO_HITCH_TRACE` | 1: why a slow frame was slow (input, update, render, GC, the chart clock's steps). | App, Platform.Sdl |
| `WADDAMBURO_HOST_TRACE` | 1: every movie-to-game host call on the console. | Lumen |
| `WADDAMBURO_ICON_GALLERY` | 1: Song Select shows every option icon on its boards. | Game |
| `WADDAMBURO_INPUT_TRACE` | 1: presses printed in --press format (KEY@tick). | App |
| `WADDAMBURO_NAME` | Player 1's name board shows this name. | App |
| `WADDAMBURO_NO_GENRE_PATCH` | 1: Song Select's movie runs without the genre patch. | Game |
| `WADDAMBURO_ONLINE_ECHO` | Online play alone: your own hits come back as the partner's lane, this many ms later. | App |
| `WADDAMBURO_ONLINE_LAN` | 1: LAN addresses offered to strangers too. | App |
| `WADDAMBURO_ONLINE_RELAY_ONLY` | 1: online play through the server's relay only, no direct link. | App |
| `WADDAMBURO_ONLINE_SERVER` | The online rooms' server instead of the settings' one. | App |
| `WADDAMBURO_ONLINE_TOKEN` | The online rooms' login token instead of the player's. | App |
| `WADDAMBURO_PRELOAD` | 1: every screen and jingle loads at boot and stays in memory (slow storage). | App |
| `WADDAMBURO_PRESENT_MODE` | immediate or mailbox: the swap chain's present mode. | Platform.Sdl |
| `WADDAMBURO_PROFILE` | 1: load and boot timings, full exception traces. | App, Game, Platform.Sdl |
| `WADDAMBURO_READ` | Movie viewer: a script value read and printed (path). | App |
| `WADDAMBURO_REJECT_CARDS` | 1: every paired card is rejected (the red band). | App |
| `WADDAMBURO_RENDERER` | The renderer (vulkan, gles, opengl, d3d11, metal); config.cfg's renderer sets it when unset. | App, Platform.Sdl |
| `WADDAMBURO_RESULT_PLAYERS` | 2: --start-scene results show the diagnostic result for both players. | App |
| `WADDAMBURO_SCROLL_TRACE` | 1: Song Select's scroll steps on the console. | Game |
| `WADDAMBURO_SETUP_BOTH` | 1: the home player setup opens for both drums. | App |
| `WADDAMBURO_SETUP_TRACE` | 1: the home player setup's columns on the console. | App |
| `WADDAMBURO_TICK_STATS` | 1: the game logic's average time and allocation per tick, every 600 ticks. | App |
| `WADDAMBURO_TITLE` | Player 1's title plate (plate:text). | App |
| `WADDAMBURO_TREE_EVERY` | Movie viewer: the display list printed every this many ticks. | App |
| `WADDAMBURO_TREE_FILTER` | Movie viewer: only display-list lines with these parts (a|b). | App |
| `WADDAMBURO_UPDATE_URL` | The release feed the self-update reads instead of GitHub's. | App |
| `WADDAMBURO_UPSCALE_MODEL` | The upscaler's model folder. | App |
| `WADDAMBURO_WAIWAI_RARE` | 1: every Waiwai song gets its rare note. | App |
| `WADDAMBURO_WAIWAI_RESULT` | --start-scene=waiwai-result: segments,duet%,rare notes. | App |

Native build policy and preset commands are documented in
[docs/development/native-builds.md](native-builds.md).
The source-neutral song/category catalog and provider contract are documented in
[docs/development/song-catalog.md](song-catalog.md).
Binary parser limits and failure behavior are documented in
[docs/development/parser-safety.md](parser-safety.md).
The Green-profile DDP archive index is described
in [docs/development/ddp-archives.md](ddp-archives.md).
NTP3 texture-pack validation and reference BC decoding are described in
[docs/development/nut-textures.md](nut-textures.md).
The lossless LMB container boundary is documented in
[docs/development/lmb-records.md](lmb-records.md).
The Lumen display-list runtime is documented in
[docs/development/lumen-runtime.md](lumen-runtime.md).
The SDL window and bgfx rendering lifecycle is documented in
[docs/development/rendering.md](rendering.md).
For memory, frame time and audio-buffer measurements, see
[docs/development/profiling.md](profiling.md).
