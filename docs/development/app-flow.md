# App layout and scene flow

`src/Waddamburo.App` is the executable: it wires the engine libraries to SDL and drives the
front end scene by scene. Namespaces follow the folders (`Waddamburo.App.Flow`, ...).

| Folder | Contents |
|---|---|
| `Cli/` | Command-line parsing (`--start-scene`, `--press`, `--invoke`, game data layout). |
| `Flow/` | `GameShell` and one `FlowScene` per scene group (see below); `InputLatches` (presses between ticks, key pulses). Drum bindings: [input](input.md). |
| `Home/` | Home mode: `HomeControls` (Escape menu, pause and resume countdown, quick restart, folder picker), `HomeMenu`, `MenuAudio`, `QuickRestart`, the player setup (`PlayerSetupController`, `PlayerSetupFlow`). |
| `Online/` | `ScoreSync` (score database, uploads, server bests, rankings), cabinet pairing, server health, chart hashes. |
| `Scenes/` | Scene ids and traced compositions (`FlowScenes`), gameplay layout, intermission, system indicators. |
| `Hosting/` | `LayerHostFactory` (a Lumen host per layer, by host id) and the console fallbacks for services. |
| `Gameplay/` | Gameplay presentation (rules: `scoring.md`) (notes, judgement, Don slots), Waiwai's shared stage, skins, the song libraries (`SongLibraries`) and catalog asset routing. |
| `Audio/` | `SoundBank` (the user's sound tree: cues, voices, music, coin) and one cue map per scene (`AttractSounds`, `FrontendSounds`, `GameplaySounds`, `ResultSounds`, `WaiwaiResultSounds`, `RetrySounds`, `GameOverSounds`), gathered in `GameSounds`; song previews. |
| `Presentation/` | Native surfaces drawn into movies (Don renderer bridge, song titles, costume icons, attract CMs, Waiwai results art); the intermission overlay, `ScenePresenter` (scene textures, the frame held across a switch), Waddamburo's own overlays. |
| `Tools/` | Movie/scene viewers, sound bank probe, screenshots. |

## Flow

`GameShell` owns the window loop, the active scene, the intermission overlay (rainbow, shutter,
fade), the system indicators, coins and the credit's joined players, and builds the services at
start. Each tick it reads input, lets home mode's controls and the player setup take it first, advances
the active scene's movies, and hands the tick to that scene's `FlowScene`:

| FlowScene | Scenes | Does |
|---|---|---|
| `AttractFlow` | boot, logo, title, caution, CM | attract rotation; a coin or drum hit starts the entry |
| `EntryFlow` | entry | entry jingle; the movie requests Song Select itself |
| `SongSelectFlow` | song select | mode switch; the rainbow cover, chart loading, handoff to gameplay |
| `GameplayFlow` | gameplay | rainbow reveal and music; song end -> shutter -> results; pause and restart for `HomeControls`; cabinet Escape abandonment |
| `CreditEndFlow` | results, revival, game over | what follows the results per the credit rules |

Every scene change goes through `GameShell.Show` (or a pending Lumen transition), which calls the
old scene's `Exit` and the new one's `Enter`; scene-specific setup and teardown (music, attract
movie, result timers) lives there, not in the loop.
`ScenePresenter` keeps the last complete frame until the new movie has advanced two ticks, with at
least one held presentation. If an intermission cover was showing, it uses black with the network icon
instead. Scene textures are released when the held frame is no longer displayed. Uncovered pixels
use black throughout the app.

## Checking a flow change

`eng/flow-smoke.sh <game-root> <out>` walks every start scene and a scripted song headless and
writes `transitions.txt` (scene changes with their ticks). Run it before and after a change and
diff the two: a refactor should not move a tick.
