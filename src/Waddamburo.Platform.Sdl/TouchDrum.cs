namespace Waddamburo.Platform.Sdl;

/// <summary>
/// The on-screen drum for touch screens, in stage coordinates (0-1 across the 1280x720 stage): a half drum at the
/// bottom centre. Its face is don (left or right half), anything else is ka (by the side of the screen's centre), and
/// the corner button at the top left is Escape (the menu). The overlay draws this same geometry.
/// </summary>
public static class TouchDrum
{
    /// <summary>Drum face: centre at the stage's bottom edge.</summary>
    public const float CenterX = .5f, CenterY = 1f;

    /// <summary>The face's radius in stage heights (config.cfg touch_drum_size scales the default .42).</summary>
    public static float FaceRadius { get; set; } = .42f;

    /// <summary>
    /// Split layout (config.cfg touch_drum_layout = split): two circles anywhere, the left one the left don and the
    /// right one the right don; anywhere else is ka, on the side of the screen the touch is on.
    /// </summary>
    public static bool Split { get; set; }

    /// <summary>The split layout's circles (left don, right don): centre in stage coordinates (0-1), radius in stage heights.</summary>
    public static (float X, float Y, float R)[] Dons { get; set; } = [(.3f, .72f, .2f), (.7f, .72f, .2f)];

    /// <summary>The split circles are being moved (Settings › Controls › Edit Touch Drums): touches drag them, the back
    /// button ends it.</summary>
    public static bool Editing { get; set; }

    // Fingers (or the mouse) holding a circle: which, and the offset from its centre (or resizing it by its knob).
    private static readonly Dictionary<ulong, (int Circle, bool Resize, float Dx, float Dy)> _held = [];

    /// <summary>The resize knob on a circle's right edge (stage coordinates).</summary>
    public static (float X, float Y) Knob(int circle, float stageAspect = 16f / 9) =>
        (Dons[circle].X + Dons[circle].R / stageAspect, Dons[circle].Y);

    /// <summary>Editing: a finger went down; it takes the knob or the circle under it, if any.</summary>
    public static void Grab(ulong finger, float x, float y, float stageAspect = 16f / 9)
    {
        for (var circle = Dons.Length - 1; circle >= 0; circle--)
        {
            var (kx, ky) = Knob(circle, stageAspect);
            if (distance(x - kx, y - ky, stageAspect) < KnobRadius * 1.5f)
            {
                _held[finger] = (circle, true, 0, 0);
                return;
            }
        }
        for (var circle = Dons.Length - 1; circle >= 0; circle--)
            if (distance(x - Dons[circle].X, y - Dons[circle].Y, stageAspect) <= Dons[circle].R)
            {
                _held[finger] = (circle, false, x - Dons[circle].X, y - Dons[circle].Y);
                return;
            }
    }

    /// <summary>Editing: a held finger moved.</summary>
    public static void Drag(ulong finger, float x, float y, float stageAspect = 16f / 9)
    {
        if (!_held.TryGetValue(finger, out var held))
            return;
        var (cx, cy, r) = Dons[held.Circle];
        Dons[held.Circle] = held.Resize
            ? (cx, cy, Math.Clamp(distance(x - cx, y - cy, stageAspect), .06f, .5f))
            : (Math.Clamp(x - held.Dx, 0, 1), Math.Clamp(y - held.Dy, 0, 1), r);
    }

    public static void Release(ulong finger) => _held.Remove(finger);

    /// <summary>The knob's radius in stage heights.</summary>
    public const float KnobRadius = .035f;

    // In stage heights, so circles are round on a 16:9 stage.
    private static float distance(float dx, float dy, float stageAspect) => MathF.Sqrt(dx * stageAspect * dx * stageAspect + dy * dy);

    /// <summary>The back button: a square in the top-left corner, in stage heights.</summary>
    public const float BackSize = .11f;

    /// <summary>What a touch at the stage point presses: 0-3 player 1's pads (left ka, left don, right don, right
    /// ka), <see cref="Back"/> Escape. Points outside the stage (letterbox bars) count from the nearest edge.</summary>
    public static int Hit(float x, float y, float stageAspect = 16f / 9, bool search = false, bool notices = false)
    {
        x = Math.Clamp(x, 0, 1);
        y = Math.Clamp(y, 0, 1);
        // In stage heights, so the drum is round on a 16:9 stage.
        var dx = (x - CenterX) * stageAspect;
        var dy = y - CenterY;
        if (x * stageAspect < BackSize && y < BackSize)
            return Back;
        if (notices && x * stageAspect < 2 * BackSize && y < BackSize)
            return Notices;
        if (search && (1 - x) * stageAspect < SearchSize && y < SearchSize)
            return Search;
        var left = x < CenterX;
        if (Split)
        {
            for (var circle = 0; circle < Dons.Length; circle++)
                if (distance(x - Dons[circle].X, y - Dons[circle].Y, stageAspect) <= Dons[circle].R)
                    return circle == 0 ? 1 : 2;
            return left ? 0 : 3;
        }
        if (dx * dx + dy * dy <= FaceRadius * FaceRadius)
            return left ? 1 : 2;
        return left ? 0 : 3;
    }

    /// <summary>The <see cref="Hit"/> code of the back button (Escape).</summary>
    public const int Back = 4;

    /// <summary>The menus' touch buttons' codes (<see cref="HitMenu"/>): the Up and Down arrows and Enter.</summary>
    public const int Up = 5, Down = 6, Ok = 7, Search = 8, Keyboard = 9, Notices = 10;

    /// <summary>The first code of the scene's own touch keys (<see cref="TouchKey"/>): KeyBase + its index.</summary>
    public const int KeyBase = 11, MaxKeys = 12, Codes = KeyBase + MaxKeys;

    /// <summary>The screen keyboard's show/hide button while a text field is open (search), above the menu buttons: the
    /// keyboard covers the screen's lower half.</summary>
    public static readonly (float X, float Y, float Width, float Height) KeyboardButton =
        (1110 / 1280f, 75 / 720f, 150 / 1280f, 120 / 720f);

    // The notices button (E): the same square beside the back button, outside songs (Notices).

    /// <summary>Song select's search button (Tab): a square in the top-right corner, in stage heights from that corner.</summary>
    public const float SearchSize = BackSize;

    /// <summary>The menus' touch buttons (stage coordinates, 0-1), a column at the right edge, while a settings or pause
    /// menu is open: the drum would cover its rows.</summary>
    public static readonly (int Code, float X, float Y, float Width, float Height)[] MenuButtons =
    [
        (Up, 1110 / 1280f, 210 / 720f, 150 / 1280f, 120 / 720f),
        (Ok, 1110 / 1280f, 345 / 720f, 150 / 1280f, 120 / 720f),
        (Down, 1110 / 1280f, 480 / 720f, 150 / 1280f, 120 / 720f),
    ];

    /// <summary>What a touch at the stage point presses while a menu is open: <see cref="Back"/>, a
    /// <see cref="MenuButtons"/> code, or -1 (nothing).</summary>
    public static int HitMenu(float x, float y, float stageAspect = 16f / 9, bool keyboard = false)
    {
        if (x * stageAspect < BackSize && y < BackSize)
            return Back;
        var (kx, ky, kw, kh) = KeyboardButton;
        if (keyboard && x >= kx && x < kx + kw && y >= ky && y < ky + kh)
            return Keyboard;
        foreach (var (code, left, top, width, height) in MenuButtons)
            if (x >= left && x < left + width && y >= top && y < top + height)
                return code;
        return -1;
    }
}

/// <summary>
/// A scene's own touch button (the scores list, replays, practice): a touch on it presses <see cref="Key"/>, held while the
/// finger stays (held arrows scrub). The rectangle is in stage coordinates (0-1); <see cref="Label"/> is drawn on it.
/// </summary>
public readonly record struct TouchKey(SdlKeyboardKey Key, string Label, float X, float Y, float Width, float Height)
{
    public bool Contains(float x, float y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}
