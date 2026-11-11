using System.Numerics;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.Editor;

public readonly record struct EditorPanel(float X, float Y, float Width, float Height)
{
    public Vector2 Position => new(X, Y);
    public Vector2 Size => new(Width, Height);
    public RenderRectangle Normalized(float width, float height) => new(X / width, Y / height, Width / width, Height / height);
}

/// <summary>Panels remain attached to the window edges; dividers resize their shared preview space.</summary>
public readonly record struct EditorLayout(EditorPanel Toolbar, EditorPanel Targets, EditorPanel Inspector,
    EditorPanel Stage, EditorPanel Tracks)
{
    public static EditorPanel Preview(EditorPanel stage) => stage with { Y = stage.Y + 37, Height = Math.Max(1, stage.Height - 37) };
    public static EditorLayout Calculate(float width, float height, float left = .18f, float right = .22f, float bottom = .34f, float? topHeight = null)
    {
        if (!float.IsFinite(width) || !float.IsFinite(height) || width < 32 || height < 32)
            throw new ArgumentOutOfRangeException(nameof(width));
        left = Math.Clamp(left, .08f, .45f);
        right = Math.Clamp(right, .1f, .45f);
        bottom = Math.Clamp(bottom, .12f, .75f);
        var top = Math.Min(topHeight ?? 94, height * .25f);
        var y = height * (1 - bottom);
        var x1 = width * left;
        var x2 = width * (1 - right);
        var gap = Math.Min(4, Math.Min(width, height) * .01f);
        return new(new(0, 0, width, top), new(0, top, x1 - gap, y - top),
            new(x2 + gap, top, width - x2 - gap, y - top), new(x1 + gap, top + gap, x2 - x1 - 2 * gap, y - top - 2 * gap),
            new(0, y + gap, width, height - y - gap));
    }

    public static float Snap(float fraction, float home) => Math.Abs(fraction - home) < .012f ? home : fraction;
}
