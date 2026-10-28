using Waddamburo.Game.Lumen;
using Waddamburo.Game.SongSelect;
using Waddamburo.Game;
using System.Runtime.CompilerServices;
using Waddamburo.Platform.Sdl.Rendering;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Presentation;

internal static class SongBrowserFramePresentation
{
    private static readonly bool GeometryTrace = Environment.GetEnvironmentVariable("WADDAMBURO_BROWSER_GEOMETRY_TRACE") == "1";
    public static void TraceCard(string kind, LumenRenderQuad quad, int tick)
    {
        if (!GeometryTrace || tick % 120 != 0) return;
        var box = LumenNativeFrame.Bounds(quad);
        if (box.Height > 230)
            Console.WriteLine(FormattableString.Invariant($"[browser-geometry] tick={tick} {kind}: {box.X:F5},{box.Y:F5},{box.Width:F5},{box.Height:F5}"));
    }

    private static readonly ConditionalWeakTable<LumenPlayer, TextureBounds> Bounds = new();
    private static readonly ConditionalWeakTable<LumenMovieContent, TextureBounds> ContentBounds = new();

    public static void Prepare(LumenMovieContent content)
    {
        if (!content.Name.EndsWith("song_select.lm", StringComparison.Ordinal)) return;
        var bounds = ContentBounds.GetValue(content, static movie => new(movie));
        for (uint index = 0; index < content.Textures.Length; index++)
            if (Math.Min(content.Textures[(int)index].Width, content.Textures[(int)index].Height) <= 192)
                bounds.TryGet(index, out _);
    }

    public static void Register(LumenPlayer player, LumenMovieContent content) =>
        Bounds.GetValue(player, _ => ContentBounds.GetValue(content, static movie => new(movie)));

    public static IEnumerable<LumenRenderQuad> NativeFrame(IEnumerable<LumenRenderQuad> pieces, LumenSceneLayer layer,
        float scale = 1, bool normalize = true, bool rotateSongPattern = false, bool metallic = false)
    {
        var artwork = pieces.Select(q => opaque(q, layer)).OfType<LumenRenderQuad>().ToArray();
        var native = LumenNativeFrame.Compose(artwork, scale, normalize).ToArray();
        if (native.Length == 0) return [];
        // The event motif contains transparent artwork, never a frame palette.
        // Sampling it as a colour ramp bakes its flowers/faces into the bevels.
        var palette = rotateSongPattern ? artwork.Where(q => q.TextureIndex != 623 + layer.TextureOffset).ToArray() : artwork;
        var paint = metallic ? SongTitleTextureCache.MetallicGoldKey : framePaint(palette, layer);
        var frame = LumenVectorGeometry.Frame(LumenNativeFrame.Bounds(native[0]), paint,
            native[0].MultiplyColor.Alpha, verticalGradient: metallic);
        if (!rotateSongPattern) return frame;
        // Read the motif before nine-slicing: resampled corner UVs do not form
        // a continuous source image. Keep its phase over the entire inner field.
        var box = LumenNativeFrame.Bounds(frame[1]);
        var inside = new LumenNativeSurfacePlacement(box.X + 10, box.Y + 10, box.Width - 20, box.Height - 20);
        var pattern = rotateNativeSongPattern(artwork, layer, inside);
        return frame.Take(frame.Length - 4).Concat(pattern).Concat(frame.TakeLast(4));
    }

    private static LumenNativeSurfaceKey framePaint(LumenRenderQuad[] pieces, LumenSceneLayer layer)
    {
        if (pieces.Length == 0) return VectorUiPaint.Solid(new(.13f, .63f, .73f, 1));
        var body = pieces.MaxBy(q => LumenNativeFrame.Bounds(q).Width * LumenNativeFrame.Bounds(q).Height);
        return Bounds.TryGetValue(layer.Player, out var textures) ? textures.Paint(body, layer.TextureOffset)
            : VectorUiPaint.Solid(new(.13f, .63f, .73f, 1));
    }

    public static LumenRenderSnapshot Compose(LumenRenderSnapshot scene, LumenSceneLayer layer, float interpolation, SongSelectHostBinding host,
        Dictionary<LumenRenderQuad, SongBrowserTextViewport>? textOffsets = null)
    {
        var replacements = new Dictionary<LumenRenderQuad, LumenRenderQuad[]>();
        var snapshots = new Dictionary<string, LumenRenderSnapshot>(StringComparer.Ordinal);
        var confirmation = snapshot("main/genreSelect_/wd_category_confirm").Quads;
        foreach (var quad in confirmation) replacements[matchingSceneQuad(quad) ?? quad] = [];
        var confirmationAlpha = confirmation.Where(q => q.MaskOperation == LumenRenderMaskOperation.Draw)
            .Select(q => q.MultiplyColor.Alpha).DefaultIfEmpty(0).Max();
        if (host.BrowserOnReturn)
            foreach (var quad in snapshot("main/genreSelect_/wd_flat_selection").Quads.Take(14))
                replacements[quad] = [];
        foreach (var path in host.BrowserRowFrames)
        {
            var pieces = snapshot(path).Quads.Where(q => q.NativeSurface is null && q.MaskOperation == LumenRenderMaskOperation.Draw).ToArray();
            replace(pieces, host.BrowserRowScale(path), artwork: pieces.Skip(5)
                .Select(q => q with { MultiplyColor = q.MultiplyColor with { Alpha = 1 } }).ToArray(),
                rotatePattern: host.BrowserUsesOsuSongPattern);
            if (host.BrowserOnReturn && path.EndsWith("/wd_return_selection/backboard_", StringComparison.Ordinal)
                && pieces.Length > 0 && replacements[pieces[0]] is { Length: > 1 } returnFrame)
            {
                var goldPieces = snapshot("main/genreSelect_/wd_flat_selection").Quads
                    .Where(q => q.NativeSurface is null && q.MaskOperation == LumenRenderMaskOperation.Draw)
                    .Skip(5).Take(9).ToArray();
                var gold = NativeFrame(goldPieces, layer).ToArray();
                if (gold.Length > 1)
                {
                    var source = LumenNativeFrame.Bounds(gold[1]);
                    // Use the same native gold template and aperture as a song;
                    // the brown Return art has different transparent margins.
                    var target = source with { X = source.X - 12, Width = source.Width + 24,
                        Height = source.Height + Math.Max(0, host.BrowserCardHeight(interpolation) - 65) };
                    TraceCard("return", gold[1] with { TopLeft = new(target.X, target.Y, 0, 0),
                        TopRight = new(target.X + target.Width, target.Y, 0, 0), BottomLeft = new(target.X, target.Y + target.Height, 0, 0),
                        BottomRight = new(target.X + target.Width, target.Y + target.Height, 0, 0) }, host.BrowserTextTick);
                    var width = Math.Max(0, target.Width * host.BrowserSongMorphWidth(interpolation));
                    target = target with { X = target.X + (target.Width - width) / 2, Width = width };
                    var goldOpacity = VerticalSongBrowser.Ease((width - 12) / 24);
                    replacements[pieces[0]] = gold.Select(q => LumenPanelMorph.WarpHorizontal(q with
                        { MultiplyColor = q.MultiplyColor with { Alpha = q.MultiplyColor.Alpha * goldOpacity } }, source, target))
                        .Where(q => q.HasValue).Select(q => q.GetValueOrDefault()).ToArray();
                    SongPanelTransitionPresentation.RememberBrowserCard(host,
                        replacements[pieces[0]].Concat(snapshot("main/genreSelect_/wd_return_selection_title").Quads),
                        target, replacements[pieces[0]].Length, interpolation);
                    if (host.BrowserSongMorphWidth(interpolation) < 1)
                        foreach (var quad in snapshot("main/genreSelect_/wd_return_selection_title").Quads)
                            replacements[quad] = [quad with
                            {
                                MultiplyColor = quad.MultiplyColor with { Alpha = quad.MultiplyColor.Alpha * host.BrowserSongMorphWidth(interpolation) * host.BrowserSongContentOpacity(interpolation) },
                                AddColor = quad.AddColor with { Alpha = quad.AddColor.Alpha * host.BrowserSongMorphWidth(interpolation) * host.BrowserSongContentOpacity(interpolation) },
                            }];
                }
            }
            if (host.CurvedScrolling && host.BrowserOnReturn
                && path.EndsWith("/wd_return_selection/backboard_", StringComparison.Ordinal)
                && pieces.Length > 0 && replacements[pieces[0]].Length > 0)
            {
                var selected = replacements[pieces[0]];
                selected[0] = selected[0] with { Source = SongBrowserWheelPresentation.SelectedMarker };
            }
        }
        if (host.CurvedScrolling)
            foreach (var path in host.BrowserRowFrames)
            {
                var root = path[..path.LastIndexOf('/')];
                var title = root.EndsWith("wd_return_selection", StringComparison.Ordinal)
                    ? root + "_title"
                    : root.Replace("/wd_song_row_", "/wd_song_row_title_", StringComparison.Ordinal);
                var pieces = snapshot(path).Quads;
                if (pieces.Length == 0) continue;
                var body = pieces.Skip(5).ToArray();
                var box = LumenNativeFrame.Bounds(body.Length > 0 ? body : pieces);
                moveGroup(snapshot(root).Quads.Concat(snapshot(title).Quads), box.Y + box.Height / 2);
            }
        foreach (var path in host.BrowserCategoryFrames)
        {
            // Stock skins have one quad per slice. Recoloured custom skins have
            // separate body and additive highlight quads: a nine-quad prefix is
            // only half their frame. The pattern/mask must start after slice nine.
            var frameSnapshot = snapshot(path);
            var pieces = frameSnapshot.Quads.Take(9).ToArray();
            if (pieces.Length != 9 || pieces.Any(q => q.NativeSurface is not null || q.MaskOperation != LumenRenderMaskOperation.Draw)
                || frameSnapshot.Quads.Length > 9 && frameSnapshot.Quads[9] is { NativeSurface: null, MaskOperation: LumenRenderMaskOperation.Draw })
                pieces = Enumerable.Range(0, 9).SelectMany(i => snapshot(path + $"/part_{i}").Quads).ToArray();
            var artwork = alignCategoryBevels(pieces, layer);
            var composed = NativeFrame(artwork, layer, normalize: false, metallic: host.BrowserCategoryIsFavourites(path)).ToArray();
            if (composed.Length < 2) continue;
            var originalRim = composed.Length > 1 ? LumenNativeFrame.Bounds(composed[1]) : default;
            var fittedRim = composed.Length > 1 ? VerticalCategoryFrame.FitPreview(LumenNativeFrame.Bounds(pieces), originalRim) : originalRim;
            LumenRenderQuad fitPreview(LumenRenderQuad quad)
            {
                LumenRenderVertex fit(LumenRenderVertex vertex) => LumenPanelMorph.Warp(vertex, originalRim, fittedRim);
                return quad with { TopLeft = fit(quad.TopLeft), TopRight = fit(quad.TopRight), BottomLeft = fit(quad.BottomLeft), BottomRight = fit(quad.BottomRight) };
            }
            if (fittedRim != originalRim)
            {
                composed = composed.Select(fitPreview).ToArray();
                // Pattern and stencil share the body's aperture. The tab/caption
                // translate together so their overlap stays unchanged.
                foreach (var quad in snapshot(path).Quads.Where(q => q.NativeSurface is not null || q.MaskOperation != LumenRenderMaskOperation.Draw))
                    replacements[quad] = [fitPreview(quad)];
                foreach (var suffix in new[] { "wd_header_", "wd_caption_" })
                    foreach (var quad in snapshot(path.Replace("/wd_open_", "/" + suffix, StringComparison.Ordinal)).Quads)
                        replacements[quad] = [LumenQuadGeometry.Move(quad, fittedRim.X - originalRim.X, fittedRim.Y - originalRim.Y)];
            }
            foreach (var quad in pieces) replacements[quad] = [];
            if (pieces.Length > 0) replacements[pieces[0]] = composed;
            if (host.BrowserFolderMorph(interpolation) is { } morph && morph.Path == path && composed.Length > 1)
            {
                var original = LumenNativeFrame.Bounds(composed[1]);
                var width = Math.Max(0, original.Width * morph.Width);
                var target = original with { X = original.X + (original.Width - width) / 2, Width = width };
                var edgeOpacity = VerticalSongBrowser.Ease((width - 12) / 24);
                replacements[pieces[0]] = composed.Select(q => LumenPanelMorph.WarpHorizontal(q with
                    {
                        MultiplyColor = q.MultiplyColor with { Alpha = q.MultiplyColor.Alpha * edgeOpacity },
                        AddColor = q.AddColor with { Alpha = q.AddColor.Alpha * edgeOpacity },
                    }, original, target))
                    .Where(q => q.HasValue).Select(q => q.GetValueOrDefault()).ToArray();
                var aperture = new LumenNativeSurfacePlacement(target.X, -200, target.Width, 1200);
                foreach (var suffix in new[] { "wd_header_", "wd_caption_" })
                {
                    var group = snapshot(path.Replace("/wd_open_", "/" + suffix, StringComparison.Ordinal)).Quads;
                    LumenRenderQuad fittedQuad(LumenRenderQuad quad) => replacements.TryGetValue(quad, out var fitted)
                        && fitted.Length == 1 ? fitted[0] : quad;
                    // The native tab is a mesh, not one rectangular bitmap. Warp
                    // all of its pieces against the same bounds so shared vertices
                    // stay joined and its rounded outline cannot split apart.
                    var sourceTab = LumenNativeFrame.Bounds(group.Select(fittedQuad));
                    var targetTab = sourceTab with { X = sourceTab.X + sourceTab.Width * (1 - morph.Width) / 2,
                        Width = sourceTab.Width * morph.Width };
                    foreach (var quad in group)
                        if (quad.MaskOperation == LumenRenderMaskOperation.Draw)
                        {
                            var content = fittedQuad(quad);
                            var caption = suffix == "wd_caption_";
                            var opacity = caption ? host.BrowserCategoryReveal(path, interpolation).Text : morph.Width;
                            var faded = content with
                            {
                                MultiplyColor = content.MultiplyColor with { Alpha = content.MultiplyColor.Alpha * opacity },
                                AddColor = content.AddColor with { Alpha = content.AddColor.Alpha * opacity },
                            };
                            var warped = caption || morph.Width >= 1 ? faded
                                : LumenPanelMorph.WarpHorizontal(faded, sourceTab, targetTab);
                            replacements[quad] = warped is { } visible ? [visible] : [];
                        }
                }
                // Pattern is a sibling of the frame slices inside the same clip.
                foreach (var quad in snapshot(path).Quads.Where(q => q.NativeSurface is not null))
                    replacements[quad] = LumenQuadGeometry.Clip(replacements.TryGetValue(quad, out var fitted) && fitted.Length == 1 ? fitted[0] : quad, aperture) is { } clipped ? [clipped] : [];
            }
            if (composed.Length > 1)
            {
                if (GeometryTrace) TraceCard("category:" + path, composed[1], host.BrowserTextTick);
                var rim = LumenNativeFrame.Bounds(composed[1]);
                var inside = new LumenNativeSurfacePlacement(rim.X + 4, rim.Y + 4, rim.Width - 8, rim.Height - 8);
                var previewPath = path.Replace("/wd_open_", "/wd_preview_", StringComparison.Ordinal);
                var preview = snapshot(previewPath);
                var counterQuads = preview.Quads.Length > 0 ? snapshot(previewPath + "/musicNumber_").Quads : [];
                var counter = counterQuads.ToHashSet();
                var hasCounter = preview.Quads.Any(counter.Contains);
                var counterBox = counterQuads.Length > 0
                    ? LumenNativeFrame.Bounds(counterQuads.Select(q => fittedRim != originalRim ? fitPreview(q) : q)) : default;
                foreach (var quad in preview.Quads)
                    if (quad.MaskOperation == LumenRenderMaskOperation.Draw)
                    {
                        var content = quad;
                        if (quad.NativeSurface is null && Bounds.TryGetValue(layer.Player, out var textures)
                            && textures.Size(quad.TextureIndex - layer.TextureOffset) == (160, 400)
                            && host.BrowserDescription(path) is { } description)
                            content = quad with { NativeSurface = description, TextureIndex = 0 };
                        if (fittedRim != originalRim) content = fitPreview(content);
                        var detail = fitCategoryDetail(content, rim, layer, hasCounter);
                        // Illustrations deliberately overlap the left rim, like the
                        // reference; descriptions and counters remain inside it.
                        var clip = detail.Illustration ? inside with { X = inside.X - 116, Width = inside.Width + 116 } : inside;
                        var fitted = detail.Quad;
                        var reveal = host.BrowserCategoryReveal(path, interpolation);
                        var opacity = detail.Illustration ? 1 : reveal.Text;
                        if (detail.Illustration)
                        {
                            var box = LumenNativeFrame.Bounds(fitted);
                            var scale = reveal.Artwork;
                            LumenRenderVertex grow(LumenRenderVertex vertex) => vertex with
                            {
                                X = box.X + box.Width / 2 + (vertex.X - box.X - box.Width / 2) * scale,
                                Y = box.Y + box.Height / 2 + (vertex.Y - box.Y - box.Height / 2) * scale,
                            };
                            fitted = fitted with { TopLeft = grow(fitted.TopLeft), TopRight = grow(fitted.TopRight),
                                BottomLeft = grow(fitted.BottomLeft), BottomRight = grow(fitted.BottomRight) };
                        }
                        if (counter.Contains(quad))
                            fitted = LumenQuadGeometry.Move(content,
                                rim.X + (rim.Width - counterBox.Width) / 2 - counterBox.X,
                                rim.Y + rim.Height - 22 - counterBox.Height - counterBox.Y);
                        fitted = fitted with
                        {
                            MultiplyColor = fitted.MultiplyColor with { Alpha = fitted.MultiplyColor.Alpha * opacity },
                            AddColor = fitted.AddColor with { Alpha = fitted.AddColor.Alpha * opacity },
                        };
                        // Content fades in place while the frame contracts. In particular,
                        // the overlapping illustration must never be eaten by that aperture.
                        if (host.BrowserFolderMorph(interpolation) is { } shrinking && shrinking.Path == path)
                            fitted = fitted with
                            {
                                MultiplyColor = fitted.MultiplyColor with { Alpha = fitted.MultiplyColor.Alpha * shrinking.Width },
                                AddColor = fitted.AddColor with { Alpha = fitted.AddColor.Alpha * shrinking.Width },
                            };
                        replacements[quad] = LumenQuadGeometry.Clip(fitted, clip) is { } clipped ? [clipped] : [];
                    }
            }
            var palette = framePaint(artwork, layer);
            var tint = host.BrowserCategoryIsFavourites(path) ? new LumenRenderColor(.94f, .81f, .15f, 1) : VectorUiPaint.Colour(palette);
            var tabPath = path.Replace("/wd_open_", "/wd_header_", StringComparison.Ordinal) + "/tab";
            var tab = snapshot(tabPath).Quads;
            if (tab.Length > 0)
            {
                var moved = tab.Select(q => replacements.TryGetValue(q, out var fitted) && fitted.Length == 1 ? fitted[0] : q).ToArray();
                var box = LumenNativeFrame.Bounds(moved);
                var alpha = tab.Max(q => q.MultiplyColor.Alpha);
                var drawing = LumenVectorGeometry.CategoryTab(box, tint with { Alpha = alpha }, fittedRim);
                if (host.BrowserFolderMorph(interpolation) is { } shrink && shrink.Path == path)
                {
                    // Rebuild the contour at the collapsed width, with one shared
                    // curve; do not stretch a raster tab or its antialias fringe.
                    var width = box.Width * shrink.Width;
                    var target = box with { X = box.X + (box.Width - width) / 2, Width = width };
                    drawing = width < 12 ? [] : LumenVectorGeometry.CategoryTab(target,
                        tint with { Alpha = alpha * shrink.Width }, fittedRim);
                }
                foreach (var quad in tab) replacements[matchingSceneQuad(quad) ?? quad] = [];
                replacements[matchingSceneQuad(tab[0]) ?? tab[0]] = drawing;
            }
            var pegs = snapshot(path + "/wd_peg_outline").Quads;
            if (pegs.Length > 0)
            {
                var box = LumenNativeFrame.Bounds(pegs) with { X = fittedRim.X };
                var drawing = LumenVectorGeometry.FolderPeg(box,
                    tint with { Alpha = pegs.Max(q => q.MultiplyColor.Alpha) }, fittedRim);
                foreach (var quad in pegs) replacements[matchingSceneQuad(quad) ?? quad] = [];
                // The cap sits in front of the card: its open coloured base
                // covers the top black rim at the folder's join.
                if (replacements.TryGetValue(pieces[0], out var front)) replacements[pieces[0]] = front.Concat(drawing).ToArray();
                foreach (var fill in snapshot(path + "/wd_peg_fill").Quads) replacements[matchingSceneQuad(fill) ?? fill] = [];
            }
            if (host.BrowserCategorySelected(path) && replacements[pieces[0]] is { Length: > 1 } categoryDrawing)
            {
                // Draw over the pattern, after its stencil closes. Drawing over
                // the base frame alone lets an opaque custom pattern hide it.
                var last = matchingSceneQuad(frameSnapshot.Quads[^1]) ?? frameSnapshot.Quads[^1];
                var tail = replacements.TryGetValue(last, out var replaced) ? replaced : [last];
                replacements[last] = tail.Concat(CategoryConfirmation(
                    LumenNativeFrame.Bounds(categoryDrawing[1]), confirmationAlpha)).ToArray();
            }
            if (host.CurvedScrolling)
            {
                var group = frameSnapshot.Quads.AsEnumerable();
                foreach (var suffix in new[] { "wd_header_", "wd_caption_", "wd_preview_" })
                    group = group.Concat(snapshot(path.Replace("/wd_open_", "/" + suffix, StringComparison.Ordinal)).Quads);
                moveGroup(group, fittedRim.Y + fittedRim.Height / 2);
            }
        }
        return new(scene.StageWidth, scene.StageHeight, scene.Quads.SelectMany(q => replacements.TryGetValue(q, out var replacement) ? replacement : [q]));

        void moveGroup(IEnumerable<LumenRenderQuad> group, float centerY)
        {
            foreach (var quad in group.Distinct())
            {
                var key = matchingSceneQuad(quad) ?? quad;
                var drawing = replacements.TryGetValue(key, out var replacement) ? replacement : [key];
                replacements[key] = SongBrowserWheelPresentation.Move(drawing, centerY, host, textOffsets).ToArray();
            }
        }

        LumenRenderQuad? matchingSceneQuad(LumenRenderQuad component)
        {
            static bool same(LumenRenderVertex a, LumenRenderVertex b) => Math.Abs(a.X - b.X) < .02f
                && Math.Abs(a.Y - b.Y) < .02f && Math.Abs(a.U - b.U) < .00001f && Math.Abs(a.V - b.V) < .00001f;
            foreach (var quad in scene.Quads)
                if (quad.TextureIndex == component.TextureIndex && quad.NativeSurface == component.NativeSurface
                    && quad.MaskOperation == component.MaskOperation && same(quad.TopLeft, component.TopLeft)
                    && same(quad.TopRight, component.TopRight) && same(quad.BottomLeft, component.BottomLeft)
                    && same(quad.BottomRight, component.BottomRight)) return quad;
            return null;
        }

        void replace(LumenRenderQuad[] pieces, float scale, bool normalize = true, LumenRenderQuad[]? artwork = null, bool rotatePattern = false)
        {
            if (pieces.Length == 0) return;
            foreach (var quad in pieces) replacements[quad] = [];
            replacements[pieces[0]] = [.. NativeFrame(artwork ?? pieces, layer, scale, normalize, rotatePattern)];
        }
        LumenRenderSnapshot snapshot(string path)
        {
            if (snapshots.TryGetValue(path, out var cached)) return cached;
            var child = layer.Player.CreateInstanceRenderSnapshot(path, interpolation);
            LumenRenderVertex point(LumenRenderVertex v)
            {
                var p = layer.Transform.Transform(v.X, v.Y);
                return v with { X = p.X, Y = p.Y };
            }
            return snapshots[path] = new(1280, 720, child.Quads.Select(q => q with { TextureIndex = q.NativeSurface is null ? q.TextureIndex + layer.TextureOffset : 0,
                TopLeft = point(q.TopLeft), TopRight = point(q.TopRight), BottomLeft = point(q.BottomLeft), BottomRight = point(q.BottomRight) }));
        }
    }

    internal static IEnumerable<LumenRenderQuad> CategoryConfirmation(LumenNativeSurfacePlacement rim, float alpha)
    {
        if (alpha <= 0 || rim.Width <= 8 || rim.Height <= 8) return [];
        return [LumenVectorGeometry.Rectangle(new(rim.X + 4, rim.Y + 4, rim.Width - 8, rim.Height - 8),
            new(1, 1, 1, Math.Clamp(alpha, 0, 1) * .35f))];
    }

    private static LumenRenderQuad[] rotateNativeSongPattern(LumenRenderQuad[] frame, LumenSceneLayer layer, LumenNativeSurfacePlacement aperture)
    {
        // Green's event-song motif is separate from the bevel textures.
        const uint patternTexture = 623;
        if (!Bounds.TryGetValue(layer.Player, out var textures) || textures.Size(patternTexture) != (72, 448)) return [];
        bool isPattern(LumenRenderQuad quad) => quad.NativeSurface is null
            && quad.TextureIndex == patternTexture + layer.TextureOffset
            && Math.Abs((quad.TopRight.U - quad.TopLeft.U) * (quad.BottomLeft.V - quad.TopLeft.V)
                - (quad.TopRight.V - quad.TopLeft.V) * (quad.BottomLeft.U - quad.TopLeft.U)) > .000001f;
        var pieces = frame.Where(isPattern).ToArray();
        if (pieces.Length == 0) return [];
        var box = LumenNativeFrame.Bounds(pieces);
        var vertices = pieces.SelectMany(q => new[] { q.TopLeft, q.TopRight, q.BottomLeft, q.BottomRight }).ToArray();
        LumenRenderVertex corner(float x, float y) => vertices.MinBy(v => (v.X - x) * (v.X - x) + (v.Y - y) * (v.Y - y)) with { X = x, Y = y };
        var pattern = pieces[0] with
        {
            TopLeft = corner(box.X, box.Y) with { X = aperture.X, Y = aperture.Y },
            TopRight = corner(box.X + box.Width, box.Y) with { X = aperture.X + aperture.Width, Y = aperture.Y },
            BottomLeft = corner(box.X, box.Y + box.Height) with { X = aperture.X, Y = aperture.Y + aperture.Height },
            BottomRight = corner(box.X + box.Width, box.Y + box.Height) with { X = aperture.X + aperture.Width, Y = aperture.Y + aperture.Height },
        };
        // The event artwork includes coloured vertical rails in source columns
        // 0..8 and 63..71. After the quarter-turn these become repeated bars;
        // mirroring their outer pixels only hides the sampling discontinuity.
        // Tile the illustration between the rails at its native texel scale.
        // Stop at texel centres: linear filtering must not sample the excluded
        // rails on either side of a mirrored repeat.
        const float sourceLeft = 9.5f / 72;
        const float sourceRight = 62.5f / 72;
        pattern = pattern with
        {
            TopLeft = pattern.TopLeft with { U = sourceLeft },
            TopRight = pattern.TopRight with { U = sourceLeft },
            BottomLeft = pattern.BottomLeft with { U = sourceRight },
            BottomRight = pattern.BottomRight with { U = sourceRight },
        };
        return LumenQuadGeometry.TileClockwise(pattern, 448 * .8f, 54 * .8f, mirrorAlternateColumns: true).ToArray();
    }

    private static (LumenRenderQuad Quad, bool Illustration) fitCategoryDetail(LumenRenderQuad quad, LumenNativeSurfacePlacement panel, LumenSceneLayer layer, bool hasCounter)
    {
        if (!Bounds.TryGetValue(layer.Player, out var textures)) return (quad, false);
        var size = quad.NativeSurface is null ? textures.Size(quad.TextureIndex - layer.TextureOffset) : default;
        var description = size == (160, 400) || quad.NativeSurface?.Value.StartsWith("folder-description", StringComparison.Ordinal) == true;
        var illustration = size == (192, 360) || quad.NativeSurface?.Value.StartsWith("image:", StringComparison.Ordinal) == true
            || quad.NativeSurface is { } art && art.Value.StartsWith("folder-art:", StringComparison.Ordinal) && art.Value.EndsWith(":Box", StringComparison.Ordinal);
        if (!description && !illustration) return (quad, false); // Keep the native song counter and its spacing.
        var source = LumenNativeFrame.Bounds(opaque(quad, layer) ?? quad);
        var target = description
            ? new LumenNativeSurfacePlacement(panel.X + 52, panel.Y + 12, panel.Width - 104, panel.Height - (hasCounter ? 76 : 24))
            : new LumenNativeSurfacePlacement(panel.X - 80, panel.Y + 6, 154, panel.Height - 12);
        if (target.Width <= 0 || target.Height <= 0) return (quad, illustration);
        var scale = Math.Min(target.Width / source.Width, target.Height / source.Height);
        var x = description ? target.X + (target.Width - source.Width * scale) / 2 : target.X;
        var y = target.Y + (target.Height - source.Height * scale) / 2;
        LumenRenderVertex fit(LumenRenderVertex vertex) => vertex with
        { X = x + (vertex.X - source.X) * scale, Y = y + (vertex.Y - source.Y) * scale };
        return (quad with { TopLeft = fit(quad.TopLeft), TopRight = fit(quad.TopRight),
            BottomLeft = fit(quad.BottomLeft), BottomRight = fit(quad.BottomRight) }, illustration);
    }

    public static LumenRenderSnapshot ScaleCourseMarkers(LumenRenderSnapshot snapshot, LumenSceneLayer layer)
    {
        if (!Bounds.TryGetValue(layer.Player, out var art)) return snapshot;
        // A carousel fade can split one marker into narrow UV strips. Use the
        // bounds of the whole marker so each strip shares the same scale origin.
        var origins = snapshot.Quads.Where(quad => quad.NativeSurface is null
                && quad.MaskOperation == LumenRenderMaskOperation.Draw
                && art.PlayerMarkers.Contains(quad.TextureIndex - layer.TextureOffset))
            .GroupBy(quad => quad.TextureIndex)
            .ToDictionary(group => group.Key, group => LumenNativeFrame.Bounds(group));
        return new(snapshot.StageWidth, snapshot.StageHeight, snapshot.Quads.Select(quad =>
        {
            if (quad.NativeSurface is not null || quad.MaskOperation != LumenRenderMaskOperation.Draw
                || !origins.TryGetValue(quad.TextureIndex, out var box)) return quad;
            var x = box.X + box.Width / 2; var y = box.Y + box.Height;
            LumenRenderVertex fit(LumenRenderVertex vertex) => vertex with
            {
                X = x + (vertex.X - x) * DifficultySelectLayout.PlayerMarkerScale,
                Y = y + (vertex.Y - y) * DifficultySelectLayout.PlayerMarkerScale,
            };
            return quad with { TopLeft = fit(quad.TopLeft), TopRight = fit(quad.TopRight),
                BottomLeft = fit(quad.BottomLeft), BottomRight = fit(quad.BottomRight) };
        }));
    }

    public static LumenRenderSnapshot CoursePanel(LumenRenderSnapshot panel, LumenSceneLayer layer)
    {
        var pieces = panel.Quads.TakeWhile(q => q.NativeSurface is null && q.MaskOperation == LumenRenderMaskOperation.Draw).ToArray();
        if (pieces.Length < 14) return panel;
        var frame = NativeFrame(pieces.Skip(5).Take(9), layer, normalize: false).ToArray();
        if (frame.Length < 2) return panel;
        var source = LumenNativeFrame.Bounds(frame[1]);
        var inset = DifficultySelectLayout.PanelTopInset * Math.Abs(layer.Transform.M22);
        var target = source with { Y = source.Y + inset, Height = source.Height - inset };
        LumenRenderVertex fit(LumenRenderVertex vertex) => LumenPanelMorph.Warp(vertex, source, target);
        return new(panel.StageWidth, panel.StageHeight, frame.Select(quad => quad with
        {
            TopLeft = fit(quad.TopLeft), TopRight = fit(quad.TopRight),
            BottomLeft = fit(quad.BottomLeft), BottomRight = fit(quad.BottomRight),
        }).Concat(panel.Quads.Skip(14)));
    }

    public static LumenRenderSnapshot ComposeCourse(LumenRenderSnapshot scene, LumenSceneLayer layer, float interpolation)
    {
        var native = layer.Player.CreateInstanceRenderSnapshot("main/coursePanel_", interpolation);
        LumenRenderVertex point(LumenRenderVertex v)
        {
            var p = layer.Transform.Transform(v.X, v.Y);
            return v with { X = p.X, Y = p.Y };
        }
        var pieces = native.Quads.Take(14).Select(q => q with
        {
            TextureIndex = q.TextureIndex + layer.TextureOffset,
            TopLeft = point(q.TopLeft), TopRight = point(q.TopRight),
            BottomLeft = point(q.BottomLeft), BottomRight = point(q.BottomRight),
        }).ToArray();
        if (pieces.Length != 14) return scene;
        var removed = pieces.ToHashSet();
        var frame = CoursePanel(new(1280, 720, pieces), layer);
        return new(scene.StageWidth, scene.StageHeight,
            scene.Quads.SelectMany(q => q == pieces[0] ? frame.Quads.AsEnumerable() : removed.Contains(q) ? [] : [q]));
    }

    private static LumenRenderQuad[] alignCategoryBevels(LumenRenderQuad[] pieces, LumenSceneLayer layer)
    {
        if (pieces.Length != 9 || !Bounds.TryGetValue(layer.Player, out var bounds)) return pieces;
        var aligned = pieces.ToArray();
        for (var column = 0; column < 3; column++)
        {
            var corner = pieces[column];
            var strip = pieces[3 + column];
            if (!bounds.TryGet(corner.TextureIndex - layer.TextureOffset, out var cornerPixels)
                || !bounds.TryGet(strip.TextureIndex - layer.TextureOffset, out var stripPixels)) continue;
            // The baked one-texel offset fits J-POP. Vocaloid's corner and strip
            // already start at the same texel: that offset exposes a seam in its bevel.
            var correction = stripPixels.Y - cornerPixels.Y - (strip.TopLeft.V - corner.TopLeft.V);
            LumenRenderVertex fit(LumenRenderVertex vertex) => vertex with { V = vertex.V + correction };
            aligned[3 + column] = strip with { TopLeft = fit(strip.TopLeft), TopRight = fit(strip.TopRight),
                BottomLeft = fit(strip.BottomLeft), BottomRight = fit(strip.BottomRight) };
        }
        return aligned;
    }

    private static LumenRenderQuad? opaque(LumenRenderQuad quad, LumenSceneLayer layer)
    {
        if (!Bounds.TryGetValue(layer.Player, out var bounds) || quad.NativeSurface is not null) return quad;
        var texture = quad.TextureIndex - layer.TextureOffset;
        if (!bounds.TryGet(texture, out var uv)) return quad;
        var a = quad.TopLeft; var b = quad.TopRight; var c = quad.BottomLeft;
        var determinant = (b.U - a.U) * (c.V - a.V) - (b.V - a.V) * (c.U - a.U);
        if (Math.Abs(determinant) < .000001f) return quad;
        LumenRenderVertex at(float u, float v)
        {
            var x = ((u - a.U) * (c.V - a.V) - (v - a.V) * (c.U - a.U)) / determinant;
            var y = ((b.U - a.U) * (v - a.V) - (b.V - a.V) * (u - a.U)) / determinant;
            return new(a.X + x * (b.X - a.X) + y * (c.X - a.X), a.Y + x * (b.Y - a.Y) + y * (c.Y - a.Y), u, v);
        }
        var box = LumenNativeFrame.Bounds(quad with { TopLeft = at(uv.X, uv.Y), TopRight = at(uv.X + uv.Width, uv.Y),
            BottomLeft = at(uv.X, uv.Y + uv.Height), BottomRight = at(uv.X + uv.Width, uv.Y + uv.Height) });
        return LumenQuadGeometry.Clip(quad, box);
    }

    private sealed class TextureBounds(LumenMovieContent content)
    {
        // Opaque marker shapes for the native solo/duet 1P and 2P cursors.
        public HashSet<uint> PlayerMarkers { get; } = content.Definition.Shapes
            .Where(shape => shape.CharacterId is 393 or 397 or 413 or 415)
            .SelectMany(shape => shape.Geometry.Select(geometry => geometry.TextureIndex)).ToHashSet();
        private readonly Dictionary<uint, LumenNativeSurfacePlacement> _bounds = [];
        private readonly HashSet<uint> _empty = [];
        private readonly Dictionary<uint, byte[]> _paintPixels = [];
        private readonly Dictionary<(uint, LumenRenderColor, LumenRenderColor), LumenNativeSurfaceKey> _paints = [];
        public LumenNativeSurfaceKey Paint(LumenRenderQuad body, uint offset)
        {
            var index = body.TextureIndex - offset;
            var multiply = body.MultiplyColor with { Alpha = 1 }; var add = body.AddColor with { Alpha = 0 };
            var cache = (index, multiply, add);
            if (_paints.TryGetValue(cache, out var cached)) return cached;
            if (!_paintPixels.TryGetValue(index, out var source)) return VectorUiPaint.Solid(new(.13f, .63f, .73f, 1));
            var texture = content.Textures[(int)index]; var box = LumenNativeFrame.Bounds(body);
            var a = body.TopLeft; var b = body.TopRight; var c = body.BottomLeft;
            var determinant = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (Math.Abs(determinant) < .000001f) return VectorUiPaint.Solid(new(.13f, .63f, .73f, 1));
            var pixels = new byte[1024];
            for (var sample = 0; sample < 256; sample++)
            {
                var x = box.X + box.Width * (.02f + .96f * sample / 255); var y = box.Y + box.Height / 2;
                var dx = x - a.X; var dy = y - a.Y;
                var along = (dx * (c.Y - a.Y) - dy * (c.X - a.X)) / determinant;
                var across = ((b.X - a.X) * dy - (b.Y - a.Y) * dx) / determinant;
                var u = a.U + along * (b.U - a.U) + across * (c.U - a.U);
                var v = a.V + along * (b.V - a.V) + across * (c.V - a.V);
                var at = (Math.Clamp((int)(v * texture.Height), 0, texture.Height - 1) * texture.Width
                    + Math.Clamp((int)(u * texture.Width), 0, texture.Width - 1)) * 4;
                byte tint(int channel, float m, float extra) => (byte)Math.Clamp(MathF.Round((source[at + channel] / 255f * m + extra) * 255), 0, 255);
                pixels[sample * 4] = tint(0, multiply.Red, add.Red);
                pixels[sample * 4 + 1] = tint(1, multiply.Green, add.Green);
                pixels[sample * 4 + 2] = tint(2, multiply.Blue, add.Blue); pixels[sample * 4 + 3] = 255;
            }
            return _paints[cache] = VectorUiPaint.Gradient(pixels);
        }
        private readonly Dictionary<(uint Cap, uint Body, byte R, byte G, byte B), LumenNativeSurfaceKey> _pegs = [];
        private readonly HashSet<(uint Cap, uint Body)> _pegTrace = [];
        public LumenNativeSurfaceKey? Peg(uint cap, LumenRenderQuad body, uint textureOffset)
        {
            var index = body.TextureIndex - textureOffset;
            if (GeometryTrace && _pegTrace.Add((cap, index)))
                Console.WriteLine($"[peg] cap={cap}/{Size(cap)} body={index}/{Size(index)} retained={_paintPixels.ContainsKey(cap)}/{_paintPixels.ContainsKey(index)}");
            if (!_paintPixels.TryGetValue(cap, out var pixels) || !_paintPixels.TryGetValue(index, out var fill)) return null;
            var texture = content.Textures[(int)index];
            // Sample the unlit body at the peg's side, rather than copying the
            // old cap's translucent highlight over an unrelated palette colour.
            var box = LumenNativeFrame.Bounds(body);
            var a = body.TopLeft; var b = body.TopRight; var c = body.BottomLeft;
            var dx = box.X + Math.Min(20, box.Width / 2) - a.X; var dy = box.Y + box.Height / 2 - a.Y;
            var determinant = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (Math.Abs(determinant) < .000001f) return null;
            var along = (dx * (c.Y - a.Y) - dy * (c.X - a.X)) / determinant;
            var across = ((b.X - a.X) * dy - (b.Y - a.Y) * dx) / determinant;
            var u = a.U + along * (b.U - a.U) + across * (c.U - a.U);
            var v = a.V + along * (b.V - a.V) + across * (c.V - a.V);
            var at = (Math.Clamp((int)(v * texture.Height), 0, texture.Height - 1) * texture.Width
                + Math.Clamp((int)(u * texture.Width), 0, texture.Width - 1)) * 4;
            byte tint(byte channel, float multiply, float add) => (byte)Math.Clamp(MathF.Round((channel / 255f * multiply + add) * 255), 0, 255);
            var red = tint(fill[at], body.MultiplyColor.Red, body.AddColor.Red);
            var green = tint(fill[at + 1], body.MultiplyColor.Green, body.AddColor.Green);
            var blue = tint(fill[at + 2], body.MultiplyColor.Blue, body.AddColor.Blue);
            var cache = (cap, index, red, green, blue);
            if (_pegs.TryGetValue(cache, out var key)) return key;
            var size = content.Textures[(int)cap];
            return _pegs[cache] = new(FolderArtPainter.RegisterPeg(pixels, size.Width, size.Height, red, green, blue));
        }
        public (int Width, int Height) Size(uint index) => index < content.Textures.Length
            ? (content.Textures[(int)index].Width, content.Textures[(int)index].Height) : default;
        public bool TryGet(uint index, out LumenNativeSurfacePlacement bounds)
        {
            if (_bounds.TryGetValue(index, out bounds)) return true;
            if (_empty.Contains(index) || index >= content.Textures.Length) return false;
            var texture = content.Textures[(int)index];
            if (texture.Rgba8.IsDefaultOrEmpty && texture.Bc7.IsEmpty) return false;
            var left = texture.Width; var top = texture.Height; var right = -1; var bottom = -1;
            var pixels = texture.Rgba8.IsDefaultOrEmpty ? Bc7.Decode(texture.Bc7.Span, texture.Width, texture.Height) : texture.Rgba8.AsSpan();
            // Capture only these tiny runtime assets before GPU upload releases
            // the decoded buffers. Never decode released texture data while drawing.
            if (texture.Width <= 96 && texture.Height <= 512)
                _paintPixels[index] = pixels.ToArray();
            // The authored bevels include an opaque black rim as well as a
            // transparent shadow. Exclude both before adding the uniform new rim.
            for (var y = 0; y < texture.Height; y++)
                for (var x = 0; x < texture.Width; x++)
                    if (pixels[(y * texture.Width + x) * 4 + 3] >= 220
                        && Math.Max(pixels[(y * texture.Width + x) * 4],
                            Math.Max(pixels[(y * texture.Width + x) * 4 + 1], pixels[(y * texture.Width + x) * 4 + 2])) > 30)
                    {
                        left = Math.Min(left, x); right = Math.Max(right, x);
                        top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    }
            if (right < left || bottom < top) { _empty.Add(index); return false; }
            bounds = new(left / (float)texture.Width, top / (float)texture.Height,
                (right - left + 1) / (float)texture.Width, (bottom - top + 1) / (float)texture.Height);
            _bounds[index] = bounds;
            return true;
        }
    }
}
