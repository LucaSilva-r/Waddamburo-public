# Lumen display-list slice

`Waddamburo.Lumen.Runtime.LumenPlayer` is the first renderer-independent runtime
slice. It consumes the immutable semantic LMB definition, resolves the candidate
root sprite (or an explicit caller override), compiles ordinary timeline records
into frame groups, and owns mutable display instances privately.

The implemented transaction is intentionally small:

- initialization enters ordinary frame zero;
- place, move, replace, and remove commands mutate depth-ordered children;
- matrix and translation pool entries compose through nested instances;
- multiply/add color transforms compose through the hierarchy; and
- a render call creates a new immutable logical-stage snapshot without exposing or
  mutating display state.

`Advance` captures the set of existing sprite instances before stepping them, so a
child created during a tick does not advance twice. Ordinary timelines loop to
frame zero. This initial loop rebuilds timeline children; identity-preserving loop
reuse belongs to the fuller scheduler implementation.

Each display instance also retains its previous transform and multiply color. A
snapshot accepts the display accumulator's fractional tick and interpolates those
values without exposing mutable timeline state to the renderer. Translation changes
greater than 200 logical pixels and multiply-color component changes greater than
0.3 are explicit cuts; new and replaced instances render their current state. Add
color remains at its current authored value.

AVM actions, F105 seek-state restoration, native fill-zero surfaces, and non-normal
blend modes are retained but not guessed. The player emits stable, deduplicated
diagnostics for those deferred paths. Synthetic tests cover root resolution,
place/move/remove/loop behavior, nested matrix order, color conversion, immutable
snapshots, interpolation and cut behavior, and explicit action diagnostics.

`Waddamburo.Game.LumenMovieContent` is the source-independent composition layer for
the vertical slice. It receives a validated DDP movie view, requires the observed
one-texture-per-NTP3-entry profile, decodes bounded RGBA base mips, reads semantic
LMB data with the matching local texture count, and creates a player. Filesystem
selection stays in the app; native upload stays in the SDL adapter.

`LumenScenePlayer` composes independent child players into one logical stage. Scene
layers retain file order, apply a top-level scale/translation, and map each child's
local texture indices into a disjoint scene texture namespace. Advancing a scene
ticks each child exactly once. The SDL host calls that transaction from its fixed
60 Hz simulation accumulator and builds a fresh immutable snapshot for every
presentation frame. The app's scene reader resolves only relative archive
paths beneath the selected asset root and caps a scene at 256 layers.

This is static host-side composition. Script-created clips and `MovieClipLoader`
remain deferred until the bounded resolver and AVM scheduling contracts exist.
