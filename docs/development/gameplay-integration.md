# Gameplay integration

This is the first repair slice for the contract review, not complete gameplay or
an arcade scoring specification.

## Sources and decisions

The private behavioral oracle supplied semantic requirements: native gameplay
positions authored note movies; note types share animated movie players; lane
target geometry determines the hit position; host callbacks update combo and
Go-Go; named child states provide drum and judgement feedback. These are
behavioral requirements, not copied implementation. No private scoring or gauge
formula is adopted. Asset selection stays in the app composition.

The timing/judgement architecture is informed by osu!lazer at
`48c4800e3ae4ee752452cdff83bd3787ccf3105f`, specifically
[GameplayClockContainer](https://github.com/ppy/osu/blob/48c4800e3ae4ee752452cdff83bd3787ccf3105f/osu.Game/Screens/Play/GameplayClockContainer.cs)
and
[DrawableHit](https://github.com/ppy/osu/blob/48c4800e3ae4ee752452cdff83bd3787ccf3105f/osu.Game.Rulesets.Taiko/Objects/Drawables/DrawableHit.cs).
The adopted principles are a source clock distinct from presentation updates,
explicit offsets, absolute-time windows, separate passive misses and triggered
judgements, and preventing simultaneous secondary input from consuming the next
ordinary note. This is independently written code, not osu! integration or a claim
of ruleset parity. The existing 35/80/95 ms windows remain a product default;
difficulty-dependent windows and held-key strong-hit semantics remain future work.

## Time contract

`GameplayTimeline` maps transport position to chart time. Chart zero follows a
three-second lead-in; the lead-in grows if needed to accommodate a negative offset.
The song begins at `leadIn + authoredOffset`, and chart time is
`transportPosition - leadIn`. Thus offset -2 places chart zero two seconds after
audio zero; offset +2 places it two seconds before audio zero. This follows the
[TJA format's offset convention](https://github.com/269Seahorse/Better-taiko-web/blob/master/TJA-format.mediawiki).

The decoder fills its bounded ring before playback. Scheduled leading silence
places audio zero at an exact sample in the transport. Judgement and rendering
use transport position, independently of discarded Lumen simulation ticks. A
source underrun or decode failure terminates the diagnostic run explicitly rather
than silently continuing out of sync. Recovery UI is not implemented.

SDL position is estimated from submitted frames minus application-queued frames
and one hardware buffer. This is not a measured DAC clock: backend buffering,
resampling, calibration, and device recovery still need integration validation.
The device receives silence while idle so queued song tails continue to drain
through a continuously advancing output timeline. With no audio, interactive
play uses a stopwatch; audio-free screenshot probes use explicit simulation time.

## Input and Lumen contracts

SDL preserves non-repeat press edges and their timestamps, including down/up pairs
within one poll. Held snapshots still serve authored UI. Interactive gameplay
receives events once per display update, processes them before passive misses,
and clamps late events to its last adjudicated horizon. Scripted tick pulses
preserve their existing diagnostic entry point. Focus clears held keys.

Lumen remains unaware of notes, scores, and audio. It exposes named instance
bounds and child label playback. The game presentation receives semantic layer
roles, composes background, barlines, notes, then foreground feedback, and calls
authored UI callbacks. Missing required target geometry/callbacks/states fail
explicitly. Four beats span the visible lane at scroll 1, a product layout choice.

Stopping a preview now cancels it without scheduling menu Bgm. Menu background
playback is requested explicitly. Escape during reveal releases the overlay and
resets the transition before returning to Song Select.

Native fill geometry is validated as a host surface, not as a reference into the
archive texture table. Ordinary textured geometry still receives range checking.
This fixes a parser/runtime disagreement that prevented the transition movie from
loading; a synthetic regression covers both reference types.

## Verification

The managed regression run passed 230 tests, with warnings treated as errors and
SDL audio using its dummy driver. The previously crashing Realm test class was
excluded, so this is not a green unfiltered suite. The app builds without warnings.
Local asset-backed runs completed Entry, Song Select, the authored cover/reveal,
and gameplay; a separate interactive-clock run returned to Song Select when Escape
was pressed during reveal. Captures and logs remain outside this repository.
These runs establish execution and presentation, not measured audible/input latency.

## Remaining scope

The first visual-runtime follow-up fixes nested calls observing pending timeline
function definitions and adds AVM1 stack-based frame/label jumps, including scene
bias. The composition now includes the dancer and a zero-initialized gauge; gauge
scoring is still not implemented. A mode-specific support overlay is deliberately
not enabled for ordinary play. Synthetic regressions and the filtered managed
suite pass (240 tests); the same Realm exclusion above still applies. The app
builds with warnings treated as errors, and a local asset-backed smoke run reaches
gameplay. Remaining diagnostics include a dancer action stack underflow and
unresolved child initialization calls. The player-side board can still display
the wrong side despite one-player initialization; loading also reports dropped
simulation ticks. Flickering and full visual compatibility
are not yet verified as fixed. Captures and diagnostic output remain private.

Long notes, score/gauge rules, hit flights, complete theme composition, two-player
play, pause/calibration/device recovery, transactional scene preparation, and
graceful launch errors are not completed by this slice. Do not describe this as
full gameplay compatibility. Synthetic tests verify the time mapping, scheduled
audio, pre-roll judgement, secondary input, and generic Lumen bounds. Asset-backed
and audible synchronization checks must be reported separately.
