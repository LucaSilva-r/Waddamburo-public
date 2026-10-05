using System.Globalization;
using System.Text;

namespace Waddamburo.Game.Flow;

/// <summary>Line breaking for the game's own text boxes (notices), which draw one line at a time.</summary>
public static class TextWrap
{
    /// <summary>
    /// Splits a message into lines of about <paramref name="maxUnits"/> character widths (wide characters,
    /// CJK, count double), breaking at spaces where it can; past <paramref name="maxLines"/> the last line
    /// ends in an ellipsis. ponytail: widths are estimated, not measured; the drawn line shrinks to fit.
    /// </summary>
    public static List<string> Lines(string text, float maxUnits, int maxLines)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = new StringBuilder();
            float width = 0;
            int lastSpace = -1;
            var elements = StringInfo.GetTextElementEnumerator(paragraph);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                var w = units(element);
                if (width + w > maxUnits && line.Length > 0)
                {
                    // Overflowing on a space: the line so far fits whole.
                    if (element == " ")
                    {
                        lines.Add(line.ToString());
                        line.Clear();
                        (width, lastSpace) = (0, -1);
                        continue;
                    }
                    if (lastSpace > 0)
                    {
                        lines.Add(line.ToString(0, lastSpace));
                        line.Remove(0, lastSpace + 1);
                    }
                    else
                    {
                        lines.Add(line.ToString());
                        line.Clear();
                    }
                    width = units(line.ToString());
                    lastSpace = -1;
                    if (element == " " && line.Length == 0)
                        continue;
                }
                if (element == " ")
                    lastSpace = line.Length;
                line.Append(element);
                width += w;
            }
            lines.Add(line.ToString());
        }
        if (lines.Count > maxLines)
        {
            lines.RemoveRange(maxLines, lines.Count - maxLines);
            lines[^1] = lines[^1].TrimEnd() + "…";
        }
        return lines;
    }

    // Wide characters (CJK and up) take about twice a Latin letter.
    private static float units(string text)
    {
        float total = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
            total += char.ConvertToUtf32(elements.GetTextElement(), 0) >= 0x2E80 ? 1.8f : 1f;
        return total;
    }
}
