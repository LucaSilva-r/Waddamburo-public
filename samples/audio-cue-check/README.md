# Audio cue check chart

This synthetic, silent TJA runs through Don, Ka, big Don, big Ka, roll, big
roll, balloon success, balloon failure, kusudama success, and kusudama failure.
The same note sequence is available on Easy, Normal, Hard, and Oni so the
authored course picker can use any of those slots.
The four long notes have quotas of 3, 99, 3, and 99 hits. Hit the first and
third long notes at least three times; let the second and fourth expire. Each
measure lasts two seconds, and the chart leaves a measure of silence before its
last Don so the long-note sounds are easier to hear.

Launch it from the public repository with user-supplied game data:

```sh
dotnet run --project src/Waddamburo.App -- \
  --entry-song-select \
  --asset-root=/path/to/USRDIR/data/lumendata/packed \
  --don-root=/path/to/USRDIR/data/don3d \
  --tja-root=samples/audio-cue-check \
  --font=/path/to/USRDIR/data/font/font.ttf \
  --sound-root=/path/to/USRDIR/data/sound \
  --start-scene=song-select
```

The bundled Ogg is silence, so only game effects and voices are audible. The
effects and voices are still loaded from the user's own sound tree.

To trace it in TaikoRecomp, copy the two sample files to a custom-song root:

```sh
custom_root=/tmp/waddamburo-audio-cue-check
song_dir="$custom_root/TJA/Variety/Audio Cue Check"
mkdir -p "$song_dir"
cp 'samples/audio-cue-check/Audio Cue Check.tja' "$song_dir/chart.tja"
cp samples/audio-cue-check/silence.ogg "$song_dir/silence.ogg"
```

Launch TaikoRecomp with `TAIKO_CUSTOM_SONGS="$custom_root"` and open
**Taiko+ → CUSTOM TJA → Variety → Audio Cue Check**.
