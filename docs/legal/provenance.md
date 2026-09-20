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

`src/Waddamburo.Providers.OsuLazer/` is original integration code written against
the public `ppy.osu.Game` 2026.916.0 and Realm 20.1.0 APIs. It consumes official
model types through NuGet and does not copy or adapt osu! source files. The package
records upstream osu! commit `98fb49876c0242fcf649e0250d6f6b3458769a9e`.

`src/Waddamburo.Providers.Tja/` is an original managed catalog implementation.
Its format behavior was checked against TaikoRecomp revision
`1dc686003e705194b0187c0cb19e84276504645d`, principally
`docs/custom_songs.md`, `tools/custom_songs.py`, and the MIT-licensed
`tools/vendor/tja2fumen` documentation and parser. No TaikoRecomp or tja2fumen
source is copied, translated, linked, or packaged. The implementation independently
expresses metadata/course normalization, encoding fallback, relative-audio, and
source-identity behavior behind Waddamburo's catalog contracts; synthetic public
tests were authored specifically for this repository.

The Song Select host, native-fill surface contract, package bootstrap, and dynamic
MovieClip behavior are original public implementations. Behavioral expectations
were checked against the private experimental viewer and user-supplied Green assets
under the evidence policy; no viewer source, game asset, font, screenshot, trace,
or content inventory is copied or packaged.

The independently written FreeType implementation in `native/text/src/text.c`
adapts song-title typography constants and Unicode-layout sets from
TaikoRecomp `src/taiko_title_render.c` at
`1dc686003e705194b0187c0cb19e84276504645d` and TaikoZucchini
`core/title_render.c` at `f3273008d24682f42e089bcf407508390abbc7d7`.
Both sources are MIT-licensed, copyright 2026 Luca Silva. The adaptation retains
the compact/expanded dimensions, font/leading/outline proportions, punctuation
rotation, small-glyph spacing, and subtitle-column behavior, but implements a new
bounded C ABI, UTF-8 decoder, mask compositor, DPI scaling, and managed cache for
Waddamburo. It accepts only an explicit user-owned font path; no font or source
asset is copied or packaged. The retained MIT attribution is recorded in
`THIRD_PARTY_NOTICES.md` and the repository `LICENSE` carries the same terms and
copyright owner.

`src/Waddamburo.Platform.Sdl/SdlApplication.cs` is original integration code written
against SDL's public GPU API and the generated `ppy.SDL3-CS` 2026.722.0 bindings.
No SDL or binding source is copied into the repository; the NuGet package supplies
the dynamically loaded platform-native SDL library.

The shader sources under `src/Waddamburo.Platform.Sdl/Shaders/` are original
Waddamburo code. Their checked-in SPIR-V and DXIL files are generated from those
sources by the unmodified Slang 2026.18 command-line compiler and, for Windows
DXIL, Microsoft DirectX Shader Compiler 1.9.2602.24 pinned in
`eng/build-shaders.*`; neither compiler is vendored or shipped at runtime.
