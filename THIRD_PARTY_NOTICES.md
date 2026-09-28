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
| QRCoder | 1.6.0; <https://www.nuget.org/packages/QRCoder/1.6.0>; upstream commit `bd980577640c47f8bb881cf24c8443415a579d36`; copyright 2013-2018 Raffael Herrmann | MIT | Builds the QR codes the home player setup shows for signing in and joining with a code; no dependencies on .NET 6+. License text: `eng/packaging/licenses/QRCoder.txt` |
| ppy.SDL3-CS | 2026.722.0; <https://www.nuget.org/packages/ppy.SDL3-CS/2026.722.0>; upstream commit `7f836c9f21dad8ee68e70432e5b7d38ceae47eaa`; copyright 2024 ppy Pty Ltd | MIT | Generated C# bindings loaded by the SDL platform adapter |
| SDL | bundled native revision `SDL-3.5.0-a8591d9`; <https://github.com/libsdl-org/SDL/tree/a8591d9>; copyright 1997-2026 Sam Lantinga | Zlib | Platform-native shared library bundled by SDL3-CS and loaded dynamically; distributions must retain SDL's copyright and Zlib license text |
| bgfx C# bindings | `bgfx/bindings/cs/bgfx.cs` from the bgfx.cmake v1.161.9510-579 release archive (below); copyright 2011-2026 Branimir Karadzic | BSD-2-Clause | Generated P/Invoke bindings vendored unchanged as `src/Waddamburo.Platform.Sdl/Rendering/Bgfx/bgfx.generated.cs` except for a first line marking it generated and disabling compiler warnings |
| bgfx shaderc | built from the bgfx.cmake v1.161.9510-579 archive (below); bundles glslang, SPIRV-Cross, SPIRV-Tools, glsl-optimizer, fcpp and DirectXShaderCompiler under their own permissive licenses | BSD-2-Clause plus bundled tool licenses | Build tool only: `eng/build-shaders.*` runs it to compile the `.sc` shaders; D3D11 bytecode uses the Windows SDK's `d3dcompiler_47.dll` on Windows. Not packaged at runtime. |
| ppy.osu.Game | 2026.916.0; <https://www.nuget.org/packages/ppy.osu.Game/2026.916.0>; upstream commit `98fb49876c0242fcf649e0250d6f6b3458769a9e` | MIT | Official osu!lazer Realm model assembly used by the isolated provider prototype |
| ppy.osu.Framework | 2026.914.0; transitive from `ppy.osu.Game` | MIT | Framework types required by the official game model assembly |
| osu!lazer Taiko gameplay reference | upstream commit `48c4800e3ae4ee752452cdff83bd3787ccf3105f`; <https://github.com/ppy/osu/tree/48c4800e3ae4ee752452cdff83bd3787ccf3105f>; copyright 2025 ppy Pty Ltd | MIT | Architectural reference for Waddamburo's independently written timestamped hit-object and separate control-point model. Consulted files include `osu.Game/Beatmaps/ControlPoints/ControlPointInfo.cs`, `TimingControlPoint.cs`, `osu.Game/Rulesets/Scoring/HitWindows.cs`, and Taiko `DrawableHit.cs` / `TaikoHitWindows.cs`; no osu! source is packaged or copied verbatim. |
| Realm .NET | 20.1.0; <https://www.nuget.org/packages/Realm/20.1.0> | Apache-2.0; bundled Realm Core/native notices must be retained | Read-only managed database API and dynamically loaded platform-native wrapper |
| MongoDB.Bson | 2.21.0; transitive from `Realm` | Apache-2.0 | Realm value support |
| tja2fumen soul-gauge table | `hp_values.csv` from <https://github.com/vivaria/tja2fumen> as vendored in TaikoRecomp `tools/vendor/tja2fumen` (2026-09-19); SHA-256 `990ccdcf0b6866c39eedd92e444bd16ea01f81129dbc4923badc16a33370557b`; copyright 2023 Vivaria | MIT | Data file embedded unchanged in `Waddamburo.Game` as `Gameplay/Data/soul-gauge-rates.csv` (per-note soul-gauge amounts by course, star band and note count); the MIT text sits beside it as `soul-gauge-rates.LICENSE.txt` and must accompany release notices. |
| tja2fumen note syllables | `fix_dk_note_types` / `replace_alternate_don_kas` in `tja2fumen/converters.py` from <https://github.com/vivaria/tja2fumen> as vendored in the Waddamburo lab `tools/tja2fumen` (2026-09-27); copyright 2023 Vivaria | MIT | Clustering rule reimplemented in `Waddamburo.Game/Gameplay/TaikoNoteText.cs` to give TJA notes their do/ko/ka text; no source copied. |
| TaikoRecomp / TaikoZucchini title-layout profiles | TaikoRecomp `src/taiko_title_render.c` at `1dc686003e705194b0187c0cb19e84276504645d`; TaikoZucchini `core/title_render.c` at `f3273008d24682f42e089bcf407508390abbc7d7`; copyright 2026 Luca Silva | MIT | Typography constants and Unicode-layout sets adapted into the independently written optional FreeType title rasterizer; no upstream source file or font is packaged. The root `LICENSE` contains the applicable MIT notice. |
| Microsoft.Data.Sqlite | 10.0.12; <https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.12>; copyright .NET Foundation | MIT | Local score database (`Waddamburo.Game/Scores`) |
| SQLitePCLRaw (bundle_e_sqlite3, core, provider, lib) | 2.1.12; transitive from `Microsoft.Data.Sqlite`; <https://github.com/ericsink/SQLitePCL.raw>; copyright Zumero, LLC | Apache-2.0 | Managed SQLite bindings and the bundled native `e_sqlite3` library, loaded dynamically; retain the Apache-2.0 text |
| SQLite | bundled in `SQLitePCLRaw.lib.e_sqlite3` 2.1.12; <https://sqlite.org/copyright.html> | Public domain | Native database engine |
| osu!lazer score storage reference | upstream commit `48c4800e3ae4ee752452cdff83bd3787ccf3105f`; <https://github.com/ppy/osu>; copyright ppy Pty Ltd | MIT | Architectural reference for keeping every play with its replay and a scoring version for rescoring (`osu.Game/Scoring/ScoreInfo.cs`, `osu.Game/Database/RealmAccess.cs` schema versioning, `osu.Game/Screens/Play/SubmittingPlayer.cs`); no osu! source is packaged or copied. |

`src/Waddamburo.Providers.OsuLazer/packages.lock.json` records the exact full
transitive graph of the official game package. BLD-011 must collect and stage the
authoritative license text for every packaged transitive component before the
provider is linked into a release application.

The native dependency pipeline can download and build:

| Component | Version and source | License | Link/use mode and obligations |
| --- | --- | --- | --- |
| FFmpeg | 8.1.2; <https://ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz>; SHA-256 `464beb5e7bf0c311e68b45ae2f04e9cc2af88851abb4082231742a74d97b524c` | LGPL-2.1-or-later for the reviewed configuration | Dynamically linked in developer presets; statically linked into `waddamburo_media` in release packages (`eng/package.sh`). GPL, nonfree, version-3-only, network, external libraries, programs, devices, and filters are disabled. Binary distributions must include the exact corresponding source, build configuration, copyright and LGPL notices, and permit relinking: the complete Waddamburo source and build recipe are public, so a user can rebuild `waddamburo_media` against a modified FFmpeg and replace it. |
| vgmstream | Commit `09c9f40caae4747e44b6a993b3d5b654cef4d1f7`; <https://github.com/vgmstream/vgmstream/tree/09c9f40caae4747e44b6a993b3d5b654cef4d1f7>; source archive SHA-256 `7f012e143fe2945b2edae44bbfdfbad4671d5277c925d38d968a32659369383c` | ISC-style permissive license; copyright 2008-2025 Adam Gashlin, Fastelbja, Ronny Elfert, bnnm, Christopher Snowhill, NicknineTheEagle, bxaimc, Thealexbarney, CyberBotX, EdnessP, et al., with additional authors listed in upstream `COPYING` | Statically linked into `waddamburo_media` by the vgmstream backend and in release packages. The recipe disables G.719 and its optional FFmpeg, MPEG, Vorbis, ATRAC9, CELT, Speex, CLI, and player components; the exact upstream `COPYING` is staged beside the build. |
| vgmstream-cli (user-supplied) | Any official release; <https://github.com/vgmstream/vgmstream/releases> | vgmstream's license plus that of its bundled codecs, including G.719 reference code with no identified redistribution grant | Not distributed, downloaded, or linked. When the user places it next to the executable, it is run as a separate program to decode files the bundled decoder rejects (Nijiiro G.719 audio). |
| bgfx, bx, bimg | bgfx.cmake release v1.161.9510-579; <https://github.com/bkaradzic/bgfx.cmake/releases/tag/v1.161.9510-579>; source archive SHA-256 `2b489206be79d0841009c15853aa8717749373528a694933b926b797b4cfc992`; copyright 2010-2026 Branimir Karadzic | BSD-2-Clause | Built as the shared `bgfx` library (bx and bimg linked in) and loaded dynamically by the renderer; release packages ship it beside the executable. Linux builds enable the GLES 3.0 and Vulkan renderers only. The BSD-2 notice must accompany binaries; `LICENSE` is staged beside the build. |
| FreeType | 2.14.2; <https://download.savannah.gnu.org/releases/freetype/freetype-2.14.2.tar.xz>; SHA-256 `4b62dcab4c920a1a860369933221814362e699e26f55792516d671e6ff55b5e1` (developer builds may use a system 2.13 or newer) | FreeType License (FTL) or GPL-2.0-or-later; used under the FTL | Statically linked into `waddamburo_text` in release packages, built without zlib, bzip2, PNG, Brotli, and HarfBuzz; dynamically linked to the system library in developer builds. The FTL requires crediting the FreeType Project in the documentation; `LICENSE.TXT` and `FTL.TXT` are staged beside the build. |

FFmpeg's combined license includes compatible separately licensed files. The built
`avcodec` library contains DCT objects derived from Independent JPEG Group software;
binary documentation must credit the Independent JPEG Group. Waddamburo applies no
changes to those files. The installed upstream `LICENSE.md` records their exact
terms and must accompany release notices.

`Directory.Packages.props` also reserves reviewed version pins for future product
dependencies—SkiaSharp 4.152.0 and Microsoft.Data.Sqlite 10.0.12—but no project
references them yet. Their full native/transitive notices must be added when the
references are introduced.

Candidate runtime dependencies include SkiaSharp, SQLite, the tja2fumen converter, and offline
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
Waddamburo never builds, downloads, or ships G.719 code. Running a user-supplied
vgmstream-cli is the user's own use of that program, not a representation that it
grants any particular patent, copyright, or redistribution rights.
