# The engine as provider, movies as presentation (design draft)

Status: **accepted as the plan** (user, 2026-11-12; see Decision). Nothing here is built yet. It follows from
[free-movies-plan.md](free-movies-plan.md) and decides how the engine and the movies talk once the engine's own
movies are more than drop-in replacements.

## The problem

Today the engine drives movies by hand. For every movie there is a host class in C# that knows its callbacks
(`SetScore`, `SetComboCount`, `SetCurrentGauge`, `SetGogo`, `SetRendaCount`, `SetPlayer`, ...), calls them in the
order the game was traced calling them, and often reaches inside the movie (clip names, script objects, fill slots).
That is right for the game's own movies: they are a fixed contract we do not control.

For our own movies it is a hybrid with no rule: some things are placed and driven by the movie, some are placed and
overlaid by the engine (name boards at fixed coordinates, song titles drawn into slots, the player setup drawn over
the entry). And it closes the door on what a movie package could be: a user's widget showing accuracy during a song
cannot exist, because no host has heard of it.

## The line

**The engine owns truth. Movies own presentation.**

| The engine, never a movie | A movie |
| --- | --- |
| Rules: judgement, score, gauge, timing, what counts as a clear | How any of it looks, moves and sounds |
| State: who plays, which song and course, credits, options | Layout and animation; which sounds to ask for |
| Files, network, accounts, the audio clock | Navigation *inside* a screen: a cursor, a list, a dialog |
| What happens next: it decides on every request | Asking: "start this song", "go back", "join this drum" |

This line is not a matter of taste. Three things already built depend on it: the server rescoring a play, replays,
and online play. Each needs a play to mean the same whatever was on screen. A movie that can change a score breaks
all three, so no movie ever can.

Corollaries:

- A movie cannot read files or reach the network. A skin is something people download.
- Notes stay drawn by the engine through the note movies' clips: timing-critical, and too hot for the script VM.
- Movies stay thin. ActionScript 2 is a poor language for large logic (the game's song select is 45,000 instructions
  and is the hardest thing in the game to follow). The provider should make a movie's script mostly "when this
  changes, show that".

## The model

Movies **pull** from a provider; the engine does not push to movies it knows by name.

1. **State.** The engine publishes values under stable names. A movie reads the ones it wants.
2. **Events.** Things that happen at a moment (a note judged, a song ended). A movie subscribes to the ones it cares
   about. Events, not per-frame polling, are the main route: polling from scripts costs every frame.
3. **Requests.** A movie asks for something to happen. The engine decides, and the result shows up as state and
   events like anything else. A refused request is not an error in the movie.
4. **Scenes are lists.** A scene says which movies are on stage and where, not code that drives them. A movie the
   engine has never heard of can be added to a scene (the widget case).

Sketch of what a script sees (names and shape to be settled, see Open questions):

```actionscript
// A combo counter, complete.
Wd.on("play.combo", function(combo) {
    self.number.text = combo > 9 ? "" + combo : "";
});

// An accuracy widget nobody in the engine knows about.
Wd.on("note.judged", function() {
    self.value.text = Math.round(Wd.get("play.accuracy") * 100) + "%";
});

// Song select deciding a song: a request, not an order.
Wd.request("song.start", { folder: folder, song: song, course: course });
```

### First data names: the gameplay screen

Taken from what the gameplay hosts push today, renamed for what they mean rather than for the game's callback:

| Name | Kind | Today's push |
| --- | --- | --- |
| `play.score`, `play.combo`, `play.maxCombo` | state | `SetScore`, `SetComboCount` |
| `play.gauge` (0..1), `play.gaugeCleared`, `play.gaugeFull` | state | `SetCurrentGauge`, the fire gauge's labels |
| `play.accuracy`, `play.counts` (good / ok / bad) | state | (nothing: results only) |
| `play.goGo` | state | `SetGogo`, `SetGogoTime`, `SetFever` |
| `play.roll.count`, `play.balloon.left` | state | `SetRendaCount`, `SetGekiRendaCount`, `SetLeftNotes` |
| `play.progress` (0..1), `play.time` | state | `SetProgress` |
| `play.beat` (fractional, every tick), `beat.passed` (whole beat) | state, event | (nothing: the game sets the note faces' frames by hand; built 2026-11-14 so any movie can move in time with the song) |
| `play.time`, `play.duration`, `play.bpm`, `play.meter`, `play.measure` + `measure.passed`, `play.scroll`, `play.rollHits`, `play.balloonHits`, `play.level`, `player.autoplay`, `player.replay`, `song.artist`, `song.genre`, `song.source`, `song.stage`, `song.finalStage`, `play.notes`, `play.notesLeft`, `play.nextNoteBeats`, `play.nextNoteSeconds`, `play.nextNoteKind` | state (one event) | (built 2026-11-14, user: skin makers get as much of the current state as they may need) |
| `song.title`, `song.subtitle`, `song.course`, `song.stars` | state | drawn into fill slots |
| `player.N.name`, `player.N.joined`, `player.N.options` | state | `SetPlayer`, `SetSpeed`, `SetDoron`, ... |
| `note.judged` (kind, result, big, player) | event | `SetHit`, the hit-effect labels |
| `drum.hit` (side, face or rim, player) | event | `SetTaikoHit`, `SetLight` |
| `combo.milestone` (count) | event | `SetComboBonus`, the bonus movies' `Start` |
| `song.started`, `song.ended` (cleared, fullCombo) | event | the result banner's labels |

Per-player values are namespaced by player (`play.N.score`) when two drums play; a movie placed for one drum reads
its own without knowing its number (see Slots).

## Scopes: what a movie can ask depends on what it is for (user, 2026-11-12)

**Scopes are a check, not a wall.** There is one API and every name is always callable; nothing is hidden or removed
by mode. A scope only says when a name has a value. Outside it the name reads as undefined, and the editor and the
language server warn where it is written. So the rule is enforced while authoring, and a mistake that gets through
shows an empty field, not a broken movie.

What data exists depends on the game's state: there is no player on the attract screen, no score in song select. So
the provider is not one flat list. Names belong to **scopes**, a scope is live only while its state is, and a movie
says which it is made for. The purpose of a movie decides what it may use, which also pushes a skin towards sensible
choices.

Scopes nest; an inner one has everything of the outer ones:

| Scope | Live | Holds (examples) |
| --- | --- | --- |
| `app` | always | clock, credits and coin state, network status, the skin's own settings |
| `session` | from the first player joining to the credit's end | `player.N.*`, songs played, the mode chosen |
| a screen: `attract`, `entry`, `select`, `play`, `results` | while that screen is up | `select.cursor`, `play.score`, `results.*` |

Modes do not multiply the screens. A mode **adds** names to a screen's scope: an AI battle adds `play.ai.*`, a dan
course adds `play.dan.*`, Waiwai adds `play.voltage`. The names every mode shares mean the same in each. A movie that
wants a mode's extras asks whether they are there (`Wd.has("play.ai")`) and is otherwise the same movie; one made
only for a mode says so.

A package declares what it needs in `movie.json`, by scope or by data:

- A screen's own movie: `"for": "play"`.
- A shared piece declares the data, not a screen: a name board has `"needs": ["player"]`, so it can go anywhere a
  player exists (entry, select, play, results) and nowhere else.

What that buys:

- **The editor knows what exists.** It lists the names a movie may use, feeds a preview with sample data of that
  scope, and refuses a name board in a slot of the attract screen with the reason: there is no player there.
- **A wrong read is not a crash.** At run time a name outside the live scope reads as undefined and is logged once,
  naming the movie. Mistakes show while authoring, not in a player's game.
- **The adapter is scoped the same way**: the game's entry movie is fed by the entry scope's adapter and no other.

The risk is confusion: too many scopes, or names that drift between them. Kept down by: few scopes (the table above
is meant to be nearly all of them), modes only ever adding, one meaning per name everywhere, and the editor showing
the list rather than a document having to be read.

## The game's movies: an adapter

The game's movies keep working, unchanged, through an **adapter** that is itself just a subscriber:

- The provider does not know the game's movies exist.
- The adapter subscribes like any movie would and translates: `note.judged` to that movie's hit callbacks,
  `play.score` to `SetScore`, and so on. Hardcoded per movie, as the host classes are today.
- The other way too: a game movie's `Lumen.NotifyEndCourseSelect(...)` becomes the same `song.start` request one of
  ours makes.
- Everything specific to the game's movies lives there and nowhere else: Lumen patches, reads and writes of their
  script objects, the key filter, the player setup overlay. This is where "the workarounds stay with the game's
  movies" (free-movies-plan.md, Menus first) physically lives.

Today's host classes are most of an adapter already. The work is moving the engine logic that leaked into them down
behind the provider, so both kinds of movie read one source of truth.

Known awkward parts:

- **State kept inside the game's movies.** Its song select holds the cursor, the chosen course and the "oni
  unlocked" flag in its scripts; the engine reads them back out. The adapter keeps doing so and publishes them.
- **Callbacks with no general meaning** (costume lists, card dialogs, coin panels). They stay adapter-only until one
  of our movies needs the same information; then it gets a name.
- **Order within a tick.** Some hosts depend on it. See Proof.

A movie is the game's or ours by capability, as with the `Wd...` callbacks now: one that uses the provider is ours.

## Proof that nothing changed

Moving a screen behind the adapter must be invisible, and that is checkable:

1. `WADDAMBURO_EDGE_LOG` records every call into and out of each movie. Record a session before, replay the same
   inputs after: the movie's lines must be identical (`tools/contract/contract.py --diff` in the lab).
2. `eng/flow-smoke.sh` gives the scene changes and their ticks: a refactor moves none.

A screen is not moved without a recorded session that covers it.

## Shared pieces

Two different things, only one of which touches the runtime.

**An asset library** (pictures, shapes, text styles, sprites without behaviour). A package names an asset of a shared
library; the loader copies it into that movie's own pool when it loads. The engine still sees a self-contained movie.
No runtime change. One set of number glyphs, one panel style, one palette, for every movie of a skin.

**Shared movies with behaviour** (name board, timer, coin message). They are already separate movies the engine
layers over scenes and feeds. What is missing is that their place is fixed in the engine's code. So:

- **Slots.** A movie has an empty named clip, e.g. `slot_nameplate_1p`. The engine puts the name board's layer where
  that clip is and follows its position, scale and visibility each frame. No such clip: today's fixed place. The
  artist lays the name board out, even animates it; the engine still owns its data.
- The slot's name says which drum it is for, so the board placed there reads "its" player.
- In the editor a slot shows the real package fed with sample data from a small `preview.json` beside it (which
  values, which events). This replaces the developer branch the game's scripts carry for the same purpose.
- Embedding a whole package as a layer of another, with its own scripts, is a large runtime change and waits until
  slots prove too limiting.

## Order of work

Each step: name the data, move the game's host onto it, prove with logs, then let our movie use it directly.

1. **Read-only provider on the gameplay screen, and widgets.** No risk to rules, useful at once, proves the API's
   shape. Our own score, combo and gauge parts are written against it instead of as more game-style callbacks.
2. Results, then entry.
3. Slots and the asset library.
4. Song select last: the most state inside the movie, the most workarounds.

## Costs

- Two paths for a long time: adapter and provider.
- The data names are a promise to everyone who makes a skin; a wrong one is hard to take back. The API is versioned
  and starts small.
- The first step is engine work with nothing new on screen except a widget.

## Decision (user, 2026-11-12)

This is the plan. The one reservation is the scopes: accepted because nothing else stops a name being used where it
has no value, and on the condition that the editor makes them plain (which names are live, why one is not). The
editor is therefore part of this work, not something after it. The open questions below are still open and are
settled as each step reaches them.

## Built so far (2026-11-12)

- `Wd.get(name)`, `Wd.has(name)`, `Wd.on(name, fn)` in the movie runtime (`LumenPlayer.Provider.cs`). A state's
  listener is told at once where things stand (the value, or undefined while there is none; user, 2026-11-12), then
  of every change; an event's listener gets its arguments. `Wd.request`
  is not built.
- `WdNames` (`src/Waddamburo.Lumen/Provider`): the one list of names, with kind, type, scope and description. A name
  not in it is reported once on the console, naming the movie. Publishing one not in it is an engine error.
- The gameplay screen publishes to every movie of a lane: `play.score`, `combo`, `maxCombo`, `gauge`,
  `gaugeClearLine`, `gaugeCleared`, `gaugeFull`, `good`, `ok`, `bad`, `accuracy`, `course`, `goGo`, `roll`, `balloon`,
  `progress`; events `note.judged`, `combo.milestone`, `score.added`, `song.ended`.
- **Two drums** (user, 2026-11-12): one set of names, an optional last argument for the player. `Wd.get(name)` and
  `Wd.on(name, fn)` give a movie its own drum's, so a part made once serves either drum; `Wd.get(name, 2)` and
  `Wd.on(name, fn, 2)` ask for a player by number (1 left, 2 right) from any movie. A movie both drums share (the
  song's title) belongs to neither and gets player 1's where it names none.
- Also published: `song.title`, `song.subtitle`, `player.name`, `player.side`, `player.speed`, `player.doron`,
  `player.abekobe`, `player.random`, `player.shinuchi`; event `drum.hit`. Read by `song_info` (title) and the board
  (the drum lights where it was struck).
- Read by the engine's own movies: the board (`lane_obi`: score, combo), the three gauges, the roll counter, the
  balloon, the end banner, the Go-Go sign (`play.goGo`), the full gauge's sign (`play.gaugeFull`), the combo sign
  (`combo.milestone`) and the points popup (worked out in the movie from `play.score`'s changes).
- `play.branch` (normal, expert, master; no value on a chart without routes), from the lane's `BranchChanged`. Read
  by the hit target's movie (`lane_hit`: a tint over the lane per route, and one for Go-Go) and by the bubble that
  says which way the route went (`fukidashi_level_1p`). Seen on the Note Kind Check chart, which ends on the three routes.
- **Note flights, the provider way** (user, 2026-11-13). `note.judged` carries the note's kind (don, ka) as a third
  argument; new events `roll.hit` (part, big) and `balloon.popped`. Our flight movie (`onp_kiseki_don_1p`) hears them
  and flies the notes itself: each hit attaches a flight of its own (`attachMovie` of an exported sprite), which
  removes itself on landing; at most 128 in the air, the oldest giving its place. The game's flight movie is still a pool of sixteen players
  jumped to a label per hit: `TaikoHitFlights` leaves all that alone when the scene's own flight movie reads the
  provider (asked once its first frame has run), and draws that one movie where the pool was drawn.
- `play.kusudama` (hits left; `play.balloon` no longer covers a kusudama), read by `action_kusudama`.
- `player.tone` (the drum's sound, 0 to 5) and `player.support` (papa-mama support: always false, the engine does
  not have the option; exposed so a skin can already read it). No movie of ours reads them yet: the game's
  `icon_neiro` and `papamama` are not placed on the gameplay screen by the engine. Their game-style callbacks stay as empty functions, because the gameplay host still calls
  them: it has not been moved behind an adapter.
- The edge log shows what a movie is told (`Wd name(value)`), so a provider-driven movie can be compared run to run.

## The adapter, first step (2026-11-13)

`GameplayGameMovies` (`src/Waddamburo.App/Gameplay`) is the gameplay screen's adapter, one per drum. The lane tells
it every state and event it publishes, after the movies; it turns them into the game's callbacks and label jumps,
and leaves alone a movie that reads the provider. The lane itself no longer calls these:

| Published | The game's movie gets |
| --- | --- |
| `play.score` | board `SetScore` |
| `play.gauge` | gauge `SetCurrentGauge(segments)`, when the segment count moves |
| `play.gaugeFull` | gauge fire `fever_start` / `fever_end` |
| `play.goGo` (on) | splash label `splash` |
| `score.added` (new event: points) | points popup `Create`, `SetCount` |
| `combo.milestone` | combo sign `SetComboBonus` |
| `song.ended` | end banner's label |

Proof (as under "Proof that nothing changed"): the benchmark song (stress chart, one drum then two, the game's
movies) recorded before and after; the lines of these movies are the same per movie, 20,516 of them. Ticks are left
out of the comparison (the benchmark runs in real time, so they wander by one between any two runs), and so is the
order between different movies within a tick.

Our own movies lost the empty callbacks that only existed to be called (`SetScore`, `SetCurrentGauge`,
`SetComboBonus`, `Create`, `SetCount`); the points popup hears `score.added` instead of working it out.

### Second step (2026-11-13)

Also in the adapter now, with the lane's classes in `Waddamburo.Game` only raising what happened (`DrumHit`,
`GoGoChanged`, `BranchChanged`) and no longer calling these:

| Published | The game's movie gets |
| --- | --- |
| `play.combo`, and every `note.judged` | board `SetComboCount` (at each judged note, changed or not) |
| `drum.hit` | board `SetTaikoHit` (`left_don`, `right_katsu`, ...) |
| `play.goGo` | hit target `SetGogo`, both edges |
| `play.branch` | hit target `SetBranch`, route bubble `level_up` / `level_down` (not for the first value) |
| at start | hit target `SetBranchAvailable` |

The board's set-up calls (side, course, option icons) are made on the game's board only. `play.goGo` has no value
until the lane's first update (it was published false at build before: the game's lane is first told then).

Same proof, wider: every gameplay movie's calls and jumps, 33,293 lines, identical per movie.

**Long notes, moved (2026-11-14)**: the roll counter, the balloon and the kusudama. `TaikoLongNotePresentation`
makes no movie calls for them now: it tells `Started`, `Ended` and `Changed`, and the lane publishes `play.roll`,
`play.balloon` and `play.kusudama` at once (and each tick, for the kusudama two drums share) and emits
`balloon.started` / `kusudama.started` (hits needed, and whether it starts in Go-Go time by the chart: `play.goGo`
can still be on at a kusudama right at the Go-Go's end) and `balloon.ended` / `kusudama.ended` (burst or not). The
adapter makes the game's calls from them: the balloon's Reset, opening frames and count, its GekiRendaEnd; the
kusudama's SetPlayerNum and SetWaiWai at the start, AddNorma and SetGogoTime, changed counts,
EndResult. With two drums each drum's adapter tells the one kusudama, as each lane did.
**Staying where they are**: label jumps that are the lane's own drawing (hit flashes, judgement words, note faces,
the game's flight pool): our movies answer the same labels with their own animation.

Our gameplay movies now have no empty callback left.

**Two drums on our movies**: the second drum's and Katsu-chan's parts (`enso_system/base2p`, `katsu1p`, `katsu2p`)
have no package of their own; the first drum's is loaded in their place, since a package reads its own drum from
the provider. The benchmark's two-drum song runs on an empty data directory. Their art is still laid out for the
upper lane only (the lower drum's gauge sits over the upper lane).

## Where the game's side lives: Waddamburo.Skins.Green (2026-11-14)

Everything that exists to drive the game's own movies goes in `src/Waddamburo.Skins.Green`, as the stock songs live
in `Waddamburo.Providers.Stock`: only the App (the composition root) references it, and the dependency test pins
that. The engine assemblies never name a game movie, a callback of one, or a label. New code that drives a game
movie goes there from the start; engine code publishes names.

Moved (user's go, 2026-11-14): the screen bindings (`Screens/`, was `Game/Lumen`), the movie patches (`Patching/`),
the gameplay adapter (`Gameplay/GameplayGameMovies`). Engine rules found in those files went to the engine
(AI battle section records, arcade credit rules, the support menu's pick). Still to move, each when its part goes
behind provider names:

1. Gameplay: done (2026-11-14). The dan course's between-songs card is `GameDanCard` (ours reads `dan.song` and
   `song.title`). The
   intermissions (rainbow, fade, dan covers, shutters) follow the transition protocol: `IntermissionOverlay.Cover(title,
   side)` / `Reveal(title)` publish `transition.title`, `transition.side` and `transition.phase` (cover, reveal) to the
   shown movie, and `GameIntermissions` (Skins.Green) gives the game's their in / out / in_extra / out_extra labels,
   Close(side) and the title fill. Still read by name: whether the rainbow's title is on screen
   (`scene_change_song_name`, to stop the preview), so under our rainbow the preview plays on. The board's setup and
   option icons went to the adapter (`SetUpBoard`, `ShowOptions`). Moved: `TaikoNoteFaces` (the adapter puts the game's notes on their combo face; ours read `play.combo`
   and take their frame from `play.beat`); `TaikoGameplayHostBinding` and `TaikoSongInfoPresentation` (song_info's
   genre, stage, title and visibility, the AI battle and dan movies' sound calls; ours read `song.*`, and
   `song.stage` has no value where the stage number is hidden); `TaikoLumenPresentation`'s label jumps (the target's
   press and hit flashes, the judgement word: the adapter, fed by `drum.hit` and the lane's `Shown` judgements, which
   for an online partner's lane are their live play; ours read `note.judged`, whose `hit` says whether a stroke judged
   it). `TaikoLumenPresentation` itself stays in the engine: it places the notes, bars and stage effects. The flight
   pool (`TaikoHitFlights`): the engine draws whatever layers it is given at the flights' depth; the App flies the
   game's pool from the judgements and the long notes' `Struck`, ours flies itself from `note.judged` and `roll.hit`.
   `TaikoLongNotePresentation` stays in the engine (which roll is on screen, where): each roll's own movie is told
   `note.length` and `note.hit` (names of the `note` scope: only that movie hears them), and the game's get their
   level01, SetWidth, OnUpdate and HitAction from the adapter through `ITaikoLongNoteMovies`. Our roll texts
   (`onp_renda_moji`) are still skeletons. A movie whose scripts name `Wd` counts as ours from its creation.
   `TaikoDonPresentation` (the game's Don-chan: idles, reactions, tints) moved, with the balloon and kusudama poses
   that were in `TaikoLongNotePresentation` (now `ITaikoLongNoteCharacter`: opened, pumped, closed). The lane still
   calls it directly; a character of ours reads `play.goGo`, `play.gauge*`, `note.judged`, `balloon.*`, `kusudama.*`
   and `song.ended`. Don's requests are in the edge log (source `don`) for the proof. Sounds a free movie asks for wait for `Wd.request`.
2. Menus, one per screen as it gets `Wd.request`: results, entry, dan select, song select last. The flows then
   keep only the engine side.
3. The end check: the engine and the free movies build and run with `Skins.Green` out of the solution.

### First menu that needs Wd.request: the dan dojo (user, 2026-11-14)

With our song select the dojo cannot be reached. The game's song select reaches it through a host hack (the dojo
became a category that, decided, opens the dan select screen); our song select has no such category and our entry
has no mode select (it always decides normal play). This is the first `Wd.request` to design: a menu movie asking
the engine to go to a mode (dan dojo, AI battle) rather than the engine filtering the game's calls. Until then a
dojo check needs the game's entry and song select.

## Open questions

0. **The scope list itself.** Whether `session` is right as a level, where player setup at home belongs, and
   whether results is its own scope or the tail of `play` (a results movie wants the play's numbers).
1. **Shape of the script API.** A `Wd` object with `get` / `on` / `request` (above), or the game's style (callbacks
   registered through `ExternalInterface`, the engine calling them by name)? The second keeps one mechanism in the
   VM; the first reads better and needs no naming convention.
2. **What may a user's widget request?** Reading state is harmless. Requests (start a song, change an option) from an
   arbitrary package are not obviously so. Perhaps only movies a scene lists in a role may request.
3. **Who lays out a scene?** Scenes as data files a skin can replace, or fixed lists with slots only.
4. **Menus.** How much navigation a movie owns (the game's own all of it) against the engine offering lists a movie
   only draws. The streamed song list wanted for song select points to the engine owning the list.
5. **Versioning.** One number for the whole provider, or per name.
6. **Performance budget** for subscribers during a song, and whether widgets run on the play's thread at all.

## Lists (user, 2026-11-13)

Some of what the engine knows is a list: a dan course's requirements now, a song library later. A list is a name
with a value per place (`WdKind.List`), read by index:

```actionscript
Wd.get("dan.exam.value", 2)            // the third requirement's count; undefined when there is none
Wd.on("dan.exam.value", function(index, value) { ... });   // every place that has a value at once, then each change
Wd.get("dan.exam.value", 2, 1)         // the player comes after the index
```

- A list's length is a state of its own (`dan.exam.count`). A movie makes or removes what it shows when the length
  changes (the dan panel attaches a row per requirement), and holds nothing else.
- A place that goes away is told as undefined. That is the whole of adding and removing: the engine can change a
  list while a movie shows it, and the movie follows from the same listeners.
- The engine side is `PublishItem(name, index, value)`.
- **For the song library** (not built): the same shape lets the engine publish only the places a movie asks to see.
  That needs the movie to say which (a window: a request, see Requests), and probably list events that say
  "inserted at" and "removed at" rather than re-telling every place after it. To be designed with song select.

**Dan course, built**: `dan.rank`, `dan.song`, `dan.notesLeft`, `dan.exam.count`, and the lists `dan.exam.type`,
`dan.exam.target`, `dan.exam.value`, `dan.exam.possible`, published by `DanExamPanel`. Our panel (`dani_enso`) reads
them (also `dan.name`, and the list `dan.exam.gold`); the game's is driven by the adapter (below). Our panel has
the game's arrangement: the course's plate and the notes left on the left, a row per requirement with its bar (what
is left, for the two that must stay under their target). The dan gauge is the song gauge's script over the lane, at
the lane's length. Both seen in a course (六段), beside the game's at the same tick: the numbers agree.

**Dan panel moved to the adapter (2026-11-14).** `DanExamPanel` only publishes; `GameplayGameMovies.DanTick` (once a
tick, after the lane) tells the game's `dani_enso` the same calls from the published names, once its callbacks exist.
Proof on the user's recorded dojo session (六段, song 1), before and after: the panel's calls are the same in the same
order once a call repeating its own last value is dropped (1,416 lines). The old code told the whole panel at every
change of the play, a roll hit included, so it repeated itself; the adapter tells it when something on it changed.
Other movies differ only as two runs of the same build do (note faces and roll widths step in real time). The
headless run could not play a course before: the tick clock ran under the first song's card and went back when its
music started (fixed in `GameplayFlow.songTime`).

## To do: limits on what a movie may cost (2026-11-13)

Not built, and not needed while every movie is the engine's own; to be done with the loading of other people's
skins. A movie can now make clips as it goes (`attachMovie`; the flight movie makes one per hit), so nothing but the
movie's own script bounds what it costs.

- **A ceiling on clips made by script**, per movie, in the engine: generous (in the order of 1024), there to stop a
  script that attaches without end from eating memory, with one warning naming the movie. The flight movie's own
  128 is its script's choice, about four times the most a play can put in the air (a flight lasts half a second;
  the fastest roll is 40 hits a second).
- **A script budget per movie per tick**, counted in instructions run (not time, so a play stays repeatable). This is
  the real guard: a clip without a script of its own is a timeline step and a quad, hundreds are cheap, while a few
  clips each running a heavy `onEnterFrame` are not, and no clip count catches that. Over budget: reported in the
  editor's Issues tab; in the game the movie's scripts are cut short for that tick rather than a song dropping
  frames.
- **Measure before choosing the numbers**: flights in the air at 128, 512 and 2048, milliseconds per tick each, on
  the slow test machine.
