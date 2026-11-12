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
  `progress`; events `note.judged`, `combo.milestone`, `song.ended`.
- **Two drums** (user, 2026-11-12): one set of names, an optional last argument for the player. `Wd.get(name)` and
  `Wd.on(name, fn)` give a movie its own drum's, so a part made once serves either drum; `Wd.get(name, 2)` and
  `Wd.on(name, fn, 2)` ask for a player by number (1 left, 2 right) from any movie. A movie both drums share (the
  song's title) belongs to neither and gets player 1's where it names none.
- Also published: `song.title`, `song.subtitle`, `player.name`, `player.side`, `player.speed`, `player.doron`,
  `player.abekobe`, `player.random`, `player.shinuchi`; event `drum.hit`. Read by `song_info` (title) and the board
  (the drum lights where it was struck).
- Read by the engine's own movies: the board (`lane_obi`: score, combo), the three gauges, the roll counter, the
  balloon, the end banner. Their game-style callbacks stay as empty functions, because the gameplay host still calls
  them: it has not been moved behind an adapter.
- The edge log shows what a movie is told (`Wd name(value)`), so a provider-driven movie can be compared run to run.

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
