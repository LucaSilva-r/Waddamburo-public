<p align="center"><img src="eng/packaging/waddamburo.svg" alt="Waddamburo logo" width="128"></p>
<h1 align="center">Waddamburo</h1>
<p align="center"><i>Play the Taiko no Tatsujin arcade data you own, on Windows and Linux.</i></p>

Waddamburo is an independent, open-source engine that plays **Taiko no Tatsujin** arcade
data you already own. Point it at the game files and you get the arcade experience on a
PC, from the attract loop to the result screen, running natively on Windows and Linux at
your monitor's refresh rate. It can also play your own TJA charts and the songs from a
Nijiiro installation, and it can save scores online to TaikOnline.

It is written from scratch in C# (.NET 10), is MIT-licensed, and ships as a single file
per platform.

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
- Affiliated with Bandai Namco (see [Legal](#legal)).
- Finished. Some things are still missing or approximate; bug reports are welcome.

## Getting started

1. Get the latest `Waddamburo.exe` (Windows x64) or `Waddamburo-x86_64.AppImage`
   (Linux x64) from [Releases](https://github.com/LucaSilva-r/Waddamburo-public/releases).
2. Put it in the game's `USRDIR` folder (the one that contains `data`). Waddamburo is
   developed against the **Green** version's data.
3. Run it. On Linux, first make the file executable (`chmod +x Waddamburo-x86_64.AppImage`).

Nothing else needs installing. Waddamburo keeps its own files in a `waddamburo` folder
next to `data`: settings (`config.cfg`), accounts, scores, and caches.

Releases update themselves. At start the game checks for a new version, downloads it and
restarts into it. Set `auto_update = false` in `waddamburo/config.cfg` to turn this off.
Without an internet connection the game just starts.

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
**Settings → Drum Controls**. More details: [input](docs/development/input.md).

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
`DEMOSTART` sets the preview. Rolls, balloons, BPM and scroll changes and the
common commands are supported. Branching charts play their Normal route. See the
[compatibility matrix](docs/development/custom-tja.md#gameplay-compatibility).

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
changed. Decoded music is cached in `waddamburo/cache/vgmstream` so later plays start
quickly.

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
[scoring](docs/development/scoring.md).

## Building from source

You need git and the .NET SDK **10.0.201** (exactly; see [BUILDING.md](BUILDING.md#1-install-the-net-sdk)).
The native libraries (FFmpeg, vgmstream, FreeType, bgfx) are downloaded from the latest
release:

```sh
git clone https://github.com/LucaSilva-r/Waddamburo-public.git
cd Waddamburo-public
./eng/fetch-native.sh       # Windows: powershell -ExecutionPolicy Bypass -File eng/fetch-native.ps1
dotnet run --project src/Waddamburo.App -- "/path/to/game/USRDIR"
```

[BUILDING.md](BUILDING.md) explains how to build the native libraries yourself (Linux, or
MSYS2 on Windows), build the release files with `eng/package.sh`, and run the tests.

Further reading:
- [native builds](docs/development/native-builds.md)
- [diagnostics and developer runs](docs/development/diagnostics.md): single movies,
  scenes, screenshots, scripted input
- [app flow](docs/development/app-flow.md)
- the rest of [docs/development](docs/development)

### Contributing a translation

The game's own text lives in `src/Waddamburo.App/Languages/<code>.json`, one file per
language. To add a language:
1. Copy `en.json` to the new language's code and translate the values. Keep the
   `{0}`-style placeholders.
2. Rebuild. The language appears in **Settings → Language**.

`LanguageFileTests` checks that no keys or placeholders are missing.

### Project rules

Waddamburo is a clean implementation. Asset formats are learned from the files
themselves, and product code never contains addresses, lifted code or original content.
Tests use synthetic data. See the [evidence policy](docs/legal/evidence-policy.md),
[provenance](docs/legal/provenance.md) and
[release content](docs/legal/release-content-policy.md) policies.

## Legal

Waddamburo is not affiliated with, authorized by, sponsored by, or endorsed by
Bandai Namco Entertainment Inc. or its affiliates. "Taiko no Tatsujin" and related
names and marks are the property of their respective owners and are referenced only
to describe compatibility.

Waddamburo source is licensed under the [MIT License](LICENSE). Dependencies keep
their own licenses, listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
