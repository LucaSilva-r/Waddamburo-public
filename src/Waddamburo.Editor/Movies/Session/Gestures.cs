using System.Numerics;
using ImGuiNET;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private int _lockedAxis;
    private sealed record StageMove(Vector2 Start, bool Moved, int Handle, Vector2 Pivot, Vector2 Min, Vector2 Max);
    private StageMove? _move;
    private (float? X, float? Y) _guides; // The stage lines the move in progress snapped to.
    private static readonly uint Guide = color(1, .2f, .75f);

    // A move snaps the selection's edges and middle to the stage's (edges, centre) and to the clips beside it, within a
    // few pixels on screen. Ctrl moves freely.
    private Vector2 snapMove(LumenPlayer player, StageMove move, Vector2 delta, float threshold)
    {
        static string parentOf(string path) => path.LastIndexOf('/') is >= 0 and var cut ? path[..cut] : "";
        var (min, max) = (move.Min + delta, move.Max + delta);
        List<float> xs = [0, 640, 1280], ys = [0, 360, 720];
        var beside = _selected is null ? null : parentOf(_selected);
        foreach (var clip in player.InspectDisplayList())
            if (clip.Level > 0 && clip.Visible && !clip.HiddenByHost && !_selection.Contains(clip.Path) && parentOf(clip.Path) == beside
                && player.TryInspectBounds(clip.Path, out var box))
            {
                xs.AddRange([box.X, box.X + box.Width / 2, box.X + box.Width]);
                ys.AddRange([box.Y, box.Y + box.Height / 2, box.Y + box.Height]);
            }
        // An axis held still (Shift) stays still.
        var (shiftX, guideX) = delta.X == 0 ? (0, null) : EffectGizmo.Snap([min.X, (min.X + max.X) / 2, max.X], xs, threshold);
        var (shiftY, guideY) = delta.Y == 0 ? (0, null) : EffectGizmo.Snap([min.Y, (min.Y + max.Y) / 2, max.Y], ys, threshold);
        _guides = (guideX, guideY);
        return delta + new Vector2(shiftX, shiftY);
    }

    private void updateStageGesture(LumenPlayer player, LumenInstanceInfo? selected, EffectViewport view, Vector2 point)
    {
        if (_move is { } move)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && selected is not null && _groupDrag.Count > 0)
            {
                var delta = point - move.Start;
                if (_stageTool == 0)
                {
                    delta = MovieTransform.ConstrainMove(delta, move.Handle, ImGui.GetIO().KeyShift, ref _lockedAxis);
                    _guides = (null, null);
                    if (move.Moved && !ImGui.GetIO().KeyCtrl)
                        delta = snapMove(player, move, delta, 6 * 1280 / view.Rectangle.Width);
                }
                if (move.Moved || delta.Length() * view.Rectangle.Width / 1280 > 3)
                {
                    var anchor = _stageTool == 2
                        ? MovieTransform.ScaleAnchor(move.Handle, move.Min, move.Max, move.Pivot, ImGui.GetIO().KeyAlt)
                        : move.Pivot;
                    _move = move with { Moved = true };
                    transformGroup(move, point, delta, anchor);
                }
            }
            else
            {
                if (!move.Moved && move.Handle < 0)
                    pick(player, move.Start); // A plain click on the selected clip reaches what is inside it.
                _move = null;
            }
        }
    }
}
