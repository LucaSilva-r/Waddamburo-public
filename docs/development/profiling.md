# Profiling the app

Build the app in Release mode, then launch a real session with a user-owned data
directory. Let a person navigate Song Select and play a song; long scripted
`--press` sequences do not represent normal use.

```sh
dotnet build src/Waddamburo.App/Waddamburo.App.csproj -c Release
WADDAMBURO_PROFILE=1 WADDAMBURO_HITCH_TRACE=1 \
  dotnet src/Waddamburo.App/bin/Release/net10.0/Waddamburo.App.dll \
  --game-data=/path/to/USRDIR --start-scene=song-select
```

`WADDAMBURO_PROFILE` reports each movie's archive size, read and parse/decode
time, decoded RGBA bytes, scene texture upload time, managed memory and process
resident memory.

Press **F5** in the running app to toggle the on-screen performance pill.
Hover it to expand poll, audio, update and draw rates and average work times.
POLL is the main-loop SDL event drain and input callback, once per rendered
frame; it is not the keyboard's hardware polling rate. Key events retain their
SDL timestamps for sub-frame judgement. UPDATE is fixed simulation ticks per
second (normally 60), DRAW is rendered frames per second, and AUDIO is mixer
blocks per second (normally around 375 for 128-frame blocks at 48 kHz).
The audio work time measures the software mixer and queue call, not
input-to-speaker latency. Values refresh
every half-second; the overlay does not upload a new texture for each refresh.
Repeated movies in one scene report `reused` when the archive buffer came from
that scene's load scope. `WADDAMBURO_HITCH_TRACE` reports update
and render portions of frames over 40 ms. Both are diagnostic environment flags.
The final `Profile exit` line runs a full collection to estimate retained managed
memory after the session.
Do not commit logs from a user-owned game session to this repository.

For a 480 Hz frame-time stress run, set `WADDAMBURO_GAMEPLAY_FRAME_PROFILE=1`
instead. Its output covers only active gameplay after the rainbow has cleared.
Add `--autoplay` to have the selected chart drive its player's drum during
gameplay; select the song and course normally. Add `--no-countdown` if more
time is needed in Song Select. This diagnostic mode taps regular and big notes
and long notes, and does not save or upload scores.
Each 1,920-frame window reports observed FPS, frame-interval p50/p95/p99/max,
counts over 2.08/4.17/8.33 ms, the longest update/render portions, process CPU
use (100% = one fully used core), RSS, managed memory and GC counts. Reports
are buffered until gameplay ends so console writes do not interrupt play. It omits
scene transitions and intervals while the window lacks focus so their pauses
do not distort note-motion timings.
These are app frame-start intervals, not measured display scanout times. The
render portion includes waiting for a swapchain image as well as recording and
submitting GPU commands; a long render duration alone does not identify GPU
work as the cause.

On Linux, `/usr/bin/time -v` reports peak resident memory after the app exits.
The live `Profile scene` lines help identify *when* memory rises; process RSS
includes driver allocations and is not directly comparable to decoded RGBA
bytes. Managed memory at a scene boundary includes garbage awaiting collection.
Repeat the same route in the same build configuration for before/after
comparisons.

In one local Linux/Vulkan comparison using the same Debug route, the first
gameplay scene boundary fell from about 1,745 to 1,027 MiB RSS and from 989 to
346 MiB managed memory after scene-scoped archive reuse and releasing uploaded
CPU pixels. The Song Select texture upload fell from about 959 to 515 ms after
GPU copy batching. These are point-in-time measurements, not a memory cap or a
cross-platform benchmark. A later Release run on a 240 Hz display recorded no
frames over 8.33 ms during steady gameplay windows; it still had a several
hundred millisecond main-thread scene transition. The remaining visible note
jitter needs display/compositor observation alongside these app-side timings.
In a local 480 Hz Release song run with live logging, 15 of 89,045 active-gameplay frame intervals
exceeded 4.17 ms, none exceeded 8.33 ms, and the longest was 6.19 ms. The
window p99 values ranged from 2.26 to 2.98 ms. Process RSS remained about
651 MiB, and process CPU usage was 25–30% of one core. A one-time GPU reading
showed about 448 MiB attributed to the app and about 14% GPU activity. These
figures describe one machine and song, not a guaranteed performance level. Live
logging may have contributed to the rare long intervals; repeat the run with
buffered reports before using those counts as a gameplay baseline.
In a separate 480 Hz Release autoplay run of a 1,262-note chart, 62,250 active
gameplay frames were measured. One 1,920-frame window contained 68 intervals
over 4.17 ms and 63 over 8.33 ms, with a 35.92 ms maximum; the other windows
combined contained 21 over 4.17 ms and one over 8.33 ms. The busiest window
averaged 403 FPS, while the others averaged 479–480 FPS. Process RSS stayed at
about 671 MiB and CPU use ranged from 25–35% of one core. One GPU sample
attributed about 352 MiB to the app and showed about 10% activity. The cause
of the concentrated long-frame window is not proven; the user interacted with
the desktop during it and reported no visible hitch. Rendering time includes
swapchain waiting. Focused-only filtering was added after this run.

The SDL audio device requests a 256-frame hardware period using
[SDL's sample-frames hint](https://wiki.libsdl.org/SDL3/SDL_HINT_AUDIO_DEVICE_SAMPLE_FRAMES)
unless `SDL_AUDIO_DEVICE_SAMPLE_FRAMES` is set. SDL and the audio backend may choose a
different period; the actual value is available as `HardwareBufferFrames` and
shown by the `--play-jingle` diagnostic. The software mixer targets 512 queued
frames in 128-frame blocks. At 48 kHz, a 256-frame hardware period plus a
512-frame queue is a nominal 16 ms buffer budget. This is not a measured
input-to-speaker latency: backend scheduling, device buffering and the input
path add time. Test audible latency and dropouts on the intended cabinet or
audio interface before making a release claim.
