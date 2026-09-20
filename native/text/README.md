# Native text adapter

`waddamburo_text` is an optional, project-owned C ABI over FreeType. Its current
vertical-title API retains a basic caller-sized profile and adds calibrated compact
(56x400) and expanded (96x400) song-title profiles. The latter use vertical scalar
layout, punctuation rotation/grouping, small-glyph spacing, an optional subtitle
column, category-colored outlines, and proportional fitting. Output is
premultiplied RGBA8 and can be rasterized at 1x through 4x for the drawable's pixel
density while preserving logical Lumen dimensions.

The context API keeps one FreeType library and face alive for repeated work with
the same font. Song-title borders are rasterized from the font outlines with
FreeType's stroker rather than by expanding a completed bitmap.

The interactive Song Select composition prepares title surfaces on one background
worker and uploads at most two completed textures per frame. Missing titles are
temporarily omitted, while a resize keeps the previous-scale texture visible until
the replacement is ready. Work is demand-driven and bounded to 16 pending jobs and
64 resident GPU textures, independent of catalog size. Fixed framebuffer probes
retain a synchronous mode so their final frame does not depend on worker timing.

Enable it with `-DWADDAMBURO_BUILD_TEXT=ON` and supply a font at application run
time with `--font=PATH`. The adapter never searches for or packages a font.

The calibrated profile parameters are an MIT-licensed adaptation documented in
`docs/legal/provenance.md`; the FreeType integration and public C ABI are original
Waddamburo code.
