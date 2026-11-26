# Frontends and features

Waddamburo is one engine with two frontends on top (user, 2026-11-26):

- **Green's frontend**: Taiko Green's screens and flow, faithful to the game (attract, entry, credit, revival, game
  over, AI battle, Waiwai, the reward shop). Its movies are the game's (`Waddamburo.Skins.Green` drives them), or
  ours in their slots with `free_movies = true`.
- **Waddamburo's frontend** (`frontend = waddamburo` in `waddamburo/config.cfg`): home rules, no attract loop (boot,
  then the entry with nobody joined), and only the features it offers: no AI battle, Waiwai or reward shop. Two
  players play side by side. Home only for now. **Nothing of the game's**: its own screen compositions
  (`WaddamburoScreens`: only the layers its screens show), movies from packages only (a slot without a package of
  ours is a yellow `missing: <slot>` placeholder, `LumenMovieContent.Placeholder`), and sounds from our pack only (a
  name it lacks is silent; no game jingles, no Green screen sounds). No mascot: Don-chan is Green's
  (`Skins.Green/Don`: his presentation, costumes and motions; the player's look stays account data, `Game.Scores.DonLook`).
  Gameplay skins give a lane character or none (`IGameplaySkin.Character`). The GPU model renderer stays in
  `Platform.Sdl` (`SdlDonRenderer`) as a drawing capability.

Neither frontend owns a feature's rules. Those live in **`Waddamburo.Features`**, which references only `Game` and
`Services` (never a frontend, a movie or the platform; `DependencyRulesTests` checks it):

| Module | Owns |
| --- | --- |
| `ShopModule` | each drum's reward shop for the credit: goods, medals, owned items, buying (with the unlock) |
| `RewardsModule` | Don Medals at a credit's end and home's "New rewards": asking TaikOnline, paying, telling it they were shown |
| `AiBattleModule` | ranks and AI-don levels, each player's standing, what a battle earns, the last battle's results |
| `DanModule` | the dojo: every `IDanCourseProvider`'s series matched against the library, the credit's course, its result, bests, keeping a passed run |
| `ChartHashes` | chart key → canonical hash, remembered in the score store |

A frontend calls a module's verbs and shows its state: Green's hosts turn the game's movie callbacks into module
calls; our movies reach the same verbs through the provider (`Wd`, [free-movies.md](free-movies.md)). Online rules
therefore exist once, in the module.

Dan courses come from providers like songs do from catalog providers: `OnlineDanCourses` (TaikOnline's official
catalogue, saved beside the scores) and `TjaDanCourses` (the player's own `Dani` folder in the TJA library,
[custom-tja.md](custom-tja.md)).

## Open

- Flows: each frontend has its own flow table. Waddamburo's has its own boot (`WaddamburoBootFlow`: to who's playing
  and the entry) and results (`WaddamburoResultFlow`: results, then the next song or the course list); the entry, Song
  Select and gameplay flows are the engine's and shared. What differs is asked of `Shell.Frontend` (`Frontend`: game
  movies, game sounds, mascot, attract loop, Waiwai, AI battle, rewards), never of the setting. The results' provider
  names are the engine's (`Game.Flow.ResultNames`).
- Screen hosts are the frontend's own: `GreenScreenHosts` (Skins.Green) for the game's movies, `WaddamburoScreenHosts`
  (App/Frontends) for ours: no bindings, only the engine's sessions (entry, Song Select) and when a screen ends.
- Waddamburo's "who's playing?" (`AccountPicker`) is a list over the entry: Guest and each stored TaikOnline account
  with its avatar; the left drum only, no signing in of a new account (that stays the home player setup's).
- Green's traced compositions are `GreenScenes` (Skins.Green); Game's `FlowScenes` keeps the shared screen ids.
- Waddamburo's gameplay screen has the gameplay roles only (lane, gauge, every note kind, rolls, balloons, hit
  effects, combo, song info, name board) at the layout's places, none of Green's scenery
  (`GameplaySceneComposition.IsGreenDecoration`), no song skins. Its roles and places are still the game's gameplay
  contract (Green's composition filtered) until our movies need their own.
- `GreenSounds` is still named in `GameShell` (composition) and by the home resume countdown.
- The home pause menu's resume countdown (`ResumeCountdown`) is still a Skins.Green type; it drives our time_counter
  through the provider (timer.left), its count cues are Green's (silent on Waddamburo's frontend).
