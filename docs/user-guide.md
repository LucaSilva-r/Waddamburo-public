# Extended user guide

[Back to the quick-start README](../README.md)

This guide covers extra song libraries, account options, saved files, and updates.
For installation, basic controls and online sign-in, start with the README.

## Startup help

If game data is missing or incomplete, Waddamburo shows which files or folders it
could not find. Open your legitimately obtained game data and put the executable
inside `USRDIR`, next to `data`. This notice uses Japanese on a Japanese system
and English otherwise, independently of the game's language setting.

If the original font is missing, the game uses a system font. Open the pause menu
to see the reminder: place your legitimately obtained **DFPKanTeiRyu-XB** font at
**`USRDIR/data/font/font.ttf`**, then restart.

## Files and updates

Nothing else needs installing. Waddamburo keeps its own files in a `waddamburo` folder
next to `data`: settings (`config.cfg`), accounts and scores. Caches go in this PC's user cache
folder (see [Nijiiro songs](#nijiiro-songs)).

Releases update themselves. At start the game checks for a new version, downloads it and
restarts into it. Set `auto_update = false` in `waddamburo/config.cfg` to turn this off.
Without an internet connection the game just starts.

## Menus and settings

### Song Select scrolling

**Settings > Songs > Song Select scrolling** switches between the original **Classic**
wheel and **Vertical**. Vertical also offers **Linear** and **Curved** paths. Resume Song
Select to apply the choice; it is saved for the next launch. The first use prepares a local
layout cache from your installed game data. No edited game archive is needed.

Two joined players use Normal Song Select by default. **Settings > Songs > Waiwai (2P)**
enables Waiwai and its mode-switch folder (`waiwai_enabled = true` in the config).
The missing player can join from Song Select by hitting their drum, including while
choosing a difficulty. See [scrolling and two-player settings](development/song-select-layouts.md).

### Costumes

On Player Entry, choose **Costumes**, next to **Normal Mode**. Use each player's
rims to preview outfits and a centre hit to confirm. In two-player mode, each
player confirms their own choice before Song Select opens.

### Controls

| | Player 1 | Player 2 |
| --- | --- | --- |
| Left rim (Ka) | D | Z |
| Left centre (Don) | F | X |
| Right centre (Don) | J | C |
| Right rim (Ka) | K | V |

- In menus, the rims move and a centre hit picks.
- **Escape** opens the menu: Resume, Restart Song, Settings, Song Select or Return to Title.
- **F11** switches between fullscreen and a window.
- Holding **Q** during a song or its results fades out and restarts the song.
- **Space** skips the startup screens.

You can bind each pad to other keys, controller buttons or a MIDI drum in
**Settings → Drum Controls**. More details: [input](development/input.md).

### Settings

Open **Escape → Settings** to set:
- language and song-title language
- song folders
- volumes
- audio and input offset, with a guided **Calibrate Audio**
- a debounce for drums that send double hits
- fullscreen, resolution, VSync, frame limit and letterboxing
- sharper upscaled textures

Changes are saved to `waddamburo/config.cfg`. That file also holds the cabinet settings
(`mode = home` or `arcade`, `free_play`, `credits_1p`, `songs_per_session`, …). Each
setting in it is explained in a comment. In coin mode, **F2** inserts a coin.

## Custom songs (TJA)

Put your `.tja` charts in a `custom_songs` folder in `USRDIR`, or choose any folder in
**Settings → Songs → Custom TJA songs**. Each chart's audio (`WAVE`) must be inside that
folder. The first folder level becomes a category in song select:

```text
custom_songs/
  Vocaloid/
    Senbonzakura/
      Senbonzakura.tja
      Senbonzakura.ogg
  Anime/
    ...
```

Folders named after an arcade genre (`Pop`, `Anime`, `Vocaloid`, `Children and Folk`,
`Variety`, `Classical`, `Game Music`, `Namco Original`) get that genre's art and colours;
other folders get a generic one. TJA files may be in
UTF-8 or Shift-JIS. `TITLEJA`/`TITLEEN` feed the song-title language switch, and
`DEMOSTART` sets the preview. `SCENEPRESET:` picks a themed gameplay background
(`SCENEPRESET:IMAS`; see the [skin list](development/gameplay-skins.md)). Rolls, balloons, BPM and scroll changes and the
common commands are supported. Branching charts play their Normal route. See the
[compatibility matrix](development/custom-tja.md#gameplay-compatibility).

## Nijiiro songs

Waddamburo can list and play the songs of a Nijiiro installation you own, alongside the
Green songs.

1. Get **vgmstream-cli** from [vgmstream's releases](https://github.com/vgmstream/vgmstream/releases)
   and put it next to Waddamburo, or in a `vgmstream` folder next to it. Nijiiro's music
   is G.719, which Waddamburo cannot ship a decoder for.
2. Open **Settings → Songs → Nijiiro songs** and choose the installation's folder (the
   one that contains `Data` and `Executable`).
3. Restart the game when it asks you to.

The row turns green when the installation can be played. Otherwise it says what is
missing: no data in that folder, a song table that cannot be read (the `Executable`
folder must be there too), or no vgmstream-cli. The installation is only read, never
changed. Decoded music is cached so later plays start quickly.

The cache (decoded music and upscaled textures) lives in this PC's user cache folder:
`%LOCALAPPDATA%\Waddamburo` on Windows, `~/.cache/Waddamburo` on Linux. A game kept on a USB
stick therefore never decodes onto the stick, and finds (or rebuilds) its cache on whichever PC
starts it. **Settings → Songs → Cache Folder** (`cache_folder` in `config.cfg`) can put it back in
the game folder (`game`) or in any folder; a cache left in `waddamburo/cache` by older versions
moves to the chosen place at start.

## TaikOnline

[TaikOnline](https://taikonline.com) saves your scores and shows online rankings in
song select. It's on by default and optional.

- **Signing in (home mode):** the player-select screen before each session offers
  **Sign in**. Scan the QR code or enter the code on the website, and the account is
  remembered on this PC. After that, each drum picks its own account, or **Guest**.
- **Playing at a friend's:** choose **Join with code** on their PC and enter the code on
  the website. Your scores go to your own account, and your login is not
  saved on their PC.
- **Cabinets:** `cabinet_token` in `config.cfg` (from the server admin) lets players log
  in with a 6-digit code shown during the attract loop.
- **Headless setups:** `Waddamburo --login` signs in from the terminal and `--logout`
  signs every account out.
- **Offline:** leave `server =` empty in `config.cfg`. Scores then stay on this PC.
  `server = https://…` points the game at another TaikOnline server.

Scoring, judgement and soul gauge follow the arcade's rules; see
[scoring](development/scoring.md).

## Development

For Windows and Linux builds, tests, release packaging, translations, and
contribution guidelines, see the [Build and development guide](../BUILDING.md).

## What it is and what it isn't

**It is**
- An engine: it runs the game's own Lumen (Flash) screens, textures, sounds and charts,
  read in place from your installation. Nothing is modified.
- A home and arcade game: free play with endless songs and a settings menu on a PC, or
  cabinet rules (coins, credits, songs per session, revival) on a cabinet.
- A player for custom charts (TJA) and Nijiiro songs, shown in the arcade's own song select.
- Available in English and Japanese (日本語), picked from your system language.

**It isn't**
- A copy of the game. No game files, music, fonts or videos come with Waddamburo, and
  none can be downloaded from here. It does nothing without your own copy of the data.
- An emulator or a recompilation. No original program code runs or is included.
- Affiliated with Bandai Namco (see [Legal](../README.md#legal)).
- Finished. Some things are still missing or approximate; bug reports are welcome.
