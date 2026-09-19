# Lossless LMB records

`LmbFile` is the structural layer beneath semantic Lumen definitions. It does not
assign behavior to tags.

The observed Green profile starts with a 64-byte header whose first four bytes are
`LMB\0`. Records then contain a big-endian tag and payload word count followed by
exactly `word_count * 4` payload bytes. Parsing validates every header and payload
span with checked offsets and enforces the configured file and record limits.

The model retains:

- all 64 header bytes;
- serialized record order;
- global and per-tag occurrence indices;
- tag and word-count header bytes;
- header and payload file offsets; and
- zero-copy raw payload views, including repeated and unknown tags.

`WriteTo` reproduces the original byte stream from this structural model. Unknown
tags are ordinary records and are never treated as no-ops or discarded. Singleton
lookup rejects both missing and repeated tags rather than silently choosing one.

Payload views reference caller-owned memory. The caller must keep that memory alive
and unchanged. Semantic decoding, reference validation, and typed timeline commands
are separate layers so a semantic failure cannot erase raw evidence.
