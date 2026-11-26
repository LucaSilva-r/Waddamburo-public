# Public Waddamburo agent guide

This is the actual public product repository. Implement new engine code, tests,
build configuration, CI, public documentation, and release packaging here.

The parent repository is a private research lab and behavioral oracle. It is not a
source tree to copy. Use it only for semantic requirements, independently described
asset-format facts, aggregate results, and synthetic test expectations. Follow
`docs/legal/evidence-policy.md`, `docs/legal/provenance.md`, and
`docs/legal/release-content-policy.md`.

Public source must not contain original game content, executable-derived code,
runtime addresses, decompilations, raw captures, private paths, credentials, or
local asset inventories. Tests use synthetic fixtures. When adding adapted or
vendored code, record its exact source, revision, license, and destination in the
provenance and third-party notice files in the same change.

The implementation starts clean on .NET 10 and targets Linux x64 and Windows x64.
Do not recreate private proof-of-concept shortcuts unless a public architecture
decision deliberately requires equivalent behavior.

## Native overlays

Build new menus and dialogs outside the game's Lumen movies with `OverlayMenu`,
`OverlayPage`, and `OverlayRow` in `src/Waddamburo.App/Home/OverlayMenu.cs`.
Settings (`Home/ServiceMenu.cs`), Online (`Online/RoomOverlay.cs`), and Song Search
(`Flow/SongSelect/SongSearch.cs`) show how to supply feature-specific pages and actions while
reusing menu rendering, navigation, and modal behavior.

Reuse the existing pieces; never draw a look of your own for something the menus already have:

- An on/off or multi-value option is a settings row: `Value` in braces (`{ON}`/`{OFF}`), `Step` to change it,
  `IsDefault` for its default, a `Hint` line explaining it, on a page with `Defaults: true` (the green
  "[DEFAULT IN GREEN]" look). The player selects it with Enter, then the arrows switch it, exactly as in Settings.
  No `Label: value` text, no colon after the label.
- A message with a few choices (yes/no, OK) is a `Centred` page with `Body` lines (`Flow/SongSelect/CustomUploadPrompt.cs`).
  Centred pages draw no value column: they are for commands, not options.
- A question while the game loads goes on the boot screen with `BootChecks.Ask` (the osu! libraries prompt in
  `Flow/GameShell.cs`); later, an `IInputOverlay` like `CustomUploadPrompt`.
- Labels, values and hints come from `Languages/*.json` (English and Japanese both); the service-menu pages are upper case
  (`OverlayMenu.upper`).

Use `Home/OverlayTextField.cs` for shared text editing. The feature controller
manages when SDL text input starts and stops. For custom drawing, use
`Presentation/OverlayPainter.cs`, as `Flow/SongSelect/ScoreListOverlay.cs` does. These paths
are relative to `src/Waddamburo.App/`.
