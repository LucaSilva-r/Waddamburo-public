using System.Runtime.CompilerServices;
using Waddamburo.Game.Lumen;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.App.Presentation;

internal readonly record struct SongBrowserTextViewport(float Left, float Right, float Scale);

/// <summary>Optional D-wheel preview. Translates complete cards, including masks and frame anchors.</summary>
internal static class SongBrowserWheelPresentation
{
    private static readonly ConditionalWeakTable<LumenRenderSnapshot, Dictionary<LumenRenderQuad, SongBrowserTextViewport>> Clips = new();

    public static void Remember(LumenRenderSnapshot snapshot, Dictionary<LumenRenderQuad, SongBrowserTextViewport>? offsets)
    {
        if (offsets is { Count: > 0 }) Clips.Add(snapshot, offsets);
    }

    public static IReadOnlyDictionary<LumenRenderQuad, SongBrowserTextViewport>? TextOffsets(LumenRenderSnapshot snapshot) =>
        Clips.TryGetValue(snapshot, out var offsets) ? offsets : null;

    public const string SelectedMarker = "browser-wheel:selected";

    public static IEnumerable<LumenRenderQuad> Move(IEnumerable<LumenRenderQuad> drawing, float centerY,
        SongSelectHostBinding host, Dictionary<LumenRenderQuad, SongBrowserTextViewport>? textOffsets)
    {
        if (!host.CurvedScrolling) return drawing;
        var focus = VerticalSongBrowser.Ease((92 - Math.Abs(centerY - 288)) / 90);
        var offset = SongSelectCircularPath.VerticalOffset(centerY) - 12 * focus;
        var scale = SongSelectCircularPath.PerspectiveScale(centerY);
        const float centerX = 639.45f;
        LumenRenderVertex project(LumenRenderVertex v) => v with
        { X = centerX + (v.X - centerX) * scale + offset, Y = centerY + (v.Y - centerY) * scale };
        return drawing.Select(quad =>
        {
            var moved = quad with { TopLeft = project(quad.TopLeft), TopRight = project(quad.TopRight),
                BottomLeft = project(quad.BottomLeft), BottomRight = project(quad.BottomRight),
                FrameBorderScale = quad.FrameBorderScale * scale };
            if (textOffsets is not null && quad.NativeSurface is { } surface && host.BrowserTextLayout(surface) is not null)
                textOffsets[moved] = new(centerX + (400 - centerX) * scale + offset,
                    centerX + (880 - centerX) * scale + offset, scale);
            return moved;
        });
    }
}
