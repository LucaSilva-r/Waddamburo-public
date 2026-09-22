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

TJA files that resolve the same relative `WAVE` asset are one browser song. This
supports libraries that store separate difficulty files beside one audio file as
well as files containing several courses. The song key is derived from the stable
provider ID and root-relative audio identity; chart keys contain the original TJA
byte hash plus course/player/occurrence. Moving the configured library root does
not change either identity, while editing a chart changes only its chart identity.
Identical chart copies within one song are diagnosed and published once.

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

Catalog scanning remains metadata-only. Selecting a chart loads its revision-pinned
notation into `PlayableChart`; supported gameplay features are listed below.

## Gameplay compatibility

The gameplay loader now implements the following scope. This is a compatibility
matrix, not a guarantee for every simulator extension or malformed chart.

| Feature | Earlier behavior | Current behavior |
| --- | --- | --- |
| `0`–`4` | Supported | Empty subdivisions, Don/Ka, big Don/Ka |
| `5`, `6`, `8` | Silently omitted | Small/big rolls with explicit start/end times; either surface counts hits |
| `7`, `9` | Silently omitted | Balloon/kusudama quotas; Don hits pop them; `8` closes either and a second `9` closes kusudama |
| `A`, `B` | Rejected | Single-player big Don/Ka interpretation of partner notes |
| BPM, measure, scroll, Go-Go, barline, delay commands | Between measures only | Commands retain their subdivision position within measures; non-negative delays |
| `#SECTION`, `#LEVELHOLD` | Rejected | Accepted under the fixed-route policy below |
| `#BRANCHSTART`, `#N`, `#E`, `#M`, `#BRANCHEND` | Rejected | Flatten one route, prefer Normal; never concatenate route timelines |
| `#SENOTECHANGE`, `#LYRIC` | Rejected | Accepted without sound-syllable/lyric presentation |
| Bomb, fuse, hidden/adlib and other extended symbols (`C` onward) | Rejected | Still unsupported |
| Complex scroll, sudden visibility, lane movement, scripted gimmicks, medleys | Rejected or ignored as headers | Outside the supported gameplay scope |

Branch selection is currently fixed, **not performance-dependent**. Normal is
preferred; if absent, the first declared route is used. Empty branch blocks are
accepted. Another branch start or chart end may close the preceding branch.
Unused routes do not change timing or effects. Global `BALLOON` entries are
consumed in file order including unused routes; when branch-specific
`BALLOONNOR`/`BALLOONEXP`/`BALLOONMAS` lists exist, branch balloons consume their
own list and common balloons consume `BALLOON`.

Missing/invalid balloon quotas default to one hit. A new long-note head or chart
end closes an unterminated long note; stray terminators are harmless. These are
explicit recovery policies. All long notes count toward the configured note limit.
Long-note hits do not generate tap judgements or alter combo; unpopped balloons
expire without a tap miss. Scoring and gauge formulas remain unimplemented.

Long-note presentation uses user-supplied authored Lumen movies: stretched small/
big roll bodies, incoming balloon notes, roll counters, and the balloon countdown/
completion overlay with a native Don surface. Hits trigger authored flights; big
rolls use their dedicated flight states and balloon pops trigger the completion
effect. Kusudama currently shares the single-player balloon presentation. Gameplay
judgement advances without input, and each interactive press is delivered once.
Chart-loading failures are reported to the console and return to a fresh Song
Select scene. Audio/scene-loading failures are outside that recovery boundary.

Synthetic regressions cover subdivision timing, branch state isolation, quotas,
long-note spans, note limits, repeated hits, popping, expiry, and presentation callback lifecycles.
No user-library corpus or asset-backed visual compatibility claim is implied.
