namespace Waddamburo.Editor;

public readonly record struct TimelineLabelPlacement(string Name, int Frame, float Left, float Width, int Row);

/// <summary>Packs label names into a bounded set of non-overlapping rows. Overflow remains available in the label list.</summary>
public static class TimelineLabelLayout
{
    public static IReadOnlyList<TimelineLabelPlacement> Arrange(IEnumerable<(string Name, int Frame, float Width)> labels,
        float pixelsPerFrame, float trackWidth, int maxRows = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        var ends = Enumerable.Repeat(float.NegativeInfinity, maxRows).ToArray();
        var result = new List<TimelineLabelPlacement>();
        foreach (var label in labels.OrderBy(label => label.Frame).ThenBy(label => label.Name, StringComparer.Ordinal))
        {
            var width = Math.Min(Math.Max(1, label.Width), Math.Max(1, trackWidth));
            var left = Math.Clamp(label.Frame * pixelsPerFrame + 4, 0, Math.Max(0, trackWidth - width));
            var row = Array.FindIndex(ends, end => left >= end + 4);
            if (row >= 0) ends[row] = left + width;
            result.Add(new(label.Name, label.Frame, left, width, row));
        }
        return result;
    }
}
