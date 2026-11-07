using System.Numerics;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Game.Gameplay;

/// <summary>
/// What a chart's effects do to the scene's movies each frame, a function of chart time alone: the parts that sit at the
/// hit target follow its #JPOSSCROLL moves, each element takes its modchart transform (<see cref="Catalog.ChartMods"/>),
/// then the camera's. A chart without effects leaves every layer as it is.
/// </summary>
public sealed class StageEffects
{
    private readonly ChartModValues _mods;
    private readonly NoteGeometry _geometry;
    private readonly bool _moves;
    // Each movie's pivot, kept with the movie (not this instance): lanes rebuilt mid-song (seeks, the editors) use the one
    // taken at the song's start (Prime), not the pose of the moment, so a layout's scale lands the same everywhere.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LumenPlayer, System.Runtime.CompilerServices.StrongBox<(float X, float Y)>> Pivots = new();

    // The player's 4:3 layout under the chart's modchart (ChartEffects.Layout): applied after it, element and camera.
    private readonly ChartModValues? _layout;

    public StageEffects(ChartModValues mods, NoteGeometry geometry, bool targetMoves, ChartModValues? layout = null)
    {
        _mods = mods;
        _layout = layout is { IsEmpty: false } ? layout : null;
        _geometry = geometry;
        _moves = targetMoves;
    }

    /// <summary>Whether any layer can move at all (else <see cref="Place"/> is never needed).</summary>
    public bool IsActive => _moves || !_mods.IsEmpty || _layout is not null;

    /// <summary>Describes a movie using the same pivots and transforms as its live placement.</summary>
    public StageEffectElement? Describe(LumenSceneLayer layer, string target, bool followsTarget, TimeSpan time)
    {
        var placed = Place(layer, target, followsTarget, time);
        var quads = layer.Player.CreateRenderSnapshot().Quads;
        if (quads.IsEmpty) return null;
        var minimum = new Vector2(float.PositiveInfinity);
        var maximum = new Vector2(float.NegativeInfinity);
        foreach (var quad in quads.Where(q => q.MaskOperation == LumenRenderMaskOperation.Draw
            && (q.MultiplyColor.Alpha > 0 || q.AddColor.Alpha > 0)))
            foreach (var vertex in (LumenRenderVertex[])[quad.TopLeft, quad.TopRight, quad.BottomRight, quad.BottomLeft])
            {
                var point = placed.Transform.Transform(vertex.X, vertex.Y);
                var position = new Vector2(point.X, point.Y);
                minimum = Vector2.Min(minimum, position); maximum = Vector2.Max(maximum, position);
            }
        if (!float.IsFinite(minimum.X) || !float.IsFinite(minimum.Y) || !float.IsFinite(maximum.X) || !float.IsFinite(maximum.Y)) return null;
        var pivot = target == "playfield" ? _geometry.Origin(time) : pivotOf(layer);
        var own = Element(target, time, pivot)?.Matrix ?? LumenMatrix.Identity;
        var parent = Element("camera", time, (640, 360))?.Matrix ?? LumenMatrix.Identity;
        var transformed = own.Then(parent).Transform(pivot.Item1, pivot.Item2);
        return new(target, minimum, maximum, new(transformed.X, transformed.Y), parent);
    }

    /// <summary>
    /// Green's gameplay role under its modchart name (the same role whatever skin or random mix fills it); the lane's
    /// own movies and the note templates are the playfield. Null: the effects leave it alone.
    /// ponytail: Waiwai's roles are not mapped (their parts stay put).
    /// </summary>
    public static string? TargetOf(string? role) => role switch
    {
        "bg_nomal" or "bg_fever" => "background",
        "donbg" => "donbg",
        "renda" => "renda",
        "dance" => "dancers",
        "dodai" => "stage",
        "fever" => "fever",
        "chibi" => "runners",
        "lane_obi" => "board",
        "song_info" => "songinfo",
        "player_name" => "nameplate",
        "action_gogotime" => "gogo",
        "action_result" => "banner",
        "gage_fire_1p" => "gauge",
        { } name when name.StartsWith("gage_don_1p_", StringComparison.Ordinal) => "gauge",
        "lane" or "lane_hit" or "lane_hit_effect" or "renda_num" or "lane_syousetsu" => "playfield",
        { } name when name.StartsWith("onp_", StringComparison.Ordinal) => "playfield",
        _ => null,
    };

    /// <summary>
    /// A layer as the effects place it at <paramref name="time"/>: moved with the target when it
    /// <paramref name="followsTarget"/> (judgement text, roll counter, balloon, flights), under <paramref name="target"/>'s
    /// transform about the element's own centre (the playfield about the target), then under the camera's.
    /// </summary>
    public LumenSceneLayer Place(LumenSceneLayer layer, string? target, bool followsTarget, TimeSpan time)
    {
        var (matrix, alpha, moved) = (LumenMatrix.Identity, layer.Alpha, false);
        if (followsTarget && _moves && _geometry.JudgeOffset(time) is var (dx, dy) && (dx != 0 || dy != 0))
            (matrix, moved) = (LumenMatrix.Identity with { X = dx, Y = dy }, true);
        var pivot = target is null ? default : target == "playfield" ? _geometry.Origin(time) : pivotOf(layer);
        if (target is not null && Element(target, time, pivot) is { } own)
            (matrix, alpha, moved) = (matrix.Then(own.Matrix), alpha * own.Alpha, true);
        if (target is not null && _layout is not null && Element(_layout, target, time, pivot) is { } laid)
            (matrix, alpha, moved) = (matrix.Then(laid.Matrix), alpha * laid.Alpha, true);
        if (Element("camera", time, (640, 360)) is { } camera)
            (matrix, alpha, moved) = (matrix.Then(camera.Matrix), alpha * camera.Alpha, true);
        if (_layout is not null && Element(_layout, "camera", time, (640, 360)) is { } frame)
            (matrix, alpha, moved) = (matrix.Then(frame.Matrix), alpha * frame.Alpha, true);
        return moved ? layer with { Transform = layer.Transform.Then(matrix), Alpha = Math.Clamp(alpha, 0, 1) } : layer;
    }

    /// <summary>
    /// Where <paramref name="target"/>'s stage points end up at <paramref name="time"/>: its modchart transform, the
    /// player's layout under it, then the cameras (what <see cref="Place"/> does to a layer, for host-drawn parts).
    /// </summary>
    public LumenMatrix Matrix(string target, (float X, float Y) pivot, TimeSpan time)
    {
        var matrix = LumenMatrix.Identity;
        if (Element(target, time, pivot) is { } own) matrix = matrix.Then(own.Matrix);
        if (_layout is not null && Element(_layout, target, time, pivot) is { } laid) matrix = matrix.Then(laid.Matrix);
        if (Element("camera", time, (640, 360)) is { } camera) matrix = matrix.Then(camera.Matrix);
        if (_layout is not null && Element(_layout, "camera", time, (640, 360)) is { } frame) matrix = matrix.Then(frame.Matrix);
        return matrix;
    }

    /// <summary>An element's transform about <paramref name="pivot"/> and its opacity at <paramref name="time"/>; null when it is untouched.</summary>
    public (LumenMatrix Matrix, float Alpha)? Element(string target, TimeSpan time, (float X, float Y) pivot) =>
        Element(_mods, target, time, pivot);

    private static (LumenMatrix Matrix, float Alpha)? Element(ChartModValues mods, string target, TimeSpan time, (float X, float Y) pivot)
    {
        if (mods.IsEmpty)
            return null;
        var scale = mods.Value(target, "scale", time);
        return Transform(mods.Value(target, "x", time), mods.Value(target, "y", time), mods.Value(target, "rotation", time),
            scale * mods.Value(target, "scalex", time), scale * mods.Value(target, "scaley", time), mods.Value(target, "alpha", time), pivot);
    }

    /// <summary>Moved by (x, y), turned by <paramref name="degrees"/> and scaled about <paramref name="pivot"/>; null when that changes nothing.</summary>
    public static (LumenMatrix Matrix, float Alpha)? Transform(double x, double y, double degrees, double scaleX, double scaleY, double alpha,
        (float X, float Y) pivot)
    {
        if (x == 0 && y == 0 && degrees == 0 && scaleX == 1 && scaleY == 1 && alpha == 1)
            return null;
        var turn = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(turn), Math.Sin(turn));
        var (m11, m12, m21, m22) = ((float)(scaleX * cos), (float)(scaleX * sin), (float)(-scaleY * sin), (float)(scaleY * cos));
        return (new LumenMatrix(m11, m12, m21, m22,
            pivot.X - (m11 * pivot.X + m21 * pivot.Y) + (float)x,
            pivot.Y - (m12 * pivot.X + m22 * pivot.Y) + (float)y), (float)alpha);
    }

    /// <summary>Takes the movies' pivots now (the song's start) unless they have them already.</summary>
    public void Prime(IEnumerable<LumenSceneLayer> layers)
    {
        foreach (var layer in layers)
            _ = pivotOf(layer);
    }

    // The centre of what a movie draws on the stage, found the first time it draws something.
    private (float X, float Y) pivotOf(LumenSceneLayer layer)
    {
        if (Pivots.TryGetValue(layer.Player, out var known))
            return known.Value;
        var quads = layer.Player.CreateRenderSnapshot().Quads;
        if (quads.IsEmpty)
            return (640, 360);
        var corners = quads.SelectMany(static quad => (LumenRenderVertex[])[quad.TopLeft, quad.TopRight, quad.BottomRight, quad.BottomLeft])
            .Select(vertex => layer.Transform.Transform(vertex.X, vertex.Y)).ToArray();
        return Pivots.GetValue(layer.Player, static _ => new()).Value = ((corners.Min(static c => c.X) + corners.Max(static c => c.X)) / 2,
            (corners.Min(static c => c.Y) + corners.Max(static c => c.Y)) / 2);
    }
}
