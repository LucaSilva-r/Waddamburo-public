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
| O1 | [Official Yellow Ver. update announcement](https://taiko-ch.net/blog/?p=1784) (2017-08-03), introducing いっしょにワイワイ演奏 | First-party description of the AC15 mode retained through Green |
| F | Fumen chart headers and notes of user-supplied charts (see below) | Asset-derived observation |
| T | tja2fumen (MIT, vendored table in `Data/`) | Public implementation (for TJA charts) |
| R | Green controlled-play observations (aggregate findings summarized below) | Local behavioral observation; raw traces stay private |

The wiki's facts are community measurements (autoplay captures, full-Great videos, arithmetic on
displayed scores). They are the behaviour to match; where it says a value is approximate, so is
ours.

## Judgement

### Windows (W2)

Offsets from the note's time, in ms (they are frame multiples at 59.94 Hz: 1.5, 2.5, 4.5, 6.5,
7.5 frames).

| Course | 良 Great | 可 Good | 不可 Bad |
|---|---|---|---|
| Oni (and ura), Hard | ±25.025 | ±75.075 | ±108.442 |
| Normal, Easy | ±41.708 | ±108.442 | ±125.125 |
| Easy with parent-and-child support (パパママサポート) | ±41.708 | ±125.125 | ±125.125 |

A hit between the Good and Bad windows is a Bad. A note never hit is a Bad at the end of its
window. Hitting the wrong colour shows no judgement but counts as a Bad in the results (W2).

**Status: wrong.** The engine uses 35 / 80 / 95 ms for every course
(`TaikoGameplayPresentation`), including Waiwai's two lanes. In particular, Hard needs the Oni
profile, not the Normal one. These are placeholders that predate this research. The wiki gives
nominal symmetric bounds; the exact Green frame-boundary inclusion and calibration offset have not
been measured locally.

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

| Combo displayed by this hit | Great score |
|---|---|
| 1-9 | base |
| 10-29 | base + step |
| 30-49 | base + 2 × step |
| 50-99 | base + 4 × step |
| 100+ | base + 8 × step |

The sum drops the ones digit. Example (W1, Red Rose Evangel, base 420, step 98): 420, 510,
610, 810, 1200.

Stock charts carry base and step in their note records (F: a scoring note's two 16-bit fields:
base, and step × 4). TJA charts give `SCOREINIT` / `SCOREDIFF`.

**Status: partial** (`TaikoScore`, `FumenChartReader`; tier boundaries need verification below).
Charts without either value get an
estimate aiming at a ~1,000,000 ceiling with base = 4 × step (Waddamburo's choice, not a game rule;
W1 notes base is "about 4× step" for all songs since AC10).

**Boundary mismatch to verify:** W1 labels the tiers by the combo *reached* (1–9, 10–29, etc.),
while `TaikoScore` chooses the tier from the combo *before* the hit. On that reading, the 10th,
30th, 50th and 100th hits earn the previous tier in Waddamburo. A controlled Green score trace at
these four hits should settle whether the wiki's labels describe the awarded hit or the state
before it; do not silently shift the thresholds from this wording alone.

### Good (可)

Half the Great score, ones digit dropped (W1, W2): Great 330 → Good 160.

**Status: implemented.** `TaikoScore` truncates the half award to tens (330 → 160).

### Go-Go Time

Notes in Go-Go Time score ×1.2, ones digit dropped (W1): 330 → 390, Good 160 → 190.

**Status: implemented for the multiplier.** `TaikoScore` truncates after applying ×1.2
(330 → 390).
It also tests Go-Go at the input timestamp; a note struck across a Go-Go boundary may therefore
receive a different factor than a note whose authored position is inside that section. The latter
boundary rule needs a controlled Green check for regular notes. Balloons have an explicit rule below.

### Big notes (特良 / 特可)

From AC15 KATSU-DON to Green, a big Great is **twice the Great score after the Go-Go factor**
(W1): 660, or 780 in Go-Go (390 × 2, not 330 × 2.4 = 790). A big Good is **the Great halved with
the ones digit dropped, then doubled**: 320, or 380 in Go-Go. This can be 10 below twice the Good.

**Status: implemented for score arithmetic.** The second hit adds the first hit's truncated points
again. The keyboard's 30 ms second-hit threshold remains a product approximation of the arcade's
strength-based big-note rule.

### Hand notes (手つなぎ音符)

Scored as big notes (AC9 onwards, W1).

**Status: partial.** Each player's hand note can receive a big-note bonus when the partner hits.
In local Green observations, hits 12–15 ms apart stayed at normal value while a 1 ms separation
earned the bonus (R). `TaikoHandNoteLink` currently accepts partners up to 35 ms apart, so it
awards bonuses that Green did not in those trials. The exact cutoff still needs measurement.

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

**Status: implemented.** The popping hit replaces the 300-point hit with 5000, and every balloon
hit uses the Go-Go state at the balloon's start.

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

## Waiwai cooperative play (AC15 Yellow through Green; O1, R)

Waiwai changes the **result being pursued**. Bandai Namco describes two players filling one gauge
on specially arranged charts with solo and together sections. The result reports a duet percentage.
It does **not** award or record a score,
crown or certain titles (O1). Do not compare Waiwai's hidden lane totals to a normal chart's
all-Great ceiling or save them as best scores. Score values are still sent to the lane displays in
Green (R); a distinct Waiwai score formula has not been established.

### Shared voltage and clear

Green's observed gameplay feeds a shared voltage on a 0–10,000 scale to one 50-segment gauge.
The stage is given thresholds 3,000 (level change) and 7,000 (clear). A clear effect fires when
voltage crosses 7,000. One traced Hard song showed alternating gains of 36/37 for successful notes
and losses of 64 for missed notes. Those are **observations for that chart**, not a general formula:
the rate's relation to course, note count, solo/together sections and adaptive support remains
unresolved (R). Rolls, balloons, kusudama, synchronized hits and rare notes need separate controlled
measurements before assigning them a voltage rule.

**Status: approximate.** `WaiwaiStage` uses +36.5 for every Great or Good and −64 for every Bad,
regardless of song, course or note type. It clamps to 0–10,000, displays voltage / 200 segments,
and uses 7,000 for clear. It does not model the observed possibility of adaptive note support (R).
The gameplay presentation also runs `TaikoScore` per lane and sends a running value to each lane board, matching
traced score callbacks, but Waiwai's result screen has no score. The gameplay value should remain
ephemeral and must not be treated as a normal play record.

### Judgement, synchronization and results

Both players' lanes currently use the same per-course-independent 35 / 80 / 95 ms windows noted
above. There is no evidence of a separate Waiwai timing profile, so the AC15 Green course windows
are the working target for ordinary and synchronized notes. This is an inference, pending a
controlled Waiwai boundary trace.

Together sections show synchronized notes on both lanes; solo sections emphasize one player.
Green reports a duet percentage (`SetDuetPercent`), shared gauge segments and optional rare-note
hits on its Waiwai result screen (R). The percentage's denominator, timing tolerance and handling
of one-sided hits have **not** been established. A missed-note run reported 82%; a clean run
reported 99%, so even apparently clean play cannot justify an exact 100% rule from the available
traces. The message category thresholds also remain unknown.

**Status: approximate.** `WaiwaiStage` computes the percentage as synchronized note times at
which both lanes scored a non-Bad judgement divided by synchronized note times judged by both.
It ignores the two hits' timing difference and any grade distinction. `WaiwaiResultHostBinding`
guesses the message categories from gauge segments (20, 35, 50). These are presentation estimates,
not verified Green formulas. The result binding correctly supplies one gauge, duet percentage and
rare-note flags; no ranked score or crown is shown.

### Research needed to close the Green gaps

1. Replay synthetic or user-driven charts with a single input stepped across each course window,
   including Hard, late misses and wrong-colour hits; record the Green result, not only the movie.
2. Measure score deltas at hits 9/10, 29/30, 49/50 and 99/100, plus Great/Good and Go-Go boundary
   examples. This resolves the tier-label ambiguity and rounding independently.
3. In Waiwai, vary only one factor per run: a Great versus Good, one missed note, a roll hit, a
   balloon pop, solo versus together, synchronized hit offset, and a rare-note hit. Record voltage
   after each event and the final duet percentage. Repeat on another course and chart before
   generalizing the voltage rate.

## Not covered here

- Branch conditions (only the normal branch is played).
- Dan dojo and AI battle rules.
