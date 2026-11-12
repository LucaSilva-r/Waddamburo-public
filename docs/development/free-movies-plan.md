# Free movies (own movie packages + movie editor)

## Context

Waddamburo has to be able to run without any Bandai file. Today every screen is one of the user's Lumen movies
(`.lm` + NUT textures in a `packeddata.ddp`), scripted in AVM1 and driven by the game-side hosts through each movie's
callbacks. The engine itself holds no Bandai content; what is missing is a way to make and load movies of our own.

A free movie is a second way to fill the model the runtime already plays (`LmbMovieDefinition` + textures +
AVM1 code): a text package of JSON, PNG and ActionScript 2 source. The hosts keep talking to movies through the same
callbacks, so a free movie that answers them replaces a Bandai movie with no engine change, one movie at a time. The
game needs no Bandai file once the last movie it loads has a free replacement.

The effects editor (`effects-editor-plan.md`) gives the tool side its shell: ImGui panels, the shrunk stage, gizmos,
timeline and graph widgets. `LumenMovieEditor` (`lumen-patches.md`) already edits the movie model in memory.

## Decisions (user, 2026-11-10)

- **Goal: the engine boots with zero Bandai files, scripts included.** A reskin layer over Bandai movies is not enough.
- **Movies stay scripted.** Behaviour lives in the movie, not in the engine, so artists and scripters change a screen
  without an engine build.
- **The script language is ActionScript 2**, compiled to the AVM1 code the existing interpreter runs. One VM, so
  practice-mode seeking and state restore keep working as they do now; a second runtime (Lua, JavaScript) would need
  its own state handling (`modchart-design`'s reason for having no script VM).
- **Callback names and signatures are kept** as the Bandai movies define them (interoperability). A free movie is a
  drop-in for the movie it replaces; the hosts do not know which kind they drive.
- **Full compatibility with the Bandai movies stays, in both directions** (2026-11-11). Free movies replace them one
  at a time, and every mix must work: a free movie among Bandai ones behaves exactly like the movie it replaces, to the
  host and to the Bandai movies that load it or reach into it (e.g. a free `time_counter` loaded by Bandai's
  `song_select`).
- **Bandai movies are never converted to files.** They load in memory as now. The package format only ever holds
  original work.

## Design

### Package

A directory per movie, found before the archive movie of the same id:

```text
<waddamburo data>/movies/<archive folder>/<movie>/     e.g. movies/indicator/time_counter/
  movie.json        frame rate, textures, shapes, sprites, labels
  textures/*        PNG or SVG, referenced by name from movie.json
  scripts/*.as      ActionScript 2 (milestone 2)
```

`movie.json` (`Waddamburo.Formats.Package.MoviePackage`; comments and trailing commas allowed; sample in
`samples/movie-package/bounce`):

- `format` (1), `frameRate`, `root` (a sprite's name), `textures` (name → file). A texture is a PNG or an SVG; an
  SVG is drawn once at load, at its own `width` / `height` (author it at twice the size it shows at for sharp edges;
  a shape's `rect` sets the size on screen).
- `texts`: name → `rect`, `size`, `align` (`left`, `center`, `right`), `color` (`#rrggbb`). A text field: a box a
  script fills (`clip.field.text = "12"`), drawn by the engine's text renderer in the game's font. This is the one way
  a free movie shows text or numbers; there are no glyph textures.
- `shapes`: name → `rect` [x, y, width, height] in the shape's own space, `texture` and optional `uv`
  [left, top, right, bottom]. One textured quad, as in Lumen: no vector art. Without a texture the shape is a slot the
  host draws into (a native fill).
- `sprites`: name → `frames`, `labels` (name → frame) and `layers`. A layer is one character (shape or sprite) at one
  `depth` (its order in the list when left out), with an instance `name`, a `blend` (`normal`, `add`), `keys` and
  `until` (the frame it is removed on; never when left out).
- A key: `frame`, `x`, `y`, `scale` or `scaleX` / `scaleY`, `rotation` (degrees), `alpha`, `tint` [r, g, b], `hold`.
  `ease` (`in`, `out`, `inOut`; linear when left out) shapes the run to the next key. A layer's `blend` is `normal`,
  `add`, `multiply` or `screen`; `masks`: n makes it a mask for the n layers above it (not drawn itself).
  Values run straight from one key to the next unless `hold`. This is the model the editor edits (layers and
  keyframes); curves other than straight lines are the editor's to add.
- **Format 2** (2026-11-12): a key holds only the channels it names (`x`, `y`, `scaleX`, `scaleY`, `rotation`,
  `alpha`, `tint`), and each channel runs from one key that names it to the next that does, by the first one's `ease`
  or `hold`, whatever keys lie between; before its first key a channel is already at that key's value, and a channel
  no key names is at its default. So an opacity key says nothing about position. A layer still appears on its first
  key's frame. In format 1 every key holds every channel (the ones left out at their defaults); both load. The editor
  works in format 2: it opens a format 1 package with the defaults written out (nothing moves) and saves it as 2.
  Two channels keyed on one frame share that key's ease.
- A layer is on stage from `from` (its first key's frame when left out) until `until`; before its first key every
  channel is already at that key's value.
- Not yet: text outlines, masks (`ClipDepth`), colour add, several quads per shape, seek snapshots (a seek replays the
  timeline from frame 0).
- ponytail: loose directories only; a single-file archive for distribution when packs are shared.
- ponytail: textures decoded or drawn to RGBA at every load, SVG at a fixed size whatever the window's; a cache, and
  drawing at the display's scale, when load time or 4K sharpness matter.

### Loader

`MoviePackage.Assemble` turns `movie.json` into the record stream the Lumen reader takes (in memory, never written):
names become pool indices, layers become per-frame place / move / remove commands. So a package movie goes through the
same parser, validation and player as an archive movie, and everything that works on the movie model (patches,
inspection, the editor) works on both.

`DirectoryLumenMovieContentSource.PackageRoot` is looked up first, per movie, then the archive
(`LumenMovieContent.LoadPackage`). The game sets it to `USRDIR/waddamburo/movies`. Packages get no patches and no
upscaling. When neither a package nor the archive has a movie, the error names the movie. The movie viewer opens a
package folder with `--package=<folder>` (with `--invoke`, `--ticks`, `--screenshot` as for archive movies).

### ActionScript 2 compiler

`Waddamburo.Formats.Package.ActionScript` (one file, no dependency): source text → the Lumen AVM1 variant (strings as
pool indices), run by the existing interpreter. Tests compile scripts, run them in the interpreter and compare what
they report to a host function (`ActionScriptTests`).

- In `movie.json`: a sprite's `scripts` (frame number or label → file, run when the frame is entered), its `export`
  (the linkage name `Object.registerClass` binds a class to), and the movie's `classes` (files run once before
  anything is placed: class definitions and `registerClass` calls).
- Language: variables, functions and closures, objects and arrays, `class` / `extends` / `super` / `static`,
  `import a.b.C`, `if`, `while`, `do`, `for`, `switch`, `break` / `continue`, the operators, `delete`, `trace`; type
  annotations are read and ignored. On a timeline `stop()`, `play()`, `gotoAndStop(x)`, `gotoAndPlay(x)` are the
  player's own instructions.
- Not yet, reported as errors: `for..in`, `try` / `throw`, `with`, `typeof`, `instanceof`, getters and setters,
  interfaces. The interpreter has no opcode for the first five (the game's movies never needed them); each is added
  to both when a free movie needs it.
- As in Flash: call arguments are evaluated last to first. Unlike Flash's compiler: in a class, only the class's own
  members can be written bare; inherited ones are `this.name` (or `Class.name`), since the compiler does not know the
  superclass's members.
- Library: the Flash 8 subset the interpreter provides; a call to something outside it is reported at run time
  (`LUM_AVM_CALL_UNRESOLVED`), not when compiling.
- Errors are `file(line,column): message`. ponytail: a script error fails the whole package load (the game then
  falls back to nothing: the scene reports it); an on-screen error with the rest of the movie running when the editor
  needs it.
- The movie viewer reloads a package when one of its files is saved (the movie starts again); a mistake is printed
  and the last good version keeps playing.
- ponytail: no optimisation (locals by name, one push per value, `a.b += 1` evaluates `a` twice); registers when a
  script shows up in a profile.

### Our own runtime's extras (user, 2026-11-11)

Since both ends are ours, the package format has features Lumen never had, where they speed up making movies: SVG
textures (Svg.Skia, so files from a drawing program work too) and text fields drawn by the engine's text renderer.
A free movie needs no artist to exist: shapes as SVG text, numbers and words as text fields. Bandai movies never see
these; free movies still answer the same callbacks.

### Movie editor

Its own editor (user, 2026-11-11), not a mode of the effects editor: that one edits tracks over a chart's time on
named screen parts; this one edits a movie's clips, their frames, its assets and its code. They share the shell
only (`Waddamburo.Editor`: the ImGui layer, panel layout, theme, stage mapping, later the keyframe and curve widgets).

Read side, built (`MovieInspectorSession`, over the player's `Inspect…` calls, which leave the edge log alone):

- **Clips**: the display list as a tree (name, kind, frame and label, dimmed when not drawn), filter by name, deeper
  levels collapsed.
- **Stage**: the running movie; a click selects the smallest clip under the pointer, outlined.
- **Clip**: the selected clip's transform, colour, blend, bounds, script variables; its timeline (frame slider, play /
  stop, a button per label); a hide switch.
- **Callbacks**: every callback the movie registered, a box per parameter, a call button, the results.
- Timeline transport: combined Play/Pause (Space), frame steps (Left/Right), previous/next label
  (Ctrl+Left/Right), frame slider and Run scripts. Controls follow the timeline being edited; root shows Root frame.
- A compact File/Edit menu replaces the top toolbar. Open movie package chooses movie.json; Open Lumen archive
  chooses a DDP and then a movie inside it (read-only). Save is explicit (Ctrl+S). Dirty documents show an asterisk;
  opening another movie, File > Close and the window close button offer Save, Discard or Cancel.
- Opened by the movie viewer on any movie: `--package=<folder>` or `--archive=… --movie=…` (`--plain` for the bare
  movie; `--inspect[=clip]` with `--ticks` / `--screenshot`). A movie from the game data is reference only.

Write side, to build (packages only):

- **Document** (`MovieDocument`): the editor edits `movie.json` as a tree (layers and keys, not the assembled
  records). Edits update the in-memory preview, with every clip put back on the frame it was on (what scripts had
  set starts over). File > Save (Ctrl+S) writes the working JSON, companion metadata and staged artwork/scripts;
  undo and redo are in-memory snapshots of all three. A file the editor has written loses its comments. The script text is never edited here (user's call):
  an outside editor, with reload on save.
- **Timeline**: the selected sprite's layers as rows, keys as the effects editor draws them (drag, box select, copy),
  labels and script markers on a ruler; the stage gizmos move / scale / rotate the selected layer at the playhead,
  writing a key.
- **Assets**: the package's textures as thumbnails (PNG and SVG); import by file drop or path (copied into
  `textures/`), replace, rename, delete with a list of the shapes using it; a new shape from a texture or a region of
  one; text fields (box, size, alignment, colour) made and edited here too. Unused assets are marked.
- **Code**: the package's scripts listed with where each runs (class file, or sprite and frame), attach / detach;
  compile errors as a list, a click going to the file and line. The text itself is edited in the user's own editor
  (opened from the list), with reload on save: ponytail, no code editor inside (ImGui has no real one); an embedded
  one if artists turn out to need it.
- Save writes the package. A game-data movie stays reference only: no save, and no copying of its shapes, textures
  or scripts into a package.

#### Looking at a game movie

A movie from the game data opens in the same panels, to look at only. `MovieReference.Describe` reads it into the
shape of a movie.json (in memory, never written; `MovieDocument.ReadOnly` ignores every edit and refuses every file
operation): sprites as layers with keys (a tween stored as a placement per frame becomes its end keys, a held value is
marked), labels, script markers (`action 12`, no code), shapes, textures. Characters are named by id and where they
are used (`sprite_1205_GenreFolderTab`). The **Library** tab lists every sprite the movie defines (frames, layers,
scripts, labels, used in); choosing one plays it alone in the middle of the stage (`ShowSprite`, a player rooted at
that sprite) with its timeline below. Approximate where the package format is simpler: a shape's first quad only, no
skew, no colour add. `--inspect=sprite:<part of name>` starts on a sprite.

#### What the panels are built on

The UI (ImGui, `Movies/Session/*.cs`) only draws and calls these; rules and file handling live below it.

- `MovieDocument` (tests: `MovieDocumentTests`, `MovieDocumentAssetsTests`, `MovieSaveTests`): `Root` (the JSON tree),
  working `Text`, `Edit(change, continues)`, `Undo` / `Redo`, `IsDirty`, `Save`, `ReadFile`, `Changed`, `Saved`; `Layer`, `KeyAt`, `Value`, `Set` for keys.
  - Assets: `ImportTexture(path)`, `ReplaceTexture(name, path)`, `AddShape(name, texture, width, height, uv)`,
    `AddSprite(name, frames)`, `Rename(group, from, to)` (follows every reference), `Delete(group, name)` (refused
    while used), `Uses(group, name)`, `Unused()`, `Exists(group, name)`. Groups: `textures`, `shapes`, `texts`,
    `sprites`. A refused operation throws `InvalidOperationException` with the sentence to show.
  - Layers (by sprite and place in its list; later is on top): `MoveLayer`, `DuplicateLayer`, `SetBlend` (`Blends`),
    `SetUntil`, `SetMask(sprite, index, count)`. Keys: `Tint` / `SetTint`, `Ease` / `SetEase` (`linear`, `in`,
    `out`, `inOut`, `hold`); change a key inside `Edit`.
  - Scripts: `Scripts()` (file, sprite, frame; classes have no sprite), `AttachScript(sprite, frame, file)`,
    `DetachScript`, `AddClass`, `RemoveClass` (a missing file is created with a comment), `OpenOutside(file)` (the
    system's program for it), `FilePath(relative)`.
  - `Format(node)`: the file's layout (small entries on one line), so an edited file stays readable and diffs short.
- In the session: `attempt(operation)` runs a document operation, reloads the stage and turns a refusal into the
  toolbar notice; `edited(change)` the same without the catch; `ScriptErrors` (file, line, column, `Problem`) are
  the scripts left out of the running movie because they do not compile (`MoviePackage.Assemble(scriptErrors:)`: the
  movie plays without them); `TextureOf(name)` gives a texture's id and size for `ImGui.Image` thumbnails; a PNG or
  SVG dropped on the window is imported (`ImGuiLayer.FileDropped`).
- On the player: the `Inspect…` calls (`LumenPlayer.Inspect.cs`).

Asset editing now available (2026-11-11): the Assets tab includes textures alongside shapes, text fields and sprites.
Import PNG / SVG opens the system picker; dropping a file imports and selects it too. Select a texture to preview it,
see its users and replace its artwork. Replacement keeps the texture name and all shape references; earlier files
are retained so Undo and Redo restore artwork as well as JSON. Files retained for history are not cleaned up yet.
New shape uses the selected texture (or a native fill); New sprite creates an empty timeline. Place either through
Timeline > Add layer. A shape's Inspector edits its texture, local bounds and UV region, with positive finite sizes
and nonempty regions in 0..1. These changes affect all instances without renaming any child clips. Select a stage
clip and use Edit source asset to reach its shared artwork. Unused assets can be deleted; references remain read-only.
The viewer can start on an asset with `--inspect=asset:textures:note` (group and exact name), including in a screenshot
run; the Inspector starts on that asset. The Assets tab lists the rest of the package.

The asset browser now supports right-click menus on assets, folders and empty space. Create / Import is also
available from the toolbar. Asset menus rename, replace artwork, move to a folder, open the physical containing
folder and delete an unused asset. F2 renames the selected asset while the browser has focus. Folders open on
double-click; breadcrumbs navigate back and accept asset drags. Folders can be renamed, moved or deleted when
empty; moving one beneath itself or a descendant is refused. Search finds assets and folders across the package.
Dropping a PNG/SVG imports into the current browser folder. Dropping a directory, or Import folder, imports its
artwork recursively with its directory structure, as one undo step. Imports are staged in memory until Save; previously saved files remain available for history.

`movie.editor.json` is the package's companion metadata, version 1: `assets` maps stable GUIDs to `group`, `name`
and browser `folder`; `folders` maps GUIDs to `name` and `parent` (empty means root). Legacy packages get identities
in memory when opened, and an explicit Save persists them. Keep this companion with the package when moving or
sharing it. Browser folders are logical, so shapes and sprites can be organized with artwork; physical texture
files stay beneath `textures/`. The runtime's existing named references stay compatible: rename updates shape
texture references, layer character references and the root while preserving child instance names and sprite
linkage/export names. It does not rewrite arbitrary script text or external packages. These identities currently
cover assets within one package; cross-package scene references are future work. Undo/Redo snapshots include both
movie and metadata, and the isolated-sprite preview follows the stable asset identity through rename.

The browser's five Lucide SVG icons are embedded and rendered through the existing SVG pipeline. Exact upstream
revision, hashes and licensing are in the public provenance and third-party notices.

Still without UI: the script list with attach / detach / open, the error list (the toolbar shows the first only).
Whole-scene composition and checks of required callbacks, child paths and labels remain future work.

### Contract per movie

What a replacement must match is everything that crosses the movie's edge:

1. **Host → movie**: its `ExternalInterface.addCallback` names, argument counts and types, return values.
2. **Movie → host**: its `Lumen.X()` calls, arguments, and when they happen (which callback or frame triggers them).
3. **Movie ↔ movie**: `MovieClipLoader.loadClip` and what the loader then calls in the loaded movie. Only the
   movies' standalone developer branch loads movies this way; in game mode every movie is a layer of its own with its
   own globals, placed and driven by the host, so this edge is thin.
4. **Host → display list**: clip names, frame labels, fill slots and depths the engine addresses directly (layout
   editor roles, `SetNativeFill` slots, Lumen patches, hard-coded Green clip names).
5. **Look and timing**: size, anchor, how long each state's animation runs (hosts and other movies wait on some).

Sources, all already in the repository's reach:

- **Static**: the bytecode gives 1, 2 and the loadClip targets of every movie. Misses names built at run time.
- **Recorded**: `LumenEdgeLog` (`WADDAMBURO_EDGE_LOG=<file>`) writes one line per crossing while the game or the
  movie viewer runs: callbacks a movie registers (with parameter names), host calls in and out with values and
  results, commands, loadClip, and everything the host reads, writes, fills, moves or looks up inside a movie by
  name (written when the result changes, since hosts poll). `Math.random` is seeded while recording. Only covers
  paths that were played.
- **Real game**: `research/traces` for call order and timing where our hosts might be wrong themselves.

Static ∪ recorded is the contract: one generated page per movie (names and signatures only, nothing of the script),
plus hand notes for 5. The pages and the logs are asset-derived listings, so they stay in the private lab
(`research/contracts/`, generator `tools/contract/contract.py`), per `docs/legal/evidence-policy.md`. The replacement
is written from that page, not from the Bandai script.

**Check**: the same recorder runs a scripted session (the movie viewer with `--invoke=Name|args@tick` and `--ticks`,
replaying the calls a real session recorded) once with the stock movie and once with the replacement; the movie's
lines must be equal (`contract.py --diff`), and screenshots at fixed ticks are compared by eye. A free movie is done
when its diff is empty for the sessions of every scene that uses it.

First target: `time_counter` (indicator archive). Small (about 800 instructions, 16 callbacks, one host call),
used by several scenes, and other movies' developer branches load it.

## Milestones

- [x] 0. Contract tooling (2026-11-11): `LumenEdgeLog` + hooks in `LumenPlayer` (test in `LumenPlayerTests`),
  page generator and `--diff` in the lab, two viewer runs of `time_counter` give identical logs, and a recorded
  arcade session (entry, song select, a song, results, game over) gives the calls the hosts make.
- [x] 1. Package format + loader (2026-11-11): `MoviePackage` (tests in `MoviePackageTests`), `LoadPackage`, lookup
  per movie before the archive with a missing-movie error (`MoviePackageSourceTests`), viewer `--package=`, sample.
  Not yet seen in the game itself: that is milestone 3, the first movie with the callbacks a host calls.
- [x] 2. Compiler (2026-11-11): `ActionScript` with its tests (27 scripts, classes bound to clips, callbacks), scripts
  and classes in packages, the sample's script, viewer reload on save (the reload itself not yet tried by hand).
- [x] 3. First replacement, by hand: `time_counter` (`movies/indicator/time_counter`, 2026-11-11), written from its
  contract page and from what the stock timer shows and answers when probed (60 scripted calls: same edge log);
  SVG art, text-field digits; `FreeTimeCounterTests` keeps the measured behaviour. User-checked in an arcade session:
  song number, countdown and the last seconds work; it is a proof that the system works, not a finished asset
  (plain look, and the host's ten-second tick sound is not in step with the number). Left as is by the user's call.
- [x] 4. Editor, read side (2026-11-11): `MovieInspectorSession` in the movie viewer, on packages and game movies
  (tried on the timer and on song_select); `Inspect…` calls on the player with a test. Not yet user-tried by hand.
- [ ] 5. Editor, write side, in this order: (a) document + undo + save, inspector fields editable (built 2026-11-11:
  `MovieDocument`, in-memory edits with explicit Save, undo / redo; a selected clip's layer shows x, y, scales,
  rotation, alpha and name, a change making a key on the current frame; user-tried, works); (b) timeline rows
  and keys, stage gizmos (built 2026-11-11: a row per layer with its keys, click / drag / double-click / Delete on
  keys, ruler with playhead and labels, frame count, add and remove layers, drag on the stage moves the selected clip;
  scale and rotate by the fields only, no handles yet; curves are straight lines); (c) assets panel (text fields built by hand in the UI; the document operations for textures, shapes,
  sprites and renames are built and tested, their UI is not); (d) code panel with the error list (operations and the
  error list built, UI not). Done when the timer can be rebuilt
  in it from an empty package.
- [ ] 6. Gameplay screen complete in free movies; a build started with an empty data directory plays a TJA.
  Groundwork done 2026-11-11 (see "Milestone 6" below): the movie overview, bundling, the `free_movies` setting.
- [ ] 7. Remaining screens by flow order (song select last: the largest script).

Milestones 1 to 3 need no editor and carry the risk (does a package movie written from a contract behave like the
stock one). The editor comes after so it has something to save.

## Milestone 6: the gameplay screen

**Where free movies come from** (`DirectoryLumenMovieContentSource`), first hit wins:

1. the user's `USRDIR/waddamburo/movies/<archive folder>/<movie>/`;
2. the engine's own, `movies/` beside the executable (the repository's `movies/`, copied by the build), when
   `free_movies = true` in config.cfg;
3. the game data's archive;
4. the engine's own again when the game data has no such archive. This is what lets the engine run on any part of the
   game data down to none: every movie that has a free version plays, the rest are reported by name.

**How much there is** (the lab's `research/contracts/overview.md`, from `tools/contract/contract.py --overview`: every
movie's script size, callbacks, host calls, and what the recorded sessions touched): the game has 700 movies, most of
them the same few parts drawn again for each gameplay theme. The solo gameplay screen is 38 system parts (about 5,000
script instructions and 52 callbacks together; two thirds of them have next to no script: a note, a lane, a label)
plus one each of 9 themed kinds (background, stand, dancers, Don-chan's backdrop, Go-Go layers, roll character). The
timer alone was 800 instructions, so the gameplay screen's scripts are about six timers; its work is mostly drawing
and animation, which is the editor's job, and the clip names the engine addresses inside these movies (frames it
jumps to, fill slots), which each contract page lists under "what the host addresses".

**Order**:

1. Notes and lane (no scripts, the engine draws through them): the smallest proof that a song plays on free movies.
2. Gauge, score, combo, roll counter, hit effects: small scripts, each with a contract page from the recorded
   sessions.
3. The lane's banner (the largest gameplay script) and the balloon and kusudama actions.
4. One themed set of our own: background, dancers, Go-Go.
5. Outside the movies: Don-chan's model, sounds, the font (see Open).
6. A run with an empty data directory: the engine has to start without the game data it reads besides movies (song
   lists, sound banks, models), each with a free stand-in or a clean "not there".

### The empty data directory, probed (2026-11-11)

The game was started on a data directory holding nothing, and given the real thing piece by piece until it ran:

- The start-up checks want the folders `data/lumendata/packed`, `data/don3d` and `data/sound` to exist (empty is
  enough to get past them).
- Three things besides movies stop the boot when missing, in this order: Don-chan's data (`data/don3d`, first file
  `ani/don_bind.bin`, at DON-CHAN), the sound data (`data/sound/config/…`, at SOUND DATA), and the gameplay layout
  (`data/ensolayout.bin`, at GAMEPLAY DATA).
- Everything else the engine reads from the game data is already optional: with only those three and the movies in
  place it boots to the title and runs (no charts, song lists, videos, fonts, icons or tuning files).
- Movies: the boot itself loads the six intermissions (rainbow, fade, shutter, the three dan covers); reaching the
  title adds the boot screen, the logo, the title and three indicator parts. Twelve movies to the title screen.

So a start with no game data needs: the three made optional (no Don-chan drawn, silence, the engine's own layout
numbers), twelve free movies of which the logo and title are ours to design rather than to replace, and then the
screens after the title. Not probed yet: song select and a song without the game data (their movies need the rest of
this milestone first).

### Running without game data, as built (2026-11-11)

Nothing of the game data is required to start. Missing Don-chan data: he is not drawn. Missing sound data: silence.
Missing `ensolayout.bin`: every gameplay part at the stage's origin. With no movie folder at all, a movie without a
free version is shown empty and named once in the log, and its stand-in takes any callback (`LumenPlayer.StandIn`);
the boot screen's stand-in ends at once, and the home setup draws no arrows when the entry movie lacks the sprite it
borrows them from.

How far that goes: boot, logo, then the entry with a key press, where it stays, because the menus' behaviour is in
their movies' scripts (entry and song select are the two largest in the game). A stand-in cannot be a menu.

**The test for this milestone**: `WADDAMBURO_BENCHMARK_ATTRACT=2 Waddamburo <empty folder> --benchmark` goes through
the menus by itself and into a song. Today it stops at "Gameplay note movie is missing its initial state": the engine
draws notes through the note movies' own clips. Each free gameplay movie moves that stop further; the milestone is
done when the benchmark song plays to its results.

**Contract pages** for the 38 gameplay parts and the 15 boot and indicator movies are generated in the lab
(`research/contracts/gameplay`, `research/contracts/boot`): callbacks with the arguments seen, calls back, and what
the engine addresses inside each (clip names, labels, frames, fill slots). They are the list of what a free version
must have.

### A song on our own movies (2026-11-11)

The benchmark song now plays on an empty data directory. `movies/enso_system/` holds a package for each of the 38
gameplay parts:

- **Drawn and behaving** (placeholder art, SVG and text fields): the lane, the four notes, the words under the
  notes, the bar lines, the hit target with its judged-hit flash and the lane's drum-hit light, the judgement word.
- **Skeletons** (31): every callback the game calls as an empty function, and the clips and labels the engine
  addresses as magenta boxes. Made by the lab's `contract.py --skeleton` from the contract pages; each is the
  starting point for drawing and writing that part in the editor. Among them the board (drum, score, combo), the
  gauges, the roll and balloon parts, the note flights.

The gameplay layout without the game's file is `EnsoLayout.Standard`: the same arrangement as the game's, so a part
of ours sits where the game's would and the two sets can be mixed. Our movies are authored in each part's own
coordinates (the lane group's origin is its top-left), not stage coordinates.

`FreeTimeCounterTests.EveryMovieOfTheEngineLoadsAndRunsItsFirstFrames` loads every package under `movies/`.

Next in this milestone: the board and the gauge (the song is not readable without score and combo), the rolls and
balloons, the note flights; then one themed set (background, dancers) and the six intermissions.

## Menus first (user, 2026-11-11)

The order changed: the bare minimum from boot to a song on the engine's own movies comes before the rest of the
gameplay screen. Entry, then song select, then the intermissions and the boot movies; results stay a stand-in.

**The host's workarounds stay with the game's movies.** What the engine does to the game's menus from outside
(Lumen patches, clips addressed by name, the key filter, the home player setup drawn over the entry) is frozen and
only used on them. A movie of ours gets the feature for real, through callbacks the game's movie does not have
(named `Wd…`), and the host picks the path by asking: it calls the callback, and a movie that has none is the
game's. Later, on that footing: a song select that is fed songs as it scrolls instead of the whole list at start,
touch input.

**Entry** (`movies/entry/entry`, 2026-11-11): each drum joins with its DON, a joined drum's DON or the timer asks
for Song Select, with the calls and order of the game's entry on that path (contract page in the lab,
`research/contracts/menus`). No mode select, costumes, cards or coins display. Its own callbacks: `WdSetupOpen()`
(the home player setup is open: the movie leaves the drums alone) and `WdSetupDone(playing1p, playing2p)` (who
plays; the movie goes on). `EntrySceneHost` uses them when `WdSetupOpen` answers. Tried on an empty data directory:
`--start-scene=entry --press=F@120` reaches Song Select. Not yet tried by hand: the home player setup over it.
Indicator parts without a movie count as ready (`IndicatorParts.PollReady`), or the entry would wait forever.

**Song select** (`movies/song_select/song_select`, 2026-11-11): a list of folders, a folder's songs, a song's
courses; rims move, DON decides, the timer running out picks. It is not handed the list: `SongSelectHostBinding`
calls its `WdInit(folder, song)` (the cursor's start; a movie that answers is ours, and none of the host's reaching
into the game's movie runs), and the movie asks for each line as it shows it: `Lumen.WdFolderCount()`,
`WdFolderName(folder)`, `WdFolderSongs(folder)` (-1: a folder that is a choice by itself, a library or a mode),
`WdSongTitle(folder, song)`, `WdSongStars(folder, song, course)`, `WdLeaveCourseSelect()`. What it tells the game are
the game's own calls (`NotifyGenreFolder`, `NotifyOpenFolder`, `NotifyStopBGM` for the preview,
`NotifyBeginCourseSelect`, `NotifyEndCourseSelect`, `SetSelectedMusic`), so the flow behind (previews, libraries,
the handoff to the song) is the same for both movies. Tried headless on a full installation: folder, song, course,
the chart loads. Not there: folder colours and art (a script cannot colour a clip yet), crowns and scores, play
options, favourites, search, a course per drum (both play the one picked), Don-chan, sounds of its own.

**Intermissions** (`movies/intermission/`, 2026-11-11): `scene_change_rainbow` (labels `in`, `out`, `in_extra`,
`out_extra`, each stopping at its end, which is what the flows wait for; the song's title in the
`scene_change_song_name` slot), `scene_change_fade` (`wait`, `in`: black in a second), `shutter` (`Close(c)`,
`Open()`). Without them a stand-in stopped the game at the first cover (it has no labels).

**Boot to a song on an empty data directory** (2026-11-11, headless): boot and logo pass as stand-ins, a DON opens
the entry, the player setup, song select on the user's own libraries, a course, the rainbow with the title, the song
on the placeholder gameplay parts. User-walked in the window the same day, to the end: the shutter closes, the
results are blank (a stand-in), and the game is back in song select. So the whole loop runs without the game's data.
A skeleton has only the labels a recorded session jumped to: the fire gauge's `fever_end` and the banner's `success`
and `fail` were added by hand after a song stopped on the first.

Blank or missing on that loop, in the order a player meets them: boot, logo and title; the results; score, combo
and gauge during the song (milestone 6); the tutorial (no way out of its stand-in); song select's "Difficulty" folder.

**Skeletons for the remaining screens of the main path** (2026-11-11), each with the one thing the flow needs from
it so the game never waits on a blank movie:

| Movie | What it does | How it tells the game it is over |
| --- | --- | --- |
| `attract/kidou` | the boot notice, 2.5 s | `_global.is_end = true` |
| `attract/logo_namco`, `title`, `keikoku` | logo, title (10 s), notice | `ExternalInterface.call("Command", "End")` |
| `enso_result/result` | the numbers the game sets, a column per drum | `_global.isAllEnd` on a DON or after 15 s |
| `enso_result/retry_game` | nothing yet (no revival challenge), 2 s | `_global.isAllEnd`, not succeeded |
| `reward_shop/shop_gameover` | 3 s | `Lumen.LumenTerminate_Gameover()` |
| `tutorial/tutorial` | the rules in four lines | `_global.isAllEnd` on a DON |

Pure skeletons (callbacks and labels, nothing drawn): the indicator parts (`indicator`, `player_name`, `over_msg`,
`network_icon`, `msg_banapass`, `msg_coins`), `waitinput`, `attract/movie`, the dan covers (`scene_change_dani1`,
`scene_change_dani2`, `dani_shutter`). On an empty data directory the attract loop now turns by itself and no movie of
the main path is reported missing; only the themed gameplay parts (backgrounds, dancers) are stand-ins.

The lab's skeleton generator now takes every label from the movie's structure (`tools/describe`), not only the ones
a recorded session jumped to, and rests at each label's last frame; the 27 earlier packages got their missing labels
the same way. Still without a skeleton: Waiwai's gameplay parts, AI battle's own copies of the common parts
(`ghost/enso_system/player`), and the themed gameplay sets. The dan and AI battle parts a recorded session reached
(2026-11-12) have one: `enso_dojo/{dani_enso,donbg_dojo_1p,chibi_dojo,gage_don_1p_dani,between}`, and under `ghost/`
the score board, lane band, roll counter, timer, section icon, battle result, song info, name, backdrop, hit effect,
tension, rank panel and rank-up list.

**Skeletons for the other modes' menus** (2026-11-12): `dani_select`, `dani_result` (and `dani_gameover`),
`entry_info`, AI battle's `ghost/{song_select,tutorial,common_bg,remind}` and `ghost/result/{result_bg,result_normal,
result_section}`, `waiwai_result`, `waiwai_tutorial`. Callbacks and labels only, taken from the movies themselves: the
dan and AI battle ones also from a recorded session (dan course to its results, one AI battle to its results); the
tutorials, `remind`, `dani_gameover`, `entry_info` and Waiwai were not visited. None has an exit signal yet. The free song select has no way into these modes, so nothing on the main path
waits on them; each needs a recorded session and its exit before it is reachable.

Next (user, 2026-11-11): the editor, to make authoring these as easy as possible. Later: Waddamburo in a folder of
its own (same layout), and a skin menu in the settings where the user points at a Taiko Green USRDIR.

## Open

- Free movies live in `movies/<archive folder>/<movie>/` in the repository; the game only looks in
  `USRDIR/waddamburo/movies` so far. Shipping them with the engine (and choosing free or original per movie in the
  settings) comes with milestone 6.

- Art: who draws the free assets, and their licence (must allow redistribution with the engine).
- Don-chan is a 3D model (`don-rendering.md`), sounds are NUB banks: both need free replacements too, outside this plan.
- Text: movies use device fonts through `LmbTextDefinition`; a free font to ship.
- Whether artists want SWF import (author in an old Flash, convert quads + AS2 to a package). Only if asked for.

Editor interaction (2026-11-11): assets are selected in the bottom browser and edited in the shared top-right Inspector. Selecting a stage clip, hierarchy entry or timeline layer returns the Inspector to that clip. Asset property gestures use one document undo step; Ctrl+Z / Ctrl+Y also work while a property input has focus.

External script editing: the Code tab opens package AS2 scripts in VS Code (`code --reuse-window --goto`); compilation errors open at their line and column. The viewer polls saved files every 30 simulation ticks, including while the inspector is paused, and recompiles/reloads the movie. Script state restarts. Original archive bytecode is reference-only, not editable AS2 source.

Movie Inspector appearance controls: selected package layers expose blend (normal/add/multiply/screen), mask range and removal frame; keys expose RGB tint and linear/hold/in/out/inOut interpolation. Changes use document history; tint drags are grouped. Original movie controls are read-only.

Editor viewport update: Inter UI font; hierarchy disclosure and sibling reordering; range/toggle selection and viewport marquee; move/rotate/scale handles with Shift constraints, Alt pivot scaling and opposite-edge anchors; combined selection bounds and group transforms (skew-requiring operations are refused). Inspector Properties/Info tabs separate editing from diagnostics. Transform centres are stored as editorPivot metadata. Run scripts can be disabled for timeline-only art preview. Multi-selection Inspector fields still edit the active object; zoom/pan and draggable pivots remain outstanding.

Editor code layout: `Movies/Session/Session.cs` owns lifecycle, host hooks and frame orchestration. Partial files own their feature state and methods: Toolbar (transport), Panels (bottom tabs), Hierarchy (tree/reorder), Selection (range/toggle/marquee/picking), Inspector and Appearance (properties), Editing (document changes and frame restoration), Viewport (stage composition), Gestures (active drags), Tools (tool controls) and Handles (handle drawing), Group (group transform math), Assets (browser), AssetPreview (thumbnails), AssetInspector (asset properties), Timeline, Library and Code. Files owns pickers and unsaved-document dialogs; MovieTransport defines label navigation. Keep new feature code with its owning partial rather than growing the coordinator.

`Movies/Document/Document.cs` owns JSON loading, formatting, persistence and history plus character lookup. Its partials separate Assets (resource operations), Layers (layer lookup/order/appearance/lifetime), Keyframes (sampling and key properties) and Scripts (attachments and external editor launching).

Transform hardening: single and multi-selection gestures share stage-space matrix operations through `MovieTransform` (`Movies/Viewport/Transform.cs`). Synthetic tests cover all opposite anchors, Alt switching, Shift axis/proportion constraints, rotated/scaled parents, reflection, invalid transforms, ancestor filtering and one-step group undo/redo. A document revision counter prevents a no-op at the start of a drag from merging into the previous undo operation. Skew and collapsed-axis transformations are rejected before document changes.

Editor source folders: `Movies/Session/` contains the movie UI partials, `Movies/Document/` the package model and edit operations, and `Movies/Viewport/` the transform helper. `Effects/Session/` contains the effects UI partials, with effects models/helpers in `Effects/`. Reusable layout, theme, readout and timeline-row code lives in `Shared/`. Filenames within each partial-class folder use their responsibility (for example `Inspector.cs`, `Selection.cs`, `Keyframes.cs`) without repeating the class name. Public type names and namespaces are unchanged.

Asset browser presentation (2026-11-11): Assets is the default view and lists shapes, text fields and sprites;
Textures is a separate view, so a one-image/one-shape pair does not appear twice. Both views retain folder and
search navigation. A shape's Source texture button opens its source in the Inspector and switches to Textures;
Used by links navigate back to shapes. These navigation controls also work for read-only archive references.
Importing artwork selects it in Textures; creating a shape returns to Assets. This changes presentation only,
without merging records or modifying package references.

Timeline labels (2026-11-11): frame numbers have their own ruler. Label names are clickable flags in a separate
track, packed into up to three non-overlapping rows; long names have full-name/frame tooltips. Clicking jumps
to that label and pauses. A Labels button opens a searchable, frame-sorted list and can hide the label track.
Extra-dense sections retain overflow markers and list access instead of overlapping names or unbounded rows.
Label flags have a right-click Go to frame/Delete menu; reference movies allow navigation only. Layer positions,
playhead extent and scroll content account for the label track height. Synthetic layout tests cover dense names,
same-frame labels, stable ordering, overflow and labels at the right edge of the track.

Timeline ownership (2026-11-11): timeline navigation is independent of selection. Clicking a layer, key, viewport
object or hierarchy row only selects it; the open timeline stays fixed. Double-click a sprite name in the hierarchy
or timeline layer list, or use Inspector > Open this timeline, to enter its live instance without isolating it.
The Editing timeline breadcrumb identifies the movie and ancestor instances with each one's local frame counter;
click an ancestor to return. Inspector placement fields explicitly use parent frames, while Sprite playback reports
the selected sprite's local frame. The duplicate Inspector playback/label controls were removed in favor of the
explicit timeline transport. A frame-script count signals logic; archive references expose compiled-script markers
without source, while authored package source stays in Code. Library/asset actions explicitly identify isolated
previews. If an instance disappears or its depth is reused for a different sprite, its context is retained rather
than silently switching to another timeline. Package contexts follow stable editor asset IDs across reindexing.

Scripts on the timeline (2026-11-12): keys say how a frame looks; what happens next (stop, loop, jump, a child sent
to one of its frames) is frame scripts, which the timeline did not show. Each script now has a mark on a "scripts"
track (a square where it stops, an arrow with the label it jumps to), a child it sends somewhere shows "→ frame" on
its own row, and a click shows the script in the Inspector. A package's script is its `.as` file. A game movie's is
read back from its bytecode by `Avm1Text` (Formats/Lmb): statements for straight code, `if (...) goto L` for
branches, an instruction listing when a jump crosses a value on the stack; held in the in-memory description and
shown in the editor only, never written to disk. It is a reading aid for how a movie is wired, not a source to copy:
replacements are still written from the contract and the observed behaviour.
