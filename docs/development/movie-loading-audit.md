# Movie loading and host composition audit

This audit distinguishes authored external movie loading from composition owned
by the game host. They need different implementations: a loaded movie must join
the requesting clip's hierarchy, whereas native scene layers are independent
players driven through game callbacks.

## Confirmed external-loading gap

Asset-derived observation: Entry, Song Select and Waiwai Song Select have
standalone initialization paths that request external indicator movies through
`MovieClipLoader.loadClip`. Executing each movie for 120 updates without a host
produced an unresolved `loadClip` diagnostic. Repeating the same execution with
the viewer host produced no loader diagnostic. This establishes the initial
branch behavior; it does not establish every later input-dependent loading path.

A broader execution check covered twelve movies with external-loading markers:
nine reached an unresolved `loadClip` in standalone initialization and none
reported it with the viewer host. Three standalone executions stopped with a
collection-modification exception, so their loading behavior remains unverified.
Those failures need separate runtime investigation; marker presence alone is not
proof that a request executes.

Entry's external-loading references include the indicator, player name, timer,
card message, over message and coin message. Song Select and Waiwai Song Select
reference the timer. These are independently described relationships, not a
committed asset inventory or instruction listing.

The runtime implements `createEmptyMovieClip`, `attachMovie`, clip duplication,
removal and depth operations. `attachMovie` resolves exported sprites within the
current movie. It does not resolve an external movie. `MovieClipLoader` and
`loadClip` remain unsupported.

`src/Waddamburo.App/Tools/ViewerHostBinding.cs` deliberately installs a `Lumen`
game-host object. `FilePreview.cs` supplies this binding to the selected movie,
so preview takes the hosted branch and bypasses the standalone loading requests.
This prevents preview from showing the composition authored by those requests.

## Placement currently owned by application code

| Location | Host-created composition | Relationship to external loading |
| --- | --- | --- |
| `Scenes/FlowScenes.cs`, Entry | Scene root, indicator, three name boards, timer and over message; explicit board positions | Some movie identities overlap Entry's standalone loading references. The hosted branch does not issue those initial requests. |
| `Scenes/FlowScenes.cs`, Song Select | Scene root, indicator, two name boards and timer; explicit board positions | The timer overlaps a standalone loading reference. The other parts are native host composition. |
| `Scenes/SystemIndicators.cs` | Persistent network, card and coin overlays; scene-dependent visibility and callbacks | Native host composition. Card and coin movies also appear in Entry's standalone references, so the two modes must avoid duplicate ownership. |
| `Scenes/FlowScenes.cs`, results and transitions | Result plus continue overlay; Waiwai result plus two name boards; shutter and fade | Native composition supported by existing behavioral observations. No external-loading markers were found in the ordinary result, retry or Waiwai result movies. |
| `Scenes/GameplaySceneComposition.cs` | ENSO skin selection, system movie roles, layout indices, depths and player lanes | Native game composition. Positions come from the layout asset; role and depth mappings are application code. |
| `Gameplay/TaikoGameplayPresentation.cs` and `Waddamburo.Game/Gameplay/TaikoLumenPresentation.cs` | Note and bar-line placement, long-note instances, hit-flight pool and note text instances | Native chart and gameplay presentation duties. These do not replace an observed external movie-loading path. |

`Waddamburo.Game/Scenes/LumenGameSceneLoader.cs` creates a separate player for
every scene layer, applies its transform and assigns a texture namespace. This
is a host composition API, not an implementation of `loadClip`.

## ENSO finding

Asset-derived observation: a scan of the available ENSO movies found no external
movie-loading markers. Existing private behavioral observations independently
describe the game creating the gameplay parts and note instances itself. There
is currently no evidence of an ENSO parent movie that imports the complete scene.
Internal `attachMovie` usage in ENSO movies must continue to run in the runtime;
it does not imply an external scene loader.

The supported conclusion is that ENSO still needs a game-host composition path.
A complete scene preview should reuse that path and its layout data. Removing it
in favor of `loadClip` would leave no observed movie responsible for assembling
the gameplay scene.

## Required change for authored preview composition

Implement a bounded external-movie resolver and `MovieClipLoader` support in the
runtime. Resolution must use the requesting movie's logical location, preserve
the destination clip's parent and depth, and provide child texture ownership,
initialization callbacks and unload behavior. Use synthetic movies to verify
these contracts.

Then expose a standalone preview mode that allows the authored initialization
path to run. Preserve a hosted mode for movies that need game services. Do not
add a second static list of indicator children to preview or remove native scene
layers merely because their names also occur in standalone loading references.

No runtime behavior was changed by this audit. Source inspection and local asset
execution informed the findings; original data and raw diagnostics remain outside
the public repository.
