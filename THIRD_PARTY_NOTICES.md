# Third-party notices

No third-party product runtime is linked into the application yet. The test project
and isolated osu!lazer provider prototype currently restore:

| Component | Version | License | Use |
| --- | --- | --- | --- |
| xUnit.net and Visual Studio runner | 2.9.3 / 3.1.4 | Apache-2.0 | Unit and architecture tests only |
| Microsoft.NET.Test.Sdk | 18.10.1 | MIT | Test discovery and execution only |
| coverlet.collector | 6.0.4 | MIT | Optional test coverage collection only |
| ppy.osu.Game | 2026.916.0; <https://www.nuget.org/packages/ppy.osu.Game/2026.916.0>; upstream commit `98fb49876c0242fcf649e0250d6f6b3458769a9e` | MIT | Official osu!lazer Realm model assembly used by the isolated provider prototype |
| ppy.osu.Framework | 2026.914.0; transitive from `ppy.osu.Game` | MIT | Framework types required by the official game model assembly |
| Realm .NET | 20.1.0; <https://www.nuget.org/packages/Realm/20.1.0> | Apache-2.0; bundled Realm Core/native notices must be retained | Read-only managed database API and dynamically loaded platform-native wrapper |
| MongoDB.Bson | 2.21.0; transitive from `Realm` | Apache-2.0 | Realm value support |

`src/Waddamburo.Providers.OsuLazer/packages.lock.json` records the exact full
transitive graph of the official game package. BLD-011 must collect and stage the
authoritative license text for every packaged transitive component before the
provider is linked into a release application.

The native dependency pipeline can download and build:

| Component | Version and source | License | Link/use mode and obligations |
| --- | --- | --- | --- |
| FFmpeg | 8.1.2; <https://ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz>; SHA-256 `464beb5e7bf0c311e68b45ae2f04e9cc2af88851abb4082231742a74d97b524c` | LGPL-2.1-or-later for the reviewed configuration | Dynamically linked libraries; GPL, nonfree, version-3-only, network, external libraries, programs, devices, and filters are disabled. Binary distributions must include the exact corresponding source, build configuration, copyright and LGPL notices, and permit replacement of the shared libraries. |

FFmpeg's combined license includes compatible separately licensed files. The built
`avcodec` library contains DCT objects derived from Independent JPEG Group software;
binary documentation must credit the Independent JPEG Group. Waddamburo applies no
changes to those files. The installed upstream `LICENSE.md` records their exact
terms and must accompany release notices.

`Directory.Packages.props` also reserves reviewed version pins for future product
dependencies—SDL3-CS 2026.722.0, SkiaSharp 4.152.0, and Microsoft.Data.Sqlite
10.0.12—but no project references them yet. Their full native/transitive notices
must be added when the references are introduced.

Candidate runtime dependencies include SDL3, SDL3-CS, SkiaSharp, SQLite,
tja2fumen, and offline shader tools. A component must not be
linked or packaged until this file records:

- its exact version and source URL;
- its license and copyright notice;
- whether it is vendored, statically linked, dynamically linked, or run as a tool;
- all required license, notice, source-offer, and relinking material; and
- any build options that change its licensing, particularly FFmpeg codecs and GPL
  features.

License summaries are not substitutes for the license texts distributed with the
exact dependency versions. Release packages must include those authoritative texts.
