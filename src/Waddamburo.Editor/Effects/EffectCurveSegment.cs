using Waddamburo.Catalog;

namespace Waddamburo.Editor;

/// <summary>Maps an incoming CSS Bezier segment's handles to beat/value space and back.</summary>
public readonly record struct EffectCurveSegment(ChartModKeyframe Start, ChartModKeyframe End)
{
    public (double Beat, double Value) Handle(int index)
    {
        var curve = End.Curve ?? throw new InvalidOperationException("The segment has no Bezier curve.");
        var (x, y) = index == 0 ? (curve.X1, curve.Y1) : (curve.X2, curve.Y2);
        return (Start.Beat + (End.Beat - Start.Beat) * x, Start.Value * (1 - y) + End.Value * y);
    }

    public ChartModCurve MoveHandle(int index, double beat, double value)
    {
        var curve = End.Curve ?? throw new InvalidOperationException("The segment has no Bezier curve.");
        var x = Math.Clamp((beat - Start.Beat) / (End.Beat - Start.Beat), 0, 1);
        // A flat segment has no value range: its handles can still move horizontally.
        var magnitude = Math.Max(1, Math.Max(Math.Abs(Start.Value), Math.Abs(End.Value)));
        var span = End.Value / magnitude - Start.Value / magnitude;
        var y = span == 0 ? (index == 0 ? curve.Y1 : curve.Y2) : (value / magnitude - Start.Value / magnitude) / span;
        var moved = index == 0 ? curve with { X1 = x, Y1 = y } : curve with { X2 = x, Y2 = y };
        if (!moved.IsValid) throw new InvalidDataException("Curve handles must be finite.");
        return moved;
    }
}
