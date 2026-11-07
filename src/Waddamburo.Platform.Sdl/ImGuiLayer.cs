using System.Numerics;
using ImGuiNET;
using SDL;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.Platform.Sdl;

/// <summary>A single-window ImGui context. Created and disposed on the application's render thread.</summary>
public sealed unsafe class ImGuiLayer : IDisposable
{
    private readonly RenderDevice _renderer;
    private readonly Action _draw;
    private readonly Action _detached;
    private readonly nint _context;
    private readonly RenderTextureId _font;
    private bool _inFrame;
    private bool _disposed;

    internal ImGuiLayer(RenderDevice renderer, Action draw, Action detached)
    {
        _renderer = renderer;
        _draw = draw;
        _detached = detached;
        _context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO();
            io.NativePtr->IniFilename = null; // No imgui.ini in a song or game directory.
            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            ImGui.StyleColorsDark();
            io.Fonts.GetTexDataAsRGBA32(out nint pixels, out int width, out int height);
            _font = renderer.UploadRgba8((uint)width, (uint)height, new ReadOnlySpan<byte>((void*)pixels, checked(width * height * 4)));
            io.Fonts.SetTexID((nint)_font.Value);
            io.Fonts.ClearTexData();
        }
        catch
        {
            ImGui.DestroyContext(_context);
            throw;
        }
    }

    internal void BeginFrame(float deltaSeconds, Vector2 windowSize, Vector2 pixelSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ImGui.SetCurrentContext(_context);
        var io = ImGui.GetIO();
        io.DisplaySize = windowSize;
        io.DisplayFramebufferScale = pixelSize / windowSize;
        io.DeltaTime = Math.Clamp(deltaSeconds, .000001f, .25f);
        ImGui.NewFrame();
        _inFrame = true;
        _draw();
    }

    internal ImDrawDataPtr EndFrame()
    {
        if (!_inFrame || _disposed)
            return default;
        ImGui.SetCurrentContext(_context);
        ImGui.Render();
        _inFrame = false;
        return ImGui.GetDrawData();
    }

    /// <summary>Feeds events before NewFrame. Captured input must not also reach the game.</summary>
    internal bool ProcessEvent(in SDL_Event e)
    {
        ImGui.SetCurrentContext(_context);
        var io = ImGui.GetIO();
        switch ((SDL_EventType)e.type)
        {
            case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                io.AddMousePosEvent(e.motion.x, e.motion.y);
                return io.WantCaptureMouse;
            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                var button = e.button.button switch { 1 => 0, 2 => 2, 3 => 1, 4 => 3, 5 => 4, _ => -1 };
                if (button >= 0)
                    io.AddMouseButtonEvent(button, e.type == (uint)SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN);
                return io.WantCaptureMouse;
            case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                var direction = e.wheel.direction == SDL_MouseWheelDirection.SDL_MOUSEWHEEL_FLIPPED ? -1 : 1;
                io.AddMouseWheelEvent(e.wheel.x * direction, e.wheel.y * direction);
                return io.WantCaptureMouse;
            case SDL_EventType.SDL_EVENT_KEY_DOWN:
            case SDL_EventType.SDL_EVENT_KEY_UP:
                var down = e.type == (uint)SDL_EventType.SDL_EVENT_KEY_DOWN;
                io.AddKeyEvent(ImGuiKey.ModCtrl, (e.key.mod & SDL_Keymod.SDL_KMOD_CTRL) != 0);
                io.AddKeyEvent(ImGuiKey.ModShift, (e.key.mod & SDL_Keymod.SDL_KMOD_SHIFT) != 0);
                io.AddKeyEvent(ImGuiKey.ModAlt, (e.key.mod & SDL_Keymod.SDL_KMOD_ALT) != 0);
                io.AddKeyEvent(ImGuiKey.ModSuper, (e.key.mod & SDL_Keymod.SDL_KMOD_GUI) != 0);
                if (Key(e.key.scancode) is var key && key != ImGuiKey.None)
                    io.AddKeyEvent(key, down);
                return io.WantCaptureKeyboard;
            case SDL_EventType.SDL_EVENT_TEXT_INPUT:
                io.AddInputCharactersUTF8(e.text.GetText());
                return io.WantTextInput || io.WantCaptureKeyboard;
            case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST:
                io.AddFocusEvent(false);
                break;
            case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED:
                io.AddFocusEvent(true);
                break;
            case SDL_EventType.SDL_EVENT_WINDOW_MOUSE_LEAVE:
                io.AddMousePosEvent(-float.MaxValue, -float.MaxValue);
                break;
        }
        return false;
    }

    // SDL scancodes use USB HID ordering, independent of the keyboard's text layout.
    private static ImGuiKey Key(SDL_Scancode scan) => scan switch
    {
        >= SDL_Scancode.SDL_SCANCODE_A and <= SDL_Scancode.SDL_SCANCODE_Z => ImGuiKey.A + (scan - SDL_Scancode.SDL_SCANCODE_A),
        >= SDL_Scancode.SDL_SCANCODE_1 and <= SDL_Scancode.SDL_SCANCODE_9 => ImGuiKey._1 + (scan - SDL_Scancode.SDL_SCANCODE_1),
        SDL_Scancode.SDL_SCANCODE_0 => ImGuiKey._0,
        >= SDL_Scancode.SDL_SCANCODE_F1 and <= SDL_Scancode.SDL_SCANCODE_F12 => ImGuiKey.F1 + (scan - SDL_Scancode.SDL_SCANCODE_F1),
        SDL_Scancode.SDL_SCANCODE_TAB => ImGuiKey.Tab,
        SDL_Scancode.SDL_SCANCODE_RETURN => ImGuiKey.Enter,
        SDL_Scancode.SDL_SCANCODE_ESCAPE => ImGuiKey.Escape,
        SDL_Scancode.SDL_SCANCODE_SPACE => ImGuiKey.Space,
        SDL_Scancode.SDL_SCANCODE_BACKSPACE => ImGuiKey.Backspace,
        SDL_Scancode.SDL_SCANCODE_DELETE => ImGuiKey.Delete,
        SDL_Scancode.SDL_SCANCODE_INSERT => ImGuiKey.Insert,
        SDL_Scancode.SDL_SCANCODE_HOME => ImGuiKey.Home,
        SDL_Scancode.SDL_SCANCODE_END => ImGuiKey.End,
        SDL_Scancode.SDL_SCANCODE_PAGEUP => ImGuiKey.PageUp,
        SDL_Scancode.SDL_SCANCODE_PAGEDOWN => ImGuiKey.PageDown,
        SDL_Scancode.SDL_SCANCODE_LEFT => ImGuiKey.LeftArrow,
        SDL_Scancode.SDL_SCANCODE_RIGHT => ImGuiKey.RightArrow,
        SDL_Scancode.SDL_SCANCODE_UP => ImGuiKey.UpArrow,
        SDL_Scancode.SDL_SCANCODE_DOWN => ImGuiKey.DownArrow,
        SDL_Scancode.SDL_SCANCODE_LCTRL => ImGuiKey.LeftCtrl,
        SDL_Scancode.SDL_SCANCODE_RCTRL => ImGuiKey.RightCtrl,
        SDL_Scancode.SDL_SCANCODE_LSHIFT => ImGuiKey.LeftShift,
        SDL_Scancode.SDL_SCANCODE_RSHIFT => ImGuiKey.RightShift,
        SDL_Scancode.SDL_SCANCODE_LALT => ImGuiKey.LeftAlt,
        SDL_Scancode.SDL_SCANCODE_RALT => ImGuiKey.RightAlt,
        SDL_Scancode.SDL_SCANCODE_LGUI => ImGuiKey.LeftSuper,
        SDL_Scancode.SDL_SCANCODE_RGUI => ImGuiKey.RightSuper,
        SDL_Scancode.SDL_SCANCODE_MINUS => ImGuiKey.Minus,
        SDL_Scancode.SDL_SCANCODE_EQUALS => ImGuiKey.Equal,
        SDL_Scancode.SDL_SCANCODE_LEFTBRACKET => ImGuiKey.LeftBracket,
        SDL_Scancode.SDL_SCANCODE_RIGHTBRACKET => ImGuiKey.RightBracket,
        SDL_Scancode.SDL_SCANCODE_BACKSLASH => ImGuiKey.Backslash,
        SDL_Scancode.SDL_SCANCODE_SEMICOLON => ImGuiKey.Semicolon,
        SDL_Scancode.SDL_SCANCODE_APOSTROPHE => ImGuiKey.Apostrophe,
        SDL_Scancode.SDL_SCANCODE_GRAVE => ImGuiKey.GraveAccent,
        SDL_Scancode.SDL_SCANCODE_COMMA => ImGuiKey.Comma,
        SDL_Scancode.SDL_SCANCODE_PERIOD => ImGuiKey.Period,
        SDL_Scancode.SDL_SCANCODE_SLASH => ImGuiKey.Slash,
        _ => ImGuiKey.None,
    };

    public void Dispose()
    {
        if (_disposed) return;
        ImGui.SetCurrentContext(_context);
        if (_inFrame) ImGui.EndFrame();
        _renderer.ReleaseTexture(_font);
        ImGui.DestroyContext(_context);
        _disposed = true;
        _detached();
    }
}
