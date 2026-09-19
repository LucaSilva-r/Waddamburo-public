# DDP archive indexing

The DDP reader implements the Green asset profile observed in user-supplied data.
These are asset-derived structural facts, not a claim that every Lumen platform or
version uses the same container layout.

Archive directory integers are big-endian and the 12-byte signature is
`LM_NUT_TYPE1`. The directory contains named movie entries followed by texture
entries. A 16-byte trailer supplies the movie-block and texture-block lengths;
entry offsets are relative to their respective block. Each movie references a
half-open range of texture directory indices.

`DdpArchiveIndex.Parse` validates before exposing metadata:

- the signature, preamble, directory, trailer, and both payload block spans;
- record and UTF-8 string limits before allocation;
- minimum table sizes before allocating directory arrays;
- nonempty, NUL-free, unique movie names using ordinal comparison;
- every movie and texture relative span; and
- ordered, in-range half-open movie texture ranges.

Unknown trailer metadata and bytes after the declared payload blocks are retained
as metadata rather than interpreted. A trailer texture count that differs from the
directory count is preserved because its universal meaning has not been established.

`DdpArchiveIndex` contains only immutable names, offsets, sizes, and indices; it
does not retain the archive payload. `DdpArchive` combines an index with one
caller-owned `ReadOnlyMemory<byte>` and returns zero-copy `DdpMovieView` and
`DdpTextureView` slices. Opening several movies therefore neither reparses nor
copies the full archive. The caller must keep that source memory alive and unchanged
while views are in use.

Public tests construct synthetic archives. No original archive names, payloads, or
private corpus manifests are included.
