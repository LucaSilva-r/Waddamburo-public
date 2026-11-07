# Effects editor (Waddamburo.Editor)

## Context

Modcharts (`ChartEffects`, `.wddmod` files) work, but writing them by hand is slow. We want a built-in **effects
editor**: notes stay in external editors (PeepoDrumKit…), effects are authored in Waddamburo with the live Lumen
preview. Decisions taken with the user:

- Separate module **`Waddamburo.Editor`** so the engine stays clean.
- **Dear ImGui** (ImGui.NET) for the UI.
- **Shrunk preview + panels**: game stage letterboxed into a viewport, panels around it.
- **Full graph editor** and a **keyframe** timeline (value at a beat; the segment into a keyframe carries its curve).

Ground rules kept: every effect stays a pure function of chart time (seek/practice/replay safe), drawing only
(no `ChartHash` change), Green only.

## Architecture

```
Waddamburo.Platform.Sdl   + ImGuiLayer: SDL input -> ImGui IO, ImGui draw data -> bgfx (existing quad program)
Waddamburo.Catalog        + keyframe tracks + bezier curves in ChartModFile / ChartMods
Waddamburo.Game           + TaikoLumenPresentation.ReplaceEffects (live preview of edits)
Waddamburo.Editor (new)   document model, undo, panels, timeline, graph, viewport gizmos (ImGui widget code)
Waddamburo.App            opens the editor from practice mode; references Editor
```

- `tests/Waddamburo.Architecture.Tests/DependencyRulesTests.cs`: add
  `["Waddamburo.Editor"] = ["Waddamburo.Catalog", "Waddamburo.Game", "Waddamburo.Lumen", "Waddamburo.Platform.Sdl"]`,
  App gains `Waddamburo.Editor`. ImGui.NET's P/Invokes live in its own assembly, so the "native imports only in
  Platform.Sdl" rule holds.
- `Directory.Packages.props`: `ImGui.NET` (bundles cimgui for win-x64/linux-x64). `eng/package.sh` must ship the cimgui
  native with the other self-extracted natives (check the single-file layout).

## 1. ImGui in Platform.Sdl (no new shaders)

- **Rendering** (`Rendering/RenderDevice.cs`): the quad program (`vs_quad`/`fs_quad`, layout pos2/uv2/color0 f4/
  color1 f4, positions normalised 0..1) draws ImGui as is: per ImGui vertex `color0 = rgba8/255`, `color1 = 0`,
  position / display size. Use transient vertex + index buffers (`bgfx.TransientIndexBuffer`) and `bgfx.set_scissor`
  per draw command, in a new view after `MainView`/`PresentView` covering the whole window. Font atlas via
  `UploadRgba8`; `ImTextureID` = `RenderTextureId.Value` so the editor can show textures too.
- **Input** (`SdlApplication.cs` event loop, lines ~760–840): when an `ImGuiLayer` is attached, feed it mouse
  pos/buttons/wheel, key down/up with modifiers and `SDL_EVENT_TEXT_INPUT` (start/stop text input exists:
  `StartTextInput`), and let the game skip input ImGui wants (`WantCaptureMouse/Keyboard`).
- **Viewport**: the editor sets `RenderDevice.Letterbox` to place the stage in the middle region; expose the last
  stage viewport rect so window-space mouse positions map to stage coordinates (click-to-select).
- New `ImGuiLayer` (Platform.Sdl): owns the ImGui context, `BeginFrame(dt, windowSize)`, `Render()` → draw data handed
  to `RenderDevice.Present`.

## 2. Engine: keyframes and curves (format not released yet, so `.wddmod` v1 is redefined in place)

- **File** (`src/Waddamburo.Catalog/ChartModFile.cs`): tracks of keyframes per `target.property`:
  ```json
  { "wddmod": 1,
    "tracks": { "don.yaw": [ { "beat": 8, "value": 0 },
                             { "beat": 16, "value": 360, "ease": "outBack" },
                             { "beat": 20, "value": 90, "curve": [0.3, 0, 0.2, 1] } ] } }
  ```
  A song file beside a TJA nests the same per course: `"courses": { "*": { "tracks": … }, "oni": { "tracks": … } }`.
  Keyframe semantics (animation-tool style): before the first key the track holds its first value; each later key
  eases from the previous key's value over the beats between them, by its `ease` (named) or `curve` (cubic bezier,
  CSS-style handles). Converted at load to the existing `ChartModEase` list (one segment per key), so
  `ChartMods.ValueAt`, `ChartModValues`, `NoteGeometry`, `StageEffects` stay unchanged.
- **Bezier**: `ChartModEase` gains an optional `Curve` (x1, y1, x2, y2); `ChartMods.Eased` solves it (Newton on x(t),
  bisection fallback). `ChartDrawing` (online block) serialises it.
- Course-specific tracks replace the corresponding wildcard track as a whole; an empty track disables it. Keys are
  sorted on read and must have distinct beats. `ease: "step"` holds the previous value until the next key (instant SET).
- **Writing**: `ChartModFile.Write(tracks)` for the editor; round-trips with `Read`.
- Migrate `Test Charts/Modchart - EASE and SET demo/Modchart Demo.wddmod` and the mikuse user file.
- **Live preview** (`src/Waddamburo.Game/Gameplay/TaikoLumenPresentation.cs`): `ReplaceEffects(ChartEffects)` rebuilds
  `_mods`, `_geometry`, `_stage` and clears `_noteWindows`/`_barStreams`; exposed through `TaikoGameplayPresentation`
  (App) per lane.

## 3. Waddamburo.Editor

**Document** (pure, unit-tested): `EffectDocument` = immutable map trackKey → keyframes (beat, value, ease | curve).
Undo/redo = stack of documents. Load/save via `ChartModFile`. Beat ↔ time through the chart's beat map
(`ChartEffects.Beats`, else `ChartModFile.BeatsOf(chart.TimingPoints)`). Every edit → `ChartModFile.Apply`-equivalent
→ `ReplaceEffects` for an instant preview.

**Layout** (fixed docked ImGui windows around the letterboxed stage):
```
+-- toolbar: Save | Undo Redo | ▶ speed | loop A-B | snap 1/4 | auto-key ------------------------+
| Targets      |                                                         | Inspector             |
| camera       |              live game stage (letterboxed)              | keyframe: beat value  |
| playfield    |              click an element to select it              | ease ▾  [curve thumb] |
| don (+model) |              gizmo: move / rotate / scale               | bezier x1 y1 x2 y2    |
| dancers …    |                                                         | element now: x y rot  |
| notes        |                                                         |                       |
+--------------+---------------------------------------------------------+-----------------------+
| Timeline | Graph   (beats; bar lines from the chart; playhead; loop region)                    |
| don.yaw     ◆─────────outBack─────────◆            ◆                                            |
| notes.drunk           ◆──◆                                                                      |
+-------------------------------------------------------------------------------------------------+
```
- **Targets**: `ChartMods.Elements`/`NoteProperties`/`DonModelProperties`; “+” adds a track.
- **Timeline**: rows per track, keyframe diamonds, drag to move (snapped), box/multi-select, playhead = practice
  `ReviewClock`; click ruler to seek; wheel zoom, middle-drag pan.
- Panel snapping/docking is requested for the editor layout milestone (the integration demo is temporary).
- **Graph**: selected track(s) as value-over-beat curves drawn with ImDrawList; drag keys (beat/value) and bezier
  handles; frame-all.
- **Inspector**: exact numbers, easing dropdown with curve thumbnails.
- **Viewport gizmos**: click selects the element under the cursor (bounds from `StageEffects` pivots, extended to
  bounds); drag to move, ring to rotate, corner to scale; with auto-key, writes keyframes at the playhead. Don-chan's
  model: yaw/pitch/roll sliders.
- **Controls**: Space play/pause, ←/→ beat (Shift measure), K key the selected property, Del, Ctrl+Z/Y, Ctrl+S,
  A/B loop (practice's own keys).

**Saving**: the chart's own player file `waddamburo/modcharts/<chart hash>.wddmod` (`PlayerModcharts.PathFor`); for TJA
charts also “Save into Song.wddmod” (its course section) so authors ship it with the chart.

**Entry point**: home mode, practice → pause menu “Edit effects” (and a key in practice). Opens the editor session
around the existing `TrainingSession`/`ReviewSession` (clock, seek, loop, speed). Leaving restores the full-size
stage. Judgement is untouched; the drum still plays live when the editor is playing.

## Progress

Work spans several sessions. Tick a box when a milestone is committed, and add a dated note: what landed (commits),
what changed from this plan, and anything the next session must know. Start each session from the first unchecked box.

- [ ] 1. ImGui layer + Editor skeleton
- [x] 2. Keyframe format + bezier + ReplaceEffects
- [x] 3. Read-only editor (layout, transport, timeline, graph view)
- [x] 4. Editing (keys, inspector, undo/redo, save/load, live preview)
- [ ] 5. Graph editing (keys and bezier handles)
- [ ] 6. Viewport (click-select, gizmos, auto-key, Don model sliders)

### Notes

- 2026-11-07: plan agreed (ImGui, shrunk preview + panels, keyframes, full graph editor). Branch `gimmick-charts`
  (merges to master once the editor is usable). State before milestone 1: modcharts load from `.wddmod` beside a TJA
  or `waddamburo/modcharts/<chart hash>.wddmod`; effects run through `ChartEffects` → `NoteGeometry`/`StageEffects`.

- 2026-11-07: milestone 1 implementation landed in `d6ecda3` (ImGui.NET 1.91.6.1, owned UI context, SDL event
  capture, indexed/scissored bgfx overlay, high-DPI scaling, Editor project and dependency rules, practice pause
  menu / F8 entry, Esc exit, cursor/text-input/letterbox restoration). No new shaders. Linux GPU smoke rendered
  the demo successfully at 150% display scale; a second synthetic smoke passed closing during an active frame,
  reopening, viewport restoration and double disposal. All 685 managed tests pass (the 690 count above was stale).
  Linux self-contained publish includes `libcimgui.so`; `eng/package.sh windows` produces the single executable,
  with a build assertion that `cimgui.dll` enters its self-extracting bundle. Dependency and font licenses ship in
  the release notices. Milestone 1 stays unchecked until the user-driven practice entry/exit check and Windows VM
  runtime check are completed. Keyframe/editor authoring features (milestones 2–6) are not implemented yet.

- 2026-11-07: user confirmed the ImGui UI renders correctly. The Windows runtime check remains outstanding;
  Linux confirmation does not stand in for that check. Panel snapping/docking is part of the layout work.

- 2026-11-07: milestone 2 landed in `b5b6b1c`. Version 1 `.wddmod` now reads/writes keyframe tracks;
  first values hold through pre-roll and initial delays, named/Bezier curves describe incoming segments, and course
  tracks replace matching wildcard tracks. Added `step` easing for instantaneous SET changes. Legacy `changes`
  files report a migration error instead of silently dropping effects. Compiled initial-value metadata preserves
  pre-roll semantics without changing TJA SET timing. `ChartDrawing` writes v2 (initial values and curves), reads
  v1/v2; older binaries cannot consume the new v2 drawing blocks. `ReplaceEffects` rebuilds geometry/stage caches
  per lane and clears prior target offsets without resetting judgement. Fixed empty-chart drawing deserialization.
  All 703 managed tests pass (18 new cases); the App builds. The demo and player modcharts were migrated locally
  with `.pre-keyframes.bak` backups; one same-beat rise/reset endpoint in each is separated by one millionth of a
  beat, with maximum sampled value deviation below 0.000007. Local paths and files remain outside this repository.
  Restart using the updated build before testing migrated files. UI remains the integration demo; next is milestone 3
  (layout, transport, timeline and read-only graph), with panel snapping/docking requested by the user.

- 2026-11-07: milestone 3 landed in `84fc9c9`. The demo is replaced with target/inspector panels, transport and
  A/B loop controls on the existing practice clock, a beat timeline with chart bars, key inspection and ruler seek,
  and multi-track read-only graphs with wheel zoom, middle-button pan and frame-all/selected. Edge-attached panels
  resize using dividers and snap near their default proportions; arbitrary floating-window docking is not implemented.
  The stage aspect-fits an explicit normalized center region and restores its prior region on exit. Authored keyframes
  (including first-key beats and empty tracks) remain local chart metadata so the UI does not guess them from timed
  drawing segments; chart hashes and drawing-block formats are unchanged. 715 tests pass, the App builds, and a
  synthetic native UI smoke verifies timeline seek, divider resizing, playback, key inspection, graph selection,
  pan/zoom and viewport restoration at 150% DPI. Preview viewing is usable; authoring/save remains milestone 4.

- 2026-11-07: milestone 4 landed in `ba8e35d`. Immutable documents and gesture history now support adding,
  moving, deleting and box/multi-selecting timeline keys, exact inspector numbers, named easing thumbnails and
  Bezier handles as numeric fields. Every edit updates the practice preview and survives seeks. Ctrl+S atomically
  saves the player override; TJA authors can also save the selected course into the song adjunct while retaining
  other courses and masking deleted inherited tracks. Reload and unsaved-close confirmation are available.
  Native chart-command effects remain a read-only preview until “Start keyframe modchart” explicitly replaces them;
  Undo restores them. User confirmed milestone 3 preview controls. Focus-loss auto-pause is disabled during editing
  (`75928a0`); native pause-menu input also stays out of the editor. All 727 tests pass; App builds without warnings.
  A synthetic native UI probe verified snapped dragging as one undo step, redo, numeric input/Enter, Ctrl+S round-trip,
  K at playhead, unsaved prompt/cancel and closing, with six live preview updates. Graph dragging and viewport
  manipulation remain milestones 5 and 6. Windows runtime validation from milestone 1 remains pending.

## Milestones (each ends committed and tested)

1. **ImGui layer + Editor skeleton**: ImGuiLayer render/input, letterboxed stage, Editor project + dependency test,
   open/close from practice showing the ImGui demo window. Verify on Linux + Windows VM (packaging).
2. **Keyframe format + bezier + ReplaceEffects**: Catalog/Game changes, migrations, tests.
3. **Read-only editor**: layout, toolbar transport bound to ReviewClock, timeline showing tracks/keys/bars/playhead,
   click-to-seek, graph view read-only.
4. **Editing**: add/move/delete keys, inspector, undo/redo, save/load, live preview.
5. **Graph editing**: drag keys and bezier handles.
6. **Viewport**: click-select, move/rotate/scale gizmos, auto-key, Don model sliders.

## Critical files

- `src/Waddamburo.Platform.Sdl/Rendering/RenderDevice.cs`, `src/Waddamburo.Platform.Sdl/SdlApplication.cs`, new
  `src/Waddamburo.Platform.Sdl/ImGuiLayer.cs`
- `src/Waddamburo.Catalog/ChartModFile.cs`, `ChartMods.cs`, `ChartDrawing.cs`
- `src/Waddamburo.Game/Gameplay/TaikoLumenPresentation.cs`, `StageEffects.cs`
- `src/Waddamburo.App/Flow/TrainingSession.cs`, `GameShell.cs` (frame/input routing), `Gameplay/PlayerModcharts.cs`
- new `src/Waddamburo.Editor/*`, `tests/Waddamburo.Editor.Tests/*`
- `tests/Waddamburo.Architecture.Tests/DependencyRulesTests.cs`, `Directory.Packages.props`, `eng/package.sh`

## Verification

- Unit tests: keyframe → ease conversion, bezier solver (endpoints, monotonic x), file round-trip, document undo/redo,
  beat↔time mapping; existing suites stay green (690 tests).
- Manual (user drives, per CLAUDE.md): practice a chart → Edit effects; add a `don.yaw` track, keys at two beats,
  outBack, scrub/play to see Don spin; drag a key in the graph; select dancers in the viewport and drag them; save,
  restart the song normally and see the effect; Ctrl+Z restores.
- Windows build via `eng/package.sh windows` and run on the Windows VM to confirm cimgui ships.
