using Waddamburo.Formats.Lmb;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The entry's own animated blue arrow pair (sprite 142 of entry.lm, the arrows that slide out and fade
/// beside the mode panels), played once per drum and moved to that drum's stand for the home setup.
/// </summary>
internal sealed class SetupArrows
{
    public const uint ArrowSprite = 142;
    private readonly LumenPlayer[] _players;

    public SetupArrows(LmbMovieDefinition entry) =>
        _players = [new(entry, 1280, 720, ArrowSprite), new(entry, 1280, 720, ArrowSprite)];

    public void Advance()
    {
        foreach (var player in _players)
            player.Advance(LumenInputSnapshot.Empty);
    }

    /// <summary>
    /// The side's arrows around a stand: the pair is authored far apart (around the mode panels), so each
    /// arrow keeps its own animation but is moved to <paramref name="gap"/> from the stand's centre.
    /// </summary>
    public LumenRenderSnapshot Snapshot(int side, float centreX, float centreY, float gap, float scale, float interpolation)
    {
        var snapshot = _players[side].CreateRenderSnapshot(interpolation);
        _rest ??= restCentres(snapshot);
        var (left, right, restY) = _rest.Value;
        var middle = (left + right) / 2;
        return new LumenRenderSnapshot(snapshot.StageWidth, snapshot.StageHeight, snapshot.Quads.Select(quad =>
        {
            var isLeft = (quad.TopLeft.X + quad.BottomRight.X) / 2 < middle;
            // Each arrow scales about its rest centre, then moves beside the stand (its motion scales too).
            float restX = isLeft ? left : right, targetX = isLeft ? centreX - gap : centreX + gap;
            LumenRenderVertex move(LumenRenderVertex vertex) => vertex with
            {
                X = targetX + (vertex.X - restX) * scale,
                Y = centreY + (vertex.Y - restY) * scale,
            };
            return quad with
            {
                TopLeft = move(quad.TopLeft),
                TopRight = move(quad.TopRight),
                BottomRight = move(quad.BottomRight),
                BottomLeft = move(quad.BottomLeft),
            };
        }));
    }

    // Where the two arrows sit at rest (their centres on the sprite's first frame).
    private (float Left, float Right, float Y)? _rest;

    private static (float, float, float) restCentres(LumenRenderSnapshot snapshot)
    {
        var centres = snapshot.Quads.Select(static quad => ((quad.TopLeft.X + quad.BottomRight.X) / 2,
            (quad.TopLeft.Y + quad.BottomRight.Y) / 2)).ToList();
        if (centres.Count == 0)
            return (-1, 1, 0);
        return (centres.Min(static centre => centre.Item1), centres.Max(static centre => centre.Item1),
            centres.Average(static centre => centre.Item2));
    }
}
