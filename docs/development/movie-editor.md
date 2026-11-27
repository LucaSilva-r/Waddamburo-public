# Movie editor

The editor for [free movies](free-movies.md) packages: its own editor (user, 2026-11-11), not a mode of the effects
editor; they share the shell only. Claude builds the backend (document operations, format, runtime hooks, tests); the
UI follows Blender's conventions and colours where possible, laid out like Unity's editor (user, 2026-11-12).

Status: read side done; write side built in parts: document, undo, save, timeline rows and keys, stage gizmos, assets
browser, code list with errors. Done when the timer can be rebuilt in it from an empty package. Script text is edited
in an outside editor only (user's call).

## Skin editor (2026-11-27)

`Waddamburo.App --skin[=folder]` opens the editor on a whole skin (bare: the nearest `movies/` with a skin.json, up
from the current folder). `--inspect=scene:<screen>` starts on a scene view (for screenshots).

- **Home**: Scenes, every screen of `scenes.json` as a flow map (boot, entry, song select, rainbow, gameplay, results;
  the dan dojo below; transitions in their own row) with live thumbnails; Components, every package by area with a
  thumbnail and the screens using it, a slot without a package made on click.
- **Scene view**: the screen playing; layers listed back to front (gameplay's own movies first). The stage works as
  the component editor's, on the same code: the tools and handles (`StageHandles`: Q free transform, W move, E
  rotate, R scale), the group change (`MovieTransform.Change` / `TryApply`, each layer read back as place, turn,
  scale), the snapping (`EffectGizmo.Snap`, plus a grid) and the keys. Several layers are selected at once:
  Ctrl+click adds or drops one, Shift+drag draws a box, a click again on the same spot reaches the layer under;
  Ctrl/Shift in the list too. Arrows nudge the selection, Delete removes the skin's selected layers, F frames it; a
  canvas (wheel zooms about the pointer, middle drag moves, Home fits; the screen fitted with a margin). One gesture
  is one undo step (`SceneDocument.EditMany`). Drag a component onto the stage to add it. Saves `scenes.json` (Ctrl+S).
- **Gameplay's own movies** (lane, hit target, gauge, song info, combo...) are listed first and placed with the same
  tools as the skin's layers, written as scenes.json gameplay `places`; Back to the layout's place drops one. The
  notes follow `lane_hit` (the hit target); the engine's note templates are not shown.
- **Beside the game (F7)**: in Waddamburo's frontend, F7 starts the skin editor as a second process
  (`--skin=<folder> --attach=<port>`, `App/Flow/SkinEditorLink`), on `--skin-dev`'s folder or the nearest `movies/`
  (`SkinStage.FindRoot`). The game then plays that folder's packages and shows a saved change at once
  (`SkinDevReload`: the folder listed every half second, kept copies dropped, the screen shown again; Song Select
  under its quick rainbow; during a song, after it). Over a loopback socket (`Skin/SkinLink.cs`, one JSON line a
  message) the game tells the editor its screen (the scene view follows) and everything its provider gives each movie
  on it (`LumenPlayer.ProviderTap`, `ProvidedNow` for a late join); the editor's players of those movies follow it
  (`SkinLive.Follow`), shown as "Live from the game" in the scenario picker: the real song list, the real play. F7 is
  caught by the window loop before ImGui (`SdlApplication.SkinEditorKey`). The game sends each state at most once a
  tick (its last value) and events in order; the editor takes them in for at most 4 ms a tick, so a song never holds
  it up. While the editor runs (`GameShell.SkinEditing`): plays are voided (not saved, not sent) with the
  "Scores not saved: skin editor open" banner, and the game does not pause when it loses focus. Closing the editor
  restarts the game from boot (`RequestRestart`): nothing of the session stays. Batches keep the order things were
  given in (`ProvidedBatch`: an older value of a state dropped, nothing reordered): scripts react to it (Song Select
  shifts its panels on a count that follows its own open).
- **A saved package swaps in place** (`SkinDevReload` → `LumenGameSceneInstance.Swap`): its movie on the shown screen
  is replaced, the screen going on: the engine's states and lists, request handler, editor tap and host carried over,
  its textures appended (`ScenePresenter.AddSceneTextures`). Song Select keeps its list and cursor (its flow restarts
  them only for another showing). scenes.json reloads the screen as before; during a song the screen reloads after
  it (the lane holds its movies). A script's own state (a timer mid-way) starts over. `--skin-dev=<folder>` alone gives the
  reload without the editor. ponytail: unsaved edits reach the game on save only; the editor's movies send no
  requests to the game.
- The component editor's player gets the same data as its part in the skin (live, else the example data); its Wd
  panel publishes only what was typed.
- **Picking a screen moves the game** (linked by F7): a click on the home page's flow map, or Show in game in a scene
  view, asks the game for that screen (`SkinLinkWire.Show`). Entry, Song Select (a guest), the dan list, the tutorial
  and boot are reached straight; gameplay goes through Song Select and plays its first song (the flow smoke's request,
  `FlowSmokeRun.FirstPlay`); a transition plays over the screen. Results, and anything during a song, are refused
  with the reason in the editor's bar (`Notice`).
- **Problems** (`Skin/SkinLint.cs`; the home page's Problems tab, `--lint-skin[=folder]`, exit 1 on an error, run by
  the skin repository's CI): skin.json fields; scenes.json as the engine reads it; screens naming a missing package;
  per package: movie.json, assembly, scripts that do not compile, Wd names the engine does not give, missing
  textures, unused assets (an exported sprite counts as used: scripts make it); preview scenarios. Checked in the
  background after each save; a line opens its place (VS Code at the line, or the package). File > Export skin
  (zip) writes `<name>-<version>.zip` beside the folder, without dot folders.
- **Live scene window** (F4, Skin menu) over the component editor: the screen with the package being edited, from its
  unsaved document; only the edited movie restarts.
- **Example data** (`Skin/PreviewData.cs`): every state at its sample, lists filled, both drums joined with names, the
  engine's `LibraryRows` over an example library for Song Select and the dan list (the movie's row requests answered).
  Over it, **preview scenarios** (`Skin/PreviewScenarios.cs`): `previews/<screen>.json` in the skin, named sets of
  states (`"player.name@2"`: a drum's), lists (their `.count` set too) and timed events looped
  (`{"at": 0.5, "name": "note.judged", "args": [...]}`, `"loop": 2`), names checked against WdNames. Chosen in the
  scene view and the live window; New adds one to the file and opens it in VS Code; a saved file is read again.
  ponytail: gameplay scenarios are states and events, not a chart played (the --play driver could feed one later).
- Built on `SkinStage` (plays screens and packages into preview textures: `SdlApplication.CreatePreview` /
  `DrawPreview`, renderer views 4-10, 7 a frame) and `SceneDocument` (scenes.json edits, refused when a screen would
  lose a required role). Thumbnails are drawn again each start (no disk cache yet). The live window is an ImGui
  window inside the editor, not an OS window yet.

## Read side

Built (`MovieInspectorSession`, over the player's `Inspect…` calls, which leave the edge log alone):

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

## Write side (packages only)


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

## Looking at a game movie

A movie from the game data opens in the same panels, to look at only. `MovieReference.Describe` reads it into the
shape of a movie.json (in memory, never written; `MovieDocument.ReadOnly` ignores every edit and refuses every file
operation): sprites as layers with keys (a tween stored as a placement per frame becomes its end keys, a held value is
marked), labels, script markers (`action 12`, no code), shapes, textures. Characters are named by id and where they
are used (`sprite_1205_GenreFolderTab`). The **Library** tab lists every sprite the movie defines (frames, layers,
scripts, labels, used in); choosing one plays it alone in the middle of the stage (`ShowSprite`, a player rooted at
that sprite) with its timeline below. Approximate where the package format is simpler: a shape's first quad only, no
skew, no colour add. `--inspect=sprite:<part of name>` starts on a sprite.

## What the panels are built on

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

## Assets, as built

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

## Notes by area

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

Way back up (2026-11-27): while inside a nested movie (an instance's timeline opened, or a sprite entered), the
stage shows the same breadcrumb as the Timeline tab at its top left, with Up (Alt+Up): out of the instance timeline
to its parent's, else out of the sprite entered.

Scripts on the timeline (2026-11-12): keys say how a frame looks; what happens next (stop, loop, jump, a child sent
to one of its frames) is frame scripts, which the timeline did not show. Each script now has a mark on a "scripts"
track (a square where it stops, an arrow with the label it jumps to), a child it sends somewhere shows "→ frame" on
its own row, and a click shows the script in the Inspector. A package's script is its `.as` file. A game movie's is
read back from its bytecode by `Avm1Text` (Formats/Lmb): statements for straight code, `if (...) goto L` for
branches, an instruction listing when a jump crosses a value on the stack; held in the in-memory description and
shown in the editor only, never written to disk. It is a reading aid for how a movie is wired, not a source to copy:
replacements are still written from the contract and the observed behaviour.
