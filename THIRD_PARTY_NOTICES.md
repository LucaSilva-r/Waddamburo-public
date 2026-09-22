# Third-party notices

The SDL platform adapter and application now restore the SDL3-CS runtime package.
The gameplay clock/input repair also uses osu!lazer's `GameplayClockContainer.cs`
and Taiko `DrawableHit.cs` at commit `48c4800e3ae4ee752452cdff83bd3787ccf3105f`
as architectural references (MIT, copyright ppy Pty Ltd). No source was copied;
the independent implementation and source links are recorded in
`docs/development/gameplay-integration.md` and `docs/legal/provenance.md`.

The test project and isolated osu!lazer provider prototype also restore:

| Component | Version | License | Use |
| --- | --- | --- | --- |
| xUnit.net and Visual Studio runner | 2.9.3 / 3.1.4 | Apache-2.0 | Unit and architecture tests only |
| Microsoft.NET.Test.Sdk | 18.10.1 | MIT | Test discovery and execution only |
| coverlet.collector | 6.0.4 | MIT | Optional test coverage collection only |
| ppy.SDL3-CS | 2026.722.0; <https://www.nuget.org/packages/ppy.SDL3-CS/2026.722.0>; upstream commit `7f836c9f21dad8ee68e70432e5b7d38ceae47eaa`; copyright 2024 ppy Pty Ltd | MIT | Generated C# bindings loaded by the SDL platform adapter |
| SDL | bundled native revision `SDL-3.5.0-a8591d9`; <https://github.com/libsdl-org/SDL/tree/a8591d9>; copyright 1997-2026 Sam Lantinga | Zlib | Platform-native shared library bundled by SDL3-CS and loaded dynamically; distributions must retain SDL's copyright and Zlib license text |
| Slang | 2026.18; <https://github.com/shader-slang/slang/releases/tag/v2026.18>; tag commit `3ed902e99fc730099900837bc8c2ddb9b0423df1` | Apache-2.0 WITH LLVM-exception; release archive includes component notices | Downloaded and executed only by `eng/build-shaders.*`; not linked or packaged at runtime. Linux x64 glibc 2.27 SHA-256 `e45ea4f117d51b8c1e84fa49f562081e73a9f29d02bd4f7fad20678603282829`; Windows x64 SHA-256 `6ffa4827b519fd0a85b38407049d87ab0c1f045fe2289cb1e6831f965169f8a1` |
| Microsoft DirectX Shader Compiler | 1.9.2602.24; <https://github.com/microsoft/DirectXShaderCompiler/releases/tag/v1.9.2602.24>; tag commit `d355aa8364d34df3f0822ba0de8d1dfc75ae6f48` | Microsoft DirectX Shader Compiler binary license plus bundled third-party LLVM terms | The official Windows x64 archive is downloaded and executed only by `eng/build-shaders.ps1` on Windows to generate DXIL; it is not linked or packaged at runtime. SHA-256 `cf658aacf070d3045e31b8f1f8a696c2945f37c1095019481ef7c513368db3b4` |
| ppy.osu.Game | 2026.916.0; <https://www.nuget.org/packages/ppy.osu.Game/2026.916.0>; upstream commit `98fb49876c0242fcf649e0250d6f6b3458769a9e` | MIT | Official osu!lazer Realm model assembly used by the isolated provider prototype |
| ppy.osu.Framework | 2026.914.0; transitive from `ppy.osu.Game` | MIT | Framework types required by the official game model assembly |
| osu!lazer Taiko gameplay reference | upstream commit `48c4800e3ae4ee752452cdff83bd3787ccf3105f`; <https://github.com/ppy/osu/tree/48c4800e3ae4ee752452cdff83bd3787ccf3105f>; copyright 2025 ppy Pty Ltd | MIT | Architectural reference for Waddamburo's independently written timestamped hit-object and separate control-point model. Consulted files include `osu.Game/Beatmaps/ControlPoints/ControlPointInfo.cs`, `TimingControlPoint.cs`, `osu.Game/Rulesets/Scoring/HitWindows.cs`, and Taiko `DrawableHit.cs` / `TaikoHitWindows.cs`; no osu! source is packaged or copied verbatim. |
| Realm .NET | 20.1.0; <https://www.nuget.org/packages/Realm/20.1.0> | Apache-2.0; bundled Realm Core/native notices must be retained | Read-only managed database API and dynamically loaded platform-native wrapper |
| MongoDB.Bson | 2.21.0; transitive from `Realm` | Apache-2.0 | Realm value support |
| TaikoRecomp / TaikoZucchini title-layout profiles | TaikoRecomp `src/taiko_title_render.c` at `1dc686003e705194b0187c0cb19e84276504645d`; TaikoZucchini `core/title_render.c` at `f3273008d24682f42e089bcf407508390abbc7d7`; copyright 2026 Luca Silva | MIT | Typography constants and Unicode-layout sets adapted into the independently written optional FreeType title rasterizer; no upstream source file or font is packaged. The root `LICENSE` contains the applicable MIT notice. |

`src/Waddamburo.Providers.OsuLazer/packages.lock.json` records the exact full
transitive graph of the official game package. BLD-011 must collect and stage the
authoritative license text for every packaged transitive component before the
provider is linked into a release application.

The native dependency pipeline can download and build:

| Component | Version and source | License | Link/use mode and obligations |
| --- | --- | --- | --- |
| FFmpeg | 8.1.2; <https://ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz>; SHA-256 `464beb5e7bf0c311e68b45ae2f04e9cc2af88851abb4082231742a74d97b524c` | LGPL-2.1-or-later for the reviewed configuration | Dynamically linked libraries; GPL, nonfree, version-3-only, network, external libraries, programs, devices, and filters are disabled. Binary distributions must include the exact corresponding source, build configuration, copyright and LGPL notices, and permit replacement of the shared libraries. |
| vgmstream | Commit `09c9f40caae4747e44b6a993b3d5b654cef4d1f7`; <https://github.com/vgmstream/vgmstream/tree/09c9f40caae4747e44b6a993b3d5b654cef4d1f7>; source archive SHA-256 `7f012e143fe2945b2edae44bbfdfbad4671d5277c925d38d968a32659369383c` | ISC-style permissive license; copyright 2008-2025 Adam Gashlin, Fastelbja, Ronny Elfert, bnnm, Christopher Snowhill, NicknineTheEagle, bxaimc, Thealexbarney, CyberBotX, EdnessP, et al., with additional authors listed in upstream `COPYING` | Dynamically linked by the opt-in local Nijiro backend. The recipe disables its optional FFmpeg, MPEG, Vorbis, ATRAC9, CELT, Speex, CLI, and player components. The exact upstream `COPYING` is installed beside the local library. A G.719-enabled build is not approved for redistribution for the separate reason below. |
| libg719_decode and ITU-T G.719 reference code | Local developer-supplied checkout expected at commit `da90ad8a676876c6c47889bcea6a753f9bbf7a73`; <https://github.com/kode54/libg719_decode/tree/da90ad8a676876c6c47889bcea6a753f9bbf7a73> | **No open-source license or redistribution grant identified.** The repository carries copyright notices for Ericsson AB and Polycom, Inc. and references ITU-T G.719 licensing declarations. | Statically incorporated into the locally built vgmstream shared library only when a developer explicitly opts in. Waddamburo never downloads this source, never stages it in the public source tree, and marks the resulting prefix `NON-REDISTRIBUTABLE-G719.txt`. Do not publish, package, or redistribute that library without a separate rights review and any required permission. |
| FreeType | Developer opt-in accepts system 2.13 or newer; <https://freetype.org/> | FreeType License (FTL) or GPL-2.0-or-later | Dynamically linked by the optional native title rasterizer. Not enabled by default and not approved for release packaging until an exact version, source archive checksum, and authoritative FTL text are pinned and staged. |

FFmpeg's combined license includes compatible separately licensed files. The built
`avcodec` library contains DCT objects derived from Independent JPEG Group software;
binary documentation must credit the Independent JPEG Group. Waddamburo applies no
changes to those files. The installed upstream `LICENSE.md` records their exact
terms and must accompany release notices.

`Directory.Packages.props` also reserves reviewed version pins for future product
dependencies—SkiaSharp 4.152.0 and Microsoft.Data.Sqlite 10.0.12—but no project
references them yet. Their full native/transitive notices must be added when the
references are introduced.

Candidate runtime dependencies include SkiaSharp, SQLite, tja2fumen, and offline
shader tools. A component must not be
linked or packaged until this file records:

- its exact version and source URL;
- its license and copyright notice;
- whether it is vendored, statically linked, dynamically linked, or run as a tool;
- all required license, notice, source-offer, and relinking material; and
- any build options that change its licensing, particularly FFmpeg codecs and GPL
  features.

License summaries are not substitutes for the license texts distributed with the
exact dependency versions. Release packages must include those authoritative texts.
The local G.719 notice above is a prohibition for Waddamburo release engineering,
not a representation that possessing or building the third-party source grants any
particular patent, copyright, or redistribution rights.
