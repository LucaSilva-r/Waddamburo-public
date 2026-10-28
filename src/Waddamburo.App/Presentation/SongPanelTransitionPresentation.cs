using System.Runtime.CompilerServices;
using Waddamburo.Game.Lumen;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Presentation;

/// <summary>Native list fade followed by a full-size difficulty panel sliding in from the right.</summary>
internal sealed class SongPanelTransitionPresentation
{
    private sealed record BrowserCard(LumenRenderQuad[] Quads, LumenNativeSurfacePlacement Bounds,
        int BorderCount, float TopInset, float CompactHeight);
    private sealed class BrowserScrollFrames
    {
        public Dictionary<(int Genre, int Board), BrowserCard> Cards { get; } = [];
    }
    private static readonly ConditionalWeakTable<SongSelectHostBinding, BrowserScrollFrames> ScrollFrames = new();
    private const int ExitTicks = 18, EnterTicks = 12, OpeningTicks = 10;
    private static readonly string[] Chrome = ["main/courseStar_", "main/scoreView_", "main/rankingView_", "main/modeTitle_"];
    public static readonly string[] Excluded = ["main/genreSelect_", "main/coursePanel_", "main/courseSelect_", .. Chrome];
    public static readonly string[] BrowserContents = ["main/genreSelect_/expand_", "main/genreSelect_/musicDetail_",
        "main/genreSelect_/wd_flat_selection"];
    private static readonly string[] LevelNames = ["easy", "normal", "hard", "mania"];
    private LumenRenderSnapshot? _browser, _courseView, _courseHeader, _coursePanel;
    private LumenRenderSnapshot? _browserChrome, _courseChrome, _browserTitle, _browserSource;
    private bool _requested, _returning;
    private int _age, _previousAge, _readyAge, _previousReadyAge;
    private int? _pendingFrame;
    private int _pendingTicks;
    public bool Active { get; private set; }
    public bool Returning => Active && _returning;

    public void Begin(int? nativeFrame = null)
    {
        if (Active || _browser is null) return;
        Active = true; _returning = false; _age = _previousAge = _readyAge = _previousReadyAge = 0;
        _pendingFrame = nativeFrame; _pendingTicks = 0;
    }

    /// <summary>Run the two hidden native entrance tweens sooner, preserving their frame callbacks.</summary>
    public void AdvanceOpening(LumenPlayer player)
    {
        if (!Active || _returning) return;
        runAhead("main/genreSelect_", "\u66f2\u6c7a\u5b9a", "music_decide_end", 0);
        runAhead("main/coursePanel_", "\u5927\u30aa\u30fc\u30d7\u30f3", "\u5927\u30af\u30ed\u30fc\u30ba", 1);

        void runAhead(string path, string firstLabel, string nextLabel, int endOffset)
        {
            if (player.InstanceFrame(path) is not { Playing: true } frame
                || !player.TryGetLabelFrame(path, firstLabel, out var first)
                || !player.TryGetLabelFrame(path, nextLabel, out var next)) return;
            var last = next - endOffset;
            if (frame.Frame >= first && frame.Frame < last)
                player.TryFastForward(path, Math.Min(last, frame.Frame + 4));
        }
    }

    public void BeginReturn()
    {
        if (_browser is null || _courseView is null) return;
        Active = true; _returning = true;
        _age = _previousAge = _readyAge = _previousReadyAge = 0;
        _pendingFrame = null;
    }

    public void Advance(SongSelectHostBinding host, bool ready, int? browserFrame, bool browserPlaying, bool browserTitleReady)
    {
        var requested = host.CourseSelectSong is not null;
        if (_pendingFrame is { } pending)
        {
            if (requested || browserPlaying || browserFrame != pending) _pendingFrame = null;
            else if (++_pendingTicks > 1) { Active = false; _pendingFrame = null; }
        }
        if (requested && !_requested) Begin();
        if (Active && !_returning && host.LeavingCourseSelect
            || !Active && _requested && (host.LeavingCourseSelect || !requested) && _browser is not null)
        {
            BeginReturn();
        }
        _requested = requested;
        if (!Active) return;
        // Restart on every return tick, including the final one: the one-second
        // marquee pause belongs to the visible list, not the closing animation.
        if (_returning) host.RestartBrowserTitleScroll();
        var available = _returning ? !requested && browserTitleReady : ready;
        _previousAge = _age; _previousReadyAge = _readyAge;
        // Keep the previous view opaque until its replacement exists. Holding a
        // nearly transparent pose here leaves ghost rows over the background.
        // Return has a complete browser snapshot already: fade immediately,
        // holding that opaque view while the native controller finishes closing.
        var displayReady = _returning || available;
        var exitTicks = _returning ? ExitTicks : OpeningTicks;
        var enterTicks = _returning ? EnterTicks : OpeningTicks;
        _age = displayReady ? Math.Min(exitTicks, _age + 1) : 0;
        if (displayReady) _readyAge = Math.Min(enterTicks, _readyAge + 1);
        if (available && _age == exitTicks && _readyAge == enterTicks) Active = false;
    }

    public void Capture(LumenSceneLayer layer, float interpolation, SongSelectHostBinding host, LumenRenderSnapshot? cards = null)
    {
        if (Active) return;
        var player = layer.Player;
        if (host.CourseSelectSong is not null)
        {
            var content = convert(player.CreateInstanceRenderSnapshot("main/courseSelect_", interpolation,
                cards is not null ? ChartCarouselPresentation.OriginalBoards : null), layer);
            _courseView = cards is null ? content : new(1280, 720, content.Quads.Concat(cards.Quads));
            _courseHeader = convert(player.CreateInstanceRenderSnapshot("main/coursePanel_/wd_course_header", interpolation), layer);
            _coursePanel = SongBrowserFramePresentation.CoursePanel(convert(player.CreateInstanceRenderSnapshot("main/coursePanel_", interpolation,
                ["main/coursePanel_/song_name_detail", "main/coursePanel_/wd_course_header"]), layer), layer);
            _courseChrome = chromeSnapshot(layer, interpolation);
        }

    }

    public void CaptureBrowser(LumenRenderSnapshot snapshot, LumenSceneLayer layer, float interpolation,
        LumenRenderSnapshot source)
    {
        if (!Active)
        {
            _browser = snapshot;
            _browserSource = source;
            _browserChrome = chromeSnapshot(layer, interpolation, captureBrowserTitle: true);
        }
    }

    private LumenRenderSnapshot chromeSnapshot(LumenSceneLayer layer, float interpolation,
        bool captureBrowserTitle = false, bool includeTitle = true)
    {
        var drawing = new List<LumenRenderQuad>();
        foreach (var path in Chrome)
        {
            if (!includeTitle && path == "main/modeTitle_") continue;
            var snapshot = convert(layer.Player.CreateInstanceRenderSnapshot(path, interpolation), layer);
            if (captureBrowserTitle && path == "main/modeTitle_") _browserTitle = snapshot;
            drawing.AddRange(snapshot.Quads);
        }
        return new(1280, 720, drawing);
    }

    public static bool BrowserTitleReady(LumenPlayer player)
    {
        if (player.InstanceFrame("main") is not { Playing: false } main
            || !player.TryGetLabelFrame("main", "music", out var music) || main.Frame != music) return false;
        // The authored fade reaches full opacity before its horizontal rebound
        // finishes. Keep the cached heading until the native clip has stopped
        // and both interpolation endpoints describe the same final pose.
        if (player.InstanceFrame("main/modeTitle_") is not { Playing: false }) return false;
        var title = player.CreateInstanceRenderSnapshot("main/modeTitle_", 1).Quads;
        var previous = player.CreateInstanceRenderSnapshot("main/modeTitle_", 0).Quads;
        return title.Length > 0 && title.SequenceEqual(previous)
            && title.Where(q => q.MaskOperation == LumenRenderMaskOperation.Draw)
                .All(q => q.MultiplyColor.Alpha >= .999f);
    }

    public static LumenRenderSnapshot BrowserSnapshot(LumenSceneLayer layer, float interpolation, SongSelectHostBinding host,
        bool scrollTitles = true)
    {
        var browser = convert(layer.Player.CreateInstanceRenderSnapshot("main/genreSelect_", interpolation,
            host.BrowserSongPanel is null ? null : BrowserContents), layer);
        var textOffsets = host.CurvedScrolling ? new Dictionary<LumenRenderQuad, SongBrowserTextViewport>() : null;
        browser = ComposeBrowser(browser, layer, interpolation, host, textOffsets: textOffsets);
        browser = SongBrowserFramePresentation.Compose(browser, layer, interpolation, host, textOffsets);
        browser = outgoingSongCards(browser, host, interpolation, textOffsets);
        // The flow retains the unscrolled source for the difficulty transition;
        // carry each row's viewport through that later marquee pass as well.
        SongBrowserWheelPresentation.Remember(browser, textOffsets);
        return scrollTitles ? SongTitleMarqueePresentation.Compose(browser, host, interpolation, textOffsets) : browser;
    }

    internal static void RememberBrowserCard(SongSelectHostBinding host, IEnumerable<LumenRenderQuad> drawing,
        LumenNativeSurfacePlacement box, int borderCount, float interpolation)
    {
        if (host.BrowserSongMorphWidth(interpolation) < 1) return;
        ScrollFrames.GetOrCreateValue(host).Cards[host.BrowserSongIdentity] = new(drawing.ToArray(), box,
            borderCount, box.Y - host.BrowserSelectedRowTop(interpolation),
            box.Height - Math.Max(0, host.BrowserCardHeight(interpolation) - 65));
    }

    private static LumenRenderSnapshot outgoingSongCards(LumenRenderSnapshot scene, SongSelectHostBinding host, float interpolation,
        Dictionary<LumenRenderQuad, SongBrowserTextViewport>? textOffsets)
    {
        if (!ScrollFrames.TryGetValue(host, out var frames)) return scene;
        var outgoing = host.BrowserOutgoingSongRows(interpolation).ToArray();
        var visible = outgoing.Select(row => (row.Genre, row.Board)).Append(host.BrowserSongIdentity).ToHashSet();
        foreach (var key in frames.Cards.Keys.Where(key => !visible.Contains(key)).ToArray()) frames.Cards.Remove(key);
        if (outgoing.Length == 0) return scene;
        var drawing = scene.Quads.ToList();
        foreach (var row in outgoing)
        {
            if (!frames.Cards.TryGetValue((row.Genre, row.Board), out var previous)) continue;
            var target = previous.Bounds with { Y = row.Top + previous.TopInset,
                Height = previous.CompactHeight + row.Expansion };
            var remaining = Math.Clamp(row.Expansion / 181, 0, 1);
            var outgoingDrawing = new List<LumenRenderQuad>();
            for (var i = 0; i < previous.Quads.Length; i++)
            {
                var quad = previous.Quads[i];
                // Keep text/icons at their original size while they fade. Only
                // the nine-slice border and clipping aperture change height.
                LumenRenderVertex fit(LumenRenderVertex vertex) => i < previous.BorderCount || quad.MaskOperation != LumenRenderMaskOperation.Draw
                    ? LumenPanelMorph.Warp(vertex, previous.Bounds, target)
                    : vertex with { Y = vertex.Y + target.Y - previous.Bounds.Y };
                var alpha = quad.MaskOperation != LumenRenderMaskOperation.Draw ? 1
                    : i < previous.BorderCount ? remaining : remaining * remaining * remaining;
                outgoingDrawing.Add(quad with { TopLeft = fit(quad.TopLeft), TopRight = fit(quad.TopRight),
                    BottomLeft = fit(quad.BottomLeft), BottomRight = fit(quad.BottomRight),
                    MultiplyColor = quad.MultiplyColor with { Alpha = quad.MultiplyColor.Alpha * alpha },
                    AddColor = quad.AddColor with { Alpha = quad.AddColor.Alpha * alpha } });
            }
            drawing.AddRange(SongBrowserWheelPresentation.Move(outgoingDrawing,
                row.Top + (65 + row.Expansion) / 2, host, textOffsets));
        }
        if (host.CurvedScrolling)
        {
            var selected = -1;
            for (var i = 0; i < scene.Quads.Length; i++)
                if (scene.Quads[i].Source == SongBrowserWheelPresentation.SelectedMarker) { selected = i; break; }
            if (selected >= 0)
                return new(scene.StageWidth, scene.StageHeight, scene.Quads.Take(selected)
                    .Concat(drawing.Skip(scene.Quads.Length)).Concat(scene.Quads.Skip(selected)));
        }
        return new(scene.StageWidth, scene.StageHeight, drawing);
    }

    public static LumenRenderSnapshot InsertBrowser(LumenRenderSnapshot scene, LumenRenderSnapshot browser,
        LumenSceneLayer layer, float interpolation)
    {
        var background = convert(layer.Player.CreateInstanceRenderSnapshot("main/backGround_", interpolation), layer);
        var insertion = insertionAfter(scene, background);
        return new(scene.StageWidth, scene.StageHeight,
            scene.Quads.Take(insertion).Concat(browser.Quads).Concat(scene.Quads.Skip(insertion)));
    }

    public static LumenRenderSnapshot ComposeBrowser(LumenRenderSnapshot scene, LumenSceneLayer layer, float interpolation,
        SongSelectHostBinding host, Action<LumenRenderSnapshot>? capture = null,
        Dictionary<LumenRenderQuad, SongBrowserTextViewport>? textOffsets = null)
    {
        if (host.InterpolatedBrowserSongPanel(interpolation) is not { } destination) return scene;
        var player = layer.Player;
        var gold = convert(player.CreateInstanceRenderSnapshot(BrowserContents[0], interpolation), layer);
        var background = gold.Quads.TakeWhile(q => q.MaskOperation == LumenRenderMaskOperation.Draw && q.NativeSurface is null).ToArray();
        if (background.Length == 0) return scene;
        // Resize the stable native compact frame, never an authored expansion tween.
        // Its nine slices retain their corner dimensions and transparent margins.
        var row = convert(player.CreateInstanceRenderSnapshot(BrowserContents[2], interpolation), layer);
        var rowBackground = row.Quads.TakeWhile(q => q.MaskOperation == LumenRenderMaskOperation.Draw && q.NativeSurface is null).ToArray();
        if (rowBackground.Length < 14) return scene;
        var source = panelBounds(new(1280, 720, rowBackground));
        var expansion = Math.Max(0, destination.Height - 65);
        destination = source with { X = source.X - 12, Width = source.Width + 24, Height = source.Height + expansion };
        LumenRenderVertex warp(LumenRenderVertex vertex) => LumenPanelMorph.Warp(vertex, source, destination);
        var drawing = rowBackground.Select((q, index) =>
        {
            // The rotated native highlight casts left. Match the other rows' right
            // shadow while keeping its offset fixed throughout the expansion.
            LumenRenderVertex fit(LumenRenderVertex v)
            {
                // Extrude a single native texel along each flat span. Border
                // gradients across its thickness and the four corner UVs stay intact.
                return warp(index < 5 ? v with { X = 2 * source.X + source.Width - v.X } : v);
            }
            return q with
            {
                TopLeft = fit(q.TopLeft), TopRight = fit(q.TopRight),
                BottomLeft = fit(q.BottomLeft), BottomRight = fit(q.BottomRight), UseNearestSampling = index >= 5,
            };
        }).Concat(gold.Quads.Skip(background.Length).Select(q => q.MaskOperation == LumenRenderMaskOperation.Draw
            ? q with { MultiplyColor = q.MultiplyColor with { Alpha = q.MultiplyColor.Alpha * (host.BrowserSongTitleOpacity(interpolation) * host.BrowserSongContentOpacity(interpolation)) },
                AddColor = q.AddColor with { Alpha = q.AddColor.Alpha * (host.BrowserSongTitleOpacity(interpolation) * host.BrowserSongContentOpacity(interpolation)) } } : q)).ToList();
        var nativeFrame = drawing.Skip(5).Take(rowBackground.Length - 5).ToArray();
        var outline = SongBrowserFramePresentation.NativeFrame(nativeFrame, layer).ToArray();
        drawing = outline.Concat(drawing.Skip(rowBackground.Length)).ToList();
        if (outline.Length > 1) SongBrowserFramePresentation.TraceCard("song", outline[1], host.BrowserTextTick);
        var progress = Math.Clamp(expansion / 181, 0, 1);
        if (progress > 0 && host.BrowserPreviewLevels.Count == 4)
        {
            var center = destination.X + destination.Width / 2;
            var top = destination.Y + 92;
            var tile = rowBackground[5];
            var clip = tile with
            {
                TopLeft = new(destination.X + 12, destination.Y + 74, tile.TopLeft.U, tile.TopLeft.V),
                TopRight = new(destination.X + destination.Width - 12, destination.Y + 74, tile.TopLeft.U, tile.TopLeft.V),
                BottomLeft = new(destination.X + 12, destination.Y + Math.Max(74, destination.Height - 10), tile.TopLeft.U, tile.TopLeft.V),
                BottomRight = new(destination.X + destination.Width - 12, destination.Y + Math.Max(74, destination.Height - 10), tile.TopLeft.U, tile.TopLeft.V),
                MultiplyColor = LumenRenderColor.White, AddColor = LumenRenderColor.Transparent,
                MaskOperation = LumenRenderMaskOperation.Push, MaskDepth = 0,
            };
            drawing.Add(clip);
            var buttonPaint = VectorUiPaint.Solid(new(.66f, .45f, .16f, 1));
            var reveal = host.BrowserSongDifficultyOpacity(interpolation) * host.BrowserSongContentOpacity(interpolation);
            var contentAlpha = 1f;
            for (var i = 0; i < 4; i++)
            {
                var x = center - 219 + i * 114;
                var level = host.BrowserPreviewLevels[i];
                drawing.AddRange(LumenVectorGeometry.Frame(new(x, top, 96, 128), buttonPaint, progress * reveal, border: 2)
                    .Select(q => q with { MaskDepth = 1 }));
                contentAlpha = i == 3 ? host.BrowserPreviewOpacity : 1;
                art("main/wd_song_icon_" + (i == 3 && host.BrowserPreviewShowsUra ? "extreme" : LevelNames[i]), x + 12, top + 8, 72, 72, level is null ? .3f : 1);
                if (level is not { } stars) continue;
                if (stars is >= 1 and <= 10)
                {
                    art($"main/wd_song_rating_{i}", x + 25.6f, top + 82, 44.8f, 24, counter: true);
                    continue;
                }
                var two = stars >= 10;
                art("main/wd_song_star", x + (two ? 22 : 28), top + 85, 15, 15);
                if (two) art($"main/wd_song_level_{i}_1", x + 41, top + 82, 16, 20);
                art($"main/wd_song_level_{i}_0", x + (two ? 57 : 48), top + 82, 16, 20);
            }

            drawing.Add(clip with { MaskOperation = LumenRenderMaskOperation.Pop, MaskDepth = 1 });


            void art(string path, float x, float y, float width, float height, float alpha = 1, bool counter = false)
            {
                LumenRenderSnapshot snapshot;
                player.TrySetInstanceLayoutVisibility(path, true);
                try { snapshot = convert(player.CreateInstanceRenderSnapshot(path, 1), layer); }
                finally { player.TrySetInstanceLayoutVisibility(path, false); }
                if (counter)
                {
                    snapshot = new(1280, 720, snapshot.Quads.Where(q =>
                    {
                        var x0 = Math.Min(Math.Min(q.TopLeft.X, q.TopRight.X), Math.Min(q.BottomLeft.X, q.BottomRight.X));
                        var x1 = Math.Max(Math.Max(q.TopLeft.X, q.TopRight.X), Math.Max(q.BottomLeft.X, q.BottomRight.X));
                        var y0 = Math.Min(Math.Min(q.TopLeft.Y, q.TopRight.Y), Math.Min(q.BottomLeft.Y, q.BottomRight.Y));
                        var y1 = Math.Max(Math.Max(q.TopLeft.Y, q.TopRight.Y), Math.Max(q.BottomLeft.Y, q.BottomRight.Y));
                        return Math.Abs(x1 - x0 - 112) < .1f && Math.Abs(y1 - y0 - 60) < .1f;
                    }));
                }
                if (snapshot.Quads.Length == 0) return;
                if (path == "main/wd_song_star")
                {
                    snapshot = new(1280, 720, snapshot.Quads.Select(q =>
                    {
                        var vertices = new[] { q.TopLeft, q.TopRight, q.BottomLeft, q.BottomRight };
                        var u0 = vertices.Min(v => v.U);
                        var v0 = vertices.Min(v => v.V); var dv = vertices.Max(v => v.V) - v0;
                        LumenRenderVertex crop(LumenRenderVertex v) => v with
                        { U = u0 + (v.U - u0) * .5f, V = v0 + dv * .15f + (v.V - v0) * .7f };
                        return q with { TopLeft = crop(q.TopLeft), TopRight = crop(q.TopRight),
                            BottomLeft = crop(q.BottomLeft), BottomRight = crop(q.BottomRight) };
                    }));
                }
                var box = bounds(snapshot);
                var scale = Math.Min(width / box.Width, height / box.Height);
                LumenRenderVertex fit(LumenRenderVertex v) => v with
                {
                    X = x + width / 2 + (v.X - box.X - box.Width / 2) * scale,
                    Y = y + height / 2 + (v.Y - box.Y - box.Height / 2) * scale,
                };
                drawing.AddRange(snapshot.Quads.Select(q => q with
                {
                    TopLeft = fit(q.TopLeft), TopRight = fit(q.TopRight), BottomLeft = fit(q.BottomLeft), BottomRight = fit(q.BottomRight),
                    MultiplyColor = new(1, 1, 1, progress * reveal * alpha * contentAlpha),
                    AddColor = LumenRenderColor.Transparent,
                    Blend = LumenRenderBlend.Normal, MaskDepth = 1,
                }));
            }
        }
        if (host.BrowserSongMorphWidth(interpolation) < 1 && outline.Length > 1)
        {
            var original = LumenNativeFrame.Bounds(outline[1]);
            var width = Math.Max(0, original.Width * host.BrowserSongMorphWidth(interpolation));
            var target = original with { X = original.X + (original.Width - width) / 2, Width = width };
            var edgeOpacity = VerticalSongBrowser.Ease((width - 12) / 24);
            drawing = drawing.Select((q, i) => i < outline.Length ? LumenPanelMorph.WarpHorizontal(q with
                {
                    MultiplyColor = q.MultiplyColor with { Alpha = q.MultiplyColor.Alpha * edgeOpacity },
                    AddColor = q.AddColor with { Alpha = q.AddColor.Alpha * edgeOpacity },
                }, original, target)
                : q.MaskOperation != LumenRenderMaskOperation.Draw ? q : q with
                {
                    MultiplyColor = q.MultiplyColor with { Alpha = q.MultiplyColor.Alpha * host.BrowserSongMorphWidth(interpolation) },
                    AddColor = q.AddColor with { Alpha = q.AddColor.Alpha * host.BrowserSongMorphWidth(interpolation) },
                })
                .Where(q => q.HasValue).Select(q => q.GetValueOrDefault()).ToList();
        }
        if (outline.Length > 1) RememberBrowserCard(host, drawing, LumenNativeFrame.Bounds(outline[1]), outline.Length, interpolation);
        if (host.CurvedScrolling && drawing.Count > 0)
            drawing[0] = drawing[0] with { Source = SongBrowserWheelPresentation.SelectedMarker };
        drawing = SongBrowserWheelPresentation.Move(drawing, destination.Y + destination.Height / 2, host, textOffsets).ToList();
        var column = convert(player.CreateInstanceRenderSnapshot("main/genreSelect_", interpolation, BrowserContents), layer);
        capture?.Invoke(new(1280, 720, column.Quads.Concat(drawing)));
        var insertion = insertionAfter(scene, column);
        return new(scene.StageWidth, scene.StageHeight, scene.Quads.Take(insertion).Concat(drawing).Concat(scene.Quads.Skip(insertion)));
    }

    public LumenRenderSnapshot Compose(LumenRenderSnapshot scene, LumenSceneLayer layer, float interpolation,
        LumenRenderSnapshot? customCards, bool ready, SongSelectHostBinding host)
    {
        if (!Active || _browser is null) return scene;
        var leaving = VerticalSongBrowser.Ease((_previousAge + (_age - _previousAge) * interpolation) / (_returning ? ExitTicks : OpeningTicks));
        var entering = VerticalSongBrowser.Ease((_previousReadyAge + (_readyAge - _previousReadyAge) * interpolation) / (_returning ? EnterTicks : OpeningTicks));
        var drawing = new List<LumenRenderQuad>();
        if (_returning)
        {
            if (_coursePanel is not null) append(_coursePanel, 1 - leaving, 0, 0);
            if (_courseView is not null) append(_courseView, 1 - leaving, 0, 0);
            if (_courseHeader is not null) append(SongTitleMarqueePresentation.Compose(_courseHeader, host, interpolation), 1 - leaving, 0, 0);
            if (_courseChrome is not null) append(_courseChrome, 1 - leaving, 0, 0);
            if (entering > 0)
            {
                // The cached list must retain its unscrolled title geometry;
                // a clipped marquee snapshot cannot be restored to its start.
                append(host.CourseSelectSong is null ? BrowserSnapshot(layer, interpolation, host)
                    : SongTitleMarqueePresentation.Compose(_browserSource ?? _browser, host, interpolation), entering, 0, 0);
                // Utility selection can legitimately hide the native ranking.
                // Fade the live auxiliary UI, not an old ranking from before
                // course select. Keep the title until its native fade-in is ready.
                append(chromeSnapshot(layer, interpolation, includeTitle: false), entering, 0, 0);
                if (_browserTitle is not null) append(_browserTitle, entering, 0, 0);
            }
        }
        else
        {
            append(_browser, 1 - leaving, 0, -12 * leaving);
            if (_browserChrome is not null) append(_browserChrome, 1 - leaving, 0, 0);
            if (entering > 0 && ready)
            {
                var player = layer.Player;
                append(SongBrowserFramePresentation.CoursePanel(convert(player.CreateInstanceRenderSnapshot("main/coursePanel_", interpolation,
                    ["main/coursePanel_/song_name_detail", "main/coursePanel_/wd_course_header"]), layer), layer), entering, 64 * (1 - entering), 0);
                append(convert(player.CreateInstanceRenderSnapshot("main/courseSelect_", interpolation,
                    customCards is not null ? ChartCarouselPresentation.OriginalBoards : null), layer), entering, 64 * (1 - entering), 0);
                if (customCards is not null) append(customCards, entering, 64 * (1 - entering), 0);
                append(SongTitleMarqueePresentation.Compose(convert(player.CreateInstanceRenderSnapshot("main/coursePanel_/wd_course_header", interpolation), layer), host, interpolation), entering, 64 * (1 - entering), 0);
                append(chromeSnapshot(layer, interpolation), entering, 0, 0);
            }
        }
        var background = convert(layer.Player.CreateInstanceRenderSnapshot("main/backGround_", interpolation), layer);
        var insertion = insertionAfter(scene, background);
        return new(scene.StageWidth, scene.StageHeight, scene.Quads.Take(insertion).Concat(drawing).Concat(scene.Quads.Skip(insertion)));

        void append(LumenRenderSnapshot snapshot, float alpha, float dx, float dy)
        {
            if (alpha <= 0) return;
            LumenRenderVertex move(LumenRenderVertex vertex) => vertex with { X = vertex.X + dx, Y = vertex.Y + dy };
            drawing.AddRange(snapshot.Quads.Select(q => q with
            {
                TopLeft = move(q.TopLeft), TopRight = move(q.TopRight), BottomLeft = move(q.BottomLeft), BottomRight = move(q.BottomRight),
                // Stencil writes define geometry, not opacity. Fading a mask
                // makes it fail its alpha test halfway through the transition.
                MultiplyColor = q.MaskOperation == LumenRenderMaskOperation.Draw
                    ? q.MultiplyColor with { Alpha = q.MultiplyColor.Alpha * alpha } : q.MultiplyColor,
                AddColor = q.MaskOperation == LumenRenderMaskOperation.Draw
                    ? q.AddColor with { Alpha = q.AddColor.Alpha * alpha } : q.AddColor,
            }));
        }
    }

    public static LumenRenderSnapshot InsertCourseCards(LumenRenderSnapshot scene, LumenRenderSnapshot cards,
        LumenSceneLayer layer, IReadOnlyList<string> excluded, float interpolation)
    {
        // Keep custom charts at the native difficulty depth, behind playerBoard_
        // and the footer. Additional drums must disappear behind the same foreground.
        var anchor = convert(layer.Player.CreateInstanceRenderSnapshot("main/courseSelect_", interpolation, excluded), layer);
        var insertion = insertionAfter(scene, anchor);
        return new(scene.StageWidth, scene.StageHeight,
            scene.Quads.Take(insertion).Concat(cards.Quads).Concat(scene.Quads.Skip(insertion)));
    }

    private static int insertionAfter(LumenRenderSnapshot scene, LumenRenderSnapshot anchor)
    {
        if (anchor.Quads.Length == 0) return 0;
        var last = anchor.Quads[^1];
        for (var i = scene.Quads.Length - 1; i >= 0; i--)
            if (scene.Quads[i] == last) return i + 1;
        return 0;
    }

    private static LumenNativeSurfacePlacement panelBounds(LumenRenderSnapshot snapshot)
    {
        // The first five native quads are the drop shadow. Its transparent padding
        // is outside the panel and must not shrink the visible frame during a morph.
        return bounds(snapshot.Quads.Length >= 14 ? new(1280, 720, snapshot.Quads.Skip(5).Take(9)) : snapshot);
    }
    private static LumenNativeSurfacePlacement bounds(LumenRenderSnapshot snapshot) => LumenNativeFrame.Bounds(snapshot.Quads);
    private static LumenRenderSnapshot convert(LumenRenderSnapshot snapshot, LumenSceneLayer layer)
    {
        LumenRenderVertex vertex(LumenRenderVertex value)
        {
            var point = layer.Transform.Transform(value.X, value.Y);
            return value with { X = point.X, Y = point.Y };
        }
        return new(1280, 720, snapshot.Quads.Select(q => q with
        {
            TextureIndex = q.NativeSurface is null ? q.TextureIndex + layer.TextureOffset : 0,
            TopLeft = vertex(q.TopLeft), TopRight = vertex(q.TopRight), BottomLeft = vertex(q.BottomLeft), BottomRight = vertex(q.BottomRight),
        }));
    }
}
