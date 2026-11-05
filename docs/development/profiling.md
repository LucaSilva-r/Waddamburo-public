# Profiling the app

## Performance reports from players

Two ways to get a report a player can send with a performance problem. Both write one plain-text file
to the game's `logs` folder (next to the crash logs), rewritten every 30 s and once more at the end, so a
crash or a killed run keeps what came before:

- **Record Performance** (Settings, or `record_performance = true` in `config.cfg`, or `--benchmark-record`):
  records normal play until turned off or the game closes; `performance-<time>.txt`.
- **Run Benchmark** (Settings > Display, or `--benchmark`): a fixed route that compares across machines:
  the attract loop for 3 minutes (its demo songs and scene parts seeded, so every run picks the same),
  Song Select scrolled by script (20 s right, 10 s left), then the built-in stress song
  (`src/Waddamburo.App/Benchmark/stress.tja`, about 1,600 notes: streams, big notes, rolls, balloons,
  Go-Go, scroll 0.5 to 4, BPM 240, branches) on autoplay over a click track, so it needs no song
  library or music file; then its first 75 s again for two players (two Don-chans), then the two players'
  results for 15 s. Song Select and the first song load in the background before their switch, as Entry and
  Song Select do in play; the 1P to 2P switch loads cold (a cold-load measurement). The report names the songs
  `benchmark-1p` and `benchmark-2p`. `--benchmark` quits when
  done; from the settings the game starts again as usual. `benchmark-<time>.txt`.
  `WADDAMBURO_BENCHMARK_ATTRACT=<seconds>` shortens the attract step when testing the route.

The file, top to bottom: the machine (OS, CPU, RAM, every GPU the system lists and the one bgfx renders
with, the display mode, the settings that matter); a summary per scene (fps, 1% low, worst frame, frames
of 25 ms or more); memory (RSS and managed first/last/peak, the lowest RSS of every 5 minutes and its
trend in MiB/hour after the first window: a steady climb is a leak); every frame of 25 ms or more with
its update/render split, GC collections and pause, and the bgfx stats; events (scene switches with
their load and collection times, focus changes, benchmark steps); then one tab-separated line per
second: fps, 1% low, p50/p99/max frame time, longest update and render, ticks, process and system CPU
(share of all cores), process RSS and managed heap, system RAM used/total, GC collections and pause.


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
resident memory. Each `Profile switch` line times one scene switch (exit, load,
enter, and the compacting collection that follows it unseen under a fade) with
the bytes it allocated and the GC counts. It also prints a fatal error's stack.
The Windows release exe logs when its output is redirected (`Waddamburo.exe > log.txt`).

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

## Texture memory and loading

The game's NTP3 textures are mostly BC3 (79% of all pixels), the rest BC1 or ARGB. BC1/BC3 textures whose
sizes are multiples of 4 go to the GPU as their own blocks (`LumenMovieContent.Load(keepCompressed: true)`,
when the GPU draws both natively): a quarter of RGBA's memory, which on an integrated GPU is system RAM,
and no decoding at load. Code that needs pixels decodes the one texture it reads
(`LumenTextureContent.Pixels()`): the genre-colour patch the textures it recolours, pixel-art movies all of
theirs, the background upscaler what it upscales. `WADDAMBURO_DECODE_TEXTURES=1` decodes everything to RGBA
as before, for comparisons.

The upscaled-texture cache is named by a hash of each texture's decoded pixels. `index.tsv`, next to
`textures.tar`, remembers each texture's name by archive, movie and texture index, with the archive's size
and modification time: a texture is decoded and hashed the first time it is loaded, and found by lookup
after that. An archive whose size or time changed has its lines ignored and learned again.

Measured with the benchmark on one machine (Ryzen 7 5800X, RTX, upscaling on, 6,576 cached): entering the
stress song went from 5,468 ms and 2,330 MiB allocated to 50 ms and 101 MiB (the lookups replaced decoding
and hashing every texture); Song Select's movie from 343 MiB of decoded pixels to 17 MiB; the run's managed
peak from 1,024 to 417 MiB and its RSS peak from 2,384 to 1,581 MiB.
