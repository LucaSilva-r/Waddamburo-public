using System.Numerics;
using ImGuiNET;

namespace Waddamburo.Editor;

public sealed partial class EffectsEditorSession
{
    private GraphScale? _graphDragScale;
    private float _graphDragHeight;
    private (EffectCurveSegment Segment, int Handle)? _handleDrag;

    private EffectCurveSegment? SelectedSegment()
    {
        if (_key is not { Index: > 0 } selected || !_preview.Tracks.TryGetValue(selected.Track, out var keys)
            || selected.Index >= keys.Length || keys[selected.Index].Curve is null) return null;
        return new(keys[selected.Index - 1], keys[selected.Index]);
    }

    private IEnumerable<double> VisibleHandleValues()
    {
        if (SelectedSegment() is not { } segment) yield break;
        yield return segment.Handle(0).Value;
        yield return segment.Handle(1).Value;
    }

    private bool GraphHandles(ImDrawListPtr draw, Vector2 origin, Vector2 size, GraphScale scale, bool hovered)
    {
        if (_history.Current.UsesEmbeddedEffects || SelectedSegment() is not { } segment) return false;
        var hitAny = false;
        Vector2 Point(double beat, double value) => new(X(beat, origin, size.X), origin.Y + (float)(1 - scale.Fraction(value)) * size.Y);
        for (var i = 0; i < 2; i++)
        {
            var handle = segment.Handle(i);
            if (!double.IsFinite(handle.Value)) continue;
            var center = Point(handle.Beat, handle.Value);
            var anchor = i == 0 ? segment.Start : segment.End;
            draw.AddLine(Point(anchor.Beat, anchor.Value), center, Muted, 1.5f);
            var hit = hovered && center.X >= origin.X && center.X <= origin.X + size.X
                && center.Y >= origin.Y && center.Y <= origin.Y + size.Y && Vector2.Distance(ImGui.GetIO().MousePos, center) < 8;
            draw.AddRectFilled(center - new Vector2(4), center + new Vector2(4), hit ? Playhead : Accent);
            if (!hit) continue;
            hitAny = true;
            ImGui.SetTooltip($"Bezier handle {i + 1}: drag to shape the incoming segment");
            if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left)) continue;
            if (!CommitInspector() || _key is not { } selected || SelectedSegment() is not { } current) continue;
            _dragBefore = _history.Current; _dragAnchor = new(selected.Track, current.End.Beat);
            _handleDrag = (current, i); _graphDragScale = scale; _graphDragHeight = size.Y;
            _dragWidth = size.X; _dragSpan = _view.Span;
            _history.BeginGesture();
        }
        return hitAny;
    }

    private void MoveCurveHandle()
    {
        if (_handleDrag is not { } drag || _dragBefore is null || _graphDragScale is not { } scale) return;
        var original = drag.Segment.Handle(drag.Handle);
        var delta = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left);
        var beat = original.Beat + delta.X / _dragWidth * _dragSpan;
        var value = scale.ValueAt(scale.Fraction(original.Value) - delta.Y / _graphDragHeight);
        try
        {
            var curve = drag.Segment.MoveHandle(drag.Handle, beat, value);
            var document = _dragBefore.SetKey(_dragAnchor.Track, drag.Segment.End with { Curve = curve }, _dragAnchor.Beat);
            if (_history.Preview(document)) ApplyCurrent();
            FocusKey(_dragAnchor);
        }
        catch (InvalidDataException e) { _message = e.Message; _messageError = true; }
    }
}
