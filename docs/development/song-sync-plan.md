# Song sync plan

Any Waddamburo (desktop or phone) sends songs to any other over the local network: a player brings their library to
their phone, sets a phone up from their PC, or pushes a custom song to a friend's game. Both sides ask for it, the
sender chooses the songs with Song Select's search, and only what the receiver lacks travels.

Replaces the browser upload page (removed) and the folder-by-folder release copy (`PhoneSync`'s arcade/Wii/Wii U
plan, kept only until step 1 lands).

## Rules

- **Both sides opt in.** The receiver opens *Receive*: it listens (`PhoneReceiver`, UDP discovery + HTTP) and shows a
  code; closing it stops listening. The sender opens *Send*, picks the device from the discovered list, enters the code
  and a search. Before anything is written, the receiver is asked: "<device> wants to send N songs (X GB): Accept?".
  Ten wrong codes close the receiver until it is reopened.
- **The search picks the songs** (`SongBrowse.Matches`: `source=`, `category=` (new), stars, artist, words...). The
  sender sees the count and size before sending.
- **You receive exactly the songs the search matched.** A chart shared with another song is a storage detail: sending
  `source=wiiu` brings Wii U songs only, never Wii ones that share a chart.
- **A song keeps its origin.** It shows on the receiver in its own source, release and category, as the sender has it
  (Wii U › its category; Nijiiro › Anime; TJA › its folder). The search never creates folders.
- **Sync only adds.** Songs a later search doesn't match stay. Removing received songs is a separate action (later).
- **Green's songs are songs like any other.** Green's game data (movies, textures, sounds) still travels as raw files
  for a device's first setup, unchanged. A Green song the receiver's own Green already has with the same chart hashes
  is skipped.

## The store (receiver)

Content-addressed: every file once, named by its SHA-256.

- `blobs/<sha256>`: canonical charts (`ChartHash.Serialize`, the bytes chart hashes are made from: scores, replays,
  TaikOnline and modcharts keep matching), their drawing data (`ChartDrawing.Serialize`), music files as the sender's
  game plays them (Nijiiro's converted on the sender).
- `songs.db` (SQLite): a row per song **as one source lists it**: source kind, release, category (name, genre, order),
  song key (`SongKey`, unchanged, so favourites and scores follow), titles, artist, subtitle, preview, BPM, length, music
  blob and its format; per course: level, difficulty name, chart blob, drawing blob, duet blobs.

Two sources with the same chart are two rows pointing at one blob. A row ties its charts to its own music: storage is
shared, timing never is. Charts are stored in audio time (the readers apply authored offsets before canonicalising),
so a row is in sync by construction; step 1 proves it by comparing every received chart's notes and timing with the
sender's.

A received-songs catalog provider reads the rows and serves the blobs (`ICatalogAssetResolver`,
`IPlayableChartProvider` via `ChartHash.Deserialize` + `ChartDrawing.Apply`). Its songs join their source's library
(a received Wii U song is in the WII U library), received rows after local ones for the same key.

## Protocol

On the existing receiver (code in every path):

1. `offer`: the sender posts the rows (JSON, with blob hashes and sizes). The receiver asks its player, then answers
   the blob hashes it lacks (or a refusal).
2. `blob/<sha256>` PUT, batched like today's files; the receiver checks each hash before keeping it.
3. `commit`: the rows are written once their blobs are all there. An interrupted send resumes: the next offer finds
   the blobs already kept.

## Steps

1. **Store + reader + local proof.** *Done 2026-11-17.* `Sync/SongStore`, `StoredSongsProvider`, `OverlayCatalogProvider`
   (received songs join their source's library; it owns their assets as `received|...` and passes the library's modchart
   and upload-privacy answers through), `SongExport`, receiver `offer`/`batch?target=blobs`/`commit`, offer `Format`
   (another version is refused: it would drop fields quietly), `category=` search filter, rows carry the sender's
   `donotupload` as `NoUpload`. Proof on one PC, every non-Nijiiro song (11,618 songs, 35,192 charts, 38 GB in 60,500
   blobs, 6.5 min): an independent check built from the readers found 0 mismatches (metadata, category, canonical
   chart, drawing, length, shin-uchi base, duets, music bytes); a second send moved 0 bytes; the global catalog loads them
   alone and over local libraries without errors. Rows are JSON (SyncJson, generated: trimmed builds have no reflection).
2. **Screens.** Settings › Send to device (devices, code, search with count/size, progress) and Settings › Receive
   (code, accept prompt, progress), desktop and phone. The receiver listens only while Receive (or the phone's setup)
   is open.
3. **Nijiiro.** Music converted on the sender (vgmstream) to a format every platform decodes.
4. **Green setup** through the same screens: game data as raw files, then its songs as rows.

## Open points

- Music formats: the receiver decodes what the sender's game decodes (nub, nus3bank, ogg/mp3/wav). Check each platform's
  decoder covers every source before step 3 settles Nijiiro's target format.
- Big libraries: SQLite rows for ~15,000 songs, catalog load time on a phone.
- Wii U games whose update and DLC are in Cemu's `mlc01` layout (folder release copy didn't cover them; rows do, since
  they go through the reader).
