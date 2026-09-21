# Don rendering

Waddamburo treats Don-chan as a native 3D surface composed by Lumen, not as a
special case in the movie renderer. The movie owns placement, animation of the
slot, alpha, and 2P mirroring; the platform renderer owns the off-screen 3D
target and exposes it through the existing named-native-surface contract.

## Evidence and current scope

The format facts below are asset-derived observations under
[`evidence-policy.md`](../legal/evidence-policy.md). Public tests build synthetic
files and do not contain original models, animations, textures, captures, or
asset manifests.

- NDP3 model data is big-endian and separates object, polygon/index, vertex,
  additional skinned-vertex, and name clumps. Polygons use restartable triangle
  strips and up to four material passes.
- Rigid vertices use a single object bind; skinned vertices carry four palette
  indices and weights. Compact 25/26-entry palettes expand into the character's
  39-entry animation palette.
- Don motions contain a big-endian float frame count followed by fixed-stride,
  fully baked 60 Hz frames. Character frames contain 294 floats and accessory
  frames contain 15.
- Local transforms use `scale * rotationX * rotationY * rotationZ * translation`
  with row vectors. Child world transforms multiply by their parent on the
  right. Skinning matrices are `inverse(bindWorld) * animatedWorld`.
- Character motions carry a bounded face-expression index at value 291.

`Waddamburo.Formats` now validates these inputs into immutable model, material,
vertex, animation, and pose data. `SongSelectHostBinding` forwards authored
`Lumen.SetMotion(player, once, loop)` calls through `IDonPresentationController`
and the shared `DonLumenBinding` assigns each player's output to `don1pM` /
`don2pM` in both Player Entry and Song Select. Numeric motion ids are resolved
from the authored `DON_*` variables visible to the calling AVM action, including
pending local definitions, so the product does not need a duplicated id table.
The ANI payload has no playback-mode flag: an authored third `SetMotion` argument
selects the follow-up loop, while an absent loop holds the one-shot's final frame
until the movie requests another motion. Attaching a fresh movie resets both
players to the default select loop before that movie's authored motion calls run,
so one-shot state cannot leak across scene transitions.

## GPU integration

`SdlDonRenderer` uploads these immutable resources to the existing SDL_GPU
device, renders both players to independent transparent 600×600 color/depth
targets, applies the silhouette pass, and exposes the resulting GPU textures to
the normal Lumen compositor. The prepasses and 2D pass share one command buffer
and device. Normal presentation never downloads or re-uploads a Don target;
screenshots remain the only readback path.

The material path implements the default body's nearest-sampled repeating RGB
replacement mask, ordinary textured and alpha-tested passes, the face-expression
atlas, front-cull inverted hulls, and the silhouette dilation pass. Packaged
GLSL/SPIR-V and HLSL/DXIL build inputs cover the Vulkan and D3D12 backends.
The reflected 2P camera reverses projected triangle winding, so its render pass
swaps front/back culling while retaining the movie-authored 2P placement. Player
Entry instead uses the ordinary camera for 2P and lets that movie's mirrored
right-hand slot turn the players inward. The default 2P palette swaps the body's
red/teal regions and derives the matching teal face from the default paint atlas,
preserving the same authored expressions and facial details.

Normal game-data boot resolves `data/don3d`. Diagnostic Entry-to-Song-Select runs
can opt in with `--don-root=/path/to/don3d`; omitting it keeps diagnostics usable
without proprietary assets.

The initial appearance uses the default body/head and select-loop motion.
Costume, paint, accessory, persisted color, and broader motion selection remain
separate game-state work rather than format or compositor policy.
