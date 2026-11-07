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
- [ ] 3. Layering: `depth` property, single sorted draw list (Don and the notes block included), order list + buttons.
- [ ] 4. Gameplay options entry key; manual check (user drives): move the lane, rotate Don, 4:3 framing, Reset.
