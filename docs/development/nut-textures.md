# NTP3/NUT texture packs

`NutFile` implements the NTP3 layouts observed in Green Lumen and Don assets.
These are asset-derived profile facts; unsupported versions or pixel formats fail
explicitly instead of being guessed.

The 16-byte file header and all multi-byte header values are big-endian. Versions
1 and 2 differ in placement:

- version 1 places each payload after its texture header and advances by the
  texture's declared total size;
- version 2 stores the texture headers together and addresses each payload using
  a header-relative offset.

The reader supports multiple textures, mip counts, formats 0 (BC1), 2 (BC3), 14
and 17 (raw A,R,G,B), dimensions, payload sizes, and optional IDs from a terminal
`GIDX` header block. A missing `GIDX` remains a null identity rather than an
invented ordinal. Complete file and texture headers are retained as immutable byte
arrays so unknown fields remain available for later analysis.

Parsing validates header and payload spans, version-specific placement, v2 header
table separation and payload overlap, dimensions, possible mip count, minimum
payload bytes, and parser resource limits. Texture payloads remain zero-copy views
of caller-owned source memory. The caller must keep that memory alive and unchanged.

`BlockCompressionDecoder` is a portable reference decoder for the base-level BC1
and BC3 block formats. It writes R,G,B,A bytes, handles partial edge blocks, BC1
transparent mode, both BC3 alpha modes, and allocation limits. Rendering backends
should normally consume the original compressed `NutTexture.Data` directly when
their GPU format supports it; the CPU path exists for tests and diagnostics.

`NutTextureDecoder.DecodeRgba8` is the common base-mip upload fallback. It routes
BC1/BC3 through the reference decoder and converts raw formats 14/17 from stored
A,R,G,B order to R,G,B,A. It enforces the same allocation ceilings, so platform
adapters do not need format-specific byte-order logic.

Public tests use synthetic headers, payloads, and fixed colour vectors only.
