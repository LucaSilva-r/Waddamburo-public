# Waddamburo media C ABI

`waddamburo_media` is the only interface between managed audio code and the
minimal FFmpeg build. Its public header contains no FFmpeg types. ABI version 1.0
uses opaque owned decoder handles, UTF-8 file paths or caller-owned I/O callbacks,
interleaved float output, frame-based seeking, cooperative cancellation, and copied
error data. A zero requested sample rate or channel count preserves the source
format. `total_frames` uses `WADDAMBURO_MEDIA_UNKNOWN_FRAME_COUNT` when the input
cannot report a duration. ABI 1.2 adds optional half-open loop start/end frames;
both use the unknown-frame sentinel when no authored loop is present.

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

Builds with `WADDAMBURO_MEDIA_FFMPEG=ON` decode incrementally through FFmpeg and
return interleaved float samples. File and callback inputs share the same AVIO
path. Before opening the demuxer, seekable inputs are scanned for a bounded complete
RIFF/WAVE stream; this permits Green-era NUB files to expose their contained
ATRAC3plus stream without teaching managed code about the container. File-backed
NUB decoding also reads the enclosing RIFF `smpl` loop when the elementary stream
does not expose one. Builds without the option retain the explicit
`BACKEND_UNAVAILABLE` behavior.

`WADDAMBURO_MEDIA_VGMSTREAM=ON` adds file decoding for `.nus3bank`, `.nus3audio`,
`.bnsf`, `.spsis14`, `.spsis22`, and `.idsp`. The backend uses vgmstream's public
library API and FFmpeg `swresample` for the same output contract. NUS3BANK currently
selects its default/first stream. Callback inputs do not expose a filename and remain
on the FFmpeg path. The pinned local build recipe is documented in
`docs/development/ffmpeg.md`; its G.719-enabled output is explicitly not
redistributable by this project.

The decoder owns demux, codec, resampling, packet, frame, and pending-output state.
It never retains a complete decoded song. Requested output rate/channel conversion
uses `swresample`; zero values retain source properties. Reads return pending frames
first, then advance the demuxer until the destination is full or EOF. Seek flushes
codec and resampler state. Cancellation is checked by AVIO and FFmpeg's interrupt
callback.

The .NET mapping is manually audited in
`src/Waddamburo.Platform.Sdl/Media/NativeMediaMethods.cs`. It maps fixed-width C
integers directly, opaque pointers and callbacks to `nint`, and the C `float *` to
an unmanaged pointer. The committed native ABI test validates negotiation, structure
sizing, copied creation errors, null-handle behavior, and a synthetic NUB-carried
PCM RIFF decoded through memory callbacks. The complete-backend preset also tests a
project-authored silent BNSF/IS22 file, decoding, duration, and seek. Public tests
never use game audio.
