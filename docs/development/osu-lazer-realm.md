# osu!lazer library

`Waddamburo.Providers.OsuLazer` lists the native osu!taiko beatmaps of an installed osu!lazer. Home
mode loads it from the folder set in Settings (`osu_folder`), or else from the default install:
`$XDG_DATA_HOME/osu` (`~/.local/share/osu`), the Flatpak's `~/.var/app/sh.ppy.osu/data/osu`, or
`%APPDATA%/osu`, following `storage.ini`'s `FullPath` when the data was moved.

## Reading the Realm

The provider depends only on the `Realm` package (20.1.0, the version osu! itself ships), not on
osu!'s own model assembly. `client.realm` is opened with `IsReadOnly` and `IsDynamic`: the schema
comes from the file, so there is no schema version to pin and any lazer release whose beatmap tables
keep the fields read here works. The reader rejects a missing path before Realm could create it,
never writes, migrates or compacts, and copies what it needs (sets, beatmaps, metadata, named files,
collections) into records before closing the Realm. A read-only open does not take part in Realm's
lock-file protocol and leaves no files behind (tested byte for byte); a scan while osu! is saving
could in principle see a half-written version.

Charts and audio are read straight from lazer's content-addressed store,
`files/<h>/<hh>/<sha-256>`; a beatmap's `Hash` is its `.osu` file's name there.

Tests that write a Realm fixture must not let Realm deliver the post-commit notification on another
thread (xunit's SynchronizationContext did, and Realm aborted the test host in `verify_thread`); the
fixture parks those notifications.

## From beatmaps to songs

A set's taiko beatmaps (osu!standard, catch and mania are left out; hidden ones too) are grouped per
audio file. osu! difficulties follow no Taiko course scheme, so `OsuCourseLayout` uses the courses
as plain slots: the charts in star order fill Easy, Normal, Hard and Oni, four per entry; further
charts open another song entry, and entries of a split set name their charts in the subtitle. Ura is
never used (the song select only reveals it by pressing right on Oni). Stars are osu!'s own rating,
rounded (1-10), and every osu! chart plays with Oni's judgement windows whatever its slot.

For osu! songs only, the host marks every course an entry lacks invalid (`SetInvalidCourse`, hidden
Easy/Normal/Hard/Oni included). That puts the board in the movie's restricted mode
(`MusicInfo.HasInvalidCourse` → `CheckMania`): Oni shows at once without the right-ka presses, and
missing courses are greyed and cannot be picked. Other libraries keep zero bits: a song with Ura in
that mode shows Ura in Oni's place.

`OsuTaikoChartReader` follows lazer's decoder and taiko converter: whistle/clap = ka, finish = big,
sliders are drumrolls (length × spans / (100 × SliderMultiplier × SV) beats), spinners are balloons
(OD-scaled hits per second × 1.65), green points set the scroll and red ones reset it, kiai is
Go-Go, bar lines every measure from each red point, files before v5 shifted 24 ms. A slider
multiplier of 1.4 (the usual taiko base) is scroll 1.

## Browsing

The library is listed by `SongBrowse` (Game/SongSelect) after osu!'s own song select groups: Group
None/Title/Artist/Mapper/Difficulty/Collections/Date Added/BPM/Length (Title, Artist and Mapper by
first letter with "0-9" and "Other"; Difficulty lists a song under each star level of its charts),
sorted by Title/Artist/Mapper/Difficulty/Date Added/BPM/Length. A folder over 999 songs (the count
label has three digits) splits into numbered parts. Two spines after the folders, "Group: …" and
"Sort: …", cycle the modes; Song Select reloads at once on that spine and the choice is saved as
`osu_group` / `osu_sort` in config.cfg.

Folder names use the movie's feature folders (`イベント` art), which name themselves from ten fill
slots (`feature_board_{tate,yoko}_00-09`). With more folders than slots, folder i uses slot i % 10
and the host names each spine per board: the 13 recycled boards (`musicBoard_left6_` … `center_` …
`right6_`, in `resource.musicboardList` order) each report the folder they show
(`musicInfo.genreIndex`), and an ancestor-scoped native fill (`"musicBoard_right2_/feature_board_tate_03"`)
names that board's spine. The centre banner follows the cursor (`container.GetCurrentMusicInfo()`),
polled each tick because `NotifyGenreFolder` only reports it once the list settles.

## Search and reloads

Tab in Song Select opens a search field (`SongSearch`, drawn by the host): every library's songs
whose titles, subtitles, artist or mapper contain every typed word; Enter lists them in a
"Search: …" folder before the others, Escape closes the field. In that folder each board shows its
song's library (`SongSelectCatalogView.SourceLabel`): the host wraps `MusicBoard.ResetMusicInfo`
(genre, board) and answers `GetGenreLabel` during it with stock J-POP blue, custom TJA Variety
green, osu! Kids pink or Nijiiro おすすめ (`LumenPlayer.TryWrapScriptMethod`); the title outlines
follow (`SongSelectCatalogView.BoardStyle`).

Library switches, the Group/Sort spines and search results all reload Song Select through
`SongSelectReload`: the plain rainbow (Don-chan) covers at three ticks per tick while the scene's
movies are prefetched on worker threads, the change runs, Song Select reloads with its music playing
on (`SongSelectHostBinding.Reloading`), and the rainbow opens.

