# Parser safety foundation

All binary format readers build on `BoundedBinaryReader` in
`Waddamburo.Formats.IO`. It accepts either `ReadOnlyMemory<byte>` or a readable
stream, decodes explicitly selected little- or big-endian scalars, and reports
malformed reads using absolute asset offsets. Seekable and non-seekable streams
are supported; callers must not assume a stream fills a requested span in one
read.

`ParserLimits` is immutable and is supplied when constructing a reader or parser.
The defaults are ceilings rather than expected asset sizes:

| Resource | Default maximum |
| --- | ---: |
| File | 2 GiB |
| Single allocation | 256 MiB |
| Records | 1,000,000 |
| String bytes | 1 MiB |
| Strings | 250,000 |
| Texture dimension | 16,384 |
| Texture bytes | 512 MiB |
| Action bytes | 16 MiB |
| Vertices | 10,000,000 |
| Indices | 30,000,000 |
| Bones | 4,096 |
| Frames | 1,000,000 |
| Measures | 1,000,000 |
| Notes | 10,000,000 |

Format-specific parsers may impose lower structural limits. They must check counts
and checked 64-bit spans before loops, slicing, or allocation. A
`FormatLimitException` identifies the breached limit, actual value, ceiling, and
absolute offset. A `FormatReadException` identifies truncation or malformed data,
including the requested and available lengths where applicable.

Tests use only synthetic byte sequences and streams. Original assets and observed
private corpus data do not belong in this test suite.
