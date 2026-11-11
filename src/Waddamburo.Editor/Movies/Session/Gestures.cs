using System.Numerics;
using ImGuiNET;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private int _lockedAxis;
    private sealed record StageMove(Vector2 Start, bool Moved, int Handle, Vector2 Pivot, Vector2 Min, Vector2 Max);
    private StageMove? _move;

    private void updateStageGesture(LumenPlayer player, LumenInstanceInfo? selected, EffectViewport view, Vector2 point)
    {
        if (_move is { } move)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && selected is not null && _groupDrag.Count > 0)
            {
                var delta = point - move.Start;
                if (_stageTool == 0)
                    delta = MovieTransform.ConstrainMove(delta, move.Handle, ImGui.GetIO().KeyShift, ref _lockedAxis);
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
