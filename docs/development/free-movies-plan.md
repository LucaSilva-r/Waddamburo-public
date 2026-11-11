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
  textures/*.png    referenced by name from movie.json
  scripts/*.as      ActionScript 2 (milestone 2)
```

`movie.json` (`Waddamburo.Formats.Package.MoviePackage`; comments and trailing commas allowed; sample in
`samples/movie-package/bounce`):

- `format` (1), `frameRate`, `root` (a sprite's name), `textures` (name → file).
- `shapes`: name → `rect` [x, y, width, height] in the shape's own space, `texture` and optional `uv`
  [left, top, right, bottom]. One textured quad, as in Lumen: no vector art. Without a texture the shape is a slot the
  host draws into (a native fill).
- `sprites`: name → `frames`, `labels` (name → frame) and `layers`. A layer is one character (shape or sprite) at one
  `depth` (its order in the list when left out), with an instance `name`, a `blend` (`normal`, `add`), `keys` and
  `until` (the frame it is removed on; never when left out).
- A key: `frame`, `x`, `y`, `scale` or `scaleX` / `scaleY`, `rotation` (degrees), `alpha`, `tint` [r, g, b], `hold`.
  Values run straight from one key to the next unless `hold`. This is the model the editor edits (layers and
  keyframes); curves other than straight lines are the editor's to add.
- Not yet: texts, masks (`ClipDepth`), colour add, several quads per shape, seek snapshots (a seek replays the
  timeline from frame 0).
- ponytail: loose directories only; a single-file archive for distribution when packs are shared.
- ponytail: PNG decoded to RGBA at load; BC7 through the existing upscale cache if load time matters.

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

### Ideas for our own runtime (user, 2026-11-11)

Since both ends are ours, the package format may grow features Lumen never had, where they speed up making movies:

- **SVG textures**: a texture file may be an `.svg`, rasterised at load at the size the movie needs, so art can be
  generated or edited as text. Planned for milestone 3 in place of drawn art.
- **Text**: one text function in the runtime, drawing through the engine's FreeType renderer, for every text a free
  movie shows (digits included), instead of baked glyph textures. Also milestone 3.

Bandai movies never see these; free movies still answer the same callbacks.

### Movie editor

A third mode of `Waddamburo.Editor`, opened from the dev tools on any movie:

- **Tree** panel (sprites and their children), **stage** with click selection and the existing gizmos, **timeline**
  (frames, labels, per-depth placements; the keyframe widgets), **inspector** (matrix, colour transform, blend, texture,
  UVs, label, depth), **textures** panel (import PNG, cut a region into a shape).
- Scripts are edited in the user's own text editor; the editor watches the package and reloads on change (as
  `ChartFileWatch` does for TJA), keeping the current frame where it can.
- A **host panel** calls the movie's callbacks by hand with typed arguments, so a movie can be tried without the game.
- A Bandai movie opens **read-only**: inspect, measure, play. No save, no copy of its shapes, textures or scripts into
  a package.
- Save writes the package; undo as in the effects editor.

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
- [ ] 3. First replacement, by hand: `time_counter` from its contract page, its art as SVG and its digits through the
  text function (see the ideas above); empty log diff against the stock one in every scene that uses it.
- [ ] 4. Editor, read side: tree, stage selection, timeline scrubbing, inspector, host panel; works on Bandai movies.
- [ ] 5. Editor, write side: edit, textures panel, save, undo; the milestone 3 movie rebuilt in it.
- [ ] 6. Gameplay screen complete in free movies; a build started with an empty data directory plays a TJA.
- [ ] 7. Remaining screens by flow order (song select last: the largest script).

Milestones 1 to 3 need no editor and carry the risk (does a package movie written from a contract behave like the
stock one). The editor comes after so it has something to save.

## Open

- Art: who draws the free assets, and their licence (must allow redistribution with the engine).
- Don-chan is a 3D model (`don-rendering.md`), sounds are NUB banks: both need free replacements too, outside this plan.
- Text: movies use device fonts through `LmbTextDefinition`; a free font to ship.
- Whether artists want SWF import (author in an old Flash, convert quads + AS2 to a package). Only if asked for.
