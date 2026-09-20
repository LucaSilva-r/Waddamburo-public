# Custom TJA provider

`Waddamburo.Providers.Tja` is the read-only catalog adapter for user-supplied TJA
libraries. Configure it with one absolute library root and a stable provider ID.
It recursively discovers `.tja` files without traversing symbolic-link directories
and publishes one validated contribution through the shared catalog interface.

## Metadata and categories

The initial inspector supports UTF-8 (with or without BOM), UTF-16 BOMs, and
Shift-JIS. It reads metadata without interpreting note data:

- `TITLE`, `TITLEJA`, `TITLEEN`, `SUBTITLE`, `SUBTITLEJA`, and `ARTIST`;
- `WAVE` and non-negative `DEMOSTART` seconds;
- required positive `BPM` metadata;
- named or numeric Easy, Normal, Hard, Oni, and Edit/Ura courses;
- `LEVEL`, plus distinct `#START P1` and `#START P2` variants; and
- `GENRE` for root-level files.

For nested files, the first directory below the configured root is the category.
Root-level files use `GENRE`, falling back to `Uncategorized`. Song order,
categories, duplicate handling, and diagnostics are deterministic.

Course levels follow the reference converter's 1–10 clamp, `1P`/`2P` spellings
normalize to `P1`/`P2`, and a course must be declared before `#START`. Negative
`DEMOSTART` values clamp to zero.

Song keys are SHA-256 identities of the original chart bytes. This keeps identity
stable when a library root moves and intentionally creates a new identity when the
chart file changes. Chart keys add course/player/occurrence identity. Identical
chart copies within one provider are diagnosed and published once.

## Assets and safety

`WAVE` is resolved relative to its TJA file and must remain beneath the configured
library root. Absolute paths, root escapes, symbolic-link traversal, missing audio,
and malformed values do not expose a file to consumers. Chart and audio references
are opaque provider-owned keys; `ICatalogAssetResolver` validates ownership and
containment again whenever a caller opens one.

Scans are bounded by configurable file-count and per-chart byte limits. A malformed,
unsupported, oversized, or incomplete TJA produces a structured diagnostic while
healthy files remain available. Scanning and asset reads never write to the user
library.

This catalog pass does not yet parse note commands into `PlayableChart`. Full TJA
timing, branches, note families, offsets, and gameplay normalization remain part of
the managed chart-format milestone.
