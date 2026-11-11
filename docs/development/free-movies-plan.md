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
  Values run straight from one key to the next unless `hold`. This is the model the editor edits (layers and
  keyframes); curves other than straight lines are the editor's to add.
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
- Play / pause, single step (Space, Right).
- Opened by the movie viewer on any movie: `--package=<folder>` or `--archive=… --movie=…` (`--plain` for the bare
  movie; `--inspect[=clip]` with `--ticks` / `--screenshot`). A movie from the game data is reference only.

Write side, to build (packages only):

- **Document** (`MovieDocument`): the editor edits `movie.json` as a tree (layers and keys, not the assembled
  records). There is no Save: every edit is written to the file at once, which reloads the movie on stage with every
  clip put back on the frame it was on (what scripts had set starts over). Undo and redo are earlier and later texts
  of the file. A file the editor has written loses its comments. The script text is never edited here (user's call):
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

#### What the panels are built on

The UI (ImGui, `MovieInspectorSession.*.cs`) only draws and calls these; rules and file handling live below it.

- `MovieDocument` (tests: `MovieDocumentTests`, `MovieDocumentAssetsTests`): `Root` (the JSON tree), `Edit(change,
  continues)`, `Undo` / `Redo`, `Changed`; `Layer`, `KeyAt`, `Value`, `Set` for keys.
  - Assets: `ImportTexture(path)`, `ReplaceTexture(name, path)`, `AddShape(name, texture, width, height, uv)`,
    `AddSprite(name, frames)`, `Rename(group, from, to)` (follows every reference), `Delete(group, name)` (refused
    while used), `Uses(group, name)`, `Unused()`, `Exists(group, name)`. Groups: `textures`, `shapes`, `texts`,
    `sprites`. A refused operation throws `InvalidOperationException` with the sentence to show.
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

Still without UI: texture thumbnails and import / replace / rename / delete buttons, new shape from a texture,
new sprite, the script list with attach / detach / open, the error list (the toolbar shows the first only).

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
  `MovieDocument`, every edit written to movie.json at once, undo / redo; a selected clip's layer shows x, y, scales,
  rotation, alpha and name, a change making a key on the current frame; user-tried, works); (b) timeline rows
  and keys, stage gizmos (built 2026-11-11: a row per layer with its keys, click / drag / double-click / Delete on
  keys, ruler with playhead and labels, frame count, add and remove layers, drag on the stage moves the selected clip;
  scale and rotate by the fields only, no handles yet; curves are straight lines); (c) assets panel (text fields built by hand in the UI; the document operations for textures, shapes,
  sprites and renames are built and tested, their UI is not); (d) code panel with the error list (operations and the
  error list built, UI not). Done when the timer can be rebuilt
  in it from an empty package.
- [ ] 6. Gameplay screen complete in free movies; a build started with an empty data directory plays a TJA.
- [ ] 7. Remaining screens by flow order (song select last: the largest script).

Milestones 1 to 3 need no editor and carry the risk (does a package movie written from a contract behave like the
stock one). The editor comes after so it has something to save.

## Open

- Free movies live in `movies/<archive folder>/<movie>/` in the repository; the game only looks in
  `USRDIR/waddamburo/movies` so far. Shipping them with the engine (and choosing free or original per movie in the
  settings) comes with milestone 6.

- Art: who draws the free assets, and their licence (must allow redistribution with the engine).
- Don-chan is a 3D model (`don-rendering.md`), sounds are NUB banks: both need free replacements too, outside this plan.
- Text: movies use device fonts through `LmbTextDefinition`; a free font to ship.
- Whether artists want SWF import (author in an old Flash, convert quads + AS2 to a package). Only if asked for.

Editor interaction (2026-11-11): assets are selected in the bottom browser and edited in the shared top-right Inspector. Selecting a stage clip, hierarchy entry or timeline layer returns the Inspector to that clip. Asset property gestures use one document undo step; Ctrl+Z / Ctrl+Y also work while a property input has focus.
