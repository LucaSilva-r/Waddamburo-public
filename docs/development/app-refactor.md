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

### 2.1 Two axes for a play: mode and lane drivers

Today every play-time branch mixes two independent questions:

- **Mode**: what the play is for. It picks the charts, whether and how it saves, what the results
  scene receives, what restart means. Normal (1P/2P), Waiwai, AI battle, dan course, training,
  attract demo.
- **Lane driver**: where a lane's hits come from. Local drum, autoplay, AI bot, online echo,
  recorded replay (review).

Demo is Normal with an autoplay driver and no save. Review is any mode with replay drivers and
saving off. Splitting the two removes most of the 131 branches: a mode never asks about review, a
driver never asks about dan.

**Online** (decided) is Normal with a fake second player: the partner's lane is an `EchoDriver`,
while the credit stays a single-player credit for its own rules (one joined side, only the local
lane saves, single-player kusudama → balloons on the partner's lane). The one thing online adds to
Normal is the end: results wait for the partner's and show both, synchronised. That is an
`IPlayMode.End` option on `NormalPlay` (an `OnlineLink` it is given), not a mode of its own.

**Training** (decided) is a mode, `TrainingPlay`: it owns a looping, speed-changing clock and
section restarts, never saves and has no results. It reuses the review clock that
`TrainingSession` shares with `ReviewSession` today.

```csharp
// Flow/Play/IPlayMode.cs
internal interface IPlayMode
{
    PlayableChart[] PrepareCharts(PlayRequest request, PlayPreparation prep); // simplify, modcharts, layout, kusudama
    GameplayTimeline Timeline(PlayableChart[] charts);
    bool Saves { get; }                       // dan saves whole, at its own end
    void Tick(PlayContext play);              // dan: next song card; AI: section scoring
    SceneId End(PlayContext play, bool finished); // save and hand the results their payload
    bool CanRestart { get; }
}

// Flow/Play/ILaneDriver.cs
internal interface ILaneDriver
{
    void Tick(PlayContext play, int lane, TimeSpan chartTime);
}
```

Modes: `NormalPlay` (with an optional `OnlineLink`), `WaiwaiPlay`, `AiBattlePlay`,
`DanCoursePlay`, `TrainingPlay`, `DemoPlay`. Drivers: `DrumDriver`, `AutoplayDriver`,
`AiBotDriver`, `EchoDriver`, `ReplayDriver`. `ReviewSession` becomes the review clock plus replay
drivers; `TrainingSession` becomes `TrainingPlay`.

`GameplayFlow` keeps only what every play has: the clock and audio, rainbow reveal, shutter,
pause, quick restart, the editor hooks. `Play` shrinks to those fields; each mode and driver owns
its own (`CourseSong`, `CourseHold`, `AiPrior`, `AiUploads`, `Echo`…).

### 2.2 `CreditSession`: one owner for the credit

```csharp
// Flow/CreditSession.cs
internal sealed class CreditSession
{
    public CreditMode Mode { get; set; }          // Normal, Waiwai, AiBattle, Dan
    public CreditPlayers Players { get; }         // joined sides, side, partner, entry trigger (from PlayerSession + Hosts)
    public int SongsPlayed { get; set; }
    public TutorialsSeen Tutorials { get; set; }
    public SceneHandoff Handoff { get; set; }     // the last play's payload: result, AI outcome, dan result, Waiwai outcome
    public void Reset();                          // the one place a credit ends
}
```

`Handoff` is written by a mode's `End` and read by the result hosts; nothing else carries results
between scenes. `PlayerSession` merges into `CreditPlayers`. `Shell.Hosts.AiBattle` becomes
`Credit.Mode == CreditMode.AiBattle`.

`SongSelectMemory` (library, osu! browse, focus, search, last pick, online song) is a separate class:
it outlives credits at home and is cleared by `CreditSession.Reset` in arcade.

### 2.3 `LayerHostFactory` becomes a factory

It keeps `Create(SceneLayerDefinition)` and the private host builders. It reads `CreditSession`,
`SongSelectMemory` and one `Func<ArcadeSettings>`; the `Func<bool>` shims go. The loaded binding
handles move to `LoadedHosts` (a plain class the factory fills as it builds: `SongSelect`, `Entry`,
`Retry`, `GameOver`, `DaniSelect`, `WaiwaiResult`, `AiRemind`…), so flows poll `Shell.Loaded.Retry`
instead of the factory.

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
catalog). They may not hold or write another flow's state; shared state goes in `CreditSession`,
`SongSelectMemory` or `SceneHandoff`. We will not replace `Shell` with constructor injection
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

Each phase is its own commit series on `main`, contains no feature work, and ends with an empty diff
on the phase 0 runs and green tests.

| # | Phase | Exit criterion |
|---|---|---|
| 0 | **Safety net.** Extend `flow-smoke.sh` with the modes it does not reach: AI battle song, dan course, Waiwai 2P, training, a replay review, the attract demo, a 2P normal play. Online needs a partner, so it is checked by hand. It runs locally on the user's game data before each phase is merged (not in public CI). Record the final score and replay hash per play run (`--play` already plays deterministically). Add `tests/Waddamburo.App.Tests` with `InternalsVisibleTo` for the pure classes extracted later. | Two runs of the same build give identical output. |
| 1 | **`CreditSession` + `SongSelectMemory` + `SceneHandoff`.** Move state out of `LayerHostFactory` and `PlayerSession`; repoint every use. Unit tests for `Reset` and mode switches. | `LayerHostFactory` has no public settable state. |
| 2 | **`LoadedHosts`; factory takes `Func<ArcadeSettings>`.** Delete the `Func<bool>` shims. | Factory constructor ≤ 8 parameters, no `init` funcs. |
| 3 | **Lane drivers.** Autoplay, AI bot, echo, replay out of `GameplayFlow`. | No `_play.Autoplay/AiBot/Echo` in `GameplayFlow`. |
| 4 | **Play modes.** Dan, AI battle, Waiwai, demo, normal; `Enter`, `Tick`, `end`, `saveScore`, `saveCourse` dispatch to the mode. | Mode-branch count in `GameplayFlow` under 15 (from 131); `Play` under 15 fields. |
| 5 | **GameShell split.** `InputStack`, `FrameComposer`, `RumbleController`, `AudioOutputHost`, `PresenceReporter`, `GameServices`, `DiagnosticsOptions`. | `GameShell.cs` under 600 lines; `tickOnce` under 40. |
| 6 | **Mechanical.** Settings table, `GameOptions.Parse`, `HomeMenu` statics, `IGameLoop`. | Each settings key written once. |
| 7 | (optional) LumenPlayer split. | Lumen.Tests and `--scan` unchanged. |

Phases 1–2 unblock everything else and are the cheapest. Phases 3–4 matter most. Phase 5 is
easiest once 1–4 have removed the state GameShell is holding for others.

## 4. Decisions

1. **Online play** is Normal with a fake second player (an echo lane) and a synchronised result
   screen; the credit stays single-player for its own rules (2026-11-18).
2. **Training** is a mode (2026-11-18).
3. **Phase 0** runs locally on the user's game data, before each phase is merged; not in CI.
4. **Docs**: `app-flow.md` gets rewritten at the end of phase 5; this file is deleted then (its
   result is the code).
