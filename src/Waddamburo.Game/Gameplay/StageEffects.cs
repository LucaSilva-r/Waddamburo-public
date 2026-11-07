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
    private readonly Dictionary<LumenPlayer, (float X, float Y)> _pivots = [];

    public StageEffects(ChartModValues mods, NoteGeometry geometry, bool targetMoves)
    {
        _mods = mods;
        _geometry = geometry;
        _moves = targetMoves;
    }

    /// <summary>Whether any layer can move at all (else <see cref="Place"/> is never needed).</summary>
    public bool IsActive => _moves || !_mods.IsEmpty;

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
        if (target is not null && Element(target, time, target == "playfield" ? _geometry.Origin(time) : pivotOf(layer)) is { } own)
            (matrix, alpha, moved) = (matrix.Then(own.Matrix), alpha * own.Alpha, true);
        if (Element("camera", time, (640, 360)) is { } camera)
            (matrix, alpha, moved) = (matrix.Then(camera.Matrix), alpha * camera.Alpha, true);
        return moved ? layer with { Transform = layer.Transform.Then(matrix), Alpha = Math.Clamp(alpha, 0, 1) } : layer;
    }

    /// <summary>An element's transform about <paramref name="pivot"/> and its opacity at <paramref name="time"/>; null when it is untouched.</summary>
    public (LumenMatrix Matrix, float Alpha)? Element(string target, TimeSpan time, (float X, float Y) pivot)
    {
        if (_mods.IsEmpty)
            return null;
        var scale = _mods.Value(target, "scale", time);
        return Transform(_mods.Value(target, "x", time), _mods.Value(target, "y", time), _mods.Value(target, "rotation", time),
            scale * _mods.Value(target, "scalex", time), scale * _mods.Value(target, "scaley", time), _mods.Value(target, "alpha", time), pivot);
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

    // The centre of what a movie draws on the stage, found the first time it draws something.
    private (float X, float Y) pivotOf(LumenSceneLayer layer)
    {
        if (_pivots.TryGetValue(layer.Player, out var known))
            return known;
        var quads = layer.Player.CreateRenderSnapshot().Quads;
        if (quads.IsEmpty)
            return (640, 360);
        var corners = quads.SelectMany(static quad => (LumenRenderVertex[])[quad.TopLeft, quad.TopRight, quad.BottomRight, quad.BottomLeft])
            .Select(vertex => layer.Transform.Transform(vertex.X, vertex.Y)).ToArray();
        return _pivots[layer.Player] = ((corners.Min(static c => c.X) + corners.Max(static c => c.X)) / 2,
            (corners.Min(static c => c.Y) + corners.Max(static c => c.Y)) / 2);
    }
}
