# Diagnostics and developer runs

Command-line options for probing single movies, scenes and flows from source, and the
older notes on how the game flow is composed. Normal play needs none of this: see the
[README](../../README.md).

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
mode F2 inserts a coin; the first coin opens player Entry, and joining pays the credits. Space
skips the startup screens.

F3 toggles inspect mode: a left click then prints every scene quad under the pointer, topmost
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
`font.ttf`, `font.otf`, and `font.ttc` preferred in that order. Otherwise an
installed Japanese-capable system font is used. `--font=/path/to/font` remains the
highest-priority override. All granular content options below are retained for
diagnostic runs.

To test a scene without playing through the preceding screens, use
`--start-scene=attract` (skips the startup notice), `entry`, `song-select`, `result-fail`, `result-clear`, `retry`, or
`gameover` with the normal game-data argument. For example, the Revival drum-roll
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

Normal boot loops the user-owned `data/sound/bgm/nub/JINGLE_ENTRY.nub` during
Player Entry, then stops it and starts `JINGLE_GENRE.nub` when Song Select becomes
active. `--play-jingle` can override the Entry sound with a one-shot for a
diagnostic run. Jingles are decoded as bounded short clips; no referenced audio is
copied into the repository.
Interactive Song Select also resolves each TJA's opaque audio asset through the
provider and plays a debounced, seeked preview through the shared mixer. Changing
or selecting the song and unloading the scene cancel and release that stream.
Completing a one-player course selection loads the exact catalog-pinned TJA chart,
then runs the authored rainbow handoff: Song Select remains underneath `in_extra`,
the composed gameplay scene loads only once the screen is covered, and chart/audio
time starts with `out_extra`. The transition displays the selected song title in
its native fill. Gameplay renders authored Don/Ka note movies above the lane and
drives drum, judgement, combo, score, and Go-Go presentation. Chart offsets and pre-roll
are explicit; interactive judgement follows an estimated audio output clock,
independently of animation ticks. See [gameplay integration](gameplay-integration.md)
for the timing contract, reference sources, and remaining limitations.
`F`/`J` are the left and right Don inputs, and `D`/`K` are Ka. In home (PC) mode,
Escape pauses a loaded song; rims or arrow keys select Resume, Restart Song, Settings,
or Song Select, and centre or Enter confirms. In the entry and Song Select, Escape opens
Resume, Settings, or Return to Title. Settings holds the master, music, drum, effects and
Don-chan voice volumes, the audio and input offsets (applied from the next play or
restart) and the audio buffer size (applied at start); centre edits a row and the rims
change it (Left/Right also work) by 1, or by 5 then 10 when pressed rapidly, and leaving the page saves them to `config.cfg`. Settings also toggle stereo panning (off: two players share centred sounds) and muting while the window is in the background; losing focus mid-song pauses it (not once the chart's last note or roll is over). Resume counts 3-2-1 on the game's timer before the song goes on. The attract and player setup open the same menu with Escape. A sound cuts off its own previous play (Don stops Don, not Ka). Holding Q during gameplay or its
results fades toward black; releasing early fades back, while reaching black after
half a second restarts the same match and fades the new play in. Pause artwork
is decoded from the user's game archives at runtime. In cabinet mode, Escape
still abandons gameplay and returns to Song Select.
TJA supports standard rolls, balloons, partner notes, mid-measure commands, and
fixed-route branches; see the [compatibility matrix](custom-tja.md#gameplay-compatibility)
for supported features and remaining limits. Invalid or unsupported chart loads
return to Song Select with a console diagnostic.
The initial gameplay composition deliberately rejects two-player launches until a
two-lane presentation and input contract are implemented.
`--sound-root=/path/to/user-owned-sound-tree` additionally enables authored
nuSound2 bank/cue effects and voices; unresolved system/player request shapes are
logged rather than guessed.

Native build policy and preset commands are documented in
[docs/development/native-builds.md](native-builds.md).
The source-neutral song/category catalog and provider contract are documented in
[docs/development/song-catalog.md](song-catalog.md).
Binary parser limits and failure behavior are documented in
[docs/development/parser-safety.md](parser-safety.md).
The first format implementation, the Green-profile DDP archive index, is described
in [docs/development/ddp-archives.md](ddp-archives.md).
NTP3 texture-pack validation and reference BC decoding are described in
[docs/development/nut-textures.md](nut-textures.md).
The lossless LMB container boundary is documented in
[docs/development/lmb-records.md](lmb-records.md).
The initial display-list runtime is documented in
[docs/development/lumen-runtime.md](lumen-runtime.md).
The SDL window and bgfx rendering lifecycle is documented in
[docs/development/rendering.md](rendering.md).
For memory, frame time and audio-buffer measurements, see
[docs/development/profiling.md](profiling.md).
