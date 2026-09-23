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

Gameplay movies now receive an empty authored-key snapshot: native gameplay owns
drum input and sends semantic animation callbacks. Entry and Song Select retain
the authored menu key mapping; movie/scene debug viewers retain keyboard access.
This prevents drum keys from also activating keyboard handlers embedded in the
gameplay presentation. Local isolation checks reproduced a player-board animation
restart with mapped keys, but not with repeated hit/combo callbacks alone. Synthetic
input-policy regressions verify all four drum keys remain available to native
gameplay while being withheld from movie scripts, and menu mapping still works.
The follow-up managed run passes 245 tests with the existing Realm exclusion and
warnings treated as errors. A bounded local gameplay run pressing all four drum
buttons retains the 1P board; repeated callback-only probes retain its settled
player-side frame. These checks do not establish full visual compatibility.

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

The Song Select guest setup populates the P1 name board with どんちゃん and places it
at the observed lower-left position. Its player callback marks P1 as joined without
a Banapass profile and P2 as unjoined. The persistent coin indicator uses the
observed Song Select message numbers (-1 for P1, 2 for P2) so its authored movie
shows the right-side join prompt with its fade animation.

Course availability and selection restrictions are separate contracts. Ordinary
Song Select sends zero restriction bits, even when some charts are absent. Star
values describe chart availability (zero for missing charts); the native launch
boundary additionally rejects unavailable courses. Nonzero restriction bits change
the authored board into a restricted mode that can prefer the hidden side. They
must not be derived by inverting catalog availability. Synthetic regressions cover
Oni-only and Oni+Ura metadata, launch of both supported sides, and missing-course
rejection. The presentation property is named SelectionRestrictions to distinguish
it from catalog course availability; the authored callback remains SetInvalidCourse.

Song Select now has a monotonic host countdown implementing StartTimer, StopTimer,
GetTimeSec (remaining seconds rounded up), and IsTimeup. Stopping freezes the
remaining value and disables expiry; starting replaces the duration. These are
explicit host policies, tested using an injected clock rather than render ticks.
Title-surface pending diagnostics remain unchanged because rendering is asynchronous.

The Song Select host translates the authored eight-slot course domain into catalog
courses: normal slots 0–3 retain their meaning and hidden Oni slot 7 maps to Ura.
Hidden Easy/Normal/Hard slots are unsupported and rejected, rather than accidentally
treated as catalog Ura. This corrects a boundary mismatch; it does not establish
that every observed selection failure had this cause. Missing script objects,
folder labels and other unimplemented host calls still require investigation.
The filtered managed suite passes 278 tests (the existing Realm exclusion still
applies). A bounded local Song Select navigation run completes without unresolved
timer methods or host-call exceptions; it does not exercise every course or prove
the original course-selection failure is eliminated. Script-owned Board2MusicInfo
and Startup failures remain separate from missing native Lumen methods.

A subsequent playback-order repair tracks explicit play/stop requests across
frame jumps, including jumps to the current frame and nested calls targeting the
same timeline. Previously, detecting a changed frame could discard a later stop.
Six synthetic regressions cover the reproduced failure and mixed command order;
all 92 Lumen tests pass and the app builds with warnings treated as errors. This
does not resolve the remaining course/counter initialization warnings.

The next runtime slice adds embedded frame jumps, integer conversion, visual
Math.random, and in-process FSCommand notifications. General asset-supplied URLs
remain unsupported and are never opened. Empty standalone stack discard is a
narrow compatibility policy for already-initialized class guards; other consuming
instructions still check for underflow, now with an action-offset diagnostic.

Successful judgements trigger authored note-to-gauge flights, with separate Don/Ka
and normal/large states. A 16-player bounded pool shares scene textures, advances
on simulation ticks, and retires players when their timelines stop. On saturation
it restarts a slot; misses and secondary strong-note presses do not create flights.
This visual feedback does not implement gauge gain or score rules.

The filtered managed suite now passes 257 tests, including flight selection,
overlap/reuse/retirement and runtime instruction regressions. The app builds with
warnings treated as errors. Local checks render and complete all four flight
variants, and a longer gameplay smoke run no longer reports the dancer stack or
unsupported background/judgement-effect action failures. The two lane-board
initialization warnings and dropped-tick warning remain unresolved. Asset-backed
diagnostics and captures are private; no complete compatibility claim is made.

The first visual-runtime follow-up fixes nested calls observing pending timeline
function definitions and adds AVM1 stack-based frame/label jumps, including scene
bias. The composition now includes the dancer and a zero-initialized gauge; gauge
scoring was still not implemented at that stage. A mode-specific support overlay is deliberately
not enabled for ordinary play. Synthetic regressions and the filtered managed
suite pass (240 tests); the same Realm exclusion above still applies. The app
builds with warnings treated as errors, and a local asset-backed smoke run reaches
gameplay. Remaining diagnostics include a dancer action stack underflow and
unresolved child initialization calls. The player-side switching triggered by
drum input was subsequently traced to authored keyboard input leaking into gameplay
movies and isolated as described above. Loading also reports dropped
simulation ticks. Flickering and full visual compatibility
are not yet verified as fixed. Captures and diagnostic output remain private.

Long-note input and authored graphics, plus chart-load error recovery, are now
implemented; see [TJA compatibility](custom-tja.md#gameplay-compatibility).
Dynamic branching, complete theme composition, two-player
play, pause/calibration/device recovery, transactional scene preparation, and
recovery from audio/scene-loading failures remain incomplete. Do not describe this as
full gameplay compatibility. Synthetic tests verify the time mapping, scheduled
audio, pre-roll judgement, secondary input, and generic Lumen bounds. Asset-backed
and audible synchronization checks must be reported separately.

## Score policy

The game tracks one-player score independently of Lumen and sends the running
total through the lane board's `SetScore` callback. Every positive score change
also calls the authored 1P `score_add_don_1p` movie's `Create`, followed by
`SetCount` with that award. Its overlapping number animations keep their
authored local motion. The scene places the movie at x=136, y=143 so its settled
digits sit above and right-align with the lane board's score digits. This
alignment was measured from the user-supplied Green movies; the transform is
owned by the scene composition.

TJA `SCOREINIT` and `SCOREDIFF` supply the first term and difference. When both
are absent, Waddamburo estimates them from note count, large notes, and Go-Go
sections for an approximately one-million-point all-Great chart, with a 4:1
first-term-to-difference ratio. This is a fallback policy, not an original chart
value. Gen 3 score changes at 10, 30, 50, and 100 combo: the difference is
multiplied by 0, 1, 2, 4, and 8 respectively. Each base value is truncated to a
multiple of ten. Great earns the base; Good earns half; Miss earns zero and resets
combo. A 10000-point bonus arrives at each 100 combo and is unaffected by Go-Go.
Large notes earn ordinary points on the first hit and the same amount on a valid
second hand hit. Roll hits earn 100, or 200 for a big roll. Balloon and kusudama
hits earn 300 each, plus 5000 on completion. Go-Go multiplies hit and completion
points by 6/5. Non-integer-tens awards round to the nearest ten, with ties up, so
every score ends in zero. The score progression and combo bonus follow the
[Gen 3 scoring explanation](https://taikotime.blogspot.com/2018/08/feature-combo-scoring-visualized.html),
[advanced scoring rules](https://taikotime.blogspot.com/2010/08/advanced-rules.html),
and [TJA score mode 2 documentation](https://iepiweidieng.github.io/TJAPlayer3/tja/#scoremode).
The strong-hit window and fallback estimate remain Waddamburo policies pending
broader parity validation.


## Long-note presentation follow-up

Gameplay theme movies and normal Don motion now advance from integrated chart
tempo, including tempo changes inside a measure. The presentation policy uses
30 animation frames per quarter-note beat (native 60 Hz at 120 BPM), retaining
fractional frames across ticks and tempo boundaries. Repeated or slightly backward
audio position samples do not replay elapsed animation time. Hit feedback, note
flights, counters, balloon overlays, and screen transitions retain real-time ticks.
The theme's fever and Don-background movies receive `SetFever` on Go-Go changes.
The dancer's `SetDancerFrame`/`GetDancerFrame` names are internal movie methods,
not exported host callbacks; no invented BPM callback is sent. This is a product
timing policy, not a verified recreation of every authored clip's beat period.

Roll bodies have separate movie players so width and hit color do not leak between
notes. The host supplies width, hit notifications, and animation updates. The
shared counter opens once per roll, increments on each accepted hit, and closes
at its end. The balloon overlay opens once, counts remaining Don hits, and selects
success or expiry. Its opening child is prepared with two authored timeline ticks
before the initial nonzero quota is sent. Don native fills and balloon motions are
connected through the existing presentation controller. These are independent
host integration policies derived from movie interfaces, not copied game code.

The runtime now supports AVM1 NewMethod and flash.geom.ColorTransform construction
and MovieClip transform.colorTransform assignment, including action rollback and
persistence across timeline placements. These operations are required by the
roll's authored hit-color animation. Synthetic tests exercise both successful
assignment and rollback on a later unsupported operation.

Gameplay installs a Lumen host marker to disable standalone movie demo controls.
Go-Go callbacks are sent only when the effective chart state changes; duplicate
markers and repeated updates cannot replay the entrance. Transitions and gameplay
movie diagnostics are logged before scene disposal. An isolated local movie probe
reached the steady Go-Go loop without restarting the entrance; the reported
full-game symptom has not been reproduced, so its resolution remains unverified.

Local synthetic-chart probes exercised small/big rolls, balloon countdown and pop,
counters, overlapping flights, and authored hit colors without Lumen diagnostics.
These bounded checks do not establish compatibility with all maps or themes.

The follow-up managed suite passes 313 tests with warnings treated as errors and
the existing Realm exclusion. The app builds with zero warnings. A local GPU
probe verifies the balloon's native Don blowing animation and remaining-hit bubble;
raw captures and diagnostic output remain outside the public repository.

The balloon completion rainbow now uses timeline clipping masks rather than
painting the mask artwork and unclipped rainbow as ordinary quads. A local GPU
probe reproduces the prior spill and verifies the corrected arc without affecting
the surrounding lane, Don, or flights. A subsequently inspected user chart has
17 explicit Go-Go start/end pairs on Hard; repeated entrances on that chart can
therefore be requested by its authored effects. No beat-synchronized suppression
or change to those chart commands is applied.

The masking follow-up passes 316 managed tests with the existing Realm exclusion,
and the app builds without warnings. Synthetic GPU pixel checks pass for alpha
rejection, overlapping mask union, nested intersection, and restored unmasked
layers. The local roll probe now uses the composition's counter position and
confirms that its full hit-count bubble fits on-screen.
