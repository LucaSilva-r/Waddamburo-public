using System.Globalization;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.OsuLazer;

/// <summary>
/// Reads a native osu!taiko .osu file into a playable chart, following lazer's decoder and taiko
/// converter: whistle/clap = ka, finish = big, sliders are drumrolls, spinners are balloons; inherited
/// (green) points set the scroll speed and red points reset it; kiai is Go-Go time.
/// </summary>
public static class OsuTaikoChartReader
{
    private const double BaseSliderMultiplier = 1.4;

    public static PlayableChart Read(string text, ChartKey key, int level)
    {
        ArgumentNullException.ThrowIfNull(text);
        var section = "";
        var version = 14;
        var mode = 0;
        var sliderMultiplier = BaseSliderMultiplier;
        var overallDifficulty = 5.0;
        var points = new List<(double Time, double BeatLength, int Meter, bool Red, bool Kiai, bool OmitBar)>();
        var objects = new List<string[]>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("osu file format v", StringComparison.Ordinal))
            {
                int.TryParse(line["osu file format v".Length..], CultureInfo.InvariantCulture, out version);
                continue;
            }
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1];
                continue;
            }
            if (section == "TimingPoints")
            {
                var p = line.Split(',');
                if (p.Length < 2 || !number(p[0], out var time) || !number(p[1], out var beatLength))
                    continue;
                var red = p.Length < 7 || p[6].Trim() != "0";
                var effects = p.Length > 7 && int.TryParse(p[7], CultureInfo.InvariantCulture, out var flags) ? flags : 0;
                var meter = p.Length > 2 && int.TryParse(p[2], CultureInfo.InvariantCulture, out var m) && m > 0 ? m : 4;
                points.Add((time, beatLength, meter, red, (effects & 1) != 0, (effects & 8) != 0));
            }
            else if (section == "HitObjects")
            {
                var p = line.Split(',');
                if (p.Length >= 5)
                    objects.Add(p);
            }
            else if (line.IndexOf(':') is var colon and > 0)
            {
                var name = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (name == "Mode")
                    int.TryParse(value, CultureInfo.InvariantCulture, out mode);
                else if (name == "SliderMultiplier" && number(value, out var sm) && sm > 0)
                    sliderMultiplier = sm;
                else if (name == "OverallDifficulty" && number(value, out var od))
                    overallDifficulty = od;
            }
        }
        if (mode != 1)
            throw new InvalidDataException("The beatmap is not an osu!taiko beatmap.");

        // lazer shifts files older than v5 by 24 ms.
        var offset = version < 5 ? 24 : 0;
        points = [.. points.Select(p => p with { Time = p.Time + offset }).OrderBy(static p => p.Time).ThenBy(static p => !p.Red)];
        var reds = points.Where(static p => p.Red && p.BeatLength > 0).ToList();
        if (reds.Count == 0)
            throw new InvalidDataException("The beatmap has no timing point.");

        // Control points in effect at a time: the last red, and the scroll (SV) set after it.
        (double BeatLength, int Meter) redAt(double time) =>
            reds.LastOrDefault(p => p.Time <= time) is { BeatLength: > 0 } found ? (found.BeatLength, found.Meter) : (reds[0].BeatLength, reds[0].Meter);
        (double Scroll, bool Kiai) effectAt(double time)
        {
            double scroll = 1;
            var kiai = false;
            foreach (var p in points)
            {
                if (p.Time > time)
                    break;
                kiai = p.Kiai;
                scroll = p.Red ? 1 : p.BeatLength < 0 ? Math.Clamp(-100 / p.BeatLength, 0.1, 10) : scroll;
            }
            return (scroll, kiai);
        }

        var hits = new List<PlayableHitObject>();
        var longs = new List<PlayableLongNote>();
        foreach (var p in objects)
        {
            if (!number(p[2], out var start) || !int.TryParse(p[3], CultureInfo.InvariantCulture, out var type)
                || !int.TryParse(p[4], CultureInfo.InvariantCulture, out var sound))
                continue;
            start += offset;
            if (start < 0)
                continue;
            var big = (sound & 4) != 0;
            if ((type & 2) != 0 && p.Length >= 8 && int.TryParse(p[6], CultureInfo.InvariantCulture, out var spans)
                && number(p[7], out var length))
            {
                var ms = Math.Max(1, spans) * length / (100 * sliderMultiplier * effectAt(start).Scroll) * redAt(start).BeatLength;
                if (double.IsFinite(ms) && ms >= 0)
                    longs.Add(new PlayableLongNote(at(start), at(start + (int)ms), big ? PlayableLongNoteKind.BigRoll : PlayableLongNoteKind.Roll));
            }
            else if ((type & 8) != 0 && p.Length >= 6 && number(p[5], out var end))
            {
                var ms = Math.Max(0, end + offset - start);
                var perSecond = overallDifficulty > 5 ? 5 + (7.5 - 5) * (overallDifficulty - 5) / 5 : 5 - (5 - 3) * (5 - overallDifficulty) / 5;
                longs.Add(new PlayableLongNote(at(start), at(start + ms), PlayableLongNoteKind.Balloon,
                    (int)Math.Max(1, ms / 1000 * perSecond * 1.65)));
            }
            else if ((type & 1) != 0)
            {
                var ka = (sound & (2 | 8)) != 0;
                hits.Add(new PlayableHitObject(at(start),
                    ka ? big ? PlayableNoteKind.BigKa : PlayableNoteKind.Ka : big ? PlayableNoteKind.BigDon : PlayableNoteKind.Don));
            }
        }
        if (hits.Count == 0 && longs.Count == 0)
            throw new InvalidDataException("The beatmap has no hit objects.");
        hits.Sort(static (a, b) => a.StartTime.CompareTo(b.StartTime));
        longs.Sort(static (a, b) => a.StartTime.CompareTo(b.StartTime));
        var last = Math.Max(hits.Count == 0 ? 0 : hits[^1].StartTime.TotalMilliseconds,
            longs.Count == 0 ? 0 : longs.Max(static note => note.EndTime.TotalMilliseconds));
        var lastRed = redAt(last);
        var duration = last + lastRed.BeatLength * lastRed.Meter;

        // Chart time is audio time; points before zero collapse into the one in effect at zero.
        var timing = new List<ChartTimingPoint> { new(TimeSpan.Zero, 60000 / redAt(0).BeatLength, redAt(0).Meter, 4) };
        foreach (var red in reds.Where(static p => p.Time > 0))
            timing.Add(new ChartTimingPoint(at(red.Time), 60000 / red.BeatLength, red.Meter, 4));
        var scrolls = new List<ChartScrollPoint>();
        var goGo = new List<ChartEffectPoint>();
        foreach (var time in points.Select(static p => Math.Max(0, p.Time)).Prepend(0).Distinct())
        {
            var (scroll, kiai) = effectAt(time);
            // Speed follows SV × the map's slider multiplier; 1.4 (the usual taiko base) is scroll 1.
            var multiplier = scroll * sliderMultiplier / BaseSliderMultiplier;
            if (scrolls.Count == 0 || scrolls[^1].Multiplier != multiplier)
                scrolls.Add(new ChartScrollPoint(at(time), multiplier));
            if (goGo.Count == 0 || goGo[^1].IsGoGo != kiai)
                goGo.Add(new ChartEffectPoint(at(time), kiai));
        }

        // Bar lines as lazer generates them: every measure from each red point, from zero on.
        var bars = new List<ChartBarLine>();
        for (var index = 0; index < reds.Count; index++)
        {
            var red = reds[index];
            var measure = red.BeatLength * red.Meter;
            var stop = index + 1 < reds.Count ? reds[index + 1].Time : duration;
            var first = red.Time >= 0 ? red.Time : red.Time + Math.Ceiling(-red.Time / measure) * measure;
            if (red.OmitBar)
                first += measure;
            for (var time = first; time < stop - 0.5 && bars.Count < 100_000; time += measure)
                bars.Add(new ChartBarLine(at(time), true));
        }

        return new PlayableChart(key, TimeSpan.Zero, at(duration), hits, timing, scrolls, goGo, bars, longs) { Level = level };
    }

    private static TimeSpan at(double milliseconds) => TimeSpan.FromMilliseconds(Math.Round(milliseconds, 3));

    private static bool number(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}
