# Input and drum bindings

Every drum pad has an id, the key it has always been played with: player 1's left ka, left don,
right don and right ka are `D`, `F`, `J`, `K`; player 2's are `Z`, `X`, `C`, `V`
(`SdlInputBindings.Pads`). Gameplay, the menus, the movies' controls and `--press` all read those
ids from the keyboard snapshot.

Physical inputs become pad ids at the SDL boundary (`SdlApplication`), so nothing after it knows
what the player actually hit:

| Input | Written in `config.cfg` | Notes |
|---|---|---|
| Key | `d`, `semicolon`, `kp_1` | The key's place (SDL scancode name), so the pads stay under the same fingers on any keyboard layout. |
| Controller button | `pad1:dpad_left`, `pad2:south` | SDL gamepad button names. `pad1` is the first controller connected; a controller plugged in takes the lowest free number (up to `pad4`). |
| Controller trigger | `pad1:left_trigger` | Pressed past about half way, released a little under that. |
| MIDI note | `midi:36` | A note-on from any MIDI input device, any channel. |

`p1_left_ka` ... `p2_right_ka` in `config.cfg` each list any number of inputs, so a pad can have
two keys, or a key and a controller button. Every physical input presses on its own: a second key
of a pad hits while the first is still held. An input that is not bound keeps its meaning (Enter,
the arrows, a letter used as a shortcut); a pad's own letter, once unbound, does nothing. A
controller's Start is Escape.

Home mode's Settings > Drum Controls (`HomeMenu`) shows one player's four pads on one device at a
time (Player, then Device: Keyboard, Controller or MIDI). Centre on a pad, then the next input of
that device is added to it (taken from the pad it was on, either player's; removed if the pad already
had it; a pad keeps two keyboard keys at most, a third replacing the oldest); other
devices' inputs are ignored meanwhile. Set Up All Pads asks for the four pads in turn, each hit
replacing what the pad had on that device. `SdlApplication.BeginCapture` holds the input back from
the game. Escape cancels; Enter, the arrows and the other keys the menus need cannot be bound.
Delete clears a pad on that device, and Reset to Default restores the player's defaults for it.

## Menus

The drum navigates every menu (rims move, centre picks), and so do the usual controls:

- Keyboard: outside a song, the arrows and Enter also act as the drum (`GameShell.menuKeys`):
  Left/Up and Right/Down are the rims, Enter the centre, of player 1's drum (player 2's when they
  play alone). The Escape menu reads the arrows and Enter itself.
- Controllers, `pad_menus = gamepad` (the default): outside a song and in the Escape menu
  (`SdlApplication.MenuInput`), the D-pad is the rims, the bottom button the centre, and the right
  button or Start is Escape; `pad1` drives player 1's drum and `pad2` player 2's. The bound pads
  count on the gameplay lane only.
- `pad_menus = drum` (In Menus: Drum on the Controller page): for a drum that shows up as a
  controller, whose buttons are its pads: the bindings apply everywhere.

MIDI (`MidiInput`) uses no library: Linux reads the raw devices (`/dev/snd/midiC*D*`), Windows uses
winmm. Devices are looked for again every two seconds. Note-ons are stamped on arrival with SDL's
clock, like key presses, and are not "held": a MIDI pad gives presses only.
