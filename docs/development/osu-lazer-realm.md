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
