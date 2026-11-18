# Song sync plan

Any Waddamburo (desktop or phone) mirrors whole song libraries from another over the local network: a player keeps
their phone in step with their PC's libraries, sets a phone up from their PC, or follows a friend's custom TJA library.
The receiver switches libraries on or off; each sync makes its copy of every switched-on library identical to the
sender's, as the user's own files in their library layout.

Replaces the browser upload page (removed) and the folder-by-folder release copy.

## Rules (decided 2026-11-18)

- **Pair once.** The receiver (a phone) opens *Settings › Devices › Connect to a PC*: it shows a 4-digit code. The PC
  opens *Settings › Devices › Add a device*, picks it and types the code; the PC sends a random token, both keep it
  (`<home>/sync/paired.txt`). Ten wrong codes close the pairing page until it is reopened. Either side can unpair.
- **The PC serves while Waddamburo runs** (once it has a paired device): read-only, token-checked. The phone does
  everything from its own *Devices* page; nothing to open on the PC.
- **The receiver picks whole libraries.** It sees the sender's libraries (name, size) and toggles each on or off. The
  choice is remembered per sender device, so the next session just syncs. No search, no song picking.
- **True mirror.** A sync makes each switched-on library on the receiver identical to the sender's: new and changed
  files come, files the sender no longer has are removed. Switching a library off removes it after a prompt
  ("Remove WII U (12 GB) from this device?").
- **Raw files, same layout.** No Waddamburo-specific song format: the receiver's normal providers read the files, so
  a song shows in its own source, release and category exactly as on the sender.
- **Received libraries are the sender's.** They live in `<app data>/received/<sender>/<library>/` and register as
  libraries automatically. The receiver's game never writes inside them (its own modcharts go to
  `waddamburo/modcharts/<chart hash>.wddmod`, scores/favourites are keyed by song, not by path), so a mirror never
  deletes the receiver's own work.
- **No duplicates.** A library the receiver already has itself (e.g. its own Green) shows as "already here" and can't
  be switched on.
- **Green** is a library like the others; its game data (movies, textures, sounds) is part of it, so a first setup is
  "switch Green on".

(Step 1 first built a content-addressed store: `SongStore` + `StoredSongsProvider` + `OverlayCatalogProvider` +
SyncJson rows, songs picked with a search. It worked, but it was a parallel provider system, saved almost nothing
(charts are KB; music, the bulk, is rarely shared) and made the phone read songs differently from the desktop. It is
being removed.)

## Safety

Deleting is the dangerous half of a mirror.

- **Copy first, delete last.** Removals run only after every new/changed file of that library has landed: an
  interrupted sync never leaves fewer songs than before.
- **Missing root ≠ empty library.** The sender doesn't list a library whose root is missing or empty (unplugged drive,
  unmounted share). A library that disappears from the list is left alone, never removed.
- **Big removals ask.** If a sync would remove more than half of a library's files, the receiver asks before deleting.
- **Deletes stay inside** `received/<sender>/<library>/`: paths are checked to resolve under it.

## Protocol (`Sync/SyncWire.cs`)

Two services, each a UDP discovery port and a TCP port of the same number (no HTTP: `HttpListener` needs an URL
reservation on Windows):

- **Pairing, 8380** (the phone, only while its pairing page is open): probe `WADDAMBURO-PAIR?` → `WADDAMBURO-PAIR <id>
  <name>`; request `code pair <pc id> <pc name> <token>` → `<phone id> <phone name>`.
- **Sync, 8381** (the PC, while the game runs and a device is paired to pull): probe `WADDAMBURO-SYNC?` →
  `WADDAMBURO-SYNC <id> <name>`; requests carry the token: `libraries` (id, kind, name, bytes, files), `list <library>`
  (path, size, Unix time), `song-assets <library>` (shared files and each song's asset paths), `file <library> <path>` (the bytes), and
  `transfer-progress <library> <transfer id> <total bytes> <received bytes> <state>` (the sender's progress card).
  Clients that can receive Nijiiro pass `nijiiro-v1` to `libraries` and request its private configuration with
  `nijiiro-config <library> <ephemeral public key>`.

A request is one UTF-8 line, tab-separated; an answer is an 8-byte little-endian length then the bytes (negative:
refused / missing / failed). A connection carries any number of requests; four connections fetch at once.

The phone compares `list` with its folder: missing, other size or time more than 2 s apart = fetch; absent on the PC
= remove. Files download into a hidden staging directory, and completed staged files are reused after an interruption.
The receiver publishes each song's chart and audio files together once they arrive. Changes that share files publish
together, and syncs that remove old files wait for all replacements. Startup recovers an interrupted publication
before the catalog reads it. A sender without a song manifest publishes the library as one group. If a file disappears
from the sender after listing, the receiver keeps staged work and asks for an updated list on the next sync.
Received libraries: `<received>/<pc id>/<library id>/<part>/...` with a `.waddamburo-library` marker (kind, name);
SongLibraries scans the published files at boot (stock/Wii/Wii U as more releases, TJA merged after the local folder,
osu! when none is set here). Completed songs from an interrupted sync become available after a restart. Green goes into the phone's own Green folder and is never removed from.

## Steps

1. **Store + reader + local proof** (content-addressed). *Done 2026-11-17, superseded.*
2. **Mirror + pairing.** *Done 2026-11-18.* `SyncWire`, `SyncServer`, `PairingListener`, `SyncClient`,
   `ReceivedLibraries`, `SongLibraries.SyncLibraries` (what a PC offers), console `--sync-*` (`SyncCommands`). The
   store, its reader and overlay, SyncJson rows and the offer/commit protocol are gone. Proof on one PC (two homes):
   pairing (a wrong code refused), all 23 libraries listed (Green, 11 arcade releases, 5 Wii, 3 Wii U with
   update/DLC, TJA, osu!lazer, osu!stable); Wii U Tokumori + DLC, Wii Ketteiban and the 9.7 GB TJA library mirrored
   byte-identical (`diff -r`); the receiver's reader lists the same songs; a second sync moves nothing; an extra file
   is removed, a touched one fetched again, a stale `.part` cleaned; 5,000 extra files skipped without `--yes`; the
   server killed mid-copy and restarted: the copy finished; unpaired: refused; a received TJA song shows in the
   catalog (+1 song, +1 category).
3. **Screens.** Settings › Devices lists paired devices and offers Add a device and Connect to a PC. On the phone,
   select a paired PC to see its libraries and run Sync now. First-start setup pairs, then mirrors Green by itself.
4. **Nijiiro.** The sender converts up to four song banks at a time to Ogg Opus with the existing vgmstream cache
   when the receiver starts a sync. The selection page estimates the size from the source files, and the transfer uses the converted
   sizes. Datatables and fumen are copied as-is. Existing configured keys are wrapped to the receiver's ephemeral
   public key and stored in its app-private sync config for that library. Other installations keep their own keys.

## Open points

- Music formats: check that each platform's bundled decoder covers the raw formats used by its supported
  libraries. Nijiiro uses Ogg Opus for sync.
- Big libraries: boot scan time of ~15,000 songs' raw files on a phone.
- osu!: the whole folder travels (lazer's every beatmap and skin, stable's Songs of every mode): a taiko-only list if
  the size bothers anyone. Not yet tried end to end on a receiver.
- A desktop receiving a library it already has (same Wii U disc) lists it twice in its release set; harmless so far
  (the newest copy wins), "already here" if it ever matters.
- Empty folders are not mirrored.
- Firewalls: the PC must accept 8381 TCP+UDP. Windows asks once; Linux firewalls (firewalld's `public` zone, ufw) block
  it silently, so the phone shows a firewall hint when a paired PC cannot be reached. If that proves
  annoying: reverse the connection (the PC probes its paired phones and dials them; the phone asks over it), so the PC
  never accepts a connection.
- Some phones' Wi-Fi drops broadcasts (MIUI): discovery also probes every address of networks up to /22.
- Time stamps: some file systems (FAT/exFAT SD cards) round modified times to 2 s; compare with that tolerance.
