# Free movies: the engine as provider, movies as presentation

Waddamburo must run with no Bandai file, scripts included (user, 2026-11-10). Every screen is a movie; the game's
movies are the user's Lumen files, ours are **packages** (JSON, PNG/SVG, ActionScript 2) in the repository's `movies/`.
Our movies are not drop-in copies of the game's: they read the engine through a **provider** (`Wd`), and the game's
movies are driven from the same data by an **adapter** in `Waddamburo.Skins.Green`. This is the final design (user,
2026-11-12, confirmed 2026-11-14). The movie editor has its own page: [movie-editor.md](movie-editor.md).

## State (2026-11-14)

Runs on our movies alone, on an empty data directory: boot → entry (mode menu: normal, dan) → song select → rainbow →
song → results → song select; the dan dojo (dan select, course, between-songs card, dan gauge and panel). Art is
placeholder (SVG boxes, text fields). User-checked at home; arcade (coins, rejoin) not yet.

| Part | State |
| --- | --- |
| Gameplay screen | on the provider; the game's movies fully behind the adapter (step 1 below done) |
| Entry, results, dan select | on the provider with requests |
| Song select | on library rows, awaiting the user's check (uncommitted at time of writing) |
| Intermissions | rainbow, fade, shutter, dan covers on the transition protocol |
| Boot, logo, title, notice, tutorial | small callback-era packages (`_global.is_end`, `Command("End")`, `isAllEnd`) |
| `indicator/*`: `msg_coins`, `network_icon`, `time_counter`, `msg_banapass`, `player_name` | on the provider (credits, network, timer, card pairing, names) |
| Everything else | an empty **stand-in** (below) |

Next, in order: song select step 2 (big panel `selected.*`), `tree.search/sort/group/filter`, images (`Wd.fill`),
rankings (`scores.*`); dan / Waiwai / AI battle results on the provider; roll texts (`onp_renda_moji`) and a character
in place of Don-chan's model. Then the end check: the engine and
our movies build and run with `Skins.Green` out of the solution.

## The line

**The engine owns truth. Movies own presentation.**

| The engine, never a movie | A movie |
| --- | --- |
| Rules: judgement, score, gauge, timing, what counts as a clear | How any of it looks, moves and sounds |
| State: who plays, which song and course, credits, options | Layout and animation; which sounds to ask for |
| Files, network, accounts, the audio clock | Navigation *inside* a screen: a cursor, a list, a dialog |
| What happens next: it decides on every request | Asking: "start this song", "go back", "join this drum" |

Not a matter of taste: server rescoring, replays and online play each need a play to mean the same whatever was on
screen. A movie that could change a score breaks all three, so no movie can. Also:

- A movie cannot read files or reach the network. A skin is something people download.
- Notes stay drawn by the engine through the note movies' clips: timing-critical, too hot for the script VM.
- Movies stay thin: AS2 is poor for large logic (the game's song select is 45,000 instructions). A script should be
  mostly "when this changes, show that".

## Decisions

- **Movies stay scripted** (user, 2026-11-10): behaviour lives in the movie, so artists change a screen without an
  engine build. **ActionScript 2**, compiled to the AVM1 the interpreter runs: one VM, so practice seeking and state
  restore keep working (`modchart-design`'s reason for no second VM).
- **Bandai movies are never converted to files**; they load in memory. Packages hold original work only.
- **Every mix works**: any movie of ours among the game's and the reverse. The engine tells a movie of ours by
  capability: a movie whose string table names `Wd` (none of the game's 701 does) uses the provider from creation
  (`UsesProvider`); the adapter leaves it alone and the hosts skip the game's calls on it.
- **The game's movies' workarounds are frozen** (Lumen patches, by-name reaches, the key filter, the home setup
  overlay): Bandai-only, in `Skins.Green`. Our movies get the same features natively through the provider.
- **No skeleton packages** (2026-11-14). A movie with no package of ours is an empty stand-in. Generated callback
  skeletons encoded the game's contract and were removed; the lab's generator (`tools/contract/contract.py
  --skeleton`) makes one only when work on that movie starts, and it is then written on the provider.
- Later (user, 2026-11-11): Waddamburo in a folder of its own (same layout), and a skin setting that points at a Taiko
  Green USRDIR, making the game's movies an optional skin.

## The provider

Movies **pull**; the engine does not push to movies it knows by name.

1. **State**: values under stable names. `Wd.get(name)`; `Wd.on(name, fn)` is told the current value at once
   (undefined while there is none), then every change.
2. **Events**: things that happen at a moment (`note.judged`, `song.ended`). `Wd.on(name, fn)` gets the arguments.
   Events, not polling, are the main route.
3. **Requests**: `Wd.request(name, ...)` asks; the engine decides. Returns true when taken; a refusal is no error in
   the movie, and why is printed and edge-logged (unknown name, a state asked as a request, no screen taking requests
   from this movie, another screen's request, or the screen's own reason).
4. **Lists**: a name with a value per place (`WdKind.List`): `Wd.get(name, index)`, `Wd.on(name, fn(index, value))`.
   The length is its own state (`dan.exam.count`); a removed place is told as undefined, so a movie follows adds and
   removals from the same listeners. Engine side `PublishItem(name, index, value)`.
5. `Wd.has(name)`: whether a name has a value (e.g. a mode's extras).

```actionscript
Wd.on("play.combo", function(combo) { self.number.text = combo > 9 ? "" + combo : ""; });
Wd.on("note.judged", function() { self.value.text = Math.round(Wd.get("play.accuracy") * 100) + "%"; });
Wd.request("song.start");
```

**Names**: `WdNames` (`src/Waddamburo.Lumen/Provider/WdNames.cs`) is the one list (name, kind, type, scope,
description), and the reference for skin makers; this page does not repeat it. A name not in it is reported once,
naming the movie; publishing one not in it is an engine error. A name cannot be both a list and a request
(`row.opened` vs `row.open`). Names under `library.` and `mode.` are avoided (translation-key prefixes the language
file tests scan for). Skin makers get as much current state as they may need (user, 2026-11-14).

**Two drums**: one set of names, optional last argument for the player. A movie gets its own drum's by default, so a
part made once serves either drum; `Wd.get(name, 2)` / `Wd.on(name, fn, 2)` name one (1 left, 2 right). A movie both
drums share gets player 1's. The second drum's and Katsu-chan's parts (`base2p`, `katsu1p`, `katsu2p`) load the first
drum's packages (2P art layout not done: the lower gauge overlaps the upper lane).

**Runtime extras** our movies have and Lumen never had: SVG textures, text fields drawn by the engine (no glyph
textures; no text colour yet), `attachMovie` / `removeMovieClip` / `getNextHighestDepth` (sprites need `export`:
spawn clips as needed, no fixed pools; gotcha: a `stop()` on frame 0 of an attached sprite runs after the caller's
`gotoAndPlay` and freezes it). Missing in the runtime: `String.toUpperCase`; AVM `trace()` output is discarded (test
scripts with `ReadScriptValue` unit tests, `FreeTimeCounterTests`).

**Drums** (2026-11-15): `drum.hit(side, part, drum)` is an event of every screen. In a song the lane tells it to its
drum's movies; on a menu screen of ours the shell tells every movie of the screen each drum's hits, per drum, and
plays their feedback itself (`ui.don`, `ui.ka`; the game's menus play their own). A drum's own movie hears its drum;
another movie hears player 1's with `Wd.on("drum.hit", fn)`, or each drum with `Wd.on("drum.hit", fn, 1)` and
`fn, 2`: a cursor per player (the course select). Our entry, song select, dan select and results read the drums
this way; none reads keys any more.

**App and session state** (2026-11-15): every tick the shell tells each movie of ours on stage (the screen, the
indicators, the intermission) `screen.name`, `credits.freePlay` / `count` / `service` / `toJoin` / `shown`,
`network.state`, and per drum `player.joined` and `player.name` (`GameShell.publishAppState`; a movie hears only
changes). Our `indicator/msg_coins` (the credit line, each drum's name or join prompt) and `indicator/network_icon`
read them; the game's indicator code (`SystemIndicators`, `IndicatorParts`) leaves a movie of ours alone and counts
it as ready, and the home setup's name tags are not drawn over our entry.

Then: `timer.left` / `timer.shown` (the entry's and song select's countdown as their hosts run it; the pause menu's
resume count), `song.stage` / `song.finalStage` (session scope) for song select's tab, `card.accepting` / `code` /
`codeSeconds` / `player` (a cabinet's website pairing). Our `time_counter` (rewritten on these), `msg_banapass` (the
pairing code or the paired player; the engine's own pill steps aside for it) and `player_name`: the engine tells each
front-end board its own drum's `player.name` / `player.joined` (it laid the boards out: `FlowScenes.BoardSide`; the
entry's centre board shows a paired card's player); in a song the lane does. The game's code leaves all three alone
(`GuestNameBoard`, `ResumeCountdown`). Fix on the way: an app-scope name no longer makes a movie a drum's own, and
reads of app names never redirect to player 1.

### Scopes

**A check, not a wall** (user, 2026-11-12). One API, every name always callable; a scope only says when a name has a
value. Outside it the name reads undefined, and the editor (and a planned language server, `Waddamburo --lsp` with a
thin VS Code extension) warns where it is written. A mistake shows an empty field, not a broken movie.

| Scope | Live | Holds (examples) |
| --- | --- | --- |
| `app` | always | clock, credits, network, `transition.*` |
| `session` | first join to the credit's end | `player.*`, the mode chosen |
| a screen: `entry`, `select`, `play`, `result`, ... | while that screen is up | `select.timeLeft`, `play.score`, `result.*` |
| `note` | one roll's own movie | `note.length`, `note.hit` |

Scopes nest: an inner one sees the outer ones. Modes **add** names to a screen, never multiply screens (`dan.*`,
later AI battle and Waiwai extras); a movie asks `Wd.has` for a mode's extras. Results are their own scope, filled
from the finished play. **Nesting**: the scope belongs to the movie the scene places; its child clips, attached
sprites and movies loaded into its clips ask the same. Not built: a package declaring its scope in `movie.json`
(`"for": "play"`, `"needs": ["player"]`), the editor's list of live names, the language server.

### Requests

`WdRequests(screen)` (`Waddamburo.Lumen.Provider`) is one screen's table: its flow says what each request does
(`Handle`) and installs it on the screen's movies; anything else is refused with the reason. Built:

- Entry: `player.join(drum)`, `session.start(mode)` (`normal`, `dan`); states `player.joined`, `entry.timeLeft`,
  `entry.setup`.
- Results: `result.continue`; states `result.*` per drum.
- Select (dan select and song select): `rows.window`, `select.row`, `row.open`, `row.close`, `course.start`,
  `song.start`.

### Song select: one library tree

Song select as versatile as osu!'s (any number of charts per song, much data shown), and universal: the game's spine
carousel and an osu!-style list are two skins over the same data (user, 2026-11-14).

- **The tree**: folders (holding folders: library, genre, collection, search results), songs (holding their
  charts: a stock song's courses, an osu! set's difficulties), and dan courses as special songs (their charts are the
  course's songs; the rules from `dan.exam.*`). The dojo is a folder like any other, series as subfolders. How the
  tree is cut (group, sort, filter, search) is the engine's, on request.
- **What a movie reads**: one flat list of visible rows, osu!-style: a folder or song opens in place, its nodes under
  it one level deeper. `rows.count`; lists `row.kind`, `row.depth`, `row.opened`, `row.title`, ... (windowed); the
  cursor `select.row` and `selected.*` for the big panel; later rankings `scores.*`.
- **What a movie asks**: `rows.window(first, count)`, `select.row(i)`, `row.open(i)` / `row.close(i)`,
  `song.start()` / `course.start()`; later `tree.search/sort/group/filter` (not `library.`), `scores.scope`,
  `select.random`. Live library changes are the same list events.
- **Images** (proposed): an image name whose value is a key the engine can draw, `Wd.fill(clip, name[, index])`.
- Built: `LibraryRows` (`Waddamburo.Game.Library`) and our dan select on it (step 1, user-checked); our song select on
  the same rows over the whole library (step 2, awaiting check). The game's song select keeps its host and patches,
  frozen.

## The game's movies: the adapter (`Waddamburo.Skins.Green`)

Everything that drives the game's movies lives in `src/Waddamburo.Skins.Green` (`Screens/`, `Patching/`,
`Gameplay/GameplayGameMovies`, `GameIntermissions`, `GameDanCard`, `TaikoNoteFaces`, `TaikoDonPresentation`, the
flight pool), as stock songs live in `Providers.Stock`. Only the App references it (`DependencyRulesTests`). The
engine never names a game movie, callback or label; it publishes names, and the adapter subscribes like any movie
and translates (`play.score` → `SetScore`, `drum.hit` → `SetTaikoHit`, `play.branch` → `SetBranch` / route bubble,
long-note events → the balloon's and kusudama's calls, `transition.phase` → the rainbow's labels, ...). The reverse
too: a game movie's `NotifyEndCourseSelect` becomes the same start one of ours requests.

Rule: new code driving a game movie goes in `Skins.Green` from the start. Unexpected game-movie calls are answered by
feeding it the game's data and order, never by filtering the call host-side.

Known awkward parts: state kept inside the game's movies (its song select's cursor and course) is read back by the
adapter; callbacks with no general meaning (costumes, cards, coins) stay adapter-only until one of ours needs them.
Still by name: whether the rainbow's title is on screen (`scene_change_song_name`, to stop the preview).

**Where the moves stand**: (1) gameplay: done, including dan panel, long notes, transitions, between card, board
setup, note faces, song info, lane label jumps, flight pool, Don-chan (the lane still calls Don directly). (2) menus,
one screen at a time as it gets requests: entry and results done, dan select done, song select in progress. (3) the
end check without `Skins.Green`.

### Proof that nothing changed

Each move is proven by recording what crosses every movie's edge (`WADDAMBURO_EDGE_LOG=<file>`) before and after:

- `--play=<tja> [--play-players=2] [--screenshot=x.bmp]` (headless, ~12 s on "Test Charts/Note Kind Check"):
  compare `cut -f2-` per movie; `play.beat` differs run to run (audio thread, expected).
- The empty-directory benchmark (`WADDAMBURO_BENCHMARK_ATTRACT=2 Waddamburo <empty dir> --benchmark`, 1P and 2P): the
  free regression run.
- Dan, Waiwai, AI battle and transitions are not reached by either: recorded user sessions replayed with `--press`
  (lab `research/contracts/logs/`), or the user drives the window.
- Windowed hitch check: `WADDAMBURO_HITCH_TRACE=1` with `--play`, count "Frame hitch" lines.

## Sounds (user, 2026-11-14)

Three layers, split by what each knows:

1. **Decoding and playback: the engine, generic.** A codec is not a skin: ATRAC in NUB containers is needed by the
   stock Green songs and by libraries from other console versions (catalog providers), as are other old formats. The
   native decoder and vgmstream, the mixer and its buses, the voice and coin rules (`SoundBank`, App) take a
   **`SoundSource`**: a file or one stream of a container, plus its settings. Container readers stay format readers
   (`Waddamburo.Formats.Audio`: `NubCueDelay`, `NuSoundBankCatalog`).
2. **Which sound, as authored: the skin's `ISoundSet`** (`Waddamburo.Game.Sound`). `Describe(SoundRequest)` answers
   an engine name with zero or more `SoundCue`s (source + bus). A source's `SoundSettings` (gain, pan in degrees,
   pitch in cents, leading silence) are plain numbers the engine applies; the game's are read from each NUB bank's
   header (`GreenSoundSet`, in `Skins.Green`, read when first decoded, off the frame), a skin of ours writes them in a
   `sounds.json` beside its files (not built):

   ```json
   "drum.don":   { "file": "don.ogg" },
   "drum.don.2": { "file": "don.ogg", "pan": 30, "pitch": 20 }
   ```

3. **When it plays: the engine's rules, by name.** The engine asks for `drum.don`, `combo.voice`, `banner.clear`, ...
   with a `SoundRequest`: `Side` (0 left, 1 right when two players' sounds are kept apart; null centred, also when
   panning is off), `Tone`, `Variant` (a number the name takes: the combo), `Mode` (`waiwai` when the mode changes
   the sound). How a side or a tone sounds is the skin's: Green has `_L` / `_R` / `_C` banks and own cues per player
   (+1 / +2); a skin of ours may give the same file a pan or a pitch. Rules such as AI-don's silent lane, the muted
   demo and roll voices off stay in the engine (`GameplaySounds`).

**Player choices.** A player picks a drum sound in the course select: `ISoundSet.Tones` is the skin's list (names
for a selector, Green's six 音色), a player's tone is an index into it, kept per player for the session, and every
request for that player's drums carries it. Our movies: a list name for the tones and a select-scope request to
choose one (with its preview sound), to add with song select's course panel; `player.tone` already says the choice
during a song. The game's course select keeps choosing through its own calls; the adapter writes the same per-player
setting.

**Our movies** will ask for sounds with a request (`sound.play`, a name of the skin's set) once built; the game's
movies' `RequestSE(bank, cue)` is played as `GreenSoundSet.Cue(bank, cue)`.

Built (2026-11-14): the types (`SoundSource`, `SoundSettings`, `SoundCue`, `SoundRequest`, `ISoundSet`),
`GreenSoundSet` (bank cues with header settings, music, the gameplay names; `GreenSoundSetTests`), `SoundBank`
playing sources, gameplay's sounds asked by name. Then: `ISoundPlayer` (Game; `SoundBank` implements it), the game's
screens' sounds in `Skins.Green/Sound` (`GreenSounds`: bank cues and tracks as its movies ask for them; the attract,
entry and song select, results, retry, game over and Waiwai results controllers; `CountdownCues`), and every sound
the App plays by engine name (`GameSounds.Play`, `PlayMusic`, `PrepareMusic`, `Clip`, `PlayCoin`): `ui.don` /
`ui.ka`, `coin.insert` / `coin.service`, `select.join`, `select.dan`, `dan.finished`, `player.refused`,
`music.dan`, `music.tutorial(.waiwai/.ai)`, `click.accent` / `click.beat`. Sound names avoid the translation-key
prefixes (`menu.`, `setup.`, `calibration.`, ...: `LanguageFileTests`). No game bank or track name is left in the
App or the engine.

Then (2026-11-15):
- **Packs** (`SoundPack`, Game): a folder with `sounds.json` and its files. Lookup per request: `name@mode`,
  `name@tone{t}@side{s}`, `name@tone{t}`, `name@side{s}`, `name@{variant}`, `name`; an entry is one sound or a list
  played at once, with `file` (must stay inside the folder: skins are downloaded), `bus`, `gain`, `pan`, `pitch`,
  `delay`; `tones` lists the drum sounds. A name a pack lacks is asked of the next set down. The chain
  (`GameShell.createSounds`): the user's pack in `USRDIR/waddamburo/sounds`, ours (`sounds/` in the repository,
  copied beside the executable) when `free_movies` is on or there is no game sound data, then the game's
  (`GreenSoundSet`), or silence. A source's cache key has a `Variant`, so one file with two settings decodes twice.
- **Our placeholder sounds**: `sounds/*.wav`, synthesised by `tools/sounds/placeholder_sounds.py` (drum don and ka,
  a second tone pitched up, menu ticks, coin, calibration clicks). Everything else is silent on our skin for now.
- **`sound.play(name[, drum])`**: the first request of scope `app` (`WdRequests.App`, asked by every movie whatever
  its screen): the skin's sound of that name, for that drum's side and tone, else centred.
- **Tones on our song select**: `tone.count`, the list `tone.name`, `player.tone` per drum (scope now `session`),
  and the request `tone.choose(tone, drum)` (sets the player's tone for the session and plays its `drum.don`). Our
  song select movie does not offer it yet: it comes with the course panel.

## Packages

```text
movies/<archive folder>/<movie>/     e.g. movies/indicator/time_counter/
  movie.json        frame rate, textures, shapes, texts, sprites, labels, scripts
  textures/*        PNG or SVG
  scripts/*.as      ActionScript 2
```

**Where a movie comes from** (`DirectoryLumenMovieContentSource`), first hit wins: the user's
`USRDIR/waddamburo/movies/…`; the engine's own `movies/` beside the executable (copied by the build: rebuild after
editing a package before a `--no-build` run) when `free_movies = true`; the game data's archive; the engine's own
when the game data has no such archive; else an empty **stand-in** (`LumenMovieContent.Empty`, `LumenPlayer.StandIn`)
named once in the log, which takes every callback and every label seek. Nothing of the game data is required to
start: no Don-chan data, he is not drawn; no sound data, silence; no `ensolayout.bin`, `EnsoLayout.Standard` (the
game's arrangement, so parts of ours and the game's mix; our movies are authored in each part's own coordinates).

**`movie.json`** (`Waddamburo.Formats.Package.MoviePackage`; comments and trailing commas allowed; sample in
`samples/movie-package/bounce`):

- `format` (2; 1 still loads), `frameRate`, `root`, `textures` (name → PNG or SVG file; an SVG is drawn once at load
  at its own size: author it at twice the shown size).
- `texts`: name → `rect`, `size`, `align`, `color`: a box a script fills, drawn by the engine's text renderer.
- `shapes`: name → `rect`, `texture`, optional `uv`. One textured quad; without a texture a slot the host draws into.
  A shape and a sprite cannot share a name.
- `sprites`: name → `frames`, `labels`, `layers`, `scripts` (frame or label → file), `export` (linkage name for
  `registerClass` / `attachMovie`); the movie's `classes` run once before anything is placed.
- A layer: one character at one `depth`, instance `name`, `blend` (`normal`, `add`, `multiply`, `screen`), `masks`
  (n layers above it), `keys`, `from` / `until`.
- A key (format 2): `frame` and only the channels it names (`x`, `y`, `scaleX`, `scaleY`, `rotation`, `alpha`,
  `tint`); each channel runs between its own keys by the first one's `ease` (`in`, `out`, `inOut`, linear) or `hold`.
  Format 1 keys hold every channel; the editor upgrades on open.
- `MoviePackage.Assemble` turns it into the Lumen record stream in memory, so packages go through the same parser,
  validation and player as archive movies. Packages get no patches and no upscaling.
- ponytail: loose directories only (a single-file archive when packs are shared); textures decoded at every load,
  SVG at a fixed size (a cache and display-scale drawing when load time or 4K matter).

**Compiler** (`Waddamburo.Formats.Package.ActionScript`, one file): AS2 source → the Lumen AVM1 variant. Variables,
functions and closures, objects, arrays, `class` / `extends` / `super` / `static`, `import`, the usual control flow
and operators, `delete`, `trace`; type annotations read and ignored. Not yet (reported as errors): `for..in`, `try` /
`throw`, `with`, `typeof`, `instanceof`, getters / setters, interfaces: each added with its interpreter opcode when a
movie needs it. As in Flash, call arguments evaluate last to first; in a class, inherited members are written
`this.name`. Errors are `file(line,column): message`; the editor's lenient compile leaves failing scripts out and
lists them. Viewer: `--package=<folder>` reloads on save. ponytail: no optimisation (locals by name).

## Contracts (the game's side, private lab)

What crosses a game movie's edge (callbacks in, `Lumen.X()` calls out, clips / labels / fill slots the host
addresses, timing) is recorded per movie into contract pages in the private lab (`research/contracts/`, generator
`tools/contract/contract.py`, logs in `research/contracts/logs/`; asset-derived, so never in this repository). They
say what the adapter must feed a game movie and what a screen of ours must make possible; our movie itself is written
against provider names, not from the game's script.

## Open questions

1. What may a user's widget request? Reading is harmless; requests from an arbitrary package are not obviously so.
   Perhaps only movies a scene lists in a role.
2. Who lays out a scene: scene files a skin can replace, or fixed lists with **slots** (a named empty clip,
   `slot_nameplate_1p`, where the engine places a shared movie and follows its transform; preview data from a
   `preview.json` in the editor). Not built.
3. A shared **asset library** (glyphs, panels, palette copied into a movie's pool at load). Not built.
4. Versioning: one number for the whole provider, or per name. The names are a promise to skin makers: start small.
5. Performance budget for subscribers during a song.
6. `session` as a level, and where the home player setup belongs.

### Limits on what a movie may cost (to do with loading other people's skins)

- A ceiling on clips made by script per movie (around 1024), one warning naming the movie.
- A script budget per movie per tick in instructions run (repeatable, unlike time); over budget: the editor's Issues
  tab, and in the game the movie's scripts cut short for that tick.
- Measure first: flights at 128, 512 and 2048 on the slow test machine.

### Outside the movies

Art (who draws, a licence allowing redistribution), Don-chan's 3D model, sound banks (NUB) and a free font all need
free replacements. SWF import for artists only if asked for.

## History

- 2026-11-10/11: plan; edge recorder and contract tool; package format and loader; AS2 compiler; the free
  `time_counter` (user-checked in arcade); editor read side; game runs from an empty data directory.
- 2026-11-11: menus first: entry, song select, intermissions on game-style callbacks (`Wd…` extension callbacks),
  boot → song → results loop closed on our movies. Generated skeletons for the remaining screens.
- 2026-11-12: provider design accepted; `Wd.get/has/on`, `WdNames`, the gameplay parts on the provider. Editor write
  side, package format 2.
- 2026-11-13: adapter steps 1 and 2 (proof: 33,293 edge-log lines identical), lists, dan names and panel, note flights
  via `attachMovie`.
- 2026-11-14: `Skins.Green` split; long notes, note faces, lane label jumps, Don-chan, transitions behind the adapter;
  `--play`; `Wd.request`; entry and results on requests; library rows, dan select, song select on rows. The callback
  skeletons removed: stand-ins take every label seek.
