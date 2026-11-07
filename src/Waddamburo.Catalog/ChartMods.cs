using System.Collections.Immutable;

namespace Waddamburo.Catalog;

/// <summary>Easing curves for <see cref="ChartModEase"/> (the usual names: in = slow start, out = slow end).</summary>
public enum ChartEase
{
    Linear, InQuad, OutQuad, InOutQuad, InCubic, OutCubic, InOutCubic, InSine, OutSine, InOutSine,
    InExpo, OutExpo, InOutExpo, InBack, OutBack, InOutBack, OutElastic, OutBounce, Step,
}

/// <summary>
/// A modchart change (TJA #EASE / #SET): from <see cref="Start"/>, over <see cref="Length"/> (0: at once),
/// <see cref="Target"/>'s <see cref="Property"/> eases from whatever it was to <see cref="Value"/>. Drawing only.
/// </summary>
public readonly record struct ChartModEase(TimeSpan Start, TimeSpan Length, string Target, string Property, double Value, ChartEase Ease, ChartModCurve? Curve = null, bool Initial = false);

/// <summary>CSS-style cubic Bézier handles. X handles stay in [0,1]; Y may overshoot.</summary>
public readonly record struct ChartModCurve(double X1, double Y1, double X2, double Y2)
{
    public bool IsValid => double.IsFinite(X1) && double.IsFinite(Y1) && double.IsFinite(X2) && double.IsFinite(Y2)
        && X1 is >= 0 and <= 1 && X2 is >= 0 and <= 1;
}

/// <summary>
/// The modchart vocabulary and its values over time. Every value is a function of chart time alone (no state kept
/// between frames), so seeking, practice retries and replays need nothing restored.
/// </summary>
public static class ChartMods
{
    /// <summary>Scene elements (Green's gameplay roles, the same for every skin) and what moves them.</summary>
    public static readonly ImmutableArray<string> Elements =
        ["camera", "playfield", "don", "background", "donbg", "renda", "dancers", "stage", "fever", "runners",
         "gauge", "board", "songinfo", "nameplate", "gogo"];

    public static readonly ImmutableArray<string> ElementProperties = ["x", "y", "rotation", "scale", "scalex", "scaley", "alpha"];

    /// <summary>Don-chan's model turned in his gameplay view (degrees; not the balloon or kusudama views).</summary>
    public static readonly ImmutableArray<string> DonModelProperties = ["yaw", "pitch", "roll"];

    /// <summary>Note mods (the "notes" target): 1 = full strength, except zoom and alpha (1 = as drawn).</summary>
    public static readonly ImmutableArray<string> NoteProperties = ["drunk", "tipsy", "wave", "beat", "dizzy", "zoom", "alpha"];

    public static bool IsKnown(string target, string property) =>
        target == "notes" ? NoteProperties.Contains(property)
        : Elements.Contains(target) && (ElementProperties.Contains(property) || target == "don" && DonModelProperties.Contains(property));

    /// <summary>A property's value before any change: 1 for scales, zoom and alpha, else 0.</summary>
    public static double Default(string property) => property is "scale" or "scalex" or "scaley" or "zoom" or "alpha" ? 1 : 0;

    /// <summary>
    /// The value at <paramref name="time"/> of one target's property, from its changes in start order: each eases from
    /// the value before it. ponytail: overlapping changes blend from the value so far rather than from their start's.
    /// </summary>
    public static double ValueAt(ReadOnlySpan<ChartModEase> changes, TimeSpan time, double value)
    {
        foreach (var change in changes)
        {
            if (change.Initial)
            {
                value = change.Value;
                continue;
            }
            if (time < change.Start)
                break;
            if (change.Length <= TimeSpan.Zero || time - change.Start >= change.Length)
            {
                value = change.Value;
                continue;
            }
            var progress = (time - change.Start) / change.Length;
            value += (change.Value - value) * Eased(change.Ease, progress, change.Curve);
        }
        return value;
    }

    /// <summary>Solves x(u) = progress, then evaluates y(u); never clamps an overshooting Y.</summary>
    public static double Bezier(ChartModCurve curve, double progress)
    {
        if (!curve.IsValid) throw new ArgumentException("Invalid Bézier handles.", nameof(curve));
        if (progress <= 0) return 0;
        if (progress >= 1) return 1;
        var u = progress;
        for (var i = 0; i < 8; i++)
        {
            var error = coordinate(u, curve.X1, curve.X2) - progress;
            if (Math.Abs(error) < 1e-10) return coordinate(u, curve.Y1, curve.Y2);
            var derivative = 3 * (1 - u) * (1 - u) * curve.X1
                + 6 * (1 - u) * u * (curve.X2 - curve.X1) + 3 * u * u * (1 - curve.X2);
            if (Math.Abs(derivative) < 1e-8) break;
            var next = u - error / derivative;
            if (next is < 0 or > 1) break;
            u = next;
        }
        var (low, high) = (0d, 1d);
        for (var i = 0; i < 48; i++)
        {
            u = (low + high) / 2;
            if (coordinate(u, curve.X1, curve.X2) < progress) low = u;
            else high = u;
        }
        return coordinate((low + high) / 2, curve.Y1, curve.Y2);

        static double coordinate(double u, double a, double b) =>
            3 * (1 - u) * (1 - u) * u * a + 3 * (1 - u) * u * u * b + u * u * u;
    }

    public static bool TryParseEase(string name, out ChartEase ease) =>
        Enum.TryParse(name, ignoreCase: true, out ease) && Enum.IsDefined(ease);

    /// <summary>The curve at <paramref name="t"/> (0..1).</summary>
    public static double Eased(ChartEase ease, double t, ChartModCurve? curve = null)
    {
        t = Math.Clamp(t, 0, 1);
        if (curve is { } handles)
            return Bezier(handles, t);
        const double back = 1.70158, backInOut = back * 1.525;
        return ease switch
        {
            ChartEase.Step => t < 1 ? 0 : 1,
            ChartEase.InQuad => t * t,
            ChartEase.OutQuad => 1 - (1 - t) * (1 - t),
            ChartEase.InOutQuad => t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2,
            ChartEase.InCubic => t * t * t,
            ChartEase.OutCubic => 1 - Math.Pow(1 - t, 3),
            ChartEase.InOutCubic => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2,
            ChartEase.InSine => 1 - Math.Cos(t * Math.PI / 2),
            ChartEase.OutSine => Math.Sin(t * Math.PI / 2),
            ChartEase.InOutSine => -(Math.Cos(Math.PI * t) - 1) / 2,
            ChartEase.InExpo => t == 0 ? 0 : Math.Pow(2, 10 * t - 10),
            ChartEase.OutExpo => t == 1 ? 1 : 1 - Math.Pow(2, -10 * t),
            ChartEase.InOutExpo => t is 0 or 1 ? t : t < 0.5 ? Math.Pow(2, 20 * t - 10) / 2 : (2 - Math.Pow(2, -20 * t + 10)) / 2,
            ChartEase.InBack => (back + 1) * t * t * t - back * t * t,
            ChartEase.OutBack => 1 + (back + 1) * Math.Pow(t - 1, 3) + back * Math.Pow(t - 1, 2),
            ChartEase.InOutBack => t < 0.5
                ? Math.Pow(2 * t, 2) * ((backInOut + 1) * 2 * t - backInOut) / 2
                : (Math.Pow(2 * t - 2, 2) * ((backInOut + 1) * (t * 2 - 2) + backInOut) + 2) / 2,
            ChartEase.OutElastic => t is 0 or 1 ? t : Math.Pow(2, -10 * t) * Math.Sin((t * 10 - 0.75) * (2 * Math.PI / 3)) + 1,
            ChartEase.OutBounce => bounce(t),
            _ => t,
        };

        static double bounce(double t)
        {
            const double n = 7.5625, d = 2.75;
            return t < 1 / d ? n * t * t
                : t < 2 / d ? n * (t -= 1.5 / d) * t + 0.75
                : t < 2.5 / d ? n * (t -= 2.25 / d) * t + 0.9375
                : n * (t -= 2.625 / d) * t + 0.984375;
        }
    }
}
