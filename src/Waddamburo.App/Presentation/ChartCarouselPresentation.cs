using Waddamburo.Catalog;
using Waddamburo.Game;
using Waddamburo.Game.Lumen;
using Waddamburo.Game.SongSelect;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Presentation;

/// <summary>Slides the live, initialized Lumen boards without restarting their nested animations.</summary>
internal sealed class ChartCarouselPresentation : IDisposable
{
    public static readonly string[] OriginalBoards = [
        "courseSelect_/board_course0", "courseSelect_/board_course1",
        "courseSelect_/board_course2", "courseSelect_/board_course3",
        "courseSelect_/cursor_1p_", "courseSelect_/cursor_2p_",
    ];
    private SongChartCarousel? _model;
    private LumenPlayer? _moreArrow;
    private LumenNativeSurfacePlacement _arrowRest;
    private int _arrowTicks;
    private readonly float[] _cursor = new float[2];
    private readonly float[] _previousCursor = new float[2];
    private float _scroll, _previousScroll;

    public void Advance(SongChartCarousel model, bool fastScrolling)
    {
        if (!ReferenceEquals(model, _model)) return;
        // Retain every original animation frame, played at half speed.
        if (++_arrowTicks % 2 == 1) _moreArrow?.Advance(LumenInputSnapshot.Empty);
        _previousScroll = _scroll;
        _scroll = ease(_scroll, model.WindowStart, fastScrolling);
        foreach (var side in model.Sides)
        {
            _previousCursor[side] = _cursor[side];
            _cursor[side] = ease(_cursor[side], model.Selected(side), fastScrolling);
        }
    }

    public LumenRenderSnapshot Snapshot(SongChartCarousel model, LumenSceneLayer layer, LumenMovieContent content,
        float interpolation, bool verticalLayout = true)
    {
        if (!ReferenceEquals(model, _model))
        {
            Close(); _model = model;
            // The original 40-frame parent owns the arrow's travel and fade.
            // Sprite 899 alone is static. Keep a fixed rest origin so recentering
            // each snapshot does not cancel the authored movement.
            _moreArrow = new(content.Definition, 1280, 720, 900);
            var rest = _moreArrow.CreateInstanceRenderSnapshot("arrowR");
            if (rest.Quads.Length > 0) _arrowRest = LumenNativeFrame.Bounds(rest.Quads);
            _scroll = _previousScroll = model.WindowStart;
            foreach (var side in model.Sides) _cursor[side] = _previousCursor[side] = model.Selected(side);
        }
        var movie = layer.Player;
        var chartX = verticalLayout ? DifficultySelectLayout.ChartX : 0;
        var chartY = verticalLayout ? DifficultySelectLayout.ChartY : 0;
        if (!movie.TryGetInstanceTransform(OriginalBoards[0], out var first)
            || !movie.TryGetInstanceTransform(OriginalBoards[1], out var second))
            return new(1280, 720, []);
        var step = second.X - first.X;
        var scroll = _previousScroll + (_scroll - _previousScroll) * interpolation;
        var quads = new List<LumenRenderQuad>();
        var cursors = new List<LumenRenderQuad>();
        var start = Math.Max(0, (int)MathF.Floor(scroll) - 1);
        var end = Math.Min(model.Slots.Length, (int)MathF.Ceiling(scroll) + SongChartCarousel.VisibleCount + 1);
        for (var index = start; index < end; index++)
        {
            var slot = model.Slots[index];
            var sourceIndex = Math.Min((int)slot.Course, 3);
            var path = OriginalBoards[sourceIndex];
            if (!movie.TryGetInstanceTransform(path, out var at)) continue;
            var body = path + (slot.Course == TaikoCourse.Ura ? "/body_back" : "/body_top");
            var other = path + (slot.Course == TaikoCourse.Ura ? "/body_top" : "/body_back");
            var frame = movie.InstanceFrame(body);
            var visible = movie.InstanceVisibility(body);
            var otherVisible = movie.InstanceVisibility(other);
            // Read the live initialized art. Only the star count and front/back vary per chart;
            // seeking a new standalone board had advanced its drums and flip effects accidentally.
            try
            {
                if (slot.Chart is { } chart)
                {
                    var stars = Math.Clamp(chart.Level ?? 1, 1, 10);
                    if (frame?.Frame != stars - 1) movie.TryGotoLabel(body, $"_{stars}", play: false);
                    movie.TrySetInstanceVisibility(body, true);
                    movie.TrySetInstanceVisibility(other, false);
                }
                var card = movie.CreateInstanceRenderSnapshot(path, interpolation);
                var dx = first.X + (index - scroll) * step - at.X;
                append(card, dx, 0);
                if (slot.Chart is { } named && card.Quads.Length > 0)
                {
                    LumenRenderVertex corner(float x, float y, float u, float v)
                    {
                        var position = at.Transform(x, y);
                        var p = layer.Transform.Transform(position.X + chartX, position.Y + chartY);
                        var shift = layer.Transform.Transform(dx, 0);
                        return new(p.X + shift.X - layer.Transform.X, p.Y + shift.Y - layer.Transform.Y, u, v);
                    }
                    // Classic's larger drums reach the footer; put their chart name on the drum.
                    const int nameY = 475;
                    quads.Add(new(0, corner(2, nameY, 0, 0), corner(102, nameY, 1, 0),
                        corner(102, nameY + 24, 1, 1), corner(2, nameY + 24, 0, 1),
                        card.Quads[0].MultiplyColor, LumenRenderColor.Transparent,
                        NativeSurface: new($"{SpeedText.Prefix}100x24:{named.DifficultyName}")));
                }
            }
            finally
            {
                if (frame is { } old && movie.InstanceFrame(body)?.Frame != old.Frame)
                    movie.TryGotoLabel(body, $"_{old.Frame + 1}", old.Playing);
                if (visible is { } wasVisible) movie.TrySetInstanceVisibility(body, wasVisible);
                if (otherVisible is { } wasOtherVisible) movie.TrySetInstanceVisibility(other, wasOtherVisible);
            }
        }
        foreach (var side in model.Sides)
        {
            if (model.IsUtility(side)) continue;
            var path = OriginalBoards[4 + side];
            if (!movie.TryGetInstanceTransform(path, out var at)) continue;
            var column = _previousCursor[side] + (_cursor[side] - _previousCursor[side]) * interpolation;
            var cursor = new List<LumenRenderQuad>();
            append(movie.CreateInstanceRenderSnapshot(path, interpolation), first.X + (column - scroll) * step - at.X, 0, cursor);
            // A visible board's P2 flag may project into the next-board hint.
            // Fade the hidden boards, preserving both players' complete flags.
            var visibility = Math.Clamp(column - scroll + 1, 0, 1)
                * Math.Clamp(scroll + SongChartCarousel.VisibleCount - column, 0, 1);
            cursors.AddRange(cursor.Select(q => q with
            {
                MultiplyColor = q.MultiplyColor with { Alpha = q.MultiplyColor.Alpha * visibility },
                AddColor = q.AddColor with { Alpha = q.AddColor.Alpha * visibility },
            }));
        }
        // Classic's Settings buttons extend closer to the columns: keep a clipped
        // outgoing board inside the chart area instead of drawing over Sounds.
        var left = layer.Transform.Transform(first.X + chartX + (verticalLayout ? -22 : 8), 0).X;
        var fadeLeftEnd = layer.Transform.Transform(first.X + chartX + (verticalLayout ? 0 : 14), 0).X;
        var hintLeft = layer.Transform.Transform(first.X + chartX
            + SongChartCarousel.VisibleCount * step - 6 * Math.Abs(first.M11), 0).X;
        // Only half of the next board is a hint; the remaining panel stays clear.
        var right = hintLeft + 52 * Math.Abs(first.M11 * layer.Transform.M11);
        var cards = quads.Select(quad => clip(quad, left, right)).OfType<LumenRenderQuad>()
            .SelectMany(quad => LumenQuadGeometry.FadeLeft(quad, left, fadeLeftEnd))
            .SelectMany(quad => LumenQuadGeometry.FadeRight(quad, hintLeft, right))
            .Concat(cursors.Select(quad => clip(quad, left - 40 * Math.Abs(layer.Transform.M11),
                right + 40 * Math.Abs(layer.Transform.M11))).OfType<LumenRenderQuad>());
        if (scroll + SongChartCarousel.VisibleCount >= model.Slots.Length - .002f || _moreArrow is null)
            return new(1280, 720, cards);
        var arrowFraction = _arrowTicks == 0 ? 1 : ((_arrowTicks + 1) % 2 + interpolation) / 2;
        var arrow = _moreArrow.CreateInstanceRenderSnapshot("arrowR", arrowFraction);
        var bounds = _arrowRest;
        if (arrow.Quads.Length == 0 || bounds.Width <= 0 || bounds.Height <= 0) return new(1280, 720, cards);
        // Keep Classic's vertical title and subtitle clear on the right.
        var targetY = layer.Transform.Transform(0, verticalLayout ? 350 : 520).Y;
        var targetX = right + (verticalLayout ? 36 : 16) * Math.Abs(layer.Transform.M11);
        var size = 32 * Math.Abs(layer.Transform.M11);
        var scale = size / Math.Max(bounds.Width, bounds.Height);
        LumenRenderVertex arrowVertex(LumenRenderVertex vertex) => vertex with
        {
            X = targetX + (vertex.X - bounds.X - bounds.Width / 2) * scale,
            Y = targetY + (vertex.Y - bounds.Y - bounds.Height / 2) * scale,
        };
        return new(1280, 720, cards.Concat(arrow.Quads.Select(quad => quad with
        {
            TextureIndex = layer.TextureOffset + quad.TextureIndex,
            TopLeft = arrowVertex(quad.TopLeft), TopRight = arrowVertex(quad.TopRight),
            BottomLeft = arrowVertex(quad.BottomLeft), BottomRight = arrowVertex(quad.BottomRight),
        })));

        void append(LumenRenderSnapshot snapshot, float dx, float dy, List<LumenRenderQuad>? target = null)
        {
            LumenRenderVertex move(LumenRenderVertex vertex)
            {
                var p = layer.Transform.Transform(vertex.X + dx, vertex.Y + dy);
                return vertex with { X = p.X, Y = p.Y };
            }
            foreach (var quad in snapshot.Quads)
                (target ?? quads).Add(quad with
                {
                    TextureIndex = quad.NativeSurface is null ? layer.TextureOffset + quad.TextureIndex : 0,
                    TopLeft = move(quad.TopLeft), TopRight = move(quad.TopRight),
                    BottomLeft = move(quad.BottomLeft), BottomRight = move(quad.BottomRight),
                });
        }
    }

    private static float ease(float current, float target, bool fastScrolling) =>
        Math.Abs(target - current) < .002f ? target : current + (target - current) * (fastScrolling ? .55f : .28f);

    private static LumenRenderQuad? clip(LumenRenderQuad quad, float left, float right)
    {
        var min = Math.Min(quad.TopLeft.X, quad.TopRight.X);
        var max = Math.Max(quad.TopLeft.X, quad.TopRight.X);
        if (max <= left || min >= right) return null;
        if (min >= left && max <= right) return quad;
        LumenRenderVertex edge(LumenRenderVertex a, LumenRenderVertex b)
        {
            var x = Math.Clamp(a.X, left, right);
            var t = Math.Abs(b.X - a.X) < .0001f ? 0 : (x - a.X) / (b.X - a.X);
            return new(x, a.Y + (b.Y - a.Y) * t, a.U + (b.U - a.U) * t, a.V + (b.V - a.V) * t);
        }
        return quad with
        {
            TopLeft = edge(quad.TopLeft, quad.TopRight), TopRight = edge(quad.TopRight, quad.TopLeft),
            BottomLeft = edge(quad.BottomLeft, quad.BottomRight), BottomRight = edge(quad.BottomRight, quad.BottomLeft),
        };
    }

    public void Close() { _model = null; _moreArrow = null; _arrowTicks = 0; _scroll = _previousScroll = 0; }
    public void Dispose() => Close();
}
