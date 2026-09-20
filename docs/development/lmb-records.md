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

The semantic layer retains the complete `F00C` movie-properties word array. Green
asset observation identifies word 3 as a candidate root sprite ID and word 7 as a
candidate IEEE-754 frame rate; both remain evidence-labelled and are validated
without discarding the raw record.

Timeline semantics keep ordinary `0001` frame groups separate from `F105` seek-state
groups. The latter are full display-list snapshots used only by explicit seeks, not
additional sequential frames. For `0005` removal records, the high 16 bits of payload
word 1 are the timeline depth directly; no one-based adjustment is applied.
