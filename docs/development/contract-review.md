# Runtime contract review

Review date: 2026-09-22. Status: findings and proposed contracts, not an approved
implementation specification. This review includes the working-tree rainbow
transition changes. Evidence is public project source; no original content or
private observations are reproduced here.

The findings below describe the pre-repair baseline. The first implementation
slice is tracked in [gameplay integration](gameplay-integration.md): offset and
pre-roll mapping, an estimated audio transport position, timestamped input,
authored note/feedback presentation, preview-stop semantics, and reveal exit
cleanup. This does not close the broader recovery, activation, and timing
calibration contracts.

The library boundaries are useful. The main gap is the agreement between them:
which component owns time, when a session becomes active, and what must cease when
its owner exits. Extracting classes alone will not resolve these disagreements.

“Confirmed” below means visible in source, not reproduced in an asset-backed run.
Acceptance scenarios are proposed synthetic regressions, not tests already added.

## Contract map

| ID | Boundary | Missing agreement | Priority |
| --- | --- | --- | --- |
| C1 | Chart / audio / judgement / rendering | One time mapping and start epoch | First |
| C2 | SDL events / gameplay | Timestamped, lossless input delivery | First |
| C3 | Selection / transition / session | One owner for handoff and cancellation | First |
| C4 | Scene lifetime / preview controller / mixer | Stop means no future playback | First |
| C5 | CPU loading / GPU resources / activation | What must be ready before committing | Next |
| C6 | Decoder / mixer / device / game | Readiness, starvation, failure, completion | First, with C1 |
| C7 | Provider / launch validation / presentation | Supported versus partially playable charts | Next |
| C8 | Background work / scene and app shutdown | Cancellation, joining, thread affinity | Next |

The proposed owners below are responsibilities, not requirements to introduce
eight new classes or a general-purpose framework.

## C1 — Gameplay time

**Evidence.** [PlayableChart](../../src/Waddamburo.Catalog/PlayableChart.cs) stores
`AuthoredOffset`; the TJA reader and its tests preserve it. Neither gameplay nor
the app consumes it. [EntrySongSelectFlow](../../src/Waddamburo.App/Flow/GameplayFlow.cs) (now `GameplayFlow`)
starts audio at reveal and derives judgement and rendering time from simulation
tick count. [FixedStepAccumulator](../../src/Waddamburo.Platform.Sdl/Timing/FixedStepAccumulator.cs)
discards excess catch-up ticks, while audio continues on its own producer.

**Confirmed consequence.** Nonzero authored offsets have no effect on playback.
A stall that drops ticks permanently changes chart/audio alignment. Decoder start
and device queue latency are also absent from the mapping.

**Proposed owner and invariant.** A gameplay session owns a transport mapping
between monotonic event time, song playback position, and chart time. Judgement
and rendering consume that mapping. Authored animation may keep its independent
60 Hz clock; dropped animation updates must not discard song elapsed time.

**Decisions needed.** Define the offset sign with synthetic positive/negative
examples before implementing conversion. Define a pre-roll for notes at chart
zero, the audio presentation-position estimate, calibration, pause/focus policy,
and deterministic transport for audio-free probes. Device output time and mixer
production time must remain distinct; an exact hardware clock is not assumed.

**Acceptance.** A synthetic click and note remain aligned with positive, zero,
and negative offsets; a 250 ms render stall causes no permanent shift; variable
decode startup does not change alignment; repeated runs use the same mapping.
Specify a numerical error budget before calling device synchronization verified.

## C2 — Input delivery and judgement order

**Evidence.** [SdlApplication](../../src/Waddamburo.Platform.Sdl/SdlApplication.cs)
folds all polled key events into a held-key set and supplies the same snapshot to
catch-up ticks. [TaikoGameplayPresentation](../../src/Waddamburo.App/Gameplay/TaikoGameplayPresentation.cs)
reconstructs presses from successive snapshots and submits keys in fixed F/J/D/K
order. It advances miss detection before submitting those presses.

**Confirmed consequence.** A down/up pair in one polling batch disappears. Hits
receive simulation timestamps rather than event timestamps; their original order
is lost. Merely adding timestamps later would be insufficient if miss detection
has already advanced beyond them.

**Proposed owner and invariant.** The platform preserves ordered input edges with
monotonic timestamps and a stable tie-break order. The session maps each event
through C1, processes eligible events, then expires misses to the update horizon.
Held snapshots can continue serving authored UI behavior.

**Decisions needed.** Define repeat suppression, focus-loss handling, simultaneous
hits, late-event policy, and whether a key held during scene entry produces a hit.
Scripted probe inputs must enter the same semantic event path.

**Acceptance.** Down/up between updates produces exactly one hit; two presses
between updates remain two; batching and render rate do not change judgements;
catch-up does not duplicate hits; late events cannot move the session backwards.

## C3 — Transition and play-session ownership

**Evidence.** The app separately maintains `GameFlowSession`, `PlayRequestState`,
`RainbowTransitionSequence`, the overlay, chart arrays, audio handle, and gameplay
start tick. [PlayRequestState](../../src/Waddamburo.Game/Gameplay/PlayRequest.cs)
can clear an active request but has no pending cancellation. Rainbow sequence
has no abort/reset operation. Escape during reveal changes to Song Select without
releasing the overlay or completing/resetting the sequence; reveal completion is
only processed in the gameplay branch.

**Confirmed consequence.** That Escape path leaves the sequence in `Revealing`.
A subsequent pending selection cannot begin another cover. Error recovery and
duplicate/cancelled requests do not have a single end-to-end owner.

**Proposed owner and invariant.** One session/transition owner controls accepted
request, target readiness, overlay, input eligibility, and playback lifetime.
Every accepted launch ends in a running session, a recoverable rejection, or
cancellation. Every terminal path releases its resources and permits a new launch.

**Decisions needed.** Define cancellation behavior during lead-in, cover, loading,
reveal, and gameplay; whether source UI still accepts input; and recovery if an
authored animation never reaches its expected stopped state. Keep presentation
labels and timing out of generic scene lifecycle policy.

**Acceptance.** Cancel at every phase, then launch again. Exercise repeated play,
duplicate selection, failed chart/audio loading, missing transition state, and
shutdown mid-transition. Assert state consistency and exact resource disposal.

## C4 — Preview stop and scene audio ownership

**Evidence.** [SongSelectSession.StopPreview](../../src/Waddamburo.Game/SongSelect/SongSelectSession.cs)
calls `SetPreview(null)`. [SongPreviewController](../../src/Waddamburo.App/Audio/SongPreviewController.cs)
interprets null as a request to play background music after a delay.
[SongSelectHostBinding.Dispose](../../src/Waddamburo.Game/Lumen/SongSelectHostBinding.cs)
calls `StopPreview`. The app stops the Preview/Bgm buses during cover, but that
does not cancel the controller operation. Preview completion may also restore Bgm.

**Confirmed mismatch / scheduling risk.** “StopPreview” is not quiescence. An old
scene can schedule background audio during disposal, and stopping current voices
does not prevent pending work from publishing new voices. Actual audible overlap
depends on scheduling and available background audio.

**Proposed owner and invariant.** A scene owns an audio scope. Deactivation revokes
its ability to start playback before existing voices stop. Distinguish preview
selection, explicit menu-background playback, and stop-all-scene-audio. Mixer buses
classify sound; they are not scene ownership tokens.

**Acceptance.** Unload during debounce, asset open, playback, and natural preview
completion. No completion from an inactive scope may start or fade a new scene's
audio. Disposing Song Select must not schedule menu music.

## C5 — Prepare, commit, and retire a scene

**Evidence.** [GameFlowCoordinator](../../src/Waddamburo.Game/Scenes/GameFlowCoordinator.cs)
activates the loaded CPU scene and disposes the previous one before the app uploads
textures or completes gameplay presentation setup. `uploadTextures` materializes
an array through multiple uploads without local rollback if a later upload fails.
An exception disposing the old scene is caught as a load failure after activation
has already committed.

**Confirmed mismatch.** The coordinator's atomic swap covers CPU scene identity,
not complete presentation readiness. Failure before commit and failure retiring
the previous scene are conflated. Partial GPU acquisition lacks local ownership.

**Proposed owner and invariant.** The composition layer prepares an owned bundle
of required CPU, presentation, and session resources. Commit occurs only when the
bundle is usable. Before commit, failure disposes the candidate and preserves the
previous usable scene; after commit, retirement errors have a separate outcome.
Optional resources must have explicit fallback rules.

**Decisions needed.** Define whether title rasterization/audio readiness is required
or optional, and who reports cleanup failures. CPU scene libraries should remain
independent of SDL; a composition-level readiness boundary can bridge them.

**Acceptance.** Fail each acquisition, including the second GPU upload; cancel
after CPU load; throw during old-scene disposal. Verify active identity, retained
resources, fallback behavior, and disposal counts at each boundary.

## C6 — Audio transport status

**Evidence.** [AudioMixer.IsPlaying](../../src/Waddamburo.Platform.Sdl/Media/AudioMixer.cs)
explicitly reports mixer-owned samples, not samples already presented by the
device. `BufferedAudioSource` has no ready/prebuffer signal. An empty read before
completion yields silence in a mixer block. Source failures and
[AudioEngine.Failure](../../src/Waddamburo.Platform.Sdl/Media/AudioEngine.cs) are
stored, but the gameplay path does not inspect them. Stream disposal can wait for
its producer while the mixer lock is held.

**Confirmed gap.** Game code cannot distinguish buffering, advancing song content,
starvation, audible completion, or decode failure through a playback handle.
Calling `PlayStream` is not evidence that the first song sample is audible.
The impact of producer waits on audio deadlines needs measurement.

**Proposed owner and invariant.** The transport exposes explicit preparation,
position, terminal result, and failure semantics. Define which sample count drives
C1 and what an underrun does to that mapping. Mixer completion and output drain
are separate events. Blocking teardown must not hold the mixing critical section.

**Acceptance.** Synthetic sources delay initial data, starve mid-song, fail after
partial output, and finish with device samples still queued. Each has an explicit
status and timing outcome. Stopping a blocked producer must not freeze other voices.

## C7 — Launch eligibility and partial compatibility

**Evidence.** The TJA provider pins chart source identity and validates its hash on
load: preserve this useful contract. But
[TjaPlayableChartReader](../../src/Waddamburo.Providers.Tja/TjaPlayableChartReader.cs)
omits long-note digits while rejecting unsupported commands. Catalog selection
does not prove the chart is playable. Two-player requests are accepted by
`PlayRequestState` and rejected later by `TaikoGameplayPresentation.Start`, after
the scene transition. Presentation uses constant base travel speed and scroll
points, without using chart BPM for travel.

**Confirmed gap.** Accepted selection, loadable chart, partial compatibility, and
supported local player configuration are different states without a common launch
validation result. Unsupported data may either disappear or fail after handoff.

**Proposed owner and invariant.** A launch preparation step validates configuration
and capabilities before commit. Provider conversion reports unsupported features;
the product explicitly chooses rejection or a disclosed partial-play mode. Keep
format support separate from presentation completeness.

**Decisions needed.** Choose partial-chart policy and required visual timing
semantics. Do not infer those semantics from the current simplified renderer.

**Acceptance.** Unsupported commands, long notes, two players, changed chart
source, and missing audio yield predictable results before activation. Rejected
launches leave selection usable; supported notes retain their authored timing.

## C8 — Async work and thread affinity

**Evidence.** The preview controller cancels work but does not join it on dispose.
The title cache lets pending rasterization finish after disposal. CPU scene loading
uses `ConfigureAwait(false)` and invokes host creation/initialization after awaits;
host initialization may reach the SDL Don renderer. The directory content source
already uses asynchronous file reads, so initialization cannot assume it resumes
on the calling thread. SDL window/GPU ownership
is explicitly restricted to its creating thread by `SdlApplication`.

**Risk, not a demonstrated thread fault.** Cancellation alone does not establish
that children have stopped using parent resources. Host initialization has no
explicit dispatch back to the platform thread; whether a particular callback
requires that thread must be made explicit.

**Proposed owner and invariant.** Every async operation has a lifetime owner,
cancellation mechanism, completion barrier, and rule for publishing results.
CPU-only work may finish detached if it cannot touch disposed owners. GPU work and
platform-affine initialization are explicitly scheduled on the owner thread.

**Acceptance.** Delayed synthetic content completes on a worker thread; initialization
still honors platform affinity. Shutdown with pending previews/rasterization causes
no late playback/upload, unobserved failure, or use of disposed parent resources.

## Order of decisions and verification boundary

1. Specify C1/C6 together: time domains, offset examples, readiness, underruns,
   output completion, and error budget. Then specify C2 event ordering against them.
2. Specify C3/C4 together: session state, cancellation, and revocable audio ownership.
3. Define C5's activation boundary and C7's eligibility checks. Use C8 to constrain
   the asynchronous implementation of those decisions.
4. Write focused synthetic acceptance tests around these contracts before adding
   more features or doing broad structural refactoring.

Existing deterministic judgement, parser, provider identity, and dependency tests
remain valuable. Add orchestration tests with fake clock, transport, input,
resource acquisition, and controllable asynchronous completion; do not require
game assets or a GPU to test session ownership. Device timing and presentation
still need separate integration checks.

The preceding review's managed run passed 184 tests across five suites. The
architecture host crashed; excluding SDL and Realm tests passed 30 tests, and two
SDL tests passed separately with a dummy driver. This narrows investigation toward
the Realm test path without establishing the native failure's cause. It is not a
green full-suite result. No native rebuild or asset-backed validation is claimed.
