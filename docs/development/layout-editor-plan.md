# Layout editor (player gameplay layout)

## Context

Players want to rearrange the gameplay screen (move/rotate/scale elements, turn Don-chan, fit 4:3 screens). The effects
editor (`effects-editor-plan.md`) already selects every `ChartMods.Elements` target in the viewport, has move/rotate/scale
gizmos and Don's yaw/pitch/roll sliders. A layout is a modchart whose tracks hold one constant value each, so this is a
restricted mode of that editor plus a fallback file.

## Decisions (user, 2026-11-07)

- **Chart effects replace the layout, no merge.** A chart with effects of its own (TJA `Song.wddmod`, per-course adjunct,
  or the player's per-chart override) plays as authored; the layout applies only to charts without any.
- **Everything is editable**, notes mods included. A player who wrecks their own screen can use **Reset** (deletes the
  layout, back to stock). No fairness whitelist: effects only change drawing, never judgement or scores.
- **Replays do not carry the layout.** As in osu! (your skin, not the replay player's), a replay renders with the
  viewer's own layout. Nothing is added to the replay format.
- **Arcade ignores it**: no layout applied, no editor entry; arcade is always the stock screen.
- Layout mode keeps only the **chart scrubber** (jump to any section to see notes under the layout); no keyframes,
  curves or tracks list.

## Design

- **File**: `<waddamburo data>/modcharts/layout.wddmod` (`PlayerModcharts.LayoutPath`), `ChartModFile` format, one key at beat 0 per track, course-independent.
  ponytail: one layout per install; per-account layouts when home accounts need them.
- **Apply** (`GameplayFlow.cs`, where `PlayerModcharts.Apply` runs): when the chart has no effects after the chart's own
  and the player's per-chart modchart, apply the layout's tracks. Not in arcade.
- **Editor layout mode** (`Waddamburo.Editor`): opened from a key in the gameplay options (home, single player). Shows the
  viewport (selection, gizmos), inspector (values, Don model sliders) and the scrubber; auto-key forced off, every edit
  writes the track's single constant key. Save writes `layout.wddmod`; Reset deletes it after a confirmation.
- **Layering** (user, 2026-11-07): each element gets a draw-order property, e.g. Don-chan in front of everything.
  Today the order is fixed in `TaikoGameplayPresentation` (background movies by traced depth > 2000, Don slotted after
  `donbg`, bar lines and notes, foreground ≤ 1000). New `depth` element property (stepped, not interpolated; default =
  the traced depth, `playfield` = its notes/bar lines as one block) and one draw list sorted by it, re-sorted only when
  a depth changes. Being a normal property, chart modcharts can use it too. Layout mode shows it as bring-forward /
  send-back buttons and a list of elements in draw order.
- Works with any song, so the player can tune it on the one they are playing; when that chart has effects, layout mode
  previews the layout anyway (the chart's effects are hidden while editing).

## Milestones

- [x] 1. Layout file + apply fallback (no chart effects, not arcade) + test that a chart with effects ignores it.
- [x] 2. Editor layout mode: panels stripped to viewport/inspector/scrubber, constant keys, Save, Reset
  (`EffectsEditorSession.Layout.cs`; F7 in a song opens it until milestone 4; F8 ignores the layout).
- [x] 3. Layering: `depth` element property (whole steps, default 0), stable sort by depth in
  `TaikoLumenPresentation.CreateLayers` (stock order untouched when no depth is set); Send back / Bring forward in layout mode.
- [x] 4. Pause menu "Edit layout" (F7 removed); user-checked: move, rotate Don, 4:3, Reset.

## Added after the milestones (2026-11-07, user-driven)

- **4:3 screen** is part of the layout (`camera.aspect` 0.75): songs draw the stage squeezed into 4:3 (renderer
  `Squeeze43`, eased over 0.35 s), the editor previews the same. The toggle sets every element but the backgrounds to
  0.75 height (the player stretches the backgrounds). Under a chart's own modchart a 4:3 layout is kept as
  `ChartEffects.Layout`, applied after the modchart (its motion is relative to the layout); 16:9 layouts are still
  replaced. System indicators and the timing glow are drawn to match.
- Editor: corner and side scale handles (opposite edge fixed, Shift keeps shape), snapping to other elements and the
  stage (Alt off) with guides, click-through selection (a click inside the selection selects what's behind), Freeze
  animations, autoplay while editing a layout, `banner` element (the clear banner now shows in reviews and the editors;
  rebuilt lanes reset it).
- **Several layouts** (`modcharts/layouts/<name>.wddmod`, active one = config `layout`; the old single
  `modcharts/layout.wddmod` moves in as "My layout"). Pause menu › Layouts: stock, each layout (Use / Edit / Remove with
  a confirmation), New layout; Use applies to the running song at once. Rename in the layout editor's name field.
  Menus and corner indicators are widened back by 4/3 on a 4:3 screen.
- **Two-player layouts** are their own list (`modcharts/layouts-2p`, config `layout_2p`): plain element names are the
  first lane's, `*_p2` the second's (`ChartMods.ForLane` splits them), camera and 4:3 shared. Waiwai, arcade and the
  attract's demo play always use the stock screen.
- **combo / timer** elements (2026-11-08): the 100-combo announcement and the resume countdown (host-drawn, placed
  through `TaikoLumenPresentation.PlaceOverlay`). Layout mode's "Show hidden" keeps both on screen (combo re-popped,
  timer looping 3-2-1 silently). On 4:3 they, and parts no element covers, get 0.75 height automatically.
- **Canvas** (2026-11-08): the stage is a box inside the editor panel (renderer `StageBox`); outside it is drawn
  darkened and stays selectable. Wheel zooms about the pointer, right/middle drag pans, Fit resets.
- Element pivots are taken once at the song's start and kept per movie, so rebuilt lanes and the editors place a
  layout exactly as the song does.
