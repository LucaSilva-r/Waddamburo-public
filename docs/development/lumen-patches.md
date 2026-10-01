# Lumen patches

A Lumen patch edits a user's movie in memory as it decodes, before its textures are uploaded
(`DirectoryLumenMovieContentSource.Decoded` → `GameShell.patch`). The user's files are never
written and nothing of the game is shipped: a patch only rearranges what the user's own movie
contains. It works like an edited file would, so the movie's own scripts and timelines (frame
jumps, visibility logic) handle the result without host-side per-frame work.

`LumenMovieEditor` (Game/Patching) edits the movie model (`LmbMovieDefinition`) and its decoded
textures:

- `AddString`, `AddColorTransform`: pool entries (colour entries can be set at runtime with
  `LumenPlayer.SetColorTransform`, for colours the host only knows when a scene starts).
- `Clone(character, rule)`: a copy of a shape or sprite (with every child, as deep as a rule
  applies) with each texture handled by a `TextureRule`: `Tint` (placement colour multiply/add),
  `Recolour` (redraw a coloured art piece in any colour, keeping its highlights and shading),
  `Fill` (a host surface slot, `LumenPlayer.SetNativeFill`), `Hide`.
- `CloneLabel(sprite, from, label, rule)`: a new frame label copying the frames another spans.
  The copy starts at the source's keyframe and its first frame clears every depth and places the
  keyframe's display list, so the label shows the same whether the movie seeks to it or plays
  into it.
- `SplitForRecolour(texture, reference)`: each pixel read as shade × (the reference's colour mixed
  with white); a body layer (shade × colour share, multiplied by the new colour) and a light layer
  (shade × white share, added on top, Flash blend 8) redraw the art in any colour.

## Song Select custom genres

`SongSelectGenrePatch` adds 32 genres (`wd:00` … `wd:31`) to song_select.lm: copies of the J-POP
art, recoloured by one colour-pool entry each (the last 32 entries), with the genre's names,
description and mascot drawn from host slots `wd_{tate,yoko,desc,image}_NN`. When Song Select
starts, `SongSelectHostBinding` appends the labels to `GenreResource.MUSICINFO_KEY` (AssignMusic
looks folder keys up there), gives each named folder a slot, sets its colour
(`SongSelectCategory.Tint`) and names (the spine name outlined in a dark shade of the colour, the
header name shifted onto the tab's visual centre). Folders beyond 32 keep the イベント art.

Texture indices come from song_select.lm (Green); the F3 click inspector (see diagnostics.md)
shows them at runtime.
