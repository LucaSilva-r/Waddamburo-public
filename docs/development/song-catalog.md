# Song catalog providers

Waddamburo builds one immutable global catalog from independent content sources.
The shared `Waddamburo.Catalog` assembly defines the provider contracts and an
optional filesystem/SQLite metadata cache. It has no Lumen or platform dependency. Stock, Nijiiro, custom TJA, and osu!lazer integrations all
implement the same `ISongCatalogProvider` contract; Song Select consumes the
published catalog rather than branching on a source type.

## Provider contract

A provider owns discovery and source-specific parsing. One scan returns a
`SongCatalogContribution` containing:

- songs and their source-qualified stable keys;
- charts, titles, artist, preview metadata, and opaque asset keys;
- ordered categories whose members refer to songs from that contribution; and
- structured warnings or errors that do not invalidate healthy providers.

The contribution combines songs and categories deliberately. Publishing them as
one validated value prevents Song Select from observing a category whose songs
belong to a different scan or have not been published yet. Multiple provider
instances of one source kind are allowed, but provider IDs and every stable key
must be unique.

Asset references are `CatalogAssetKey` values owned by a provider. They are not
guest paths and are never sent to Lumen. Providers that can open their native
content implement `ICatalogAssetResolver`; callers route a key only to its owning
provider after fixing a selection to a catalog revision.

## Global publication

`GlobalSongCatalog.RefreshAsync` starts all configured scans, reports provider
progress, isolates ordinary provider failures, validates identities and key
collisions, and then publishes one new `SongCatalogSnapshot`. Consumers continue
to read the previous snapshot until that complete replacement is ready. A
cancelled refresh publishes nothing.

Provider order and collision outcomes are deterministic: contributions are merged
by ordinal provider ID and categories are sorted by authored order followed by
their source-qualified key. A failed or rejected provider has a status and a
diagnostic in the snapshot; contributions from healthy providers remain usable.

## Current adapters

The osu!lazer adapter converts the existing read-only Realm snapshot into this
contract. It currently publishes native taiko beatmap sets, charts, audio asset
identities, and a source category without exposing Realm or filesystem details.

The custom TJA adapter recursively discovers bounded `.tja` files, reads
UTF-8/UTF-16/Shift-JIS metadata, publishes supported courses and folder/genre
categories, and resolves relative chart/audio assets beneath its configured root.
Its details and current limits are described in
[custom-tja.md](custom-tja.md).

The stock adapter (`Waddamburo.Providers.Stock`) reads the game's own songs from
the user's `data` folder (the one holding `lumendata/packed`):

- Metadata: `musicinfo.xml` (boost XML; `Data` entries with `musicid`,
  `musicname`, `genrename`). Installs carry one per revision (`data/musicinfo.xml`,
  `data/config/<revision>/musicinfo.xml`); the one listing the most installed charts
  is used. Song order is the file's order; categories are its genres in order of first
  appearance, named J-POP, Anime, Vocaloid, Kids, Variety, Classical, Game Music,
  Namco Original, Medley.
- Charts: `fumen/<id>/solo/<id>_<e|n|h|m|x>.bin` (x = Ura). A song is listed when
  at least one exists. Charts are read as the normal branch; note times are audio
  times, and a chart whose first event precedes audio zero is shifted through
  `AuthoredOffset`.
- Music: `sound/bgm/nub/SONG_<ID>.nub` (ATRAC3plus RIFF inside, decoded by the
  native media layer). Preview start: the `.nsh` of the same name, big-endian: magic
  `0x00020100`, table offset at `0x18`, its first word points to the `at3` stream
  entry, whose 20-byte user data (length at `+0x90`) ends with the cue in
  milliseconds at `+0xB0`.

- Stars: `fumen/tuning.bin`, big-endian: record count, 0x90C-byte records, then a
  string pool addressed by offsets. A record begins with the offsets of its id, bank
  name and title; 0x80-byte course entries follow from `+0x0C` (name offset, stars),
  named `<record>1p_<e|n|h|m|x>`. Ura is the `ex_<id>` record's `ex_<id>1p_m` entry.
  The rating is published on the chart descriptor and on the playable chart.

Not yet read: duet charts, branches other than normal, English titles.

The Nijiiro adapter remains to be implemented against the same interface.
Source-specific parsing and asset resolution stay in their provider assemblies
rather than entering Song Select or the Lumen runtime.

Public tests use synthetic contributions and snapshots. User libraries, database
files, commercial assets, paths, hashes, and captured game output are not test
fixtures and must not be committed.

## Persistent metadata cache

Stock, Nijiiro and custom TJA startup scans use a separate `catalog-metadata.db`
in the configured cache directory, beside the audio cache. This is disposable
acceleration data; scores and replays remain in their existing storage.

Each library scan inventories relative paths, sizes and UTC modification times
once. File existence checks use that inventory. Parsed XML and tuning data,
branch flags, preview positions, and TJA metadata/content hashes
are reused only when the source file's size and timestamp still match. New and
changed files are parsed; deleted files disappear from the next catalog and their
cache rows are pruned. TJA folder definitions are checked independently.
Game installations (stock, Nijiiro) follow links and pass over unreadable folders,
as plain file probes would; a custom TJA library leaves links out. An installation's
inventory covers only the folders its catalog reads (charts, music, song tables),
not its models or movies. Reading
an individual TJA file in the editor does not inventory the whole library.

Entries are scoped by absolute library root and parser version. Nijiiro also
includes a hash of the configured decoding keys in that scope; keys themselves
are never stored. Parser changes affecting cached values must increment the
provider's cache version. Serialization uses generated JSON contexts for AOT.

Rows are read together per library and changes are saved in a short transaction.
An unchanged warm scan does not rewrite its cache rows. Files changed during
parsing and reads that produce diagnostics are not saved. Cancellation skips
saving; corrupt database storage is rebuilt, and an unavailable/unwritable cache
falls back to source parsing. Gameplay still opens current chart/audio files;
the inventory is used only for the startup scan.

Changes which preserve both file size and modification time cannot be detected
by this check. Removing `catalog-metadata.db` forces a fresh parse on the next
launch without deleting scores. The first scan still reads source metadata;
subsequent scans still enumerate every configured library, so disk speed and
library size continue to affect startup. Merged-library progress counts every
installation, and the loading screen reserves 100% until catalog publication.
