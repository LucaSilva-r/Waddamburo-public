# Native text adapter

`waddamburo_text` is an optional, project-owned C ABI over FreeType. Its current
vertical-title profile stacks Unicode scalars top-to-bottom, shrinks them to fit,
and emits premultiplied RGBA8 with a white fill and black outline. Typography is
deliberately an initial browser profile; punctuation orientation, Latin layout,
and spacing still require visual tuning.

Enable it with `-DWADDAMBURO_BUILD_TEXT=ON` and supply a font at application run
time with `--font=PATH`. The adapter never searches for or packages a font.
