# App refactor checkpoint

Status: proposed, 2026-11-18. Scope: `src/Waddamburo.App` (29k lines, no test project) and the two
places it leans on hardest (`ArcadeSettings`, `SdlApplication.Run`). Formats, Catalog, Providers,
Lumen and the Game rules stay as they are; `LumenPlayer` is an optional last phase.

The refactor changes no behaviour. Every phase must leave `eng/flow-smoke.sh`'s `transitions.txt`
and the mode runs of phase 0 identical, tick for tick.

## 1. What is wrong

Measured on a47ed19.

**GameShell is a service locator with state.** 1926 lines, 52 fields, about 60 members read from
outside, roughly 750 `Shell.` references across the flows (`GameplayFlow` 229, `SongSelectFlow`
152, `HomeControls` 82). Besides building services and running the loop, it does rumble shaping,
audio device open/fallback, Discord presence, the upload banner, effects-editor opening and layout
renames, screenshots, frame composition (`createFrame` + `onTop`), the service reset and restart
statics. Its constructor is 260 lines.

**Input routing is implicit.** `GameShell.tickOnce` decides who gets a tick through about twelve
early `return`s: service menu, F1, reload/overlays, home menu, player setup, quick restart, the
scene, held results, voice wait, fade. That order is the input priority, and nothing names it.

**The credit's state has no owner.** What mode this credit is in, who joined, which side, what the
previous scene produced for the next one, all live in at least four places:

| State | Where it lives today |
|---|---|
| mode (normal / Waiwai / AI battle / dan) | `Hosts.Waiwai`, `Hosts.AiBattle` (24 uses, 7 files), `Hosts.Dan*`, `GameplayFlow._waiwai`, `_course`, `AiSession` |
| players and sides | `PlayerSession.JoinedSides`, `Hosts.PlayerSide`, `Hosts.TwoPlayers`, `Hosts.PartnerSide`, `Hosts.EntryTrigger`, `EntryRejoined` |
| per-credit flags | `Hosts.TutorialSeen`, `WaiwaiTutorialSeen`, `AiTutorialSeen` (reset by `PlayerSession`) |
| scene-to-scene payloads | `Hosts.AiOutcome`, `AiNumbers`, `AiSectionBest`, `DanResult`, `WaiwaiOutcome`, `PlayRequests` |
| song select memory | `Hosts.Library`, `OsuBrowse`, `_focus`, `_search`, `_lastPick`, `OnlineSong` |

**`LayerHostFactory` is misnamed.** Building hosts is maybe a third of it. The rest is the table above,
the handles of loaded bindings (`SongSelect`, `Retry`, `GameOver`, `DaniSelect`…) and a dozen
`Func<bool>` shims for live settings (`ShowOni`, `TimerFreeze`, `HideArcadeHud`…) next to an
`arcadeSettings()` func that already gives all of them. 15 constructor parameters plus about 12
`init` funcs. `Shell.Hosts.*` is used 116 times.

**`GameplayFlow` branches on mode everywhere.** 131 references to `_waiwai`, `AiSession`, `_course`,
`_play.Review`, `_play.Training`, `_demo`, `Match.InSong`, `Hosts.AiBattle`, `_play.Echo`,
`_play.Battle` in one file. Its `Play` state bag has 40 fields covering every mode at once. `Enter`
alone (`GameplayFlow.cs:185-320`) decides seed, support level, AI session validity, modcharts,
layouts, kusudama, timeline, review, voiding, autoplay, echo, match, squeeze and the AI bot inline.

**Smaller tangles.**
- `ArcadeSettings`: 76 properties; every key is written 3–4 times (property, `Parse` switch,
  `SaveMenuSettings` dictionary, `HomeMenu` row). `Arcade.cs` also holds the coin bank.
- `Boot.Run`: 456 lines of hand-rolled `--flag=` parsing mixed with dispatch and self-update.
- 47 `WADDAMBURO_*` environment variables read in 70 places, some inside the tick.
- `HomeMenu.AsioDrivers`, `AudioDevices`, `Skins`: public static mutable lists filled from outside.
- `SdlApplication.Run`: 15 optional callback parameters.

## 2. Target shape

### 2.1 Play modes and lane drivers (built in phases 3 and 4)

Every play-time branch mixed two questions: what the play is for (its **mode**), and what plays its
lanes besides the drums (its **lane drivers**).

**Modes** are nested classes of `GameplayFlow`, one file each (`GameplayFlow.<Mode>.cs`), deriving from
`PlayMode` (`GameplayFlow.Modes.cs`). The base class's defaults are normal play; a mode overrides only
what differs. Nested so a mode can work on the play's state, clock and music without those becoming
the flow's API. The hooks came from the actual branches: the charts played (`Sources`, `Seed`), the
clock (`Timeline`, `ChartTime`), the start (`Reveal`, `SongStarts`, `MusicOpened`, `OpenAudio`), each
tick (`TakesTick`, `BeforeTick`, `AdvanceSong`), the end (`EndsAt`, `Ending`, `Finished`, `Shutter`,
`ResultsCutIn`, `End`, `Save`, `Battle`, `Results`), `Restarting`, `Exit`, plus a few facts
(`RealPlay`, `SeveralSongs`, `UsesPlayerLayout`, `EndBanner`, `EchoesPartner`, `SavesSongs`,
`Composition`, `ModeLabel`, `Indicators`, `Presence`).

| Mode | Picked by | Differs in |
|---|---|---|
| `NormalPlay` | `Prepare` | nothing: the defaults |
| `WaiwaiPlay` | `Prepare` with a Waiwai composition | its layout, no shutter, Waiwai results, not saved |
| `BattlePlay` (AI battle) | `Prepare`, an AI battle credit, two lanes | support level, server session, AI-don's lane, ends by the battle result, no shutter or banner, AI results, its battle saved with the play |
| `DanCoursePlay` | `PrepareCourse` | merged chart, song cards, next song's audio ahead, early end, dan shutter, saved whole, dan results; stays the mode through its results so Restart plays it again |
| `DemoPlay` | `StartDemo` | autoplay at 15 hits/s, not a real play (no save, pause, restart, echo), 30 s, take-over; back to normal when the scene is left |

**Online, replay and training are not modes** (user, 2026-11-18): they are extensions of normal
play. Online is normal play with the partner's lane echoed (`OnlineMatch`, synchronised results);
a replay or autoplay is reviewed (`ReviewSession` owns the clock and the presentation reads the
replays); practice is a review taken live (`TrainingSession`). They show as `_play.Review` and
`_play.Training` in `GameplayFlow`.

**Lane drivers** are `Gameplay/LaneDrivers.cs` (phase 3): the autoplay, AI-don's bot and battle
presentation, and the online echo, with the one `Advance` the frame and tick paths shared. No
interface: each kind had one caller.

### 2.2 `CreditSession`: one owner for the credit

`Flow/CreditSession.cs` (built in phase 1) is plain data with no shell: `Mode` (`CreditMode`: Normal,
Waiwai, AiBattle; `Waiwai` and `AiBattle` are views of it, so both can never hold), the players' sides
(`PlayerSide`, `TwoPlayers`, `PartnerSide`), `SongsPlayed` (0 offers the tutorials again), the
tutorials watched, what the next entry starts from (`EntryTrigger`, `EntryRejoined`, `EntrySetup`,
`EntryCard`), the dan series and challenge, and what a finished play hands its results (`AiOutcome`,
`AiNumbers`, `AiSectionBest`, `DanResult`). A separate `SceneHandoff` class was not worth it: those
four sit in their own section. `Shell.Hosts.AiBattle` is now `Shell.Credit.AiBattle`.

`PlayerSession` keeps the behaviour (joining, guests, watching a replay, swapping sides for AI
battle) and writes the credit's state into `CreditSession`. Folding it in fully waits for phase 4,
when the modes take over what it decides.

`Flow/SongSelectMemory.cs` (library, osu! browse, focus, search, last pick, online song, and
`Start(view, picked)`: where the cursor starts) outlives credits at home; a cabinet's new credit calls
`ForgetLastSong`.

### 2.3 `LayerHostFactory` becomes a factory

It keeps `Create(SceneLayerDefinition)` and the private host builders. It reads `CreditSession`,
`SongSelectMemory` and one `Func<ArcadeSettings>`; the `Func<bool>` shims go. The loaded binding
handles move to `LoadedHosts` (a plain class the factory fills as it builds: `SongSelect`, `Entry`,
`Retry`, `GameOver`, `DaniSelect`, `WaiwaiResult`, `AiRemind`…), so flows poll `Shell.Loaded.Retry`
instead of the factory. (done in phase 2)

### 2.4 GameShell becomes setup plus loop

| Moves out of GameShell | Into |
|---|---|
| the `tickOnce` return chain | `InputStack`: an ordered `IInputLayer[]` (service menu, attract shortcut, overlays, home menu, player setup, quick restart, scene). One loop, first taker wins. The order is written in one array with a comment per entry. |
| `createFrame`, `onTop`, `CameraBorder`, `Unsqueezed`, upload banner | `FrameComposer` |
| rumble methods and `_rumbles` | `RumbleController` |
| `openAudio`, `tryOpenAudio`, `SwitchAudioOutput`, `audioProblem` | `AudioOutputHost` |
| `reportPresence`, `reportStatus`, `_discord` | `PresenceReporter` |
| `OpenEffectsEditor`, `renameLayout`, `stockLayout` | `GameplayFlow.Editor.cs` (it already exists) |
| `WADDAMBURO_*` reads | `DiagnosticsOptions`, read once in `Boot` |

The constructor's service construction moves to a static `GameServices.Create(options)` returning a
record. GameShell keeps the loop, the active scene, `Tick`, and the composition.

**Access rule for flows.** Flows may use services through `Shell` (audio, sounds, scenes, overlay,
catalog). They may not hold or write another flow's state; shared state goes in `CreditSession`
or `SongSelectMemory`. We will not replace `Shell` with constructor injection
everywhere: that only renames the bag. The state rule is what removes the spaghetti.

### 2.5 Mechanical cleanups

- **Settings table.** One `SettingKey<T>` row per key (name, getter, `with` setter, parser, range)
  drives `Parse`, `SaveMenuSettings` and the menu rows. The coin bank moves to `CoinBank.cs`.
- **`GameOptions.Parse(string[])`** returns the record; `Boot.Run` only dispatches.
- **`HomeMenu`** takes its device and skin lists as constructor arguments.
- **`SdlApplication.Run(IGameLoop loop)`** with `Tick`, `CreateFrame`, `UpdateFrame`, and pointer
  and performance hooks as default interface methods. Input translation moves to `SdlInputTranslator`.

### 2.6 Optional: LumenPlayer split

`LumenPlayer.cs` (3060 lines) mixes the display tree and timeline, the MovieClip bindings
(`IAvm1Host`, `readMember`/`writeMember`), placement, interpolation and render emission. Split
into `DisplayTree`, `MovieClipBindings` and `RenderEmitter`. Lumen.Tests (2.5k lines) cover it,
and it is cohesive, so this is last and only if time allows.

## 3. Phases

Each phase is its own commit series on `main`, contains no feature work, and ends with
`eng/flow-smoke-compare.py` reporting the same on the phase 0 runs, and green tests.

| # | Phase | Exit criterion |
|---|---|---|
| 0 | **Safety net.** *Done 2026-11-18.* `flow-smoke.sh` also runs one play per mode through the real flows (`--flow-smoke=<mode>`, `FlowSmokeRun`): normal, two players, Waiwai, AI battle, dan, online echo (`WADDAMBURO_ONLINE_ECHO`, the partner's lane only; a real online match is checked by hand), review, training and the attract demo, each logging a `Play results:` line. The plays run on a scratch game folder with the real data linked in, with no music and seeded random picks so headless time follows the ticks. It runs locally on the user's game data before each phase is merged (not in public CI). | Two runs of the same build compare the same with `eng/flow-smoke-compare.py`: scenes and results exactly, ticks within ±3 (the loop drops a tick or two while loading, so exact ticks are not reproducible). |
| 1 | **`CreditSession` + `SongSelectMemory`.** *Done 2026-11-18.* State moved out of `LayerHostFactory` and `PlayerSession`, every use repointed (`Shell.Credit`, `Shell.SelectMemory`). `tests/Waddamburo.App.Tests` (`InternalsVisibleTo`) tests the mode flags and the new-credit tutorial reset. | `LayerHostFactory` keeps no credit or song select state; what it still has settable are services and callbacks (coins, dan catalogue, result and entry callbacks), phase 2's. |
| 2 | **`LoadedHosts`; settings read directly.** *Done 2026-11-18.* The movies' hosts the flows poll (`SongSelect`, `Entry`, `Retry`, …) are `Shell.Loaded`; the entry passthroughs are gone. The `Func<bool>` settings shims and `SongsStarted` are gone: the factory reads `arcade` and `credit`. The ≤ 8 constructor parameters target was dropped: the remaining score, profile and callback funcs would only move into a bag. Callbacks wired after the system indicators exist (`LayerLoading`, `EntryJoined`, `CardDialog`, `ReturnToAttract`) stay settable: wiring them earlier would run them on the first scene's load. | Flows poll no movie host through the factory; no settings shim left. |
| 3 | **Lane drivers.** *Done 2026-11-18.* `Gameplay/LaneDrivers.cs` holds the autoplay, AI-don's bot and battle presentation, and the online echo, with the one `Advance` the frame and tick paths shared as copies, and `Stop`. No `ILaneDriver` interface: review and training don't drive lanes (they own the clock; the presentation reads their replays), so each driver kind had one caller. Creating them stays in `Enter` until the modes take it in phase 4. | No `_play.Autoplay/AiBot/Echo/Battle` in `GameplayFlow`. |
| 4 | **Play modes.** *Done 2026-11-18.* `NormalPlay`, `WaiwaiPlay`, `BattlePlay`, `DanCoursePlay`, `DemoPlay` (section 2.1). First a headless fix: a dan course's songs after the first counted their headless clock from the course's reveal, so flow-smoke's dan failed on song 2; it now plays the whole course (369/0/0, gold). `GameplayFlow.cs` 1366 → 1045 lines. | No Waiwai, AI, dan or demo checks left in `GameplayFlow` (AI battle remains only in credit-wide layout and 4:3 rules); review and training stay (normal play's extensions). |
| 5 | **GameShell split.** *Done 2026-11-18.* Out into classes: `Rumble`, `PresenceReporter` (Discord and website status), `AudioOutputHost` (open, fallback, live switch, failure toast), `EffectsEditorHost` (`Shell.Editor`). Into partial files: `GameShell.Frame.cs` (frame composition, inspect, the pairing pill) and `GameShell.Tick.cs` (the tick, with its input order written at the top). Dropped: `FrameComposer` as a class (it reads ~15 of the shell's parts: it would only move the bag), the `InputStack` (the steps differ in whether the scene advances and with which keys), `GameServices.Create` (moving the constructor removes no coupling). `GameShell.cs` 1926 → 1130 lines. Left as found: `pill()` takes pairing cards while the frame is drawn (input in rendering; moving it into the tick changes its timing). | Behaviour unchanged (flow smoke). |
| 6 | **Mechanical.** Settings table, `GameOptions.Parse`, `HomeMenu` statics, `IGameLoop`. | Each settings key written once. |
| 7 | (optional) LumenPlayer split. | Lumen.Tests and `--scan` unchanged. |

Phases 1–2 unblock everything else and are the cheapest. Phases 3–4 matter most. Phase 5 is
easiest once 1–4 have removed the state GameShell is holding for others.

## 4. Decisions

1. **Online play** is Normal with a fake second player (an echo lane) and a synchronised result
   screen; the credit stays single-player for its own rules (2026-11-18).
2. **Training and replay** are extensions of normal play, not modes (2026-11-18; first decided as a mode, reversed once phase 4 showed `TrainingSession` already holds it).
3. **Phase 0** runs locally on the user's game data, before each phase is merged; not in CI.
4. **Docs**: `app-flow.md` gets rewritten at the end of phase 5; this file is deleted then (its
   result is the code).
