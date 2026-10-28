# Song Select scrolling layouts

Settings > Songs > Song Select scrolling chooses **Classic** (the original horizontal
wheel, the default) or **Vertical**. In Vertical, **Vertical scrolling path** chooses
**Linear** or **Curved** (the D-shaped wheel). The vertical path is remembered when
switching back to Classic. Leave Settings and resume Song Select to apply the change;
a restart is not required. The settings apply to both Normal and Waiwai, in 1P and 2P.

The equivalent entries in `waddamburo/config.cfg` are:

```ini
song_select_scroll = classic
vertical_scroll_path = linear
```

Accepted values are `classic` / `vertical` and `linear` / `curved`. Config version 22
adds these entries while preserving the user's other settings. `WADDAMBURO_BROWSER_PATH`
and the private edited overlay are no longer needed for the setting.

## Two-player mode

`waiwai_enabled = false` is the default (config version 23). Two joined players use
Normal Song Select and the folder that switches to Waiwai is hidden. Set it to `true`,
or enable **Settings > Songs > Waiwai (2P)**, to offer Waiwai and its mode-switch folder.
A new two-player join then starts in Waiwai; choosing Normal keeps it for that session.
Disabling the setting and resuming Song Select returns to Normal. Both layouts honor it.

The home player setup starts both offline stands as guests. One drum can confirm both
visible guests; choose **Not playing** on a stand to start solo. The missing drum can
join from Song Select, including while choosing a difficulty, without returning to the
title. The selected song is remembered when Song Select reloads with both drums joined.

Built-in library folder patterns are shared by Classic and Vertical, including the
pink osu! triangles and the continuous vector wave pattern for Original Songs.
Nijiiro and Original Songs share the Normal-mode illustration from the user's local
Waiwai assets, with the same built-in fallback when that illustration is unavailable.
In Classic, custom folder descriptions use compact, centred horizontal paragraphs
when the interface language is English, matching the supplied English genre art.
This follows the interface language independently of the song-title language option.

## Runtime and release

`Waddamburo.MenuLayouts` contains the game's portable layout generator.
The app prepares the vertical layout from the installed
original `song_select` / `waiwai_song_select` movie, before the normal colour/genre
patches and texture upload. The source archives are read only. Classic uses the original
movie. Use original archives when testing switching; an already edited private overlay
cannot supply the classic movie.

The first use prepares each movie once. Derived layouts and replacement caption pixels
are cached in `<cache_folder>/menu-layouts`; the key includes the source movie, its texture
bytes, font bytes and generator cache version. A source/font change regenerates the cache.
Linear and Curved share the same generated movie. Later switches reuse the preparation;
the regular Song Select reload discards the old uploaded scene before showing the new one.
Delete that private cache to force a cold preparation. Bump `CacheVersion` after changing
layout-generation behavior.

The existing app publish and `eng/package.sh` include the managed generator transitively.
It targets `net10.0`, uses no WinForms, and needs no editor executable, source checkout,
Python, junctions or hard-coded developer paths at runtime.
The normal SDL/FreeType native libraries remain required, as for the rest of the app.

Ship the app, its normal native libraries and notices. The built-in osu! logo and triangle
background remain embedded PNG resources. No original DDP/LMB/NUT/font, generated private
layout cache, local config, account, song library or QA screenshot is a release input.
`USRDIR/` and `out/` stay ignored. Commit the generator source/project and its lock file,
the app references and lock updates, the settings/menu/decoder/host/presentation
changes, tests, translations and this documentation. The existing unrelated working-tree
changes still need their own review; do not publish the private development resume or QA roots.

## Verification

- Game tests cover config upgrades, invalid choices and save/reload of all mode/path pairs.
- Format tests cover atomic generated placement batches, seek equivalence and remapped artwork.
- Architecture tests check project dependencies and complete translation keys.
- Native checks must use original archives, both vertical paths, Classic, live switches in both
  directions, category open/close, and the 2P Normal/Waiwai switch. Keep captures/cache under `out/`.
