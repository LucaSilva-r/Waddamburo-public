# Gameplay skins

A gameplay skin is one of the game's themed `enso_<name>` archives in
`USRDIR/data/lumendata/packed`: its own background, dancers, Don-chan backdrop, roll
character and so on. The game picks one per stock song. Custom charts get one in this order:

0. The player's override (Settings > Gameplay skin, `gameplay_skin` in config.cfg): `original` plays
   every song with the regular gameplay, a skin's name (from the table below) plays every song with
   that skin. `auto`, the default, or a name the data lacks, follows the order below. It applies to
   stock songs too; Waiwai keeps its own scene.
1. The chart's `SCENEPRESET:` header (OpenTaiko's), e.g. `SCENEPRESET:IMAS`. Several names may
   be listed with commas; the first one that matches wins. A name matches the archive with or
   without its `enso_` prefix, ignoring case and punctuation: `IMAS`, `imas`, `Imas` and
   `enso_imas` are the same, and `IMAS_SIDEM` is `enso_imasSideM`.
2. The skin of the stock song with the same title.
3. Vocaloid folders take `miku`.
4. Otherwise, and for `original` or any name that matches nothing usable, the regular gameplay
   (the random mix of the original parts).

Parts a skin does not have are left out. A skin is one fixed set: its movies take no
character or variant choice from the game (their only inputs are Go-Go, hits, the dancer count
and the roll counter), so e.g. Miku and IA are separate skins, not variants of one.

## Available skins (Green)

| Name | Theme | Notes |
| --- | --- | --- |
| `A3` | A3! | Shared Don-chan backdrop for both players; no Go-Go effect |
| `animal` | Great! Animal Kaiser | |
| `buttoburst` (also `butto`) | Kurae! Butto Burst | Background only during Go-Go |
| `dq10` | Dragon Quest X | Two dancer sets; the first is used |
| `funassyi` | Funassyi | |
| `gb` | ? | |
| `gmt` | GMT remixes (Namco game music) | |
| `gumi` | GUMI (Vocaloid) | |
| `i7id7` | IDOLiSH7 | No Go-Go effect |
| `i7natsu` | IDOLiSH7 | No Go-Go effect |
| `i7rev` | IDOLiSH7 | No Go-Go effect |
| `i7tri` | IDOLiSH7 | No Go-Go effect |
| `ia` | IA (Vocaloid) | Shared Don-chan backdrop |
| `imas` | THE IDOLM@STER | |
| `imasCG` | THE IDOLM@STER Cinderella Girls | |
| `imasML` | THE IDOLM@STER Million Live! | |
| `imasSideM` | THE IDOLM@STER SideM | |
| `kobayashi` | Sachiko Kobayashi | |
| `kumamon` | Kumamon | |
| `lovelive` | Love Live! | Background only during Go-Go |
| `mario` | Super Mario Bros. | |
| `mh3G` | Monster Hunter 3G | Unusable in Green: its Don-chan backdrops reference a missing texture, so the regular gameplay is used |
| `mh3rd` | Monster Hunter Portable 3rd | |
| `miku` | Hatsune Miku (Vocaloid) | |
| `momoclo` | Momoiro Clover Z | |
| `oshiri` | ? | |
| `pzd` | Puzzle & Dragons | Shared Don-chan backdrop |
| `toho` | Touhou Project | |
| `tonkatsudj` | Tonkatsu DJ Agetarou | Only the background, small characters, Don-chan backdrop (1P) and roll character |
| `touken` | Touken Ranbu | No Go-Go effect |
| `tt` | TT / Candy Pop | |
| `ymck` | YMCK | |
| `yokai` | Yo-kai Watch | |
| `yokai_hatsukoi` | Yo-kai Watch | |
| `yokai_matsuri` | Yo-kai Watch | |
| `yokai_tokoroten` | Yo-kai Watch | |
| `yokai_yougota` | Yo-kai Watch | |

Never used as a skin (they are other modes' scenes, not themes): `dojo` (dan courses),
`tokkun` (training), `waiwai` and `waiwai_effect` (party mode), `result`, `system`, `gudetama`
(empty), and `original` (the regular gameplay's own parts). The AI battle and RPG modes live
outside `enso_` and are never considered.

Other releases ship other sets; the resolver only offers archives present in the user's data
that have the standard parts (a Don-chan backdrop) and whose movies all load.
