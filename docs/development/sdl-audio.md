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

`StreamingMusicPlayer` is the first producer. It decodes blocks into a reusable
managed buffer and keeps at most roughly half a second on SDL's application-side
queue. It borrows the device rather than opening or closing one itself. This
ownership boundary permits the next audio-engine layer to place a mixer in front of
the same sink without BGM, previews, voices, and effects creating competing physical
devices.

The `--play-audio=` diagnostic prints both the decoded stream format and SDL's
selected hardware format. `SdlAudioDeviceTests` opens the available playback backend,
queues silent float frames while paused, verifies queue accounting and clearing, and
exercises resume, pause, argument validation, and idempotent teardown.

Still intentionally absent are mixer buses, per-source pause/gain/fades, a played
sample clock, underrun/device-change recovery, and persistent latency offsets. These
belong to PLT-024 through PLT-028 rather than the device wrapper.
