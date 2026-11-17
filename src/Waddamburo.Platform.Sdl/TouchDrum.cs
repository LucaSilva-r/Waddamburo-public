namespace Waddamburo.Platform.Sdl;

/// <summary>
/// The on-screen drum for touch screens, in stage coordinates (0-1 across the 1280x720 stage): a half drum at the
/// bottom centre. Its face is don (left or right half), anything else is ka (by the side of the screen's centre), and
/// the corner button at the top left is Escape (the menu). The overlay draws this same geometry.
/// </summary>
public static class TouchDrum
{
    /// <summary>Drum face: centre at the stage's bottom edge, radius in stage heights.</summary>
    public const float CenterX = .5f, CenterY = 1f, FaceRadius = .42f;

    /// <summary>The rim drawn around the face (ka is everywhere outside the face, not only on the rim).</summary>
    public const float RimRadius = .52f;

    /// <summary>The back button: a square in the top-left corner, in stage heights.</summary>
    public const float BackSize = .11f;

    /// <summary>What a touch at the stage point presses: 0-3 player 1's pads (left ka, left don, right don, right
    /// ka), <see cref="Back"/> Escape. Points outside the stage (letterbox bars) count from the nearest edge.</summary>
    public static int Hit(float x, float y, float stageAspect = 16f / 9, bool search = false)
    {
        x = Math.Clamp(x, 0, 1);
        y = Math.Clamp(y, 0, 1);
        // In stage heights, so the drum is round on a 16:9 stage.
        var dx = (x - CenterX) * stageAspect;
        var dy = y - CenterY;
        if (x * stageAspect < BackSize && y < BackSize)
            return Back;
        if (search && (1 - x) * stageAspect < SearchSize && y < SearchSize)
            return Search;
        var left = x < CenterX;
        if (dx * dx + dy * dy <= FaceRadius * FaceRadius)
            return left ? 1 : 2;
        return left ? 0 : 3;
    }

    /// <summary>The <see cref="Hit"/> code of the back button (Escape).</summary>
    public const int Back = 4;

    /// <summary>The menus' touch buttons' codes (<see cref="HitMenu"/>): the Up and Down arrows and Enter.</summary>
    public const int Up = 5, Down = 6, Ok = 7, Search = 8, Keyboard = 9, Codes = 10;

    /// <summary>The screen keyboard's show/hide button while a text field is open (search), above the menu buttons: the
    /// keyboard covers the screen's lower half.</summary>
    public static readonly (float X, float Y, float Width, float Height) KeyboardButton =
        (1110 / 1280f, 75 / 720f, 150 / 1280f, 120 / 720f);

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
