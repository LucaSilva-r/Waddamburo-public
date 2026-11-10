# Online AI battles

Online battles use a server-issued setup and the same `AiBattleSimulation` in live
play and `--rescore`. The setup freezes the seed, model version, chart hash, course,
options, support level, and the player's previous verified timing statistics.
Client-reported sections, timing, wins, and points are provisional; TaikOnline
replaces them after running the replay through the shared simulator.

## Starting a battle

`POST api/wdb/ai/battles` uses the usual home or cabinet authentication. Its JSON
body contains `chart_sha256`, `base_sha256`, `course`, `options`, `support_level`,
and `version`; cabinets also supply `baid`. Standard play uses the same played and
base hash. Support play hashes the deterministic `ChartSimplifier` result and
names its unsimplified base separately.

The response is an `AiBattleSession`: `id`, `seed`, `version`, `chart_sha256`,
`course`, `options`, `support_level`, `ranked`, `points`, `wins`, `hit_median_ms`,
and `hit_variance`. The timing pair may be null. Repeating an unplayed setup
returns the same session and seed. A setup binds to one player's one play ID.

A missing setup permits local, unranked play. An unapproved chart can receive an
unranked setup. Neither case earns online points or reward wins. Development and
nightly clients retain their existing upload restrictions.

## Replaying and awarding

AI uploads include the normal player replay, the setup ID as `ai_session_id`, and
the seed even when the options do not need randomness. Every AI play is retained,
including losses and failed gauge clears. Normal and Dan failures remain local.
AI plays' ordinary scores use the normal score boards and ordinary rescoring.

TaikOnline supplies the scorer with trusted `ai_battle` context: `version`,
`support_level`, `base_notes`, `base_source`, and the prior timing pair. The notes
use the existing base64 gzip canonical chart format. The scorer verifies support
charts against `ChartSimplifier`, then returns `ai_battle` with `version`,
`sections` (`W`/`L`), `level`, timing, and per-section `counts` for player and AI.
An unsupported or mismatched battle produces `ai_error` while preserving a valid
ordinary score result.

Only verified battles on approved bases count toward online progress. A battle
win can count without a gauge clear, including on a support chart. Rank points
require a win, a gauge clear, the standard chart, and the rank's star threshold.
The existing Green ladder supplies the base point and first-win/high-level
bonuses. The server recalculates points in setup order after delayed uploads,
rescoring, and moderation. Synced server results replace provisional local awards.

## Model version 1

This is a Waddamburo implementation choice, not an assertion about the original
game's random algorithm. Online tuning freezes the project's fallback ghost
parameters; local play can use installation tuning. The random stream is pinned
xorshift32. Both sides normalize notes to canonical chart bytes, and generated
offsets use whole microseconds. Decisions and section
boundaries follow chart events rather than rendering frames. The AI follows the
player's branch. Copied roll totals do not influence the section winner.

Changing this model requires a new version while retaining support for stored
older setups. A synthetic fixed replay test pins version 1's generated AI inputs.

## Remaining progression work

The normal ladder reaches rank 10. The challenge folder required for higher ranks
is not implemented. Win milestones can be displayed, but titles, songs, and
costume unlocks are not granted by this implementation.
