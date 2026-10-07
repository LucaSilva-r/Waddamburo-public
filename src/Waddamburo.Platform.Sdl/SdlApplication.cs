using System.Diagnostics;
using SDL;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.Platform.Sdl.Timing;
using static SDL.SDL3;

namespace Waddamburo.Platform.Sdl;

/// <summary>Owns SDL video, one window, and the bgfx renderer on the creating thread.</summary>
public sealed unsafe class SdlApplication : IDisposable
{
    private readonly int _ownerThreadId;
    private SDL_Window* _window;
    private RenderDevice? _renderer;
    private bool _bgfxInitialized;
    private bool _sdlInitialized;
    private bool _disposed;
    private readonly HashSet<SdlKeyboardKey> _pressedKeys = [];
    // The physical inputs held down; _pressedKeys is what they translate to.
    private readonly HashSet<SdlInput> _heldInputs = [];
    private readonly (SDL_JoystickID Id, IntPtr Pad)[] _pads = new (SDL_JoystickID, IntPtr)[MaximumPads];
    private readonly MidiInput _midi = new();
    private SdlInputBindings _bindings = SdlInputBindings.Keyboard;
    private bool _capturing;
    private SdlInput? _captured;

    /// <summary>Controllers numbered for bindings (pad1..), in the order they were connected.</summary>
    public const int MaximumPads = 4;

    // A trigger is pressed past the first value and released under the second (of 32767).
    private const int TriggerPress = 16000, TriggerRelease = 12000;

    public SdlApplication(
        string title,
        int width,
        int height,
        bool debugGpu = false,
        bool resizable = true,
        bool highPixelDensity = true,
        bool fullscreen = false,
        bool showCursor = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _ownerThreadId = Environment.CurrentManagedThreadId;

        try
        {
            if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
                throw sdlFailure("initialize SDL video");
            _sdlInitialized = true;
            // Controllers are optional: without the subsystem the keyboard still plays.
            if (!SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_GAMEPAD))
                Console.Error.WriteLine($"Controllers unavailable: {SDL_GetError()}");

            var windowFlags = highPixelDensity
                ? SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY
                : 0;
            if (resizable)
                windowFlags |= SDL_WindowFlags.SDL_WINDOW_RESIZABLE;
            if (fullscreen)
                windowFlags |= SDL_WindowFlags.SDL_WINDOW_FULLSCREEN;
            _window = SDL_CreateWindow(
                title,
                width,
                height,
                windowFlags);
            if (_window is null)
                throw sdlFailure("create the window");
            // The game is played with drums and keys: no pointer over the window (F3 inspect shows it).
            if (showCursor) SDL_ShowCursor(); else SDL_HideCursor();

            int pixelWidth, pixelHeight;
            if (!SDL_GetWindowSizeInPixels(_window, &pixelWidth, &pixelHeight))
                throw sdlFailure("query the window pixel size");
            BgfxSupport.Initialize(_window, (uint)pixelWidth, (uint)pixelHeight, debugGpu);
            _bgfxInitialized = true;
            _renderer = new RenderDevice(_window, (uint)pixelWidth, (uint)pixelHeight);
            GpuDriver = BgfxSupport.RendererName;
        }
        catch
        {
            disposeNativeResources();
            throw;
        }
    }

    public string GpuDriver { get; } = string.Empty;

    private DisplaySettings? _display;

    // Stopwatch ticks between frame starts under the frame limit; 0 = none.
    private long _frameInterval;
    private long _nextFrameAt;

    /// <summary>
    /// Applies vsync, the fullscreen kind and mode, and the letterbox. The window only enters or leaves
    /// fullscreen when that setting itself changed (F11 toggles it apart from the settings).
    /// </summary>
    public void ApplyDisplay(DisplaySettings display)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        BgfxSupport.SetVsync(display.Vsync);
        _frameInterval = display.FpsCap > 0 ? Stopwatch.Frequency / display.FpsCap : 0;
        _renderer!.Letterbox = (display.LetterboxSize, display.LetterboxX, display.LetterboxY);
        if (_display is { } previous && previous.Fullscreen == display.Fullscreen && previous.Exclusive == display.Exclusive
            && previous.Width == display.Width && previous.Height == display.Height && previous.RefreshRate == display.RefreshRate)
        {
            _display = display;
            return;
        }
        _display = display;
        SDL_DisplayMode mode;
        var (width, height) = display.Width > 0 ? (display.Width, display.Height) : desktop();
        var refresh = display.RefreshRate > 0 ? display.RefreshRate
            : FullscreenModes().Where(m => (m.Width, m.Height) == (width, height)).Select(static m => m.RefreshRate).DefaultIfEmpty(0).Max();
        var exclusive = display.Exclusive && SDL_GetClosestFullscreenDisplayMode(SDL_GetDisplayForWindow(_window),
            width, height, refresh, true, &mode);
        // Null: borderless (the desktop's mode).
        if (!SDL_SetWindowFullscreenMode(_window, exclusive ? &mode : null))
            Console.Error.WriteLine($"Fullscreen mode not applied: {SDL_GetError()}");
        SDL_SetWindowFullscreen(_window, display.Fullscreen);
    }

    private (int Width, int Height) desktop()
    {
        var mode = SDL_GetDesktopDisplayMode(SDL_GetDisplayForWindow(_window));
        return mode is null ? (1920, 1080) : (mode->w, mode->h);
    }

    /// <summary>The exclusive fullscreen modes of the window's display, largest and fastest first.</summary>
    public IReadOnlyList<(int Width, int Height, int RefreshRate)> FullscreenModes()
    {
        ensureOwnerThread();
        int count;
        var modes = SDL_GetFullscreenDisplayModes(SDL_GetDisplayForWindow(_window), &count);
        if (modes is null)
            return [];
        var list = new List<(int, int, int)>();
        for (var index = 0; index < count; index++)
            list.Add((modes[index]->w, modes[index]->h, (int)Math.Round(modes[index]->refresh_rate)));
        SDL_free(modes);
        return [.. list.Distinct()];
    }

    /// <summary>
    /// The height the stage is drawn at in fullscreen: the exclusive mode's or the desktop's, times the
    /// letterbox (what the upscaled textures are sized for).
    /// </summary>
    public int StageHeight(DisplaySettings display)
    {
        var (width, height) = display.Exclusive && display.Width > 0 ? (display.Width, display.Height) : desktop();
        return (int)(Math.Min(height, width * 9 / 16) * display.LetterboxSize);
    }

    public (int Width, int Height) GetPixelSize()
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        int width;
        int height;
        if (!SDL_GetWindowSizeInPixels(_window, &width, &height))
            throw sdlFailure("query the window pixel size");
        return (width, height);
    }

    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> PickedFolders = new();

    /// <summary>Opens the system's folder picker; the chosen folder arrives through <see cref="TryTakePickedFolder"/>.</summary>
    public void PickFolder(string? start)
    {
        var location = start is null ? null : System.Text.Encoding.UTF8.GetBytes(start + "\0");
        fixed (byte* path = location)
            SDL_ShowOpenFolderDialog(&folderPicked, IntPtr.Zero, _window, path, false);
    }

    /// <summary>The folder picked since the last call (none when the picker was cancelled).</summary>
    public static bool TryTakePickedFolder([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? folder) =>
        PickedFolders.TryDequeue(out folder);

    // Called by SDL, possibly on another thread; an empty list = cancelled, null = failed.
    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void folderPicked(IntPtr userdata, byte** list, int filter)
    {
        if (list is not null && *list is not null
            && System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)(*list)) is { Length: > 0 } folder)
            PickedFolders.Enqueue(folder);
    }

    public RenderTextureId UploadRgba8(uint width, uint height, ReadOnlySpan<byte> pixels)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer!.UploadRgba8(width, height, pixels);
    }

    /// <summary>Whether BC7 textures can be drawn (natively, or decoded by bgfx).</summary>
    public bool SupportsBc7 => _renderer?.SupportsBc7 ?? false;

    public RenderTextureId UploadBc7(uint width, uint height, ReadOnlySpan<byte> blocks)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer!.UploadBc7(width, height, blocks);
    }

    public bool TryReplaceBc7(RenderTextureId texture, uint width, uint height, ReadOnlySpan<byte> blocks)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer!.TryReplaceBc7(texture, width, height, blocks);
    }

    public RenderTextureId[] UploadRgba8Batch(IReadOnlyList<RgbaTextureUpload> uploads)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer!.UploadRgba8Batch(uploads);
    }

    public void UpdateRgba8(RenderTextureId texture, uint width, uint height, ReadOnlySpan<byte> pixels)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _renderer!.UpdateRgba8(texture, width, height, pixels);
    }

    /// <summary>F9: upscaled textures shown (true) or the originals, for comparison.</summary>
    public bool ShowUpscaled => _renderer?.ShowReplacements ?? true;

    public bool TryReplaceRgba8(RenderTextureId texture, uint width, uint height, ReadOnlySpan<byte> pixels)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer!.TryReplaceRgba8(texture, width, height, pixels);
    }

    // The last presented frame as bgfx saw it: GPU-bound, submit-bound or waiting on the swapchain.
    private string gpuStats()
    {
        if (_renderer is null)
            return "";
        var stats = Bgfx.bgfx.get_stats();
        double ms(long ticks, long frequency) => frequency == 0 ? 0 : ticks * 1000.0 / frequency;
        return $"; bgfx {stats->numDraw} draws, gpu {ms(stats->gpuTimeEnd - stats->gpuTimeBegin, stats->gpuTimerFreq):F1} ms, "
            + $"submit {ms(stats->cpuTimeEnd - stats->cpuTimeBegin, stats->cpuTimerFreq):F1} ms, "
            + $"wait render {ms(stats->waitRender, stats->cpuTimerFreq):F1} ms";
    }

    public void ReleaseTexture(RenderTextureId texture)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _renderer!.ReleaseTexture(texture);
    }

    public SdlDonRenderer CreateDonRenderer(string assetRoot)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new SdlDonRenderer(_renderer!, assetRoot);
    }

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

    /// <summary>The game is in a menu, not on a gameplay lane (set by the host each tick).</summary>
    private readonly System.Text.StringBuilder _typed = new();

    /// <summary>Starts taking typed text (the system's text input, IME included); see <see cref="TakeTypedText"/>.</summary>
    public void StartTextInput()
    {
        ensureOwnerThread();
        _typed.Clear();
        SDL_StartTextInput(_window);
    }

    public void StopTextInput()
    {
        ensureOwnerThread();
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

    // A controller in a menu. The first navigates with its D-pad (arrows, which the menus also read as player
    // 1's drum) and presses the buttons it is bound to (SdlInputBindings.Buttons, the Tatacon's keys by place
    // by default); the second drives player 2's drum (left ka, left don, right ka). Right and Start are
    // Escape on both.
    private SdlKeyboardKey? menuButton(SdlInput input)
    {
        if (input.Kind is not (SdlInputKind.PadButton or SdlInputKind.PadTrigger))
            return null;
        var button = (SDL_GamepadButton)input.Code;
        if (input.Kind == SdlInputKind.PadButton
            && button is SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST or SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START)
            return SdlKeyboardKey.Escape;
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
    private SdlKeyboardKey? translate(SdlInput input) => _menuInput && PadsAsGamepad && input.Device != 0 ? menuButton(input)
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

    private int padNumber(SDL_JoystickID id) => Array.FindIndex(_pads, pad => pad.Pad != IntPtr.Zero && pad.Id == id) + 1;

    private volatile bool _quitRequested;
    private bool _discardElapsed;

    /// <summary>
    /// The current frame stalled on purpose (a scene load): the next frame starts timing from its
    /// end instead of catching the missed ticks up in a burst (the screen jumps otherwise).
    /// </summary>
    public void DiscardElapsed() => _discardElapsed = true;

    /// <summary>Ends the running loop after the current frame; callable from any thread.</summary>
    public void RequestQuit() => _quitRequested = true;

    public int Run(
        RenderFrame frame,
        int? frameLimit = null,
        Action<RenderCapture>? captureFinalFrame = null)
        => Run(
            _ => frame,
            static () => { },
            frameLimit,
            tickLimit: null,
            captureFinalFrame).RenderedFrames;

    public SdlRunResult Run(
        Func<double, RenderFrame> createFrame,
        Action simulationTick,
        int? frameLimit = null,
        int? tickLimit = null,
        Action<RenderCapture>? captureFinalFrame = null)
    {
        ArgumentNullException.ThrowIfNull(simulationTick);
        return Run(
            createFrame,
            _ => simulationTick(),
            frameLimit,
            tickLimit,
            captureFinalFrame);
    }

    /// <summary>Whether the window has the keyboard focus (true until SDL reports otherwise).</summary>
    public bool Focused { get; private set; } = true;

    public SdlRunResult Run(
        Func<double, RenderFrame> createFrame,
        Action<SdlKeyboardSnapshot> simulationTick,
        int? frameLimit = null,
        int? tickLimit = null,
        Action<RenderCapture>? captureFinalFrame = null,
        Action<SdlKeyboardSnapshot>? updateFrame = null,
        Func<bool>? profileFrame = null,
        Action? togglePerformanceOverlay = null,
        Action<float, float>? pointerMoved = null,
        Func<bool>? performanceVisible = null,
        Action<float, float>? pointerClicked = null,
        Action? toggleInspect = null,
        Action<SdlFrameMetrics>? performanceSample = null,
        Action? pointerReleased = null,
        Action<float>? pointerScrolled = null)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(createFrame);
        ArgumentNullException.ThrowIfNull(simulationTick);
        if (frameLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(frameLimit));
        if (tickLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(tickLimit));
        if (captureFinalFrame is not null && frameLimit is null && tickLimit is null)
            throw new ArgumentException("A final-frame capture requires a frame or tick limit.");

        var renderedFrames = 0;
        var simulationTicks = 0;
        var droppedTicks = 0;
        var clock = new FixedStepAccumulator();
        var previousTimestamp = Stopwatch.GetTimestamp();
        var running = true;
        var pendingPresses = new List<SdlKeyPress>();
        void press(SdlInput input, ulong timestamp)
        {
            if (inputDown(input) is not { } key)
                return;
            if (key == SdlKeyboardKey.F5)
                togglePerformanceOverlay?.Invoke();
            else
                pendingPresses.Add(new SdlKeyPress(key, TimeSpan.FromTicks(checked((long)(timestamp / 100)))));
        }
        var hitchTrace = Environment.GetEnvironmentVariable("WADDAMBURO_HITCH_TRACE") == "1";
        (int Gen0, int Gen1, int Gen2, TimeSpan Pause) hitchGc = default;
        using var frameProfile = Environment.GetEnvironmentVariable("WADDAMBURO_GAMEPLAY_FRAME_PROFILE") == "1"
            ? new FrameProfile() : null;
        var previousProfileEligible = false;
        var windowFocused = true;
        _quitRequested = false;
        while (running && !_quitRequested && (frameLimit is null || renderedFrames < frameLimit))
        {
            var performanceActive = performanceVisible?.Invoke() == true;
            var inputStart = performanceActive ? Stopwatch.GetTimestamp() : 0;
            SDL_Event currentEvent;
            while (SDL_PollEvent(&currentEvent))
            {
                if (currentEvent.type is (uint)SDL_EventType.SDL_EVENT_QUIT
                    or (uint)SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED)
                {
                    running = false;
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST)
                {
                    _heldInputs.Clear();
                    _pressedKeys.Clear();
                    windowFocused = false;
                    Focused = false;
                    if (pointerReleased is not null)
                    {
                        SDL_CaptureMouse(false);
                        pointerReleased();
                    }
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED)
                    windowFocused = Focused = true;
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_MOUSE_MOTION && pointerMoved is not null)
                {
                    int width, height;
                    if (SDL_GetWindowSize(_window, &width, &height) && width > 0 && height > 0)
                        pointerMoved(currentEvent.motion.x / width, currentEvent.motion.y / height);
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_TEXT_INPUT)
                    _typed.Append(currentEvent.text.GetText());
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN && pointerClicked is not null
                    && currentEvent.button.button == SDL_BUTTON_LEFT)
                {
                    int width, height;
                    if (SDL_GetWindowSize(_window, &width, &height) && width > 0 && height > 0)
                    {
                        if (pointerReleased is not null) SDL_CaptureMouse(true);
                        pointerClicked(currentEvent.button.x / width, currentEvent.button.y / height);
                    }
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP
                    && currentEvent.button.button == SDL_BUTTON_LEFT && pointerReleased is not null)
                {
                    SDL_CaptureMouse(false);
                    pointerReleased();
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_KEY_DOWN
                    && currentEvent.key.key == SDL_Keycode.SDLK_F3 && toggleInspect is not null)
                {
                    // F3: inspect mode (a left click lists what is drawn under the pointer).
                    if (!currentEvent.key.repeat)
                    {
                        toggleInspect();
                        _ = SDL_CursorVisible() ? SDL_HideCursor() : SDL_ShowCursor();
                    }
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_MOUSE_WHEEL)
                {
                    // Tools receive precise wheel deltas; game menus translate each notch into a key press.
                    // ponytail: the menus read one press per key and tick, so a flick faster than 60 notches a second loses some.
                    if (pointerScrolled is not null && !_capturing)
                        pointerScrolled(currentEvent.wheel.y);
                    else if (_menuInput && !_capturing && currentEvent.wheel.y != 0)
                        for (var notch = 0; notch < Math.Max(1, (int)Math.Abs(currentEvent.wheel.y)); notch++)
                            pendingPresses.Add(new SdlKeyPress(currentEvent.wheel.y > 0 ? SdlKeyboardKey.WheelUp : SdlKeyboardKey.WheelDown,
                                TimeSpan.FromTicks(checked((long)(currentEvent.wheel.timestamp / 100)))));
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_KEY_DOWN
                    && currentEvent.key.key == SDL_Keycode.SDLK_F11)
                {
                    // F11: borderless desktop fullscreen <-> window, like a browser.
                    if (!currentEvent.key.repeat)
                        SDL_SetWindowFullscreen(_window, (SDL_GetWindowFlags(_window) & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) == 0);
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_KEY_DOWN
                    && currentEvent.key.key == SDL_Keycode.SDLK_F9)
                {
                    // F9: upscaled textures <-> the originals, for comparison (the next scene loaded shows it).
                    if (!currentEvent.key.repeat && _renderer is not null)
                    {
                        _renderer.ShowReplacements = !_renderer.ShowReplacements;
                        Console.WriteLine($"Upscaled textures {(_renderer.ShowReplacements ? "on" : "off")} (F9; scenes loaded from now on).");
                    }
                }
                else if (currentEvent.type is (uint)SDL_EventType.SDL_EVENT_KEY_DOWN
                    or (uint)SDL_EventType.SDL_EVENT_KEY_UP)
                {
                    var input = new SdlInput(SdlInputKind.Key, (int)currentEvent.key.scancode);
                    if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_KEY_UP)
                        inputUp(input);
                    else if (!currentEvent.key.repeat)
                        press(input, currentEvent.key.timestamp);
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_GAMEPAD_ADDED)
                {
                    // The first free number: a controller plugged back in takes its old one.
                    var free = Array.FindIndex(_pads, static pad => pad.Pad == IntPtr.Zero);
                    if (free >= 0 && padNumber(currentEvent.gdevice.which) == 0
                        && SDL_OpenGamepad(currentEvent.gdevice.which) is var pad && pad is not null)
                    {
                        _pads[free] = (currentEvent.gdevice.which, (IntPtr)pad);
                        Console.WriteLine($"Controller pad{free + 1}: {SDL_GetGamepadName(pad)}");
                    }
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED)
                {
                    if (padNumber(currentEvent.gdevice.which) is var number and > 0)
                    {
                        SDL_CloseGamepad((SDL_Gamepad*)_pads[number - 1].Pad);
                        _pads[number - 1] = default;
                        _heldInputs.RemoveWhere(input => input.Device == number);
                        refreshPressedKeys();
                    }
                }
                else if (currentEvent.type is (uint)SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN
                    or (uint)SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_UP)
                {
                    if (padNumber(currentEvent.gbutton.which) is var number and > 0)
                    {
                        var input = new SdlInput(SdlInputKind.PadButton, currentEvent.gbutton.button, number);
                        if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN)
                            press(input, currentEvent.gbutton.timestamp);
                        else
                            inputUp(input);
                    }
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_GAMEPAD_AXIS_MOTION
                    && (SDL_GamepadAxis)currentEvent.gaxis.axis is SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER
                        or SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER)
                {
                    if (padNumber(currentEvent.gaxis.which) is var number and > 0)
                    {
                        var input = new SdlInput(SdlInputKind.PadTrigger, currentEvent.gaxis.axis, number);
                        if (currentEvent.gaxis.value > TriggerPress)
                            press(input, currentEvent.gaxis.timestamp);
                        else if (currentEvent.gaxis.value < TriggerRelease)
                            inputUp(input);
                    }
                }
            }
            // MIDI arrives on its own threads, focused or not: the background's hits are dropped.
            while (MidiInput.TryTake(out var note))
                if (windowFocused)
                    press(new SdlInput(SdlInputKind.Midi, note.Note), note.Timestamp);

            if (!running)
                break;

            // Input runs apart from presentation, like osu!'s 1 kHz input thread: while the render
            // thread still presents the previous frame (vsync), drum presses are delivered (judged,
            // hit sound played) at once and SDL is polled again ~1 ms later, instead of waiting for
            // the next frame. Screenshot runs and minimised/hidden windows keep the blocking present.
            if (captureFinalFrame is null
                && (SDL_GetWindowFlags(_window) & (SDL_WindowFlags.SDL_WINDOW_MINIMIZED | SDL_WindowFlags.SDL_WINDOW_HIDDEN)) == 0
                && !_renderer!.TryAcquireSwapchain())
            {
                if (updateFrame is not null && pendingPresses.Count > 0)
                {
                    updateFrame(new SdlKeyboardSnapshot(_pressedKeys, pendingPresses,
                        TimeSpan.FromTicks(checked((long)(SDL_GetTicksNS() / 100)))));
                    pendingPresses.Clear();
                }
                Thread.Sleep(1);
                continue;
            }

            // The frame limit: until the next frame is due, input is still delivered as above.
            if (_frameInterval > 0 && captureFinalFrame is null)
            {
                var now = Stopwatch.GetTimestamp();
                if (now < _nextFrameAt)
                {
                    if (updateFrame is not null && pendingPresses.Count > 0)
                    {
                        updateFrame(new SdlKeyboardSnapshot(_pressedKeys, pendingPresses,
                            TimeSpan.FromTicks(checked((long)(SDL_GetTicksNS() / 100)))));
                        pendingPresses.Clear();
                    }
                    // ponytail: sleeps in 1 ms steps, spins the last one (Sleep(1) overshoots).
                    if (Stopwatch.GetElapsedTime(now, _nextFrameAt) > TimeSpan.FromMilliseconds(1.5))
                        Thread.Sleep(1);
                    else
                        Thread.Yield();
                    continue;
                }
                // Due times advance by the interval (no drift), restarting after a stall.
                _nextFrameAt = Math.Max(_nextFrameAt + _frameInterval, now);
            }

            var profileEligibleAtStart = frameProfile is not null && windowFocused && (profileFrame?.Invoke() ?? true);
            var timestamp = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(previousTimestamp, timestamp);
            previousTimestamp = timestamp;
            var remainingTicks = tickLimit is int limit ? limit - simulationTicks : int.MaxValue;
            var eventTime = TimeSpan.FromTicks(checked((long)(SDL_GetTicksNS() / 100)));
            var keyboard = new SdlKeyboardSnapshot(_pressedKeys, pendingPresses, eventTime);
            updateFrame?.Invoke(keyboard);
            var inputTime = performanceActive ? Stopwatch.GetElapsedTime(inputStart) : TimeSpan.Zero;
            var delivered = updateFrame is not null;
            var updateStart = Stopwatch.GetTimestamp();
            var update = clock.AddElapsed(elapsed, () =>
            {
                simulationTick(delivered ? new SdlKeyboardSnapshot(_pressedKeys, timestamp: eventTime) : keyboard);
                delivered = true;
            }, remainingTicks);
            if (delivered)
                pendingPresses.Clear();
            simulationTicks += update.ExecutedTicks;
            droppedTicks += update.DroppedTicks;

            var reachedTickLimit = tickLimit is int requestedTicks && simulationTicks >= requestedTicks;
            var reachedFrameLimit = frameLimit is int requestedFrames && renderedFrames + 1 >= requestedFrames;
            var shouldCapture = captureFinalFrame is not null && (reachedTickLimit || reachedFrameLimit);
            var interpolationFraction = reachedTickLimit ? 1d : clock.InterpolationFraction;
            var renderStart = Stopwatch.GetTimestamp();
            var capture = _renderer!.Present(createFrame(interpolationFraction), shouldCapture);
            if (_discardElapsed)
            {
                _discardElapsed = false;
                previousTimestamp = Stopwatch.GetTimestamp();
            }
            // Diagnostic: WADDAMBURO_HITCH_TRACE=1 reports which part of a slow frame took the time.
            var updateTime = Stopwatch.GetElapsedTime(updateStart, renderStart);
            var renderTime = Stopwatch.GetElapsedTime(renderStart);
            var profileEligibleAtEnd = frameProfile is not null && windowFocused && (profileFrame?.Invoke() ?? true);
            if (performanceActive)
                performanceSample?.Invoke(new SdlFrameMetrics(elapsed, inputTime, updateTime, renderTime, update.ExecutedTicks));
            if (frameProfile is not null)
            {
                if (previousProfileEligible && profileEligibleAtStart && profileEligibleAtEnd)
                    frameProfile.Record(elapsed, updateTime, renderTime, simulationTicks);
                else
                {
                    frameProfile.Report(simulationTicks);
                    frameProfile.Flush();
                }
            }
            previousProfileEligible = profileEligibleAtStart && profileEligibleAtEnd;
            if (hitchTrace)
            {
                // With the collections and GC pause time since the last report: a hitch that is GC shows here.
                var (gen0, gen1, gen2, pause) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration());
                if (updateTime + renderTime > TimeSpan.FromMilliseconds(25))
                    Console.Error.WriteLine($"Frame hitch at tick {simulationTicks}: update {updateTime.TotalMilliseconds:F0} ms "
                        + $"({update.ExecutedTicks} ticks), render {renderTime.TotalMilliseconds:F0} ms; GC since last "
                        + $"{gen0 - hitchGc.Gen0}/{gen1 - hitchGc.Gen1}/{gen2 - hitchGc.Gen2} (gen0/1/2), paused {(pause - hitchGc.Pause).TotalMilliseconds:F0} ms"
                        + $"{gpuStats()}.");
                hitchGc = (gen0, gen1, gen2, pause);
            }
            if (capture is not null)
                captureFinalFrame!(capture);
            renderedFrames++;
            if (reachedTickLimit)
                break;
        }
        frameProfile?.Report(simulationTicks);
        frameProfile?.Flush();
        return new SdlRunResult(renderedFrames, simulationTicks, droppedTicks, clock.InterpolationFraction);
    }

    private sealed class FrameProfile : IDisposable
    {
        private const int WindowFrames = 1920;
        private readonly double[] _frameTimes = new double[WindowFrames];
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly List<string> _reports = [];
        private int _frames;
        private double _maximumUpdateMs;
        private double _maximumRenderMs;
        private long _windowStart;
        private TimeSpan _cpuStart;
        private int _gen0Start;
        private int _gen1Start;
        private int _gen2Start;

        public void Record(TimeSpan interval, TimeSpan update, TimeSpan render, int tick)
        {
            if (_frames == 0)
            {
                _windowStart = Stopwatch.GetTimestamp();
                _cpuStart = _process.TotalProcessorTime;
                _gen0Start = GC.CollectionCount(0);
                _gen1Start = GC.CollectionCount(1);
                _gen2Start = GC.CollectionCount(2);
            }
            _frameTimes[_frames++] = interval.TotalMilliseconds;
            _maximumUpdateMs = Math.Max(_maximumUpdateMs, update.TotalMilliseconds);
            _maximumRenderMs = Math.Max(_maximumRenderMs, render.TotalMilliseconds);
            if (_frames == WindowFrames)
                Report(tick);
        }

        public void Report(int tick)
        {
            if (_frames == 0)
                return;
            var sorted = _frameTimes.AsSpan(0, _frames).ToArray();
            Array.Sort(sorted);
            var wallSeconds = Stopwatch.GetElapsedTime(_windowStart).TotalSeconds;
            var cpuSeconds = (_process.TotalProcessorTime - _cpuStart).TotalSeconds;
            var over2 = sorted.Count(static ms => ms > 1000d / 480);
            var over4 = sorted.Count(static ms => ms > 1000d / 240);
            var over8 = sorted.Count(static ms => ms > 1000d / 120);
            static double percentile(double[] values, double fraction) =>
                values[(int)Math.Ceiling((values.Length - 1) * fraction)];
            _reports.Add($"Gameplay frames to tick {tick}: {_frames} frames, "
                + $"FPS {_frames / wallSeconds:F0}, "
                + $"p50/p95/p99/max {percentile(sorted, .5):F2}/{percentile(sorted, .95):F2}/"
                + $"{percentile(sorted, .99):F2}/{sorted[^1]:F2} ms, "
                + $">2.08/>4.17/>8.33 ms {over2}/{over4}/{over8}, "
                + $"max update/render {_maximumUpdateMs:F2}/{_maximumRenderMs:F2} ms, "
                + $"CPU {cpuSeconds / wallSeconds * 100:F0}% of one core, "
                + $"RSS {_process.WorkingSet64 / 1048576d:F0} MiB, "
                + $"managed {GC.GetTotalMemory(false) / 1048576d:F0} MiB, "
                + $"GC {GC.CollectionCount(0) - _gen0Start}/"
                + $"{GC.CollectionCount(1) - _gen1Start}/"
                + $"{GC.CollectionCount(2) - _gen2Start}.");
            _frames = 0;
            _maximumUpdateMs = 0;
            _maximumRenderMs = 0;
        }

        public void Flush()
        {
            foreach (var report in _reports)
                Console.Error.WriteLine(report);
            _reports.Clear();
        }

        public void Dispose()
        {
            Flush();
            _process.Dispose();
        }
    }

    private static SdlKeyboardKey? mapKey(SDL_Keycode key) => key switch
    {
        SDL_Keycode.SDLK_BACKSPACE => SdlKeyboardKey.Backspace,
        SDL_Keycode.SDLK_TAB => SdlKeyboardKey.Tab,
        SDL_Keycode.SDLK_DELETE => SdlKeyboardKey.Delete,
        SDL_Keycode.SDLK_RETURN => SdlKeyboardKey.Enter,
        SDL_Keycode.SDLK_ESCAPE => SdlKeyboardKey.Escape,
        SDL_Keycode.SDLK_LSHIFT or SDL_Keycode.SDLK_RSHIFT => SdlKeyboardKey.Shift,
        SDL_Keycode.SDLK_SPACE => SdlKeyboardKey.Space,
        SDL_Keycode.SDLK_PAGEUP => SdlKeyboardKey.PageUp,
        SDL_Keycode.SDLK_PAGEDOWN => SdlKeyboardKey.PageDown,
        SDL_Keycode.SDLK_F1 => SdlKeyboardKey.F1,
        SDL_Keycode.SDLK_F2 => SdlKeyboardKey.F2,
        SDL_Keycode.SDLK_F5 => SdlKeyboardKey.F5,
        SDL_Keycode.SDLK_F8 => SdlKeyboardKey.F8,
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

    public void Dispose()
    {
        if (_disposed)
            return;
        ensureOwnerThread();
        disposeNativeResources();
        _disposed = true;
    }

    private void disposeNativeResources()
    {
        _midi.Dispose();
        foreach (var (_, pad) in _pads)
            if (pad != IntPtr.Zero)
                SDL_CloseGamepad((SDL_Gamepad*)pad);
        Array.Clear(_pads);
        _renderer?.Dispose();
        _renderer = null;
        if (_bgfxInitialized)
        {
            BgfxSupport.Shutdown();
            _bgfxInitialized = false;
        }
        if (_window is not null)
        {
            SDL_DestroyWindow(_window);
            _window = null;
        }
        if (_sdlInitialized)
        {
            SDL_Quit();
            _sdlInitialized = false;
        }
    }

    private void ensureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("SDL window and GPU operations must run on the creating thread.");
    }

    private static InvalidOperationException sdlFailure(string operation)
    {
        var error = SDL_GetError();
        return new InvalidOperationException($"Failed to {operation}: {error}");
    }
}

/// <summary>The display settings <see cref="SdlApplication.ApplyDisplay"/> takes. Width/Height 0: the desktop's; RefreshRate 0: the highest; FpsCap 0: none.</summary>
public readonly record struct DisplaySettings(bool Vsync, bool Fullscreen, bool Exclusive, int Width, int Height, int RefreshRate,
    float LetterboxSize, float LetterboxX, float LetterboxY, int FpsCap = 0);

public readonly record struct SdlRunResult(
    int RenderedFrames,
    int SimulationTicks,
    int DroppedTicks,
    double InterpolationFraction);
