using Waddamburo.Game.Lumen;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;

namespace Waddamburo.App.Presentation;

internal static class SongTitleMarqueePresentation
{
    public static LumenRenderSnapshot Compose(LumenRenderSnapshot scene, SongSelectHostBinding host, float interpolation,
        IReadOnlyDictionary<LumenRenderQuad, SongBrowserTextViewport>? textOffsets = null, bool scrollTitles = true)
    {
        if (scrollTitles) textOffsets ??= SongBrowserWheelPresentation.TextOffsets(scene);
        var drawing = new List<LumenRenderQuad>();
        var masks = new Stack<(int Id, LumenNativeSurfacePlacement Box)>();
        var waves = new HashSet<(LumenNativeSurfacePlacement Clip, LumenNativeSurfacePlacement Field, byte Depth, float Alpha)>();
        var nextMask = 0;
        foreach (var quad in scene.Quads)
        {
            if (quad.MaskOperation == LumenRenderMaskOperation.Push)
            {
                masks.Push((++nextMask, LumenNativeFrame.Bounds(quad))); drawing.Add(quad); continue;
            }
            if (quad.MaskOperation == LumenRenderMaskOperation.Pop)
            {
                drawing.Add(quad); if (masks.Count > 0) masks.Pop(); continue;
            }
            if (quad.NativeSurface is { } pattern && host.BrowserPatternSize(pattern) is { } tile)
            {
                if (tile.Style == FolderArtStyle.Original)
                {
                    var group = masks.Count > 0 ? masks.Peek() : (++nextMask, LumenNativeFrame.Bounds(quad));
                    var field = LumenNativeFrame.Bounds(quad);
                    var alpha = Math.Clamp(quad.MultiplyColor.Alpha * 2, 0, 1);
                    // Recoloured native frames repeat the same pattern under several
                    // equivalent masks. One opaque wave field is enough for each aperture.
                    if (waves.Add((group.Item2, field, quad.MaskDepth, alpha)))
                    {
                        var box = group.Item2;
                        if (box.Y + box.Height > -80 && box.Y < 800 && box.Width > 12 && box.Height > 12)
                        {
                            var inner = box with { X = box.X + 6, Y = box.Y + 6, Width = box.Width - 12, Height = box.Height - 12 };
                            var clip = LumenVectorGeometry.Rectangle(inner, LumenRenderColor.White) with
                            { MaskOperation = LumenRenderMaskOperation.Push, MaskDepth = quad.MaskDepth };
                            drawing.Add(clip);
                            // The aperture grows from both sides. Keep the wave field
                            // attached to the fixed artwork, just like the PNG patterns.
                            drawing.AddRange(LumenWavePattern.Draw(field, alpha, (byte)(quad.MaskDepth + 1), inner));
                            drawing.Add(clip with { MaskOperation = LumenRenderMaskOperation.Pop, MaskDepth = (byte)(quad.MaskDepth + 1) });
                        }
                    }
                }
                else
                {
                    var oriented = tile.Style == FolderArtStyle.Osu ? quad with
                    {
                        TopLeft = quad.BottomLeft with { X = quad.TopLeft.X, Y = quad.TopLeft.Y },
                        TopRight = quad.TopLeft with { X = quad.TopRight.X, Y = quad.TopRight.Y },
                        BottomRight = quad.TopRight with { X = quad.BottomRight.X, Y = quad.BottomRight.Y },
                        BottomLeft = quad.BottomRight with { X = quad.BottomLeft.X, Y = quad.BottomLeft.Y },
                    } : quad;
                    drawing.AddRange(LumenQuadGeometry.TileClockwise(oriented,
                        tile.Style == FolderArtStyle.Osu ? tile.Height : tile.Width,
                        tile.Style == FolderArtStyle.Osu ? tile.Width : tile.Height));
                }
            }
            else if (scrollTitles && quad.NativeSurface is { } surface && host.BrowserTextLayout(surface) is { } layout)
            {
                var left = 400f; var right = 880f; var scale = 1f;
                if (textOffsets is not null && textOffsets.TryGetValue(quad, out var viewport))
                { left = viewport.Left; right = viewport.Right; scale = viewport.Scale; }
                drawing.AddRange(LumenTextMarquee.Compose(quad, layout,
                    surface.Value.Contains(":CourseTitle:", StringComparison.Ordinal) ? DifficultySelectLayout.TitleLeft : left,
                    surface.Value.Contains(":CourseTitle:", StringComparison.Ordinal) ? DifficultySelectLayout.TitleRight : right,
                    Math.Max(0, host.BrowserTextAge(surface) - 1 + interpolation),
                    layout.Lines.Count > 1 ? Math.Max(0, host.BrowserTextAge(surface, subtitle: true) - 1 + interpolation) : null,
                    scale));
            }
            else drawing.Add(quad);
        }
        return new(scene.StageWidth, scene.StageHeight, drawing);
    }
}
