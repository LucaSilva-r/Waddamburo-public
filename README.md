# Waddamburo

Waddamburo is an independent, MIT-licensed C# game engine for compatible Taiko
presentation and rhythm-game data.

This is a bring-your-own-assets project. The repository and its releases do not
include a game executable, firmware, original game assets, commercial fonts,
extracted media, or generated decompilations. Users must supply files they are
entitled to use from their own local installation.

## Status

The public implementation is starting from a clean repository. It does not yet
contain a runnable game. New product code will be implemented here without copying
the private proof of concept or importing executable-derived source.

The initial targets are Linux x64 and Windows x64 on .NET 10. The intended product
is a local game plus reusable libraries for asset formats, animation, rendering,
audio, input, catalogues, and persistence.

## Project boundaries

- Product code may use public specifications, independently recorded asset-format
  facts, and black-box behavioral requirements.
- Runtime addresses, lifted code, original content, and content-reproducing research
  artifacts are prohibited.
- Tests committed here use synthetic fixtures.
- Local inputs and generated observations remain outside this repository.

See the [evidence policy](docs/legal/evidence-policy.md),
[provenance policy](docs/legal/provenance.md), and
[release-content policy](docs/legal/release-content-policy.md).

## Legal

Waddamburo is not affiliated with, authorized by, sponsored by, or endorsed by
Bandai Namco Entertainment Inc. or its affiliates. “Taiko no Tatsujin” and related
names and marks are the property of their respective owners and are referenced only
to describe compatibility.

Waddamburo source is licensed under the [MIT License](LICENSE). Dependencies retain
their own licenses and will be recorded in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
