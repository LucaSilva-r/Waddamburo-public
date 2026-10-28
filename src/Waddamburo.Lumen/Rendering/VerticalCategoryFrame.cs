namespace Waddamburo.Lumen.Rendering;

/// <summary>Native coordinates of a frame whose corners and border thickness stay fixed in screen pixels.</summary>
public static class VerticalCategoryFrame
{
    public const float Top = -139;
    public const float Left = 16;
    public const float Right = 560;
    public const float VerticalScale = .65f;
    public const float HorizontalScale = 1.1f;

    // Measured from the native gold selection template, including its four-pixel
    // rim. Opaque fringes differ between category skins; raw slice bounds do not.
    public const float PreviewHeight = 234;
    public const float PreviewWidth = 525.9f;
    public const float PreviewCenterX = 639.45f;

    public static LumenNativeSurfacePlacement FitPreview(LumenNativeSurfacePlacement raw, LumenNativeSurfacePlacement visible)
    {
        var amount = Math.Clamp((raw.Height - 58.35f) / (PreviewHeight - 58.35f), 0, 1);
        if (amount < .0001f) return visible;
        var target = new LumenNativeSurfacePlacement(PreviewCenterX - PreviewWidth / 2, raw.Y + .4f,
            PreviewWidth, raw.Height - .8f);
        return new(visible.X + (target.X - visible.X) * amount, visible.Y + (target.Y - visible.Y) * amount,
            visible.Width + (target.Width - visible.Width) * amount, visible.Height + (target.Height - visible.Height) * amount);
    }

    public static CategoryFrameSlice Slice(int row, int column, float screenHeight,
        float top = Top, float left = Left, float right = Right)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(row, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(column, 2);
        // The native top strip has five black pixels; the bottom strip has six.
        // Both become four screen pixels. The coloured bevel remains part of each slice.
        var bottom = top + Math.Max(screenHeight, 48) / VerticalScale;
        float[] xs = [top, top + 26 * .8f / VerticalScale, bottom - 25 * (4f / 6) / VerticalScale, bottom];
        float[] ys = [left, left + 22 * .8f / HorizontalScale, right - 22 * .8f / HorizontalScale, right];
        return new((xs[row] + xs[row + 1]) / 2, (ys[column] + ys[column + 1]) / 2,
            xs[row + 1] - xs[row], ys[column + 1] - ys[column]);
    }
}

public readonly record struct CategoryFrameSlice(float X, float Y, float Width, float Height);
