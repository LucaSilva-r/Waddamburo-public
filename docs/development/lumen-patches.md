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

Each genre copy of the folder sprite also gets a pattern layer (host slot `wd_pattern_NN`, the
built-in folders' drawn patterns at 50% alpha): one open-size surface fixed in place under a clip mask
that follows the front's halves frame by frame, so opening reveals it rather than stretching it. The
mask is also added to the sprite's keyframe display lists, which the movie uses when it seeks straight
to the open state. The event folder does the same job with two pattern textures crossfaded while it
opens.

The built-in folders (OSU! LAZER, NIJIIRO, CUSTOM TJA, search results) draw their art at runtime
(`FolderArtPainter`): the pattern and the emblem in the mascot slot are PNG files built into the app,
`src/Waddamburo.App/Resources/folders/<style>/{pattern,box}.png` (edit and rebuild; patterns are
stretched to 460x448 stage units, emblems fitted into 192x360; `osu/box.png` is the osu! logo, unaltered
per osu!'s brand guidelines), and the description columns (slogans, the search query) are drawn in
black-outlined text.

Renderer: Flash blend mode 3 (multiply) is drawn as a real multiply.

Texture indices come from song_select.lm (Green); the F3 click inspector (see diagnostics.md)
shows them at runtime.

A custom TJA folder's `box.def` (TJAPlayer3/OpenTaiko) sets its folder: `#TITLE`, `#GENRE` (a known
genre uses the game's own art), `#BACKCOLOR`/`#BOXCOLOR`, `#FORECOLOR` (name outline),
`#BOXEXPLANATION1-3`, and event-folder style pictures in place of the drawn names, relative to the
folder: `#SPINEIMAGE` (56x400), `#HEADERIMAGE` (256x56), `#BOXIMAGE` (192x360, open folder). Pictures
are 8-bit RGB/RGBA PNG.
