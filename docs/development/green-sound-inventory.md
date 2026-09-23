# Green sound inventory

This is a partial semantic inventory for user-supplied Green sound banks. It
records only bank and zero-based cue identifiers; it does not contain or
redistribute audio assets. Decoder stream numbers are one-based, so stream `N`
corresponds to cue `N - 1`.

## Song Select category voices

The following `VO_SELECT.nub` cues were identified by listening and are mapped by
semantic category name. They must not be indexed using the catalog's display
order, which can vary by provider.

| Cue | Category |
| ---: | --- |
| 0 | Namco Original |
| 1 | J-POP / Pop |
| 2 | Game Music |
| 3 | Classical |
| 4 | Variety |
| 5 | Anime |
| 7 | Vocaloid |

Green's stock browser also contains Kids/Children's Songs and Medley. Cues 6 and
8 are the two unresolved candidates for those categories; neither assignment
has been confirmed by listening, so neither is enabled in code. `VO_SELECT.nub` contains
additional category, event, prompt, and navigation voices after this range.
Those cues remain intentionally unmapped until both their spoken meaning and
trigger conditions are identified. Unknown catalog categories likewise remain
silent rather than borrowing an unrelated announcement.

The root browser reports a settled category through
`NotifyGenreFolder(category, -1, false)`. The host resolves that category index
against the pinned catalog, then requests the cue by semantic category name. The
selected announcement repeats while the category remains active and stops when a
folder opens or Song Select releases voice ownership.

## Common effects

| Bank | Cue | Current use |
| --- | ---: | --- |
| `SE_COM.nub` | 0 | Don / centre / confirm |
| `SE_COM.nub` | 3 | Ka / rim / navigation |
| `SE_COM.nub` | 6 | Song Select close-folder substitution |

These mappings cover the currently exercised Player Entry and Song Select paths.
Other common effects and scene-specific banks remain unclassified.

## Revival request routing

Runtime observations of one successful and two failed revivals correlated each
movie request with the bank and cue played immediately afterward. This verifies
routing for those requests; it does not identify what each sample sounds like.

| Movie request | Bank | Cue |
| --- | --- | ---: |
| `SE_REQUEST(0, 0/1/3/5/7/8/12/14/16)` | `SE_REVIVAL.nub` | Same as second argument |
| `SE_REQUEST(1, 100)` | `SE_COM.nub` | 0 |
| `SE_REQUEST(0, 101)` | `SE_COM.nub` | 13 |
| `VOICE_REQUEST(0, 1/2/3/4/6/7/8)` | `VO_REVIVAL.nub` | Same as second argument |

Other request shapes remain unmapped. The rapid `SE_REQUEST(1, 100)` sequence
occurs during the drum roll; its cue uses the drum-hit bus. The scene's
`BGM_START` request starts `bgm/nub/JINGLE_RENDA.nub`; the game loads that music
before Revival in the observed sessions, then invokes `BGM_START` at the start of
the roll. `SOUND_STOP_ALL` stops it when the roll ends.
On both observed failures, the roll ends with `SOUND_STOP_ALL`, then
`SE_REQUEST(0, 7)`, followed by `SE_REQUEST(0, 12)` alongside
`VOICE_REQUEST(0, 8)`, and finally `SE_REQUEST(0, 14)`.

## Identification workflow

Run the VS Code task `Waddamburo: Probe sound effects`, or invoke the app with
`--probe-sound-bank=PATH`. A path may name one NUB or a directory of NUB banks.
Use Up/Down to change bank, Left/Right (or D/K) to change cue, and
Space/F/Enter to replay it. Record the bank and zero-based cue printed in the
terminal, together with the scene action that triggers it.

Inventory confidence uses two levels:

- **Verified** means the cue was identified by listening and exercised through
  its matching scene action.
- **Inferred** means bank ordering or authored context suggests a meaning, but
  it must not be enabled until listening or a runtime trace confirms it.
