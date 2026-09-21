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
appropriate for jingles and effects. `BufferedAudioSource` incrementally decodes a
provider-owned `Stream` into a bounded half-second PCM ring. Its mixer-facing read
never waits for the decoder, and stopping the voice cancels decoding and releases
both the native decoder and input stream. `NativeAudioDecoder` keeps managed stream
callbacks rooted for the native decoder lifetime and supports seek and cancellation
through the same C ABI as file decoding. Loading happens before `Play`, never on the
mixer producer.

The `--play-audio=` diagnostic exercises streaming music. `--play-jingle=` decodes a
short user-owned file and plays it on the menu-sound bus; it also works with
`--entry-song-select` to accompany the real Entry-to-Song-Select composition. Both
paths print SDL's selected hardware format.

The interactive Entry-to-Song-Select flow opens one shared audio device (bounded
screenshot runs stay silent unless audio is explicitly requested). Song Select
resolves opaque catalog audio keys through their owning provider, waits for a 150 ms
selection debounce, seeks to the chart's preview time, and plays the bounded stream
on the preview bus. Superseding, stopping, selecting, or disposing the scene cancels
pending catalog I/O and decoding; an active preview fades for 30 ms. Generation
checks prevent a completed asynchronous open from attaching to a superseded scene.

Entry and Song Select now forward their authored `RequestSE`, `RequestSystemSE`,
`RequestPlayerSE`, `NotifyPlayLoopVO`, and `StopVoice` calls across typed host
contracts. In the Entry-to-Song-Select diagnostic, `--sound-root=` points at a
user-owned nuSound2 tree containing `config/nuSound2BankStr.bin` and `se/*.nub`.
The bounded table parser maps authored numeric bank IDs to sanitized bank names;
`RequestSE(bank, cue)` selects the one-based NUB substream `cue + 1`. `VO_` banks
play on the voice bus, other banks play on the menu-sound bus, loop requests replay
the latest authored voice as a loop, and `StopVoice` fades that bus. Unknown system
and player-effect request shapes are traced explicitly until their protocol is
measured; they are not guessed from UI state.

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
