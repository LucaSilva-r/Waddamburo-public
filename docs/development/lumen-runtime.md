# Lumen display-list slice

`Waddamburo.Lumen.Runtime.LumenPlayer` is the first renderer-independent runtime
slice. It consumes the immutable semantic LMB definition, resolves the candidate
root sprite (or an explicit caller override), compiles ordinary timeline records
into frame groups, and owns mutable display instances privately.

The implemented transaction is intentionally small:

- initialization enters ordinary frame zero;
- place, move, replace, and remove commands mutate depth-ordered children;
- frame actions are queued during timeline mutation and drained parent before child;
- authored `onEnterFrame` handlers then run child before parent, followed by a
  second action drain;
- matrix and translation pool entries compose through nested instances;
- multiply/add color transforms compose through the hierarchy; and
- a render call creates a new immutable logical-stage snapshot without exposing or
  mutating display state.

`Advance` captures the set of existing sprite instances before stepping them, so a
child created during a tick does not advance twice. Ordinary timelines loop to
frame zero. This initial loop rebuilds timeline children; identity-preserving loop
reuse belongs to the fuller scheduler implementation.

`Seek` is a separate cut transaction. It restores the nearest F105 display-list
snapshot at or before the requested root frame, recursively derives placed child
timeline ages from their original first-frame values, and replays only the remaining
ordinary frames. If no snapshot exists it replays from frame zero. A completed seek
sets previous state equal to current state so presentation interpolation cannot smear
across the jump. Out-of-range root frames are rejected.

Root labels are compiled into an ordinal immutable lookup. `GotoFrame` and
`GotoLabel` combine the same cut transaction with an explicit resulting playback
state; `Stop` prevents authored advancement while snapshots continue rendering,
and `Play` resumes on the next simulation tick. Seeking while stopped still rebuilds
the requested state. Skipped frames apply their display-list mutations without
running their actions; only actions on the final visible frame are queued. The
requested playback state is established before that queue drains, so an authored
target-frame `Play` or `Stop` takes precedence.

The bounded interpreter executes the primitive stack, conversion, arithmetic,
comparison, bitwise, branch, variable/member, local, array/object, call, function,
inheritance, return, and playback opcodes needed by the Entry initialization path.
`DefineFunction` and `DefineFunction2` create inert function values with validated
out-of-line bodies. Invocations seed bounded registers from parameters and observed
preload flags; recursive calls are depth-limited. The entire code block is
preflighted, and playback/member changes are committed only after successful
termination, so unsupported opcodes and stack underflow defer the whole record
without partial writes.

Authored function returns propagate through nested calls rather than collapsing to
`undefined`. Primitive strings expose `length`, `charAt`, and `toString`.
`new Array(length)` creates a bounded indexed array with the requested length,
while array member writes override their initialized indices. These small built-ins are
implemented in the AVM boundary rather than in a game host.

`Advance(LumenInputSnapshot)` installs an immutable set of Flash key codes for one
simulation tick. The built-in `Key.isDown` reads only that player's current
snapshot, and the parameterless `Advance()` supplies an empty snapshot so input
cannot remain accidentally latched. Strings also expose `charCodeAt`, allowing
authored polling code to derive the numeric codes it queries. SDL event mapping and
game-level player controls remain outside the renderer-independent runtime.

`LumenRuntimeLimits` supplies positive per-player ceilings for instructions per
action, operand-stack values, queued frame actions, registers, and call depth.
Instruction/stack/register exhaustion emits `LUM_ACTION_LIMIT`; excess queued work
emits `LUM_ACTION_QUEUE_LIMIT`, and recursive calls stop at `LUM_AVM_CALL_LIMIT`.
Defaults are 10,000 instructions, 4,096 stack values, 4,096 pending actions, 256
registers, and 64 nested calls. The pending-action ceiling also bounds the number
of enter-frame candidates collected during one dispatch. Actions queued by an
authored timeline jump drain in subsequent bounded batches during the same phase.
Tick-wide instruction budgets remain outside this slice.

Each action also receives bounded registers and a transactional variable/member
context. Named child clips, `this`, `_root`, `_parent`, and `_global` resolve without
exposing mutable display instances publicly. `_visible`, `_x`, `_y`, `_alpha`, and
`_currentframe` bridge to private clip state. Writes commit only when the action
finishes successfully, while later instructions and nested calls can read pending
instance/global member writes through the same transaction. Unregistered function calls consume the authored arguments,
return `undefined`, and emit `LUM_AVM_CALL_UNRESOLVED`, allowing optional host calls
to remain observable without aborting otherwise valid initialization.

The candidate DefineSprite header-word-2 linkage index connects exported sprites to
`Object.registerClass`. Registration is a commit-time effect: it binds both existing
and subsequently placed instances, installs the class prototype, and invokes the
constructor with preloaded `this`. `MovieClip.play` and `stop` bridge to timeline
state. `MovieClip.gotoAndPlay` and `gotoAndStop` resolve one-based frame numbers or
authored labels on the target clip. Forward jumps preserve existing children while
applying intervening display-list frames without their scripts, then run only the
destination frame's actions and newly placed children's initialization actions; backward jumps use
the current cut reconstruction. Both reset interpolation, and invalid targets emit
`LUM_AVM_GOTO_INVALID`. Other missing host methods remain explicit `LUM_AVM_METHOD_UNRESOLVED`
diagnostics with their action offset. A zero placement-name field retains an
instance's existing authored name instead of clearing it during timeline updates.
Synthetic fixtures cover class bootstrap and binding without embedding
or reproducing original content.

Game integration enters through `ILumenHostBinding`, which installs globals before
the movie enters frame zero. A binding can register named functions and objects with
named methods; callbacks receive an immutable primitive argument array and return a
bounded primitive `LumenHostValue`. Registries are private to one player, so scene
layers cannot leak native state into each other. Host exceptions become deduplicated
`LUM_HOST_CALL_FAILED` diagnostics and return `undefined`. Object references do not
cross this first public boundary yet, and host side effects are synchronous rather
than transactionally reversible. `LumenMovieContent.CreatePlayer` accepts the same
optional binding without coupling the content loader to a concrete game host.

Authored `NewObject` now resolves AVM function constructors, attaches their
prototype, preloads `this`, and runs the constructor inside the calling action's
transaction. The built-in `flash.external.ExternalInterface` captures committed
`addCallback` registrations per player. `CallbackNames` is an immutable sorted
snapshot, and `TryInvokeCallback` synchronously invokes a named callback with the
same primitive-only host values. Callback return values and object arguments remain
outside this slice. The standalone viewer installs a `Lumen` presence object
through its host binding so movies select their native-game branch. Its
deterministic `IsReady`, `InitInfo`, and `IsStartLumen` methods allow Entry's
initialization handler to complete without pretending to supply broader game state.
The standalone viewer accepts an authored entry request because it has no cabinet
credit service, retains paid-play reporting, and acknowledges voice-stop requests; other
unimplemented native methods remain diagnostics rather than hidden stubs.

The callback inspection surface also exposes immutable callback names and authored
parameter names. The standalone app uses that metadata for an interactive debug
console and pairs keyboard `1`–`9` with the first nine root labels. Terminal
commands can list callbacks/labels, switch a state, or invoke a callback with typed
primitive arguments while the movie keeps running. This debugger belongs to the
app composition layer and does not add console or keyboard dependencies to Lumen.

The Formats layer supplies immutable prevalidated code blocks rather than asking
the runtime to rediscover byte boundaries. It validates short and long action
records, branch destinations, and the out-of-line lexical bodies used by Lumen's
`DefineFunction`, `DefineFunction2`, and `With` records under instruction-count and
nesting limits. Observed control, register, string, function, URL, and heterogeneous
`Push` operands are immutable typed values; F001 references are validated before
runtime use while raw payload bytes remain available for losslessness. The simple
executor consumes this model directly.

Each display instance also retains its previous transform and multiply color. A
snapshot accepts the display accumulator's fractional tick and interpolates those
values without exposing mutable timeline state to the renderer. Translation changes
greater than 200 logical pixels and multiply-color component changes greater than
0.3 are explicit cuts; new and replaced instances render their current state. Add
color remains at its current authored value.

Flash blend mode 8 is retained as renderer-neutral additive intent through nested
clips and scene composition. Other non-normal blend modes, unsupported AVM actions,
and native fill-zero surfaces are retained but not guessed. The player emits
stable, deduplicated diagnostics for those deferred paths. Synthetic tests cover root resolution,
place/move/remove/loop behavior, nested matrix order, color conversion, F105 and
replay-based seeks, immutable snapshots, interpolation and cut behavior, simple
play/stop actions, transactional variables and clip members, function/class
bootstrap, exported-sprite constructor binding, nested function returns, primitive
string access, clip-local jumps, same-tick enter-frame dispatch, and explicit
deferred-action diagnostics.

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
