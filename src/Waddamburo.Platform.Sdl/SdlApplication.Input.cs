using SDL;
using static SDL.SDL3;

namespace Waddamburo.Platform.Sdl;

// Input: keys, controllers, touch and MIDI, their bindings and capture, menu repeats, text input and rumble.
public sealed unsafe partial class SdlApplication
{
    private readonly HashSet<SdlKeyboardKey> _pressedKeys = [];
    // The physical inputs held down; _pressedKeys is what they translate to.
    private readonly HashSet<SdlInput> _heldInputs = [];
    // Fingers on the touch drum: the input each went down as (its slot keeps two fingers on one pad apart).
    private readonly Dictionary<SDL_FingerID, SdlInput> _fingers = [];
    // Fingers dragging the drag area: where each was last (stage x, 0-1).
    private readonly Dictionary<SDL_FingerID, float> _dragFingers = [];
    private float _touchDrag;

    /// <summary>Where (stage, 0-1) a finger drags instead of playing the drum: a review's lane. Null: nowhere.</summary>
    public System.Drawing.RectangleF? TouchDragArea { get; set; }

    /// <summary>Whether a finger holds the drag area.</summary>
    public bool TouchDragging => _dragFingers.Count > 0;

    /// <summary>How far the fingers dragged across (stage widths, right positive) since the last call.</summary>
    public float TakeTouchDrag()
    {
        var drag = _touchDrag;
        _touchDrag = 0;
        return drag;
    }

    /// <summary>Whether the touch drum shows: a finger touched the screen since the last key or controller press
    /// (always at first on a phone).</summary>
    public bool TouchActive { get; private set; } = PlatformInfo.IsMobile;

    /// <summary>When each <see cref="TouchDrum.Hit"/> code was last touched (SDL nanoseconds), for the drum's flash.</summary>
    public ulong[] TouchHits { get; } = new ulong[TouchDrum.Codes];

    /// <summary>The scene's own touch buttons (none while an overlay is open: its menu buttons answer), at most
    /// <see cref="TouchDrum.MaxKeys"/>; a touch on one presses its key ahead of the drum under it.</summary>
    public IReadOnlyList<TouchKey> TouchKeys { get; set; } = [];

    /// <summary>Touches play the drum (touch_drum = off: only the back, menu and search buttons answer).</summary>
    public bool TouchDrumEnabled { get; set; } = true;

    /// <summary>Song select: the search button (Tab) answers too.</summary>
    public bool TouchSearch { get; set; }

    /// <summary>The notices button beside the back button answers touches (outside songs and menus).</summary>
    public bool TouchNotices { get; set; }

    /// <summary>A text field is open (search): the keyboard button shows or hides the screen keyboard.</summary>
    public bool TouchKeyboard { get; set; }

    /// <summary>An overlay that takes the controls is open (settings, pause, search, scores, an online room): touches
    /// press its buttons (<see cref="TouchDrum.HitMenu"/>), not the drum, and a controller's B closes it.
    /// </summary>
    public bool OverlayOpen { get; set; }

    private readonly (SDL_JoystickID Id, IntPtr Pad)[] _pads = new (SDL_JoystickID, IntPtr)[MaximumPads];
    private readonly MidiInput _midi = new();
    private SdlInputBindings _bindings = SdlInputBindings.Keyboard;
    private bool _capturing;
    private SdlInput? _captured;

    /// <summary>Controllers numbered for bindings (pad1..), in the order they were connected.</summary>
    public const int MaximumPads = 4;

    // A trigger is pressed past the first value and released under the second (of 32767).
    private const int TriggerPress = 16000, TriggerRelease = 12000;

    /// <summary>What hits each drum pad (the keyboard alone until set).</summary>
    public SdlInputBindings Bindings
    {
        get => _bindings;
        set
        {
            _bindings = value ?? throw new ArgumentNullException(nameof(value));
            refreshPressedKeys();
        }
    }

    /// <summary>
    /// Takes the next key, button, trigger or MIDI note pressed out of the input, for a binding (see
    /// <see cref="TakeCaptured"/>). Escape or a controller's Start cancels; the other keys the menus
    /// need (Enter, the arrows, ...) are ignored.
    /// </summary>
    public void BeginCapture()
    {
        _capturing = true;
        _captured = null;
    }

    /// <summary>Still waiting for the input <see cref="BeginCapture"/> asked for.</summary>
    public bool Capturing => _capturing;

    /// <summary>The captured input (once), or null when the capture was cancelled.</summary>
    public SdlInput? TakeCaptured()
    {
        var captured = _captured;
        _captured = null;
        return captured;
    }

    public void CancelCapture() => _capturing = false;

    /// <summary>The input's name for the player: a key as the keyboard's layout prints it, "Pad1 L1", "MIDI 36".</summary>
    public static string Describe(SdlInput input) => input.Kind switch
    {
        SdlInputKind.Key => SDL_GetKeyName(keycode(input))
            is { Length: > 0 } key ? key
            : SDL_GetScancodeName((SDL_Scancode)input.Code) is { Length: > 0 } scancode ? scancode : input.Token,
        SdlInputKind.PadButton => $"Pad{input.Device} " + (SDL_GamepadButton)input.Code switch
        {
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP => "Up",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN => "Down",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT => "Left",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT => "Right",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER => "L1",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER => "R1",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK => "L3",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK => "R3",
            // South, East, ...: the face buttons by place (their letters differ between controllers).
            _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                input.Token[(input.Token.IndexOf(':') + 1)..].Replace('_', ' ')),
        },
        SdlInputKind.PadTrigger => $"Pad{input.Device} "
            + ((SDL_GamepadAxis)input.Code == SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER ? "L2" : "R2"),
        _ => $"MIDI {input.Code}",
    };

    /// <summary>
    /// Controllers work as gamepads while <see cref="MenuInput"/> is set (laid out as a Tatacon: see
    /// menuButton) and hit their bound pads in songs only. False: a drum that shows up as a controller,
    /// its bound pads everywhere.
    /// </summary>
    public bool PadsAsGamepad { get; set; } = true;


    private readonly System.Text.StringBuilder _typed = new();

    /// <summary>
    /// Rumbles controllers <paramref name="pads"/> (their numbers, 1 = the first connected; unknown ones are skipped):
    /// low- and high-frequency motor strength 0-1, for <paramref name="duration"/> (a newer rumble replaces it).
    /// Nothing while the window is in the background.
    /// </summary>
    public void Rumble(IEnumerable<int> pads, float low, float high, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(pads);
        ensureOwnerThread();
        if (!Focused)
            return;
        static ushort strength(float value) => (ushort)(Math.Clamp(value, 0, 1) * ushort.MaxValue);
        foreach (var number in pads)
            if (number >= 1 && number <= _pads.Length && _pads[number - 1].Pad != IntPtr.Zero)
                SDL_RumbleGamepad((SDL_Gamepad*)_pads[number - 1].Pad, strength(low), strength(high), (uint)duration.TotalMilliseconds);
    }

    // The touch keyboard button: hides the screen keyboard (the list under it shows; typing stays on for a physical
    // keyboard's keys) or brings it back.
    private void toggleScreenKeyboard()
    {
        if (SDL_ScreenKeyboardShown(_window))
            SDL_StopTextInput(_window);
        else if (_textInput)
            SDL_StartTextInput(_window);
    }

    /// <summary>A console keyboard that edits a whole text at once (the Switch's): fields open it instead of taking typing.</summary>
    public static bool HasSystemKeyboard => PlatformInfo.IsSwitch;

    /// <summary>
    /// Shows the console's keyboard over the game (blocking) with <paramref name="text"/> to edit; the edited text, or
    /// null when cancelled (or no keyboard came up).
    /// </summary>
    public string? EditWithSystemKeyboard(string text, string hint, int maxLength)
    {
        ensureOwnerThread();
        var buffer = new byte[maxLength > 0 ? maxLength * 4 + 4 : 4096];
        return sdlnx_keyboard(text, hint, maxLength, buffer, buffer.Length) == 1
            ? System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Max(0, Array.IndexOf(buffer, (byte)0))) : null;
    }

    // The Switch's SDL (platforms/switch/shim/sdl3nx.c) only: never called elsewhere.
    [System.Runtime.InteropServices.LibraryImport("SDL3", EntryPoint = "sdlnx_keyboard",
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int sdlnx_keyboard(string initial, string guide, int maxLength, byte[] output, int outputSize);

    /// <summary>Starts taking typed text (the system's text input, IME included); see <see cref="TakeTypedText"/>.</summary>
    public void StartTextInput()
    {
        ensureOwnerThread();
        _typed.Clear();
        _textInput = true;
        SDL_StartTextInput(_window);
    }

    public void StopTextInput()
    {
        ensureOwnerThread();
        _textInput = false;
        SDL_StopTextInput(_window);
        _typed.Clear();
    }

    /// <summary>The text typed since the last call (empty when none or text input is off).</summary>
    public string TakeTypedText()
    {
        var text = _typed.ToString();
        _typed.Clear();
        return text;
    }

    /// <summary>Menus turn mouse wheel notches and left clicks (a don) into presses (false: the mouse is ignored there).</summary>
    /// <summary>Run on F7 in the window, whatever has the keyboard (the game: its skin editor); null: F7 is a plain key.</summary>
    public Action? SkinEditorKey { get; set; }

    public bool MouseControls { get; set; } = true;

    /// <summary>The <see cref="SdlInputBindings.Pads"/> entry a menu click presses: the first player's left don, or a lone right-drum player's.</summary>
    public int ClickPad { get; set; } = 1;


    private readonly System.Collections.Concurrent.ConcurrentQueue<SdlKeyboardKey> _injected = new();

    /// <summary>A key press as if typed (the benchmark's scripted scrolling); delivered with the next frame's input.</summary>
    public void InjectPress(SdlKeyboardKey key) => _injected.Enqueue(key);


    /// <summary>The game is in a menu, not on a gameplay lane (set by the host each tick).</summary>
    public bool MenuInput
    {
        get => _menuInput;
        set
        {
            if (_menuInput == value)
                return;
            _menuInput = value;
            refreshPressedKeys();
        }
    }

    private bool _menuInput;

    // Menus: a held arrow or ka (keyboard, or a controller's D-pad, which menus turn into arrows) presses
    // again after a moment and then steadily, as a keyboard repeats in a text field (SDL's own repeats are
    // dropped: songs must see one press per hit). Don and the buttons never repeat.
    private static readonly SdlKeyboardKey[] RepeatingKeys =
    [
        SdlKeyboardKey.Left, SdlKeyboardKey.Right, SdlKeyboardKey.Up, SdlKeyboardKey.Down,
        SdlInputBindings.Pads[0], SdlInputBindings.Pads[3], SdlInputBindings.Pads[4], SdlInputBindings.Pads[7],
    ];
    // While a text field takes typing (StartTextInput), Backspace and Delete repeat too: elsewhere Backspace may be a
    // BACK button, which must not leave several pages at once.
    private static readonly SdlKeyboardKey[] RepeatingTextKeys = [.. RepeatingKeys, SdlKeyboardKey.Backspace, SdlKeyboardKey.Delete];
    private const ulong RepeatDelayNs = 400_000_000, RepeatIntervalNs = 50_000_000;
    private readonly Dictionary<SdlKeyboardKey, ulong> _repeatAt = [];
    private bool _textInput;

    private void repeatHeld(List<SdlKeyPress> presses)
    {
        var now = SDL_GetTicksNS();
        foreach (var key in _textInput ? RepeatingTextKeys : RepeatingKeys)
        {
            if (!_menuInput || _capturing || !_pressedKeys.Contains(key))
            {
                _repeatAt.Remove(key);
                continue;
            }
            if (!_repeatAt.TryGetValue(key, out var at))
            {
                _repeatAt[key] = now + RepeatDelayNs; // held from now: its own press was the first
                continue;
            }
            for (; at <= now; at += RepeatIntervalNs)
                presses.Add(new SdlKeyPress(key, TimeSpan.FromTicks(checked((long)(at / 100)))));
            _repeatAt[key] = at;
        }
    }

    // Clicks within this long of the window getting focus only focus it (no menu choice by accident).
    private const ulong FocusClickGraceNs = 300_000_000;
    private ulong _focusedAt;

    // A controller in a menu. The first navigates with its D-pad (arrows, which the menus also read as player
    // 1's drum) and presses the buttons it is bound to (SdlInputBindings.Buttons, the Tatacon's keys by place
    // by default); the second drives player 2's drum (left ka, left don, right ka). Right and Start are
    // Escape on both.
    private SdlKeyboardKey? menuButton(SdlInput input)
    {
        if (input.Kind is not (SdlInputKind.PadButton or SdlInputKind.PadTrigger))
            return null;
        var button = (SDL_GamepadButton)input.Code;
        // Start opens the menu; B goes back out of an overlay only (on a game screen Escape opens the settings, which B
        // did by surprise).
        if (input.Kind == SdlInputKind.PadButton && (button == SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START
            || button == SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST && OverlayOpen))
            return SdlKeyboardKey.Escape;
        if (input.Kind == SdlInputKind.PadButton && button == SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST)
            return null;
        if (input.Device == 2)
            return input.Kind != SdlInputKind.PadButton ? null : button switch
            {
                SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT or SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP => SdlInputBindings.Pads[4],
                SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT or SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN => SdlInputBindings.Pads[7],
                SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH => SdlInputBindings.Pads[5],
                _ => null,
            };
        return input.Kind != SdlInputKind.PadButton ? _bindings.Button(input) : button switch
        {
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT => SdlKeyboardKey.Left,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT => SdlKeyboardKey.Right,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP => SdlKeyboardKey.Up,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN => SdlKeyboardKey.Down,
            _ => _bindings.Button(input),
        };
    }

    /// <summary>A controller input's name, Xbox's by place (A bottom, B right, X left, Y top), as hints show it.</summary>
    public static string GamepadName(SdlInput input) => input.Kind == SdlInputKind.PadTrigger
        ? (SDL_GamepadAxis)input.Code == SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER ? "LT" : "RT"
        : (SDL_GamepadButton)input.Code switch
        {
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH => "A",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST => "B",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST => "X",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH => "Y",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER => "LB",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER => "RB",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK => "Back",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START => "Start",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK => "LS",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK => "RS",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP => "Up",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN => "Down",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT => "Left",
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT => "Right",
            _ => Describe(input),
        };

    // What an input is to the game: its pad or button when bound; else a key is itself (but a pad's or a
    // button's own key, unbound, is nothing), a controller's Start is Escape and its Back Space (a practice
    // attempt's pause).
    // A key the player bound to a pad or a button: it does that, not the window's own shortcut (F4, F9, F11).
    private bool bound(SDL_Scancode scancode) =>
        new SdlInput(SdlInputKind.Key, (int)scancode) is var input && (_bindings.Pad(input) is not null || _bindings.Button(input) is not null);

    private SdlKeyboardKey? translate(SdlInput input)
    {
        var key = translateInput(input);
        if (_soleDrum is not { } sole || key is not { } pressed || SdlInputBindings.Pads.IndexOf(pressed) is not (var pad and >= 0))
            return key;
        return sole == 0 ? pad < 4 ? pressed : null : pad >= 4 ? SdlInputBindings.Pads[pad - 4] : null;
    }

    /// <summary>
    /// One drum plays (AI battle): 0 the left drum alone, 1 the right drum alone, as the left drum (the game puts a
    /// lone right-drum player on player 1's side). Null: both drums.
    /// </summary>
    public int? SoleDrum
    {
        get => _soleDrum;
        set
        {
            if (_soleDrum == value)
                return;
            _soleDrum = value;
            refreshPressedKeys();
        }
    }

    private int? _soleDrum;

    private SdlKeyboardKey? translateInput(SdlInput input) => input.Kind == SdlInputKind.Touch
        ? input.Code switch
        {
            TouchDrum.Back => SdlKeyboardKey.Escape,
            TouchDrum.Up => SdlKeyboardKey.Up,
            TouchDrum.Down => SdlKeyboardKey.Down,
            TouchDrum.Ok => SdlKeyboardKey.Enter,
            TouchDrum.Search => SdlKeyboardKey.Tab,
            TouchDrum.Notices => SdlKeyboardKey.E,
            >= TouchDrum.KeyBase => input.Code - TouchDrum.KeyBase < TouchKeys.Count ? TouchKeys[input.Code - TouchDrum.KeyBase].Key : null,
            _ => SdlInputBindings.Pads[input.Code],
        }
        // An overlay's own navigation (#48): Enter, Escape and the arrows are always themselves there and a controller is
        // always a gamepad (a drum controller's bound pads first), whatever is bound; bindings only add inputs.
        : OverlayOpen && input.Kind == SdlInputKind.Key && mapKey(keycode(input)) is (SdlKeyboardKey.Enter or SdlKeyboardKey.Escape
            or SdlKeyboardKey.Up or SdlKeyboardKey.Down or SdlKeyboardKey.Left or SdlKeyboardKey.Right) and var menuKey ? menuKey
        : OverlayOpen && input.Device != 0 && !PadsAsGamepad ? _bindings.Pad(input) ?? menuButton(input)
        : _menuInput && PadsAsGamepad && input.Device != 0 ? menuButton(input)
        // In songs a controller is its drum only (its buttons are the keyboard's there).
        : _bindings.Pad(input) ?? (input.Device == 0 ? _bindings.Button(input) : null) ?? input.Kind switch
    {
        SdlInputKind.Key => mapKey(keycode(input))
            is { } key && !SdlInputBindings.Pads.Contains(key) && !SdlInputBindings.Buttons.Contains(key) ? key : null,
        SdlInputKind.PadButton when input.Code == (int)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START => SdlKeyboardKey.Escape,
        SdlInputKind.PadButton when input.Code == (int)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK => SdlKeyboardKey.Space,
        _ => null,
    };

    // The key at that place in the keyboard's layout.
    private static SDL_Keycode keycode(SdlInput input) =>
        SDL_GetKeyFromScancode((SDL_Scancode)input.Code, SDL_Keymod.SDL_KMOD_NONE, false);

    private void refreshPressedKeys()
    {
        _pressedKeys.Clear();
        foreach (var input in _heldInputs)
            if (translate(input) is { } key)
                _pressedKeys.Add(key);
    }

    // An input went down: the key it presses, or null (held already, not bound, or taken by a capture).
    private SdlKeyboardKey? inputDown(SdlInput input)
    {
        if (_capturing)
        {
            // The menus' own keys (neither letters nor digits) and a controller's Start cannot be bound.
            var reserved = input.Kind switch
            {
                SdlInputKind.Key => mapKey(keycode(input)) is { } key && key is < SdlKeyboardKey.Digit0 or > SdlKeyboardKey.Z
                    ? key : (SdlKeyboardKey?)null,
                SdlInputKind.PadButton when input.Code == (int)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START => SdlKeyboardKey.Escape,
                _ => null,
            };
            if (reserved == SdlKeyboardKey.Escape)
                _capturing = false;
            else if (reserved is null)
            {
                _captured = input;
                _capturing = false;
            }
            return null;
        }
        // A MIDI drum sends hits, not holds.
        if (input.Kind != SdlInputKind.Midi && !_heldInputs.Add(input))
            return null;
        var pressed = translate(input);
        if (pressed is { } held && input.Kind != SdlInputKind.Midi)
            _pressedKeys.Add(held);
        return pressed;
    }

    private void inputUp(SdlInput input)
    {
        if (_heldInputs.Remove(input))
            refreshPressedKeys();
    }

    // A finger's place in the window (0-1) on the touch drum, through the stage's last viewport.
    private int touchCode(float x, float y)
    {
        var (stageX, stageY, aspect) = stagePoint(x, y);
        if (!OverlayOpen)
            for (var index = 0; index < Math.Min(TouchKeys.Count, TouchDrum.MaxKeys); index++)
                if (TouchKeys[index].Contains(stageX, stageY))
                    return TouchDrum.KeyBase + index;
        return OverlayOpen ? TouchDrum.HitMenu(stageX, stageY, aspect, TouchKeyboard) : TouchDrum.Hit(stageX, stageY, aspect, TouchSearch, TouchNotices);
    }

    // A place in the window (0-1) on the stage (0-1), and the stage's aspect.
    private (float X, float Y, float Aspect) stagePoint(float x, float y)
    {
        int width, height;
        var stage = _renderer!.StageViewport;
        if (!SDL_GetWindowSizeInPixels(_window, &width, &height) || stage.Width <= 0 || stage.Height <= 0)
            return (x, y, 16f / 9);
        // A stage wider or narrower than 1280 (LumenStage): the point on the 1280 stage everything is made on.
        var stageX = (x * width - stage.X) / stage.Width;
        if (Waddamburo.Lumen.Rendering.LumenStage.Width != Waddamburo.Lumen.Rendering.LumenStage.BaseWidth)
            return (Waddamburo.Lumen.Rendering.LumenStage.FromStage(stageX) / Waddamburo.Lumen.Rendering.LumenStage.BaseWidth, (y * height - stage.Y) / stage.Height, 16f / 9);
        return (stageX, (y * height - stage.Y) / stage.Height, (float)stage.Width / stage.Height);
    }

    // Editing the split touch drum: a finger (or the mouse, finger MouseFinger) went down, moved or came up. The back
    // button ends it. True: the event was the editor's.
    private bool editTouchDrum(ulong finger, float x, float y, bool down, bool up)
    {
        if (!TouchDrum.Editing)
            return false;
        var (stageX, stageY, aspect) = stagePoint(x, y);
        if (up)
            TouchDrum.Release(finger);
        else if (down && stageX * aspect < TouchDrum.BackSize && stageY < TouchDrum.BackSize)
            TouchDrum.Editing = false;
        else if (down)
            TouchDrum.Grab(finger, stageX, stageY, aspect);
        else
            TouchDrum.Drag(finger, stageX, stageY, aspect);
        return true;
    }

    private const ulong MouseFinger = ulong.MaxValue;

    private int padNumber(SDL_JoystickID id) => Array.FindIndex(_pads, pad => pad.Pad != IntPtr.Zero && pad.Id == id) + 1;

    // A controller connected: it takes the first free number (one plugged back in takes its old one).
    private void addPad(SDL_JoystickID which)
    {
        var free = Array.FindIndex(_pads, static pad => pad.Pad == IntPtr.Zero);
        if (free >= 0 && padNumber(which) == 0 && SDL_OpenGamepad(which) is var pad && pad is not null)
        {
            _pads[free] = (which, (IntPtr)pad);
            TouchActive = false; // a controller to play with: the touch drum gets out of the way (a touch brings it back)
            Console.WriteLine($"Controller pad{free + 1}: {SDL_GetGamepadName(pad)}");
        }
    }

    private static SdlKeyboardKey? mapKey(SDL_Keycode key) => key switch
    {
        SDL_Keycode.SDLK_BACKSPACE => SdlKeyboardKey.Backspace,
        SDL_Keycode.SDLK_TAB => SdlKeyboardKey.Tab,
        SDL_Keycode.SDLK_DELETE => SdlKeyboardKey.Delete,
        SDL_Keycode.SDLK_RETURN => SdlKeyboardKey.Enter,
        SDL_Keycode.SDLK_ESCAPE or SDL_Keycode.SDLK_AC_BACK => SdlKeyboardKey.Escape, // Android's back
        SDL_Keycode.SDLK_LSHIFT or SDL_Keycode.SDLK_RSHIFT => SdlKeyboardKey.Shift,
        SDL_Keycode.SDLK_SPACE => SdlKeyboardKey.Space,
        SDL_Keycode.SDLK_PAGEUP => SdlKeyboardKey.PageUp,
        SDL_Keycode.SDLK_PAGEDOWN => SdlKeyboardKey.PageDown,
        SDL_Keycode.SDLK_F1 => SdlKeyboardKey.F1,
        SDL_Keycode.SDLK_F2 => SdlKeyboardKey.F2,
        SDL_Keycode.SDLK_F3 => SdlKeyboardKey.F3,
        SDL_Keycode.SDLK_F5 => SdlKeyboardKey.F5,
        SDL_Keycode.SDLK_F7 => SdlKeyboardKey.F7,
        SDL_Keycode.SDLK_F8 => SdlKeyboardKey.F8,
        SDL_Keycode.SDLK_F12 => SdlKeyboardKey.F12,
        SDL_Keycode.SDLK_LEFT => SdlKeyboardKey.Left,
        SDL_Keycode.SDLK_UP => SdlKeyboardKey.Up,
        SDL_Keycode.SDLK_RIGHT => SdlKeyboardKey.Right,
        SDL_Keycode.SDLK_DOWN => SdlKeyboardKey.Down,
        >= SDL_Keycode.SDLK_0 and <= SDL_Keycode.SDLK_9 =>
            SdlKeyboardKey.Digit0 + (int)(key - SDL_Keycode.SDLK_0),
        >= SDL_Keycode.SDLK_A and <= SDL_Keycode.SDLK_Z =>
            SdlKeyboardKey.A + (int)(key - SDL_Keycode.SDLK_A),
        _ => null,
    };
}
