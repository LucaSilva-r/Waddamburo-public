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
    public static int Hit(float x, float y, float stageAspect = 16f / 9)
    {
        x = Math.Clamp(x, 0, 1);
        y = Math.Clamp(y, 0, 1);
        // In stage heights, so the drum is round on a 16:9 stage.
        var dx = (x - CenterX) * stageAspect;
        var dy = y - CenterY;
        if (x * stageAspect < BackSize && y < BackSize)
            return Back;
        var left = x < CenterX;
        if (dx * dx + dy * dy <= FaceRadius * FaceRadius)
            return left ? 1 : 2;
        return left ? 0 : 3;
    }

    /// <summary>The <see cref="Hit"/> code of the back button.</summary>
    public const int Back = 4;
}
