using System.Diagnostics;
using SDL;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.Platform.Sdl.Timing;
using static SDL.SDL3;

namespace Waddamburo.Platform.Sdl;

// The game loop: events, input delivery, frame pacing, presenting, and the frame diagnostics.
public sealed unsafe partial class SdlApplication
{
    private volatile bool _quitRequested;
    private bool _discardElapsed;

    /// <summary>
    /// The current frame stalled on purpose (a scene load): the next frame starts timing from its
    /// end instead of catching the missed ticks up in a burst (the screen jumps otherwise).
    /// </summary>
    public void DiscardElapsed() => _discardElapsed = true;

    /// <summary>Ends the running loop after the current frame; callable from any thread.</summary>
    public void RequestQuit() => _quitRequested = true;

    /// <summary>Hears the next presented frame once (the screenshot button), then clears itself.</summary>
    public Action<RenderCapture>? CaptureNextFrame { get; set; }

    /// <summary>
    /// Outside <see cref="Run(Func{double, RenderFrame}, Action{SdlKeyboardSnapshot}, int?, int?, Action{RenderCapture}?, Action{SdlKeyboardSnapshot}?, Func{bool}?, Action?, Action{float, float}?, Func{bool}?, Action{float, float}?, Action?, Action{SdlFrameMetrics}?, Action?, Action{float}?)"/>
    /// (the boot screen while the game loads, or its error): the window's events are handled and <paramref name="frame"/>
    /// shown when the renderer is ready for it. False once the window was closed; <paramref name="escape"/>: Escape
    /// was pressed.
    /// </summary>
    public bool PumpAndPresent(RenderFrame frame, out bool escape) => PumpAndPresent(frame, out escape, null);

    /// <summary>As above; <paramref name="menuKeys"/> gets the menu keys pressed (arrows and Enter, a controller's
    /// d-pad and A; its B or Start is Escape), for a question on the boot screen.</summary>
    public bool PumpAndPresent(RenderFrame frame, out bool escape, List<SdlKeyboardKey>? menuKeys)
    {
        ensureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        escape = false;
        var open = true;
        SDL_Event currentEvent;
        while (SDL_PollEvent(&currentEvent))
        {
            if (currentEvent.type is (uint)SDL_EventType.SDL_EVENT_QUIT or (uint)SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED)
                open = false;
            else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_KEY_DOWN && currentEvent.key.key == SDL_Keycode.SDLK_ESCAPE)
                escape = true;
            else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_KEY_DOWN && !currentEvent.key.repeat && menuKeys is not null
                && mapKey(currentEvent.key.key) is (SdlKeyboardKey.Up or SdlKeyboardKey.Down or SdlKeyboardKey.Left
                    or SdlKeyboardKey.Right or SdlKeyboardKey.Enter) and var key)
                menuKeys.Add(key);
            else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN && menuKeys is not null)
            {
                switch ((SDL_GamepadButton)currentEvent.gbutton.button)
                {
                    case SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP: menuKeys.Add(SdlKeyboardKey.Up); break;
                    case SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN: menuKeys.Add(SdlKeyboardKey.Down); break;
                    case SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH: menuKeys.Add(SdlKeyboardKey.Enter); break;
                    case SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST or SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START: escape = true; break;
                }
            }
            // SDL announces the controllers already connected at start as added: they arrive during the boot screen.
            else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_GAMEPAD_ADDED)
                addPad(currentEvent.gdevice.which);
        }
        if (_renderer!.TryAcquireSwapchain())
            _renderer.Present(frame);
        return open;
    }

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

    private bool _focusLost;

    /// <summary>
    /// Whether focus was lost since the last call, even if it is back: a Switch suspended by its HOME menu delivers
    /// the loss and the return in one poll, and the song never paused.
    /// </summary>
    public bool TakeFocusLoss()
    {
        var lost = _focusLost;
        _focusLost = false;
        return lost;
    }

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
            if (input.Kind is SdlInputKind.PadButton or SdlInputKind.PadTrigger
                || input is { Kind: SdlInputKind.Key, Code: not (int)SDL_Scancode.SDL_SCANCODE_AC_BACK })
                TouchActive = false; // played with keys or a controller: the drum gets out of the way
            if (inputDown(input) is not { } key)
                return;
            if (key == SdlKeyboardKey.F5)
                togglePerformanceOverlay?.Invoke();
            else
                pendingPresses.Add(new SdlKeyPress(key, TimeSpan.FromTicks(checked((long)(timestamp / 100)))));
        }
        SystemPriority.FavourThread(); // the game thread paces the frames
        var hitchTrace = DiagnosticSwitches.HitchTrace;
        long previousFrameEnd = 0;
        (int Gen0, int Gen1, int Gen2, TimeSpan Pause) hitchGc = default;
        using var frameProfile = DiagnosticSwitches.GameplayFrameProfile
            ? new FrameProfile() : null;
        var previousProfileEligible = false;
        var windowFocused = true;
        var inspecting = false; // F4: a click inspects, it does not decide
        _quitRequested = false;
        while (running && !_quitRequested && (frameLimit is null || renderedFrames < frameLimit))
        {
            if (Interlocked.Exchange(ref s_surfaceReady, 0) == 1)
            {
                BgfxSupport.SurfaceReturned();
                _renderer!.SurfaceLost();
            }
            while (_injected.TryDequeue(out var injected))
                pendingPresses.Add(new SdlKeyPress(injected, TimeSpan.FromTicks(checked((long)(SDL_GetTicksNS() / 100)))));
            var performanceActive = performanceVisible?.Invoke() == true;
            var inputStart = performanceActive ? Stopwatch.GetTimestamp() : 0;
            _pixelSize = null; // asked again this frame (a resize, a display change)
            SDL_Event currentEvent;
            while (SDL_PollEvent(&currentEvent))
            {
                // F11 (fullscreen) before any UI: the effects editor's panels take the keys under the mouse.
                if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_KEY_DOWN && currentEvent.key.key == SDL_Keycode.SDLK_F11 && !bound(currentEvent.key.scancode))
                {
                    // F11: borderless desktop fullscreen <-> window, like a browser.
                    if (!currentEvent.key.repeat)
                        SDL_SetWindowFullscreen(_window, (SDL_GetWindowFlags(_window) & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) == 0);
                    continue;
                }
                if (_renderer!.ImGuiLayer?.ProcessEvent(currentEvent) == true)
                {
                    // A key pressed before UI capture must still be released in game state.
                    if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_KEY_UP)
                        inputUp(new SdlInput(SdlInputKind.Key, (int)currentEvent.key.scancode));
                    continue;
                }
                if (currentEvent.type is (uint)SDL_EventType.SDL_EVENT_QUIT
                    or (uint)SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED)
                {
                    running = false;
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST)
                {
                    _heldInputs.Clear();
                    _fingers.Clear();
                    _pressedKeys.Clear();
                    windowFocused = false;
                    Focused = false;
                    _focusLost = true;
                    Recorder?.Event("window lost focus");
                    foreach (var pad in _pads) // a rumble playing stops at once
                        if (pad.Pad != IntPtr.Zero)
                            SDL_RumbleGamepad((SDL_Gamepad*)pad.Pad, 0, 0, 0);
                    if (pointerReleased is not null)
                    {
                        SDL_CaptureMouse(false);
                        pointerReleased();
                    }
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED)
                {
                    windowFocused = Focused = true;
                    Recorder?.Event("window focused");
                    _focusedAt = currentEvent.window.timestamp;
                }
                // The split touch drum's editor takes the mouse too (a PC with a touch screen, or trying it out).
                else if (TouchDrum.Editing && currentEvent.type is (uint)SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN
                    or (uint)SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP or (uint)SDL_EventType.SDL_EVENT_MOUSE_MOTION
                    && currentEvent.button.which != SDL_TOUCH_MOUSEID)
                {
                    int width, height;
                    var motion = currentEvent.type == (uint)SDL_EventType.SDL_EVENT_MOUSE_MOTION;
                    if ((!motion || (currentEvent.motion.state & SDL_MouseButtonFlags.SDL_BUTTON_LMASK) != 0)
                        && SDL_GetWindowSize(_window, &width, &height) && width > 0 && height > 0)
                        editTouchDrum(MouseFinger, (motion ? currentEvent.motion.x : currentEvent.button.x) / width,
                            (motion ? currentEvent.motion.y : currentEvent.button.y) / height,
                            currentEvent.type == (uint)SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN,
                            currentEvent.type == (uint)SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP);
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_MOUSE_MOTION && pointerMoved is not null)
                {
                    int width, height;
                    if (SDL_GetWindowSize(_window, &width, &height) && width > 0 && height > 0)
                        pointerMoved(currentEvent.motion.x / width, currentEvent.motion.y / height);
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_TEXT_INPUT)
                    _typed.Append(currentEvent.text.GetText());
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN
                    && currentEvent.button.button == SDL_BUTTON_LEFT)
                {
                    // In menus a left click is a don (player 1's left don key: decide everywhere menus are).
                    // Not the click that brings the window up: some systems send it before the focus, others just after.
                    if (_menuInput && MouseControls && !_capturing && !inspecting && windowFocused && currentEvent.button.timestamp > _focusedAt + FocusClickGraceNs)
                        pendingPresses.Add(new SdlKeyPress(SdlInputBindings.Pads[ClickPad],
                            TimeSpan.FromTicks(checked((long)(currentEvent.button.timestamp / 100)))));
                    if (pointerClicked is null)
                        continue;
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
                    && currentEvent.key.key == SDL_Keycode.SDLK_F4 && toggleInspect is not null && !bound(currentEvent.key.scancode))
                {
                    // F4: inspect mode (a left click lists what is drawn under the pointer).
                    if (!currentEvent.key.repeat)
                    {
                        toggleInspect();
                        inspecting = !inspecting;
                        _ = SDL_CursorVisible() ? SDL_HideCursor() : SDL_ShowCursor();
                    }
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_MOUSE_WHEEL)
                {
                    // Tools receive precise wheel deltas; game menus translate each notch into a key press.
                    // ponytail: the menus read one press per key and tick, so a flick faster than 60 notches a second loses some.
                    if (pointerScrolled is not null && !_capturing)
                        pointerScrolled(currentEvent.wheel.y);
                    else if (_menuInput && MouseControls && !_capturing && currentEvent.wheel.y != 0)
                        for (var notch = 0; notch < Math.Max(1, (int)Math.Abs(currentEvent.wheel.y)); notch++)
                            pendingPresses.Add(new SdlKeyPress(currentEvent.wheel.y > 0 ? SdlKeyboardKey.WheelUp : SdlKeyboardKey.WheelDown,
                                TimeSpan.FromTicks(checked((long)(currentEvent.wheel.timestamp / 100)))));
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_KEY_DOWN
                    && currentEvent.key.key == SDL_Keycode.SDLK_F9 && !bound(currentEvent.key.scancode))
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
                    // Android turns a controller's unhandled B into its Back key as well: with a controller connected the
                    // system Back is ignored (B played a ka and opened the menu at once); Start and the touch Back remain.
                    if (currentEvent.key.scancode == SDL_Scancode.SDL_SCANCODE_AC_BACK && _pads.Any(static pad => pad.Pad != IntPtr.Zero))
                        continue;
                    if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_KEY_UP)
                        inputUp(input);
                    else if (!currentEvent.key.repeat)
                        press(input, currentEvent.key.timestamp);
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_FINGER_DOWN
                    && editTouchDrum((ulong)currentEvent.tfinger.fingerID, currentEvent.tfinger.x, currentEvent.tfinger.y, true, false))
                    TouchActive = true;
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_FINGER_MOTION)
                    editTouchDrum((ulong)currentEvent.tfinger.fingerID, currentEvent.tfinger.x, currentEvent.tfinger.y, false, false);
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_FINGER_DOWN)
                {
                    TouchActive = true;
                    var code = touchCode(currentEvent.tfinger.x, currentEvent.tfinger.y);
                    // The buttons always answer (a player without a controller must reach the menus); the drum may be off.
                    if (code < 0 || !OverlayOpen && code < TouchDrum.Back && !TouchDrumEnabled)
                        continue;
                    if (code == TouchDrum.Keyboard)
                    {
                        TouchHits[code] = currentEvent.tfinger.timestamp;
                        toggleScreenKeyboard();
                        continue;
                    }
                    var slot = 1;
                    while (_fingers.Values.Any(held => held.Device == slot))
                        slot++;
                    var input = new SdlInput(SdlInputKind.Touch, code, slot);
                    _fingers[currentEvent.tfinger.fingerID] = input;
                    TouchHits[code] = currentEvent.tfinger.timestamp;
                    press(input, currentEvent.tfinger.timestamp);
                }
                else if (currentEvent.type is (uint)SDL_EventType.SDL_EVENT_FINGER_UP or (uint)SDL_EventType.SDL_EVENT_FINGER_CANCELED)
                {
                    TouchDrum.Release((ulong)currentEvent.tfinger.fingerID);
                    if (_fingers.Remove(currentEvent.tfinger.fingerID, out var input))
                        inputUp(input);
                }
                else if (currentEvent.type == (uint)SDL_EventType.SDL_EVENT_GAMEPAD_ADDED)
                    addPad(currentEvent.gdevice.which);
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
            repeatHeld(pendingPresses);
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
            // Event pumping, frame pacing and the hitch log itself: what no other part of the frame line covers.
            var betweenFrames = previousFrameEnd == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(previousFrameEnd, timestamp);
            previousTimestamp = timestamp;
            var remainingTicks = tickLimit is int limit ? limit - simulationTicks : int.MaxValue;
            var eventTime = TimeSpan.FromTicks(checked((long)(SDL_GetTicksNS() / 100)));
            if (_renderer!.ImGuiLayer is { } gui)
            {
                int logicalWidth, logicalHeight;
                SDL_GetWindowSize(_window, &logicalWidth, &logicalHeight);
                var (pixelWidth, pixelHeight) = GetPixelSize();
                gui.BeginFrame((float)elapsed.TotalSeconds,
                    new(Math.Max(1, logicalWidth), Math.Max(1, logicalHeight)),
                    new(Math.Max(1, pixelWidth), Math.Max(1, pixelHeight)));
            }
            var keyboard = new SdlKeyboardSnapshot(_pressedKeys, pendingPresses, eventTime);
            var frameInputStart = Stopwatch.GetTimestamp();
            updateFrame?.Invoke(keyboard);
            var frameInputTime = Stopwatch.GetElapsedTime(frameInputStart);
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
            var captureRequest = CaptureNextFrame;
            CaptureNextFrame = null;
            var interpolationFraction = reachedTickLimit ? 1d : clock.InterpolationFraction;
            var renderStart = Stopwatch.GetTimestamp();
            var capture = _renderer!.Present(createFrame(interpolationFraction), shouldCapture || captureRequest is not null);
            if (capture is not null)
                captureRequest?.Invoke(capture);
            if (_discardElapsed)
            {
                _discardElapsed = false;
                previousTimestamp = Stopwatch.GetTimestamp();
            }
            // Diagnostic: WADDAMBURO_HITCH_TRACE=1 reports which part of a slow frame took the time.
            var updateTime = Stopwatch.GetElapsedTime(updateStart, renderStart);
            var renderTime = Stopwatch.GetElapsedTime(renderStart);
            previousFrameEnd = Stopwatch.GetTimestamp();
            var profileEligibleAtEnd = frameProfile is not null && windowFocused && (profileFrame?.Invoke() ?? true);
            if (performanceActive)
                performanceSample?.Invoke(new SdlFrameMetrics(elapsed, inputTime, updateTime, renderTime, update.ExecutedTicks));
            // A frame that can cost a screen refresh (the note lane visibly jumps): longer than the refresh interval
            // when uncapped, 1.5 intervals with vsync (its frames are one interval give or take).
            var hitchAt = Recorder is not null || hitchTrace ? refreshInterval() * (_display?.Vsync == true ? 1.5 : 1) : TimeSpan.MaxValue;
            Recorder?.Record(elapsed, updateTime, renderTime, update.ExecutedTicks, gpuStats, missedRefresh: elapsed > hitchAt);
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
                // The whole frame, too: a stall in present or a descheduled thread is in neither update nor render.
                if (frameInputTime + updateTime + renderTime > hitchAt || elapsed > hitchAt)
                    Console.Error.WriteLine($"Frame hitch at tick {simulationTicks}: frame {elapsed.TotalMilliseconds:F0} ms, "
                        + $"between frames {betweenFrames.TotalMilliseconds:F0} ms, input {frameInputTime.TotalMilliseconds:F0} ms (judged per frame), update {updateTime.TotalMilliseconds:F0} ms "
                        + $"({update.ExecutedTicks} ticks), render {renderTime.TotalMilliseconds:F0} ms; GC since last "
                        + $"{gen0 - hitchGc.Gen0}/{gen1 - hitchGc.Gen1}/{gen2 - hitchGc.Gen2} (gen0/1/2), paused {(pause - hitchGc.Pause).TotalMilliseconds:F0} ms"
                        + $"{gpuStats()}.");
                hitchGc = (gen0, gen1, gen2, pause);
                // Every 300 frames, a hitch or not: where the GPU's time goes in a scene that only runs slow.
                if (renderedFrames % 300 == 0)
                    Console.Error.WriteLine($"GPU sample at tick {simulationTicks}{gpuStats()}.");
            }
            if (capture is not null && shouldCapture)
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

    /// <summary>The session's performance report, when one is being recorded: every frame goes to it.</summary>
    public PerformanceRecorder? Recorder { get; set; }
}

public readonly record struct SdlRunResult(
    int RenderedFrames,
    int SimulationTicks,
    int DroppedTicks,
    double InterpolationFraction);
