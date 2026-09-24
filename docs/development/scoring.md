# Scoring, judgement and soul gauge

The rules Waddamburo follows for judgement, score, soul gauge and combo, with their sources and
the engine's status. The target is the arcade release the supported data comes from: **AC15
(the "new cabinet" series, Green Ver.)**. `config/S11100-1/musicinfo.xml` identifies itself as
`TaikoAC15 MusicInfo`. AC16 (Nijiiro) changed scoring substantially; its rules are noted only where
they help avoid confusion.

## Sources

| Id | Source | Category |
|---|---|---|
| W1 | 太鼓の達人 譜面とかWiki, [システム/配点](https://wikiwiki.jp/taiko-fumen/%E3%82%B7%E3%82%B9%E3%83%86%E3%83%A0/%E9%85%8D%E7%82%B9) (last modified 2026-09-07) | Public community documentation |
| W2 | 太鼓の達人 譜面とかWiki, [システム/基本システム](https://wikiwiki.jp/taiko-fumen/%E3%82%B7%E3%82%B9%E3%83%86%E3%83%A0/%E5%9F%BA%E6%9C%AC%E3%82%B7%E3%82%B9%E3%83%86%E3%83%A0) | Public community documentation |
| W3 | 太鼓の達人 譜面とかWiki, [システム/魂ゲージの伸び率](https://wikiwiki.jp/taiko-fumen/%E3%82%B7%E3%82%B9%E3%83%86%E3%83%A0/%E9%AD%82%E3%82%B2%E3%83%BC%E3%82%B8%E3%81%AE%E4%BC%B8%E3%81%B3%E7%8E%87) | Public community documentation |
| F | Fumen chart headers and notes of user-supplied charts (see below) | Asset-derived observation |
| T | tja2fumen (MIT, vendored table in `Data/`) | Public implementation (for TJA charts) |

The wiki's facts are community measurements (autoplay captures, full-Great videos, arithmetic on
displayed scores). They are the behaviour to match; where it says a value is approximate, so is
ours.

## Judgement

### Windows (W2)

Offsets from the note's time, in ms (they are frame multiples at 59.94 Hz: 1.5, 2.5, 4.5, 6.5,
7.5 frames).

| Course | 良 Great | 可 Good | 不可 Bad |
|---|---|---|---|
| Oni (and ura) | ±25.025 | ±75.075 | ±108.442 |
| Hard, Normal, Easy | ±41.708 | ±108.442 | ±125.125 |
| Easy with parent-and-child support (パパママサポート) | ±41.708 | ±125.125 | ±125.125 |

A hit between the Good and Bad windows is a Bad. A note never hit is a Bad at the end of its
window. Hitting the wrong colour shows no judgement but counts as a Bad in the results (W2).

**Status: wrong.** The engine uses 35 / 80 / 95 ms for every course
(`TaikoGameplayPresentation`). These are placeholders that predate this research.

### Big notes (W2)

A big note hit hard enough is a 特良 / 特可 (big Great / big Good). On the arcade that means a hit
above the cabinet's big-note strength limit (default 25, settable from the small-note limit + 1 up
to 100), with one hand or two. Home versions need both hands. A big note hit normally scores as a
small note.

**Status: implemented as two hands**, i.e. a second hit on the same colour within 30 ms
(keyboards have no strength). That is the home rule, and the only one a keyboard can follow.

### Rolls and balloons (W2)

Drum rolls, balloons and kusudama never produce a Bad and never touch the gauge or the combo.

**Status: implemented.**

## Score (AC15, W1)

All values are for one player and a Great unless stated.

### Small notes: base and step

Every chart (per course) has a **base** (初項) and a **step** (公差). The score for a Great
depends on the current combo:

| Combo before the hit | Great score |
|---|---|
| 0-9 | base |
| 10-29 | base + step |
| 30-49 | base + 2 × step |
| 50-99 | base + 4 × step |
| 100+ | base + 8 × step |

The sum drops the ones digit. Example (W1, Red Rose Evangel, base 420, step 98): 420, 510,
610, 810, 1200.

Stock charts carry base and step in their note records (F: a scoring note's two 16-bit fields:
base, and step × 4). TJA charts give `SCOREINIT` / `SCOREDIFF`.

**Status: implemented** (`TaikoScore`, `FumenChartReader`). Charts without either value get an
estimate aiming at a ~1,000,000 ceiling with base = 4 × step (Waddamburo's choice, not a game rule;
W1 notes base is "about 4× step" for all songs since AC10).

### Good (可)

Half the Great score, ones digit dropped (W1, W2): Great 330 → Good 160.

**Status: wrong.** The engine rounds half up (330 → 170).

### Go-Go Time

Notes in Go-Go Time score ×1.2, ones digit dropped (W1): 330 → 390, Good 160 → 190.

**Status: wrong.** The engine rounds instead of dropping the digit (330 → 400).

### Big notes (特良 / 特可)

From AC15 KATSU-DON to Green, a big Great is **twice the Great score after the Go-Go factor**
(W1): 660, or 780 in Go-Go (390 × 2, not 330 × 2.4 = 790). A big Good is **the Great halved with
the ones digit dropped, then doubled**: 320, or 380 in Go-Go. This can be 10 below twice the Good.

**Status: implemented** as "the second hit adds the note's points again", which gives both
values once Good and Go-Go round correctly.

### Hand notes (手つなぎ音符)

Scored as big notes (AC9 onwards, W1).

**Status: implemented** (each player's hand note is a big note with its partner's hit as the
second one).

### 100-combo bonus

+10,000 points each time the combo reaches a multiple of 100 (W2, AC15; removed in AC16).

**Status: implemented.**

### Drum rolls

Small roll 100 per hit, big roll 200 per hit, ×1.2 in Go-Go (120 / 240) (W1).

**Status: implemented.**

### Balloons

`(hits − 1) × 300 + 5000`: every hit is 300 and the popping hit is 5000 instead (W1). Whether
it counts as Go-Go is decided by **where the balloon starts**, for every hit (W1, example
恋はみずいろ). In Go-Go: 360 and 6000. An unpopped balloon scores its hits × 300 only.

**Status: wrong on both points.** The engine adds 300 for the popping hit too, and applies
Go-Go per hit time.

### Kusudama

Scored like a balloon, with the pop worth 5000, or **1000 when popped late** (W1 table: 早い /
遅いくす玉割り, 5000 / 1000; ×1.2 in Go-Go). What counts as late is not stated.

Kusudama are replaced by balloons in these cases (W1):
- the 真打 option;
- AI battle;
- multiplayer where one chart has a kusudama the other lacks, or has it at a different time.

**Status: partial.** Kusudama score as balloons (always 5000 on pop). The late pop and the
replacement rules are missing.

### Ceiling

Not needed to play, but a useful check: the wiki lists each chart's all-Great ceiling. AC15 aims at
about 1,000,000 for Oni ★7 ("基本天井"). The 真打 formula below makes this exact.

### 真打 (Shinuchi) option (AC15, W1)

An optional scoring mode (and the fixed mode of the AI battle and dan dojo). The score never grows
with combo:
- Every Great is the chart's 真打 base.
- A Good is half of it, rounded **up** to ten.
- There is no 100-combo bonus and no Go-Go factor.
- Kusudama become balloons.
- Ceiling = `(max combo + big notes) × base + (balloon hits − balloons) × 300 + balloons × 5000`.

**Status: not implemented.** Where the stock charts keep the 真打 base is not known yet (the fumen
note's second 16-bit field is taken as step × 4).

## Soul gauge (W2, W3, F)

- **Scale:** 10,000 points internally, never below 0 or above 10,000. One segment is 200 points,
  so the gauge shows 50.
- **Clear line (ノルマ):** Easy 6,000, Normal and Hard 7,000, Oni 8,000 (30 / 35 / 40
  segments). Full (魂) is 10,000.
- **Gains:** a Great, a Good and a Bad each move it by a fixed amount **per chart**. These are set
  by hand. They roughly follow course, stars and max combo, with ±1 exceptions (W3).
- **Where the amounts come from:** stock charts store them in the fumen header (F). At byte 436
  there are five 32-bit integers: max (10000), clear line, Great gain, Good gain, Bad loss
  (negative). Then three 16.16 fixed-point ratios for the normal, professional and master
  branches. Example (F, a J-POP song): Easy 397 / 298 / −198, Normal 238 / 179 / −119,
  Hard 106 / 80 / −106 with ratios 1 / 0.878 / 0.828, Oni 69 / 34 / −110.
- **Branches:** each branch scales the three amounts by `normal combo ÷ branch combo`, keeping
  fractions (W3).
- **Dan dojo:** roughly a third of each song's amounts (W3).

| Course | Good vs Great | Bad vs Great (W3, approximate) |
|---|---|---|
| Easy | 0.75 | −0.5 |
| Normal ★1-3 / ★4 / ★5-7 | 0.75 | −0.5 / −0.75 / −1 |
| Hard ★1-2 / ★3 / ★4 / ★5-8 | 0.75 | −0.75 / −1 / ≈−1.17 / −1.25 |
| Oni ★1-7 / ★8-10 | 0.5 | −1.6 / −2 |

**Status: partial.** Scale, segments and clear lines are implemented (`TaikoSoulGauge`). The
amounts come from tja2fumen's table (T) for every chart, stock included. For stock charts they
should be read from the header (exact, including the hand-set exceptions); the table stays for TJA.
Branch ratios are unused (only the normal branch is played).

## Combo presentation (W2)

- **Note faces (AC15):** the notes' mouths open and close on eighth notes from 50 combo, on
  sixteenths from 150, and their eyes turn angry from 300. AC16 made this per course.
- **Combo callouts:** a voice at 50 combo and at every 100 (AC4 onwards). A speech bubble every
  100 combo on the new cabinet (up to Green).
- **Numbers:** turn red from 100 combo on every course (AC15). Above 999 the digits get smaller
  (AC15).

**Status:** callouts implemented. The note-face tiers are not in the public engine yet.

## Not covered here

- Waiwai (party mode): its voltage and results are not ranked, and Waddamburo keeps them approximate.
- Branch conditions (only the normal branch is played).
- Dan dojo and AI battle rules.
