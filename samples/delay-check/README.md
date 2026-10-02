# Delay check chart

This synthetic TJA tests `#DELAY`, including negative delays: the per-note "Time Offset"
from PeepoDrumKit (`#DELAY x` before the note, `#DELAY 0` after it), notes pushed early or
late and put back, a note landing before one written earlier, two measures overlapping, a
roll stretched and a balloon cut short by a delay, delays in Go-Go at a different BPM, and a
`#SCROLL` moved before an earlier one. The comment above each section in the `.tja` says
where its notes should land.

`click.ogg` clicks every beat at 120 BPM (high on the bar lines, every 2 s), so you can hear
which notes sit on the grid. The unit test `DelayCheckSampleLoadsWithEveryNoteWhereItsCommentSays`
checks the parsed note times.

Install it with the other test charts in the custom-song folder of the game data, for example
`USRDIR/custom_songs/Test Charts/Delay Check/`, copying both files.
