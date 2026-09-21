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
appropriate for jingles and effects; songs remain on
the incremental streaming path until each mixed long-form source has its own bounded
decode ring. Loading happens before `Play`, never on the mixer producer.

The `--play-audio=` diagnostic exercises streaming music. `--play-jingle=` decodes a
short user-owned file and plays it on the menu-sound bus; it also works with
`--entry-song-select` to accompany the real Entry-to-Song-Select composition. Both
paths print SDL's selected hardware format.

`SdlAudioDeviceTests` opens the available playback backend, queues silent float
frames while paused, verifies queue accounting and clearing, and exercises resume,
pause, argument validation, and idempotent teardown. `AudioMixerTests` use synthetic
PCM to lock bus summation, volume, saturation, mute timing, looping, fade-out, clip
ownership, and format rejection.

Still intentionally absent are source pause/resume, scene/session-owned voice groups,
mixed streaming sources, a played-sample clock, underrun/device-change recovery, and
persistent latency offsets. Those remaining pieces belong to PLT-024 through PLT-028.
