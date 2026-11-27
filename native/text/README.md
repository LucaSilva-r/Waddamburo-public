# Native text adapter

`waddamburo_text` is an optional, project-owned C ABI over FreeType. Its current
vertical-title API retains a basic caller-sized profile and adds calibrated compact
(56x400), expanded (96x400), and scene-transition (720x103) song-title profiles.
The Song Select profiles use vertical scalar
layout, punctuation rotation/grouping, small-glyph spacing, an optional subtitle
column, category-colored outlines, and proportional fitting. Output is
premultiplied RGBA8 and can be rasterized at 1x through 4x for the drawable's pixel
density while preserving logical Lumen dimensions.

The transition profile lays out one centered horizontal title with proportional
fitting and the same outline-based rasterization used by the vertical profiles.

The context API keeps one FreeType library and face alive for repeated work with
the same font. Song-title borders are round dilations of the rendered glyph mask,
so small enclosed spaces do not pick up contour-stroker artifacts.

The interactive Song Select composition prepares title surfaces on one background
worker and uploads at most two completed textures per frame. Missing titles are
temporarily omitted, while a resize keeps the previous-scale texture visible until
the replacement is ready. Work is demand-driven and bounded to 16 pending jobs and
64 resident GPU textures, independent of catalog size. Fixed framebuffer probes
retain a synchronous mode so their final frame does not depend on worker timing.

Styled text (ABI 1.8, `waddamburo_text_context_render_styled_rgba8`) draws any text in any box
from a style instead of a calibrated profile: letter spacing, line height, slant, alignment along
and across the lines, a fit mode for text too big for its box (clip, shrink, squeeze along the
line, wrap then shrink), and top-to-bottom columns with the song titles' punctuation rules. Its look
is a stack of up to 16 layers drawn back to front, each the text's shape grown outwards, moved,
blurred and painted with a gradient (ABI 1.9: any angle, where it starts and ends, over each line or the whole
text): a shadow, two borders and a
gradient fill are four layers. Borders are grown from the whole text, so overlapping glyphs share
one border, and one distance field serves every layer. Work is confined to the ink's box grown by
the layers' reach (a 7-digit score with a border: about 0.15 ms at 1x, 1.3 ms at 3x).

Enable it with `-DWADDAMBURO_BUILD_TEXT=ON` and supply a font at application run
time with `--font=PATH`. The adapter never searches for or packages a font.

The calibrated profile parameters are an MIT-licensed adaptation documented in
`docs/legal/provenance.md`; the FreeType integration and public C ABI are original
Waddamburo code.
