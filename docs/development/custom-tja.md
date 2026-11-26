# Custom TJA provider

`Waddamburo.Providers.Tja` is the read-only catalog adapter for user-supplied TJA
libraries. Configure it with one absolute library root and a stable provider ID.
It recursively discovers `.tja` files without traversing symbolic-link directories
and publishes one validated contribution through the shared catalog interface.

## Score uploads and local replays

Settings > Songs > Upload Custom Songs controls TaikOnline uploads for TJA and osu!
songs (`upload_custom_songs` in `config.cfg`). It defaults to on. On first entering
a TJA or osu! library, a fullscreen message explains automatic uploads and the
`donotupload` option, then asks whether to disable custom-song uploads. Both answers
show a confirmation and a reminder that the setting can be changed anytime in game
settings. The answer is remembered by `custom_upload_notice_seen`; setting it to
false asks again on the next library entry. Existing explicit upload preferences
are preserved when upgrading the configuration.

Put an empty file named `donotupload` (no extension) in a chart folder to exclude
all charts in that folder and its subfolders, even with custom-song uploads on.
The check stops at the configured library root; a marker there protects the whole
library. For osu!stable, use the song's folder under `Songs` or the installation
root. The marker is supported for TJA and osu!stable only. osu!lazer's hashed
asset store has no per-song folder, so use the global custom-song upload setting
for lazer instead. Markers are checked fresh, so no library rescan is required.

Plays made under folder protection remain local permanently, including after the
marker is removed or the game restarts. Markers also block earlier pending plays
while present. Custom charts no longer resolvable in the current catalog stay
pending rather than bypassing privacy checks. Official Stock and Nijiiro songs
follow their usual upload rules. A top banner distinguishes uploads disabled for
all custom songs from a chart protected by a folder marker, using the nightly
banner's presentation.

While the setting is off, custom scores, replays and their chart uploads stay on
this PC. The score list's own plays includes successful saved plays awaiting
upload, with local replay playback available even when the score server cannot be
reached. Plays started or saved while custom uploads are off are marked permanently
local and never enter the pending upload queue. Re-enabling applies to new plays;
it does not upload scores made while off. Plays made while uploads were enabled
still use the normal offline retry queue. Turning uploads off also pauses that
queue's custom plays; it cannot recall a request already sent or remove existing
server scores. Local replay data is retained after upload.

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
expire without a tap miss. Score and gauge use the product policies described in
[gameplay integration](gameplay-integration.md#score-policy); full arcade parity is
not established.

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

## Custom dan dojo

`Dani/` in the TJA library (created when missing; the game rewrites `Example.json.txt` and the player-facing
`README.txt` every start, `CustomDans.Example`/`Readme`) holds the player's own dan dojo series:
each `*.json` file is one series, listed in the Dan Dojo folder after the official ones (`Online/CustomDans.cs`,
read by `DanCourses`). JSON comments and trailing commas are allowed.

```json
{
  "name": "My Dojo",
  "courses": [
    {
      "name": "五段",
      "songs": [
        { "path": "Vocaloid/Senbonzakura/Senbonzakura.tja", "course": "oni" },
        { "hash": "<chart sha256>", "title": "optional, shown while the chart is missing" }
      ],
      "requirements": [
        { "type": "soul_gauge", "red": 98, "gold": 100 },
        { "type": "goods", "red": [600, 700], "gold": [700, 800] }
      ],
      "rules": "nijiiro"
    }
  ]
}
```

- A song is a TJA `path` relative to the TJA library with its `course` (easy, normal, hard, oni, ura; default oni),
  or a chart `hash` (`ChartHash`, any library: official, TJA, osu!, Wii). Song Select's **H** copies the song under
  the cursor as one `{ "hash": ... }` line per course. A hash the store does not know yet hashes the library once.
- A course named after a dan rank (五段, 十段, 達人...) uses that rank's board; any other name is a challenge board.
- Requirements are the official courses' types (`soul_gauge`, `goods`, `oks`, `bads`, `combo`, `hits`, `score`,
  `rolls`); `red`/`gold` are one value or one per song. `rules: "nijiiro"` uses Nijiiro's gauge.
- Unranked: course hashes are `custom:` + a hash of the course's JSON and its charts (an edit starts its bests over).
  Plays are saved and their bests show on the boards, but they are never uploaded nor counted for Don Medals
  (`ScoreStore.PendingDanPlays`, `PendingMedalSongs`).

## Background videos

`BGMOVIE:file` (TJAPlayer3, OpenTaiko) plays a video from the chart's folder behind the lane; `MOVIEOFFSET:seconds`
starts it that long after the song's audio (negative: before). osu! beatmaps' `Video,<ms>,"file"` event is read the
same way (`OsuTaikoChartReader.Video`). The chart carries it as `PlayableChart.Video` (`ChartVideo`), resolved when
the chart loads; gameplay draws it over the backdrops, or below 1P's lane (`background_video`, `background_video_dim`).

Black bars are drawn as they are (guessing them clipped figures rising into rows not shown yet and left gaps under
osu! videos). Waddamburo's own headers:

| Header | Effect |
|---|---|
| `BGMOVIEBLEND:add` | The video is added onto the backdrops (light on black); the dancers are hidden. Washes out over bright backdrops (Go-Go, a full gauge): keying holds up better. |
| `BGMOVIEBLEND:luma[,curve]` | A luma key, for videos of light on black (holograms): brightness is opacity, alpha = 1 - (1 - brightness)^curve (8 by default; bigger keeps dark parts more solid), the colour divided back so it matches the video over black. Black goes, lit parts are solid, dark parts partly see-through (no holes). Drawn by the renderer (`fs_luma`, `RenderQuad.LumaKey`): no CPU work. The dancers are hidden. |
| `BGMOVIEKEY:RRGGBB[,tolerance]` | A matte for that colour (green screens; on black videos the luma key holds up better): pixels within the tolerance (RGB distance, 60 by default) are background where they join the frame's edge, or anywhere within a third of it; darker detail inside the figure stays. Softened edge; the dancers are hidden. |
| `BGMOVIERECT:x,y,width,height` | The whole video in that box (1280x720 stage pixels), over the player's area setting (off still hides it). Below 1P's lane, 16:9: `309,348,662,372`. |

Not read: OpenTaiko's `BGA<nn>:` videos switched by `#BGAON`/`#BGAOFF`, and `BGIMAGE:`.
