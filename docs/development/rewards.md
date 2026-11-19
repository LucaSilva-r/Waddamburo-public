# Rewards: Don Medals, reward shop, Don Points, titles

Plan for Green's reward systems in Waddamburo (Discord suggestion "Events/Gouhoubi Shop").
Status: design, nothing built. Delete this file when done (git history keeps it).

## What Green has

Green's ごほうびタイム **is `reward_shop`**. Traced (`research/traces/session-rewards-1.md`, a 2-song credit
then two card entries, 2026-11-19): last result → `reward_shop` in medal mode → `shop_gameover`, and at the next
card entry → `reward_shop` in shopping mode → song select. Green never showed `total_result` and made no Don
Point or present calls.

| System | Earned by | Spent / given | Data we have | Movie |
|---|---|---|---|---|
| **Don Medals** (どんメダル) | fixed per credit: 100, plus +50 for the first credit of the day, +100 for the first of the week, +300 for the first of the month. Medals carry over between seasons | **reward shop**: seasonal goods (head, body, make, suit, acce, tone, song) | the 4 Green seasons (Spring/Summer/Fall/Winter 2019-20, 28 goods) in TaikOnline `storage/app/game-data/green/green_item_shop_data.json` | `reward_shop/reward_shop.lm` |
| **Condition titles** | play conditions checked by the game at the end of a credit (≈430: clear all songs with an option, a genre or a tag; FC specific songs; first play in a month; …). Another ≈190 were given by Donder Hiroba (the website) | the title, **silently**: no screen. The game sends it in `playresult` (`get_title_no`) and it shows up in the title list | id = position in the game's list (106 = first play ever, 140 = clear all with Abekobe); `rewardtitlefiltering.xml` lists the 309 ids this cabinet supports. The conditions are in the game code, not in any data file. | none |
| ~~Don Points~~ | `total_result` + `present.xml` (10 presents, 1000…30000) ship with Green, but the game never loads `present.xml` and the traced flow never showed `total_result`. Blue's presents screen; Green dropped it | | | |

Verified (session-rewards-2): a credit cleared with Abekobe gave title 140 on the card, and Green showed nothing
for it. That matches players remembering no presents screen in Green (Blue had one).

`rewardgasha` (a reward card printer gacha) is out of scope.

### reward_shop protocol

Open, in this order (same in both modes):
- `Settings(mode, season)`: mode 0 = medal (end of credit, then game over), 1 = shopping (after card entry).
  Season 4 = Winter.
- `Telop(text)`.
- For each goods slot: `PushGoods(no, type, id, price)`, then `SetAcquiredGoods(no, ownedP1, ownedP2)`. Song
  goods also get `SetGenreGoods(genre)` and `SetStarGoods(easy, normal, hard, oni)`. Types: 5 head, 4 body,
  1 song (others from the movie's `TYPE_*`: make, suit, acce, tone).
- `SetPlayerEnter(side, a, b)` ×2. P1 with a card: `(0, true, true)`. Empty side: `(1, false, false)`.
- `SetPlayerBonus(daily, weekly, monthly)`: medal mode only.
- `SetPlayerMedal(balance, 7600, 0, gained)`. Gained = 550 with all bonuses, 0 in shopping mode. 7600 is
  constant, probably the cap (`MaxMedalPlaying`, `FullMedalMsg`).

Shopping, movie → host:
- `ShopStart`, `StartTimer`, then polls `GetSecTimer`/`IsTimeup` (100 s).
- Cursor moves: `UpdateFillrect(slot, type, id)` for the costume icons. On a costume: `UpdatePreview(side,
  type, id)` → 1, and the host answers `FinishCostumeLoad(side)` when the Don is dressed.
- Buy: `PauseTimer`, then `SendShopServer(side, goodsNo)`. The host sends 2 `itempurchase` requests and calls
  `UpdateMedal(balance, spentThisVisit)` after each, then `ResponseServer(0)` (0 = ok). Then `SetMotion(0,
  48, 61)` (Don costume-change motion) and `ResumeTimer`. The balance argument stays the same; the movie shows
  balance − spent.
- Leave: `NotifyEndSequence`.

Sounds: `VO_GOHOBI`, `SE_GOHOBI` banks plus `SE_COM`; medal mode ends with `SE_GAMEOVER`.

TaikOnline check: `SetPlayerBonus` was all true on 2026-11-08 and again on 11-19 (same month, so monthly
should have been false). The server probably always sends the bonus flags.

## Decisions (user, 2026-11-19)

- **Home credit = every 3 songs** (Green's songs per credit). Each block of 3 pays one credit's medals. The
  first block of the day/week/month gets that bonus. Arcade uses the real credit.
- **Seasons follow the calendar.** Mar-May Spring (season 1), Jun-Aug Summer (2), Sep-Nov Fall (3), Dec-Feb
  Winter (4). Missed goods come back the next year.
- **Lock mode is per profile.** A TaikOnline profile option has two values: "Unlock everything" (default, as
  today) or "Only what I earned". Tracking always runs, so switching shows what the player has already earned.
- **Guests and offline-only players get no rewards** and keep everything unlocked. TaikOnline is the only
  authority, as it is for dan badges and AI battle progress.
- Arcade mode copies Green 1:1. Home mode shows a notification ("new rewards"); the real animations play from
  the song select pause menu (lawnred, Discord 2026-11-16).
- Skins without the movies (the simple skin) disable the screens. Tracking continues.

## Design

### Authority: TaikOnline

The client never says "I earned 100 medals". It uploads plays, as it already does, and the server derives
everything from them.

- **Credit grouping.** Each upload gets an optional `credit_id`. Arcade sends the real credit. Home sends none,
  and the server cuts the player's plays into blocks of 3, in order.
- **Medals.** On each completed credit or block, the server adds 100 plus any bonus. The bonus date is the
  play's own local date, which the client sends. Faking it gains at most 450 medals a month; not worth more.
- **Condition titles.** A data file (title id → condition) with a small fixed set of condition kinds (clear
  all with option/genre/tag, FC song N, first play in month, hits in a credit, …). The server evaluates it per
  credit on rescored plays (the `--rescore` path already exists). The conditions come from the game's reward
  names plus wikiwiki.
- **Storage.** Reuse the Green tables with `game_version = 'waddamburo'`, like the Waddamburo
  `PlayerCosmetic` row. `PlayerShopSeasonState` keeps the medal balance (one wallet: sum across seasons),
  `PlayerShopItem` keeps purchases, `PlayerCosmetic.unlocked_*` keeps owned items. Add an `earned_only` flag
  and a `pending_rewards` list (new titles to announce at home).
- **Purchase API.** Add a Waddamburo endpoint that calls the existing purchase code: lock the row, check the
  balance, insert the item, merge it into `unlocked_*`.

### Client

- **Gating.** With `earned_only` on, the costume picker, tones, titles and the stock songs sold in the shop
  show only what the player owns. Custom libraries (TJA, osu, Wii) are never locked.
- **Arcade flow (Green 1:1).** Last result → `reward_shop` medal mode (`Settings(0, season)`) →
  `shop_gameover`. Next card entry → `reward_shop` shopping mode (`Settings(1, season)`) → song select.
  Titles: silent, as in Green. Feed the movies Green's own data and call order; never patch the movies.
- **Home flow.** After each 3-song block, a toast ("+550 Don Medals", "New title: …"). The song select pause
  menu gets "Reward shop", which opens `reward_shop` in shopping mode and goes back to song select.
- **Skins.** Show the shop only when the skin provides the `reward_shop` slot.
- **Shop tutorial.** `reward_shop` has a first-visit tutorial, shown when the card's `itemshop_tutorial_flg`
  is 0 (seen in the recomp with a card that never used the shop; TaikOnline already stores the flag from
  `playresult`). Waddamburo shows it on a profile's first shop visit and saves the flag the same way.
- **Website.** The TaikOnline profile shows medals and points (BoardController already has `don_medals`).
  A web shop like Donder Hiroba's can come later.

## Phases

0. **Research. Done 2026-11-19:** `research/traces/session-rewards-{1,2}.md` (shop in both modes, 6
   purchases; an Abekobe credit earning title 140 silently).
1. **TaikOnline fixes** (they also affect real cabinets). Fixed 2026-11-19 (TaikOnline `staging`, uncommitted):
   the release-song bitfield was off by one (song n at bit n−1; Green reads bit n), so bought songs showed as
   locked and unbought ones as owned, on every non-legacy version (Red, White, Yellow, Blue, Green); medals
   now carry over (wallet = sum of season rows) and past-season purchases stay owned; `players.total_*_donmedal`
   is the lifetime total (backfill migration). Bonus flags: the cabinet gives every bonus
   when the last play is outside the shop season's [start, end), and TaikOnline sent Green's 2019-20 dates;
   the active season is now dated into the current cycle (`TAIKO_GREEN_ACTIVE_SHOP_SEASON_ID=0` follows the
   calendar). Rule: bonus = last play before the start of today/week/month, or outside the season.
2. **Server ledger.** Credit grouping, medals, calendar season, purchase API, `earned_only`, and the earned
   list in the profile payload. Add PHPUnit tests.
3. **Client gating.** Earned-only filtering in the pickers and the song list.
4. **Arcade screens.** `reward_shop` host binding, both modes. Prove it with flow-smoke and `--play`.
5. **Home screens.** Toast and the pause menu entry.
6. **Condition titles.** Condition data file (from the reward names + wiki), then server evaluation.
7. **Later.** Custom unlockables (own costumes and content) once the skin system can supply them.

## Open questions

- `SetPlayerMedal(balance, 7600, 0, gained)`: is 7600 the cap? What is the third argument?
- Exact conditions behind each title name (difficulty? song count?). The game's checker code is the source;
  not yet located.
- Do condition titles depend on the song select timer? The "Awakener" titles stop the timer at an odd or
  even number. That needs the timer value in the play upload.
