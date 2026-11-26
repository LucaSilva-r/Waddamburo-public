# Frontends and features

Waddamburo is one engine with two frontends on top (user, 2026-11-26):

- **Green's frontend**: Taiko Green's screens and flow, faithful to the game (attract, entry, credit, revival, game
  over, AI battle, Waiwai, the reward shop). Its movies are the game's (`Waddamburo.Skins.Green` drives them), or
  ours in their slots with `free_movies = true`.
- **Waddamburo's frontend** (`frontend = waddamburo` in `waddamburo/config.cfg`): our movies always, home rules, no
  attract loop (boot, then the entry with nobody joined), and only the features it offers: no AI battle, Waiwai or
  reward shop. Two players play side by side. Home only for now.

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

- The flows (`App/Flow`) are still shared: Waddamburo's frontend is Green's flow with its differences switched on
  `ArcadeSettings.WaddamburoFrontend`. Once its flow differs more than that, it gets its own flow table.
- Screen hosts are still `GreenScreenHosts` for both frontends (they leave our movies to the provider).
- The game's indicator legend still shows over our song select (no movie of ours for that slot yet).
- `GreenSounds` is still named in `GameShell` (composition) and by the home resume countdown.
