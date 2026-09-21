# SDL audio output

`Waddamburo.Platform.Sdl.Media.SdlAudioDevice` owns the current playback sink.
It initializes the SDL audio subsystem, opens the system's default logical playback
device, and presents one fixed application-side stream: interleaved 32-bit float,
48 kHz, stereo. SDL may choose a different physical format and converts between the
two; the selected driver, hardware rate/channel count, and hardware buffer size are
exposed for diagnostics.

The device starts paused. Producers call `Queue` from their worker thread, bound the
amount reported by `QueuedFrames`, and call `Flush` when no more source data will be
submitted. The owner controls `Resume`, `Pause`, and `Clear`. Native calls and
destruction share one lock so a producer cannot write through an SDL stream while it
is being destroyed. `SubmittedFrames` is the first piece of playback accounting; it
does not yet claim to be a played-sample clock because SDL and the physical device
may hold additional buffered frames.

`StreamingMusicPlayer` handles long-form diagnostic playback. It decodes blocks into
a reusable managed buffer and keeps at most roughly half a second on SDL's
application-side queue. It borrows the device rather than opening or closing one
itself.

`AudioEngine` is the first software-mixed path. Its background producer renders
256-frame blocks and bounds SDL's queue to 1,024 application frames. `AudioMixer`
provides BGM, preview, menu-sound, voice, and drum-hit buses, master/per-bus volume,
bus mute, concurrent voices, looping, immediate stop, and sample-counted fade-out.
Mixing is additive float with final saturation to `[-1, 1]`.

`AudioClip` predecodes short one-shots into the device format, defaults to a
30-second safety limit, and never permits a limit above two minutes. This is
appropriate for jingles and effects. Clips retain an optional half-open authored
loop region. The mixer plays the intro once, wraps from the authored end back to
the authored start, and does not replay codec tail padding. Stock ATRAC NUBs expose
that range through their enclosing RIFF `smpl` chunk even when the elementary
stream decoder has no loop metadata. `BufferedAudioSource` incrementally decodes a
provider-owned `Stream` into a bounded half-second PCM ring. Its mixer-facing read
never waits for the decoder, and stopping the voice cancels decoding and releases
both the native decoder and input stream. `NativeAudioDecoder` keeps managed stream
callbacks rooted for the native decoder lifetime and supports seek and cancellation
through the same C ABI as file decoding. Loading happens before `Play`, never on the
mixer producer.

The `--play-audio=` diagnostic exercises streaming music. `--play-jingle=` decodes a
short user-owned file; in the Entry-to-Song-Select composition it plays on the BGM
bus. When a sound root is present and no override is supplied, that composition
resolves and loops the user-owned `bgm/nub/JINGLE_ENTRY.nub` during Player Entry.
On activation it stops that voice and loops Song Select's distinct
`bgm/nub/JINGLE_GENRE.nub`. Both paths print SDL's selected hardware format.

The interactive Entry-to-Song-Select flow opens one shared audio device (bounded
screenshot runs stay silent unless audio is explicitly requested). Song Select
resolves opaque catalog audio keys through their owning provider, waits for a 250 ms
stable hover, seeks to the chart's preview time, and plays the bounded stream
on the preview bus. Superseding, stopping, selecting, or disposing the scene cancels
pending catalog I/O and decoding; the active browser-music voice fades for 100 ms.
Generation checks prevent a completed asynchronous open from attaching to a
superseded scene. The authored `NotifyStopBGM(category, song)` call selects one
logical browser-music slot: valid song coordinates fade the previous voice out,
leave the debounce/decode interval silent, then fade the preview in over 300 ms.
The movie sends `song = -1` for Return and other non-song boards. Leaving a preview
fades it immediately, then restarts `JINGLE_GENRE` from its intro only after the
cursor remains on a non-song board for 250 ms. Moving among non-song boards leaves
an already playing jingle untouched, including a Return confirmation that closes a
category. The jingle and previews therefore never play concurrently, and scrolling
cannot expose fragments of the background between successive previews.

Entry and Song Select now forward their authored `RequestSE`, `RequestSystemSE`,
`RequestPlayerSE`, `NotifyPlayLoopVO`, and `StopVoice` calls across typed host
contracts. In the Entry-to-Song-Select diagnostic, `--sound-root=` points at a
user-owned nuSound2 tree containing `config/nuSound2BankStr.bin` and `se/*.nub`.
The bounded table parser maps authored numeric bank IDs to sanitized bank names;
`RequestSE(bank, cue)` selects the one-based NUB substream `cue + 1`. `VO_` banks
play exclusively on the voice bus, while other banks play on the menu-sound bus.
An authored loop notification replays the latest voice once only when it is no
longer active; it does not create an unbounded PCM loop. `StopVoice` stops only
that replay, not an ordinary spoken one-shot. Entry keeps an accepted scene
transition pending until the ordinary or replayed voice completes.
Song Select's observed player-effect protocol carries a drum-tone ID and a semantic
hit kind. Browser feedback uses the common `SE_COM` bank: cue 0 for Don (centre)
and cue 3 for Ka (rim). This remains driven by authored `RequestPlayerSE` calls,
not by platform-specific key bindings, and plays on the dedicated drum-hit bus.
Unknown system and player-effect shapes are traced.

`SdlAudioDeviceTests` opens the available playback backend, queues silent float
frames while paused, verifies queue accounting and clearing, and exercises resume,
pause, argument validation, and idempotent teardown. `AudioMixerTests` use synthetic
PCM to lock bus summation, volume, saturation, mute timing, looping, fade-out, clip
ownership, streaming-source mixing/disposal, full teardown, and format rejection.
The managed callback bridge is also checked locally with a synthetic in-memory WAV,
including frame seek; no game audio is used by that check.

Still intentionally absent are complete system/player-effect mappings, source
pause/resume, mixed gameplay BGM, a played-sample clock, underrun/device-change
recovery, and persistent latency offsets. Those remaining pieces belong to PLT-024
through PLT-028.
