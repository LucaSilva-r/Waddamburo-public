# Waddamburo media C ABI

`waddamburo_media` is the only interface between managed audio code and the future
minimal FFmpeg build. Its public header contains no FFmpeg types. ABI version 1.0
uses opaque owned decoder handles, UTF-8 file paths or caller-owned I/O callbacks,
interleaved float output, frame-based seeking, cooperative cancellation, and copied
error data. A zero requested sample rate or channel count preserves the source
format. `total_frames` uses `WADDAMBURO_MEDIA_UNKNOWN_FRAME_COUNT` when the input
cannot report a duration.

Callers must negotiate `WADDAMBURO_MEDIA_ABI_VERSION_1_0` before using the decoder.
The high 16 bits are the breaking major version and the low 16 bits are the additive
minor version. Existing exports and fields cannot be reordered or removed within a
major version. Every public input/output structure starts with `struct_size`; callers
initialize it to `sizeof(structure)`, and implementations accept larger structures
to permit additive tails.

The decoder returned by either create function is owned by the caller and must be
passed to `waddamburo_media_decoder_destroy` exactly once. Destroying `NULL` is safe.
Callback function pointers and `user_data` must remain valid until destruction.
Errors returned during creation are copied into the caller's error structure so no
native string lifetime crosses the boundary.

The read callback reports end of input with `WADDAMBURO_MEDIA_END_OF_STREAM`. Seek
callbacks use the declared `SEEK_BEGIN`, `SEEK_CURRENT`, and `SEEK_END` constants;
a null seek callback declares a non-seekable input. The optional cancellation
callback returns nonzero when work should stop. `decoder_cancel` provides the same
signal from another thread after a handle has been created.

The current library intentionally returns `BACKEND_UNAVAILABLE` from valid create
calls. PLT-022 will connect the pinned dynamic FFmpeg build without changing this
ABI.

The .NET mapping is manually audited in
`src/Waddamburo.Platform.Sdl/Media/NativeMediaMethods.cs`. It maps fixed-width C
integers directly, opaque pointers and callbacks to `nint`, and the C `float *` to
an unmanaged pointer. The committed native ABI test validates negotiation, structure
sizing, copied creation errors, and null-handle behavior.
