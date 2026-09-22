# Implementation provenance

Gameplay Don marker/state selection and fever callbacks were independently checked
using the public runtime with user-supplied movie data. The tempo integrator,
motion-state policy, and camera construction are original project choices; no
private implementation, asset script, or capture is copied. Synthetic fixtures
cover tempo boundaries and native-surface ownership. No dependency was added.
Gameplay composite dimensions, center, and semantic camera framing were measured
from a user-supplied graphics capture. Only geometric behavioral requirements are
recorded publicly; raw draws, shader data, GPU constants, addresses, and screenshots
remain outside the public repository. The gameplay camera is constructed with
standard look-at/perspective operations rather than copying captured matrices.

Audio transport interpolation is an original project timing policy. A monotonic
clock drives playback independently of block-sized queue observations. Audio drift
outside a hardware-buffer uncertainty interval is corrected at at most 1% speed;
unchanged output for the greater of 100 ms or four buffers freezes the clock.
These are project policies, not precise hardware playhead measurements. Synthetic
tests exercise high refresh rates, irregular consumption, and stalled output; no
external implementation was adapted.

Song Select countdown handling is an original host implementation based on the
asset-derived start-duration/poll/stop protocol. Monotonic timing, upward rounding
of displayed seconds, and stopped-timer semantics are explicit product policies.
Course conversion keeps the authored normal/hidden eight-slot domain separate
from the catalog's five-course enum; the supported hidden-Oni slot maps to Ura.
Tests use synthetic calls and an injected clock. No asset script or private
diagnostic output is included.

The stack-based AVM1 frame-jump instruction is independently implemented from
Adobe's *SWF File Format Specification*, version 10, ActionGotoFrame2
([specification mirror](https://www.flashrealtime.com/content/dam/Adobe/en/devnet/swf/pdf/swf_file_format_spec_v10.pdf)).
No reference implementation or private bytecode was copied. Synthetic tests cover
numeric frames, labels, scene bias, playback state, and invalid targets. Nested
function visibility is a project runtime repair with a synthetic regression.
Additional gameplay composition layers were identified from user-supplied asset
relationships; no assets or diagnostic captures are included.

The subsequent gameplay-runtime repairs implement ActionGotoFrame, ActionToInteger,
and FSCommand-form ActionGetURL from the same public SWF specification. FSCommand
is exposed only as an in-process notification; general URLs remain unsupported.
Math.random uses the framework's pseudorandom generator for visual effects, not
judgement. An empty standalone Pop is treated as a no-op as a narrow compatibility
decision based on asset-derived class-guard behavior; other stack requirements
remain checked. No external interpreter source was adapted.

Hit-flight state names and lane placement are asset-derived relationships, also
consistent with the private viewer's observable hit-to-gauge behavior. The public
implementation independently owns a bounded pool of animation players sharing
texture resources; no private implementation, bytecode, or captures were copied.

The gameplay repair in `src/Waddamburo.Game/Gameplay/`, the app gameplay adapter,
and the platform transport/input additions are independently written. Semantic
Lumen integration requirements were derived from the private behavioral oracle;
no private implementation or guessed scoring/gauge formulas were copied. The
clock and judgement separation was informed by osu!lazer commit
`48c4800e3ae4ee752452cdff83bd3787ccf3105f`, specifically
`osu.Game/Screens/Play/GameplayClockContainer.cs` and
`osu.Game.Rulesets.Taiko/Objects/Drawables/DrawableHit.cs` (MIT, ppy Pty Ltd).
The project implements its own source-position estimate, pre-roll scheduling,
input delivery and semantic presentation. See `../development/gameplay-integration.md`
for source links, behavioral requirements, decisions, and limitations.

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

`native/media/src/media.c` is an original decoder wrapper written against FFmpeg's
public libavformat, libavcodec, libavutil, and libswresample APIs. Its NUB behavior
uses the independently described fact that the container carries a complete RIFF/
WAVE stream; no TaikoRecomp, proof-of-concept, executable-derived, or game source
code is copied or translated. Synthetic RIFF data supplies the public regression.

The optional vgmstream integration in `native/media/src/media.c` is original wrapper
code written against vgmstream's public `libvgmstream` API. The dependency recipe in
`native/vgmstream/` is original Waddamburo build configuration against upstream
vgmstream commit `09c9f40caae4747e44b6a993b3d5b654cef4d1f7`. It downloads the
unmodified upstream archive and accepts a separately obtained local G.719 source
directory; neither dependency's source is vendored in this repository. Container
selection uses filename extensions and public library behavior. The public decode
regression uses a project-authored, silent synthetic BNSF/IS22 fixture. Private game
audio was used only for local compatibility checks; no sample, hash, path, capture,
or content-derived fixture is stored here. Licensing and the resulting local-only
distribution restriction are recorded in `THIRD_PARTY_NOTICES.md`.

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

`src/Waddamburo.Catalog/PlayableChart.cs` and the playable-chart conversion in
`src/Waddamburo.Providers.Tja/` are original Waddamburo implementations. Their
separation of absolute-time hit objects from timing, scroll, and effect control
points was architecturally informed by the MIT-licensed osu!lazer source at commit
`48c4800e3ae4ee752452cdff83bd3787ccf3105f`, principally
`osu.Game/Beatmaps/ControlPoints/ControlPointInfo.cs`, `TimingControlPoint.cs`,
`osu.Game/Rulesets/Scoring/HitWindows.cs`, and the Taiko ruleset's
`Objects/Drawables/DrawableHit.cs` and `Scoring/TaikoHitWindows.cs`. No osu! source
is copied verbatim, linked, or packaged; the TJA timeline conversion and public
synthetic regressions were independently written for Waddamburo. The retained
upstream attribution is recorded in `THIRD_PARTY_NOTICES.md`.

The Song Select host, native-fill surface contract, package bootstrap, and dynamic
MovieClip behavior are original public implementations. Behavioral expectations
were checked against the private experimental viewer and user-supplied Green assets
under the evidence policy; no viewer source, game asset, font, screenshot, trace,
or content inventory is copied or packaged.

`src/Waddamburo.Formats/Audio/NuSoundBankCatalog.cs` and the authored-audio host
adapters are original implementations from asset-derived table facts and observed
movie-to-host call behavior. The public regression constructs a synthetic big-endian
bank-name table. Local compatibility checks established only the numeric-bank/name
relationship, zero-based cue relationship, and voice lifecycle ordering; no game
table, audio, identifier inventory, trace, decompilation, or proof-of-concept source
is copied or packaged. Multi-stream selection is implemented independently against
vgmstream's public API described above.

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


The expanded TJA gameplay reader and long-note session/presentation are original
project implementations. Format facts were checked against the public
[taiko-web TJA documentation](https://github.com/269Seahorse/Better-taiko-web/blob/master/TJA-format.mediawiki)
(master, accessed 2026-09-22) and the
[TJAPlayer3 format reference](https://iepiweidieng.github.io/TJAPlayer3/tja/)
(revision dated 2026-09-13). No upstream implementation or private PoC code was
copied or adapted, and no dependency was added. Fixed branch selection, permissive
long-note closure, missing-quota defaults, and chart-load
recovery are Waddamburo product policies, not original-game behavior claims.
All new regression fixtures are synthetic.


The authored long-note presentation adapters are independently written from
user-supplied movie interface facts (callbacks, labels, and native-fill semantics).
No movie scripts, assets, captures, private PoC code, or identifier inventory are
copied into the repository. The AVM1 NewMethod and ColorTransform support was
implemented against the public
[Adobe SWF specification](https://www.flashrealtime.com/content/dam/Adobe/en/devnet/swf/pdf/swf_file_format_spec_v10.pdf)
and [ActionScript 2 ColorTransform reference](https://open-flash.github.io/mirrors/as2-language-reference/flash/geom/ColorTransform.html)
(accessed 2026-09-22), with original synthetic bytecode fixtures. No code was
adapted and no dependency was introduced.

Timeline clip-depth interpretation is independently derived from user-supplied
placement data and observed mask/artwork relationships. Stencil command scoping,
the binary alpha threshold, and backend implementation are original project
choices. The backend uses SDL's public
[depth/stencil state](https://wiki.libsdl.org/SDL3/SDL_GPUDepthStencilState) and
[render-target API](https://wiki.libsdl.org/SDL3/SDL_GPUDepthStencilTargetInfo)
(accessed 2026-09-22). Tests use synthetic shapes, textures, and placements. No
private implementation, asset, or raw capture is copied. The new mask shaders use
the existing Slang 2026.18 and DXC 1.9.2602.24 toolchain; no dependency was added.
