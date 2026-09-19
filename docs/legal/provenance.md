# Implementation provenance

This repository began as a clean public implementation on 2026-09-19. It does not
inherit the commit history of the private research and proof-of-concept repository.

Unless a file-specific entry says otherwise, Waddamburo code is original project
work by Luca Silva and contributors and is licensed under the root MIT license.

No Zucchini, TaikoRecomp, proof-of-concept viewer, decompiled, or executable-derived
source code is currently included. Private research may supply semantic behavioral
requirements and independently described asset-format facts under the evidence
policy; it is not a source tree for copying implementation code.

Before adding adapted or vendored code, update this document in the same change with:

- the destination file or directory;
- the upstream project, source file, and exact revision;
- the upstream license and retained notice location; and
- whether the change is copied, adapted, generated, or independently implemented
  from documented behavior.

Third-party packages and tools must also be recorded in the root
`THIRD_PARTY_NOTICES.md`.

The dependency recipe in `native/ffmpeg/` is original Waddamburo build
configuration written against FFmpeg's public configure interface. It downloads
the unmodified official FFmpeg 8.1.2 archive by version and SHA-256; no FFmpeg or
private proof-of-concept source is vendored in this repository.
