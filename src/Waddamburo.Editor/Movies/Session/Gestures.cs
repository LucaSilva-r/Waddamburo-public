using System.Numerics;
using ImGuiNET;
using Waddamburo.Lumen.Runtime;

namespace Waddamburo.Editor;

public sealed partial class MovieInspectorSession
{
    private int _lockedAxis;
    private sealed record StageMove(Vector2 Start, bool Moved, int Handle, Vector2 Pivot, Vector2 Min, Vector2 Max)
    {
        /// <summary>What the drag does: 0 move, 1 rotate, 2 scale. The free transform tool decides by the handle taken.</summary>
        public int Op { get; init; }
    }

    private const int RotateHandle = 11;
    private bool _autoKey = true;

    /// <summary>
    /// Sets a layer's values as they should be on <paramref name="frame"/> (inside a document edit). Auto key on: in a
    /// key on that frame, made when there is none. Off: every key changes by the same amount, so the whole animation
    /// moves and no key is added; for a property that does not animate that is simply its resting value.
    /// </summary>
    // Keys one property on a frame. A property nothing holds yet first gets its resting value written on the layer's
    // first key, so the new key animates from where the clip was (on the first key's own frame it is just set).
    private static void keyValue(System.Text.Json.Nodes.JsonObject layer, int frame, string property, double value)
    {
        var keys = layer["keys"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>().ToArray();
        if (!keys.Any(key => MovieDocument.Has(key, property)))
            MovieDocument.Set(keys.MinBy(static key => (int)key["frame"]!)!, property, MovieDocument.Default(property));
        MovieDocument.Set(MovieDocument.BareKey(layer, frame), property, value);
    }

    private void setValues(System.Text.Json.Nodes.JsonObject layer, int frame, params (string Property, double Value)[] values)
    {
        if (_autoKey)
        {
            foreach (var (property, value) in values) keyValue(layer, frame, property, value);
            return;
        }
        var now = MovieDocument.KeyAt((System.Text.Json.Nodes.JsonObject)layer.DeepClone(), frame);
        foreach (var (property, value) in values)
        {
            var delta = value - MovieDocument.Value(now, property)!.Value;
            if (delta == 0) continue;
            var held = layer["keys"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>().Where(key => MovieDocument.Has(key, property)).ToArray();
            foreach (var key in held)
                MovieDocument.Set(key, property, MovieDocument.Value(key, property)!.Value + delta);
            if (held.Length == 0)
                MovieDocument.Set(layer["keys"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>().MinBy(static key => (int)key["frame"]!)!, property, value);
        }
    }
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
                if (move.Op == 0)
                {
                    delta = MovieTransform.ConstrainMove(delta, move.Handle, ImGui.GetIO().KeyShift, ref _lockedAxis);
                    _guides = (null, null);
                    if (move.Moved && !ImGui.GetIO().KeyCtrl)
                        delta = snapMove(player, move, delta, 6 * 1280 / view.Rectangle.Width);
                }
                if (move.Moved || delta.Length() * view.Rectangle.Width / 1280 > 3)
                {
                    var anchor = move.Op == 2
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
