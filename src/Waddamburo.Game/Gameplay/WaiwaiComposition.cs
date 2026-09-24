using System.Collections.Immutable;
using System.Xml.Linq;
using Waddamburo.Catalog;

namespace Waddamburo.Game.Gameplay;

/// <summary>A Waiwai section: measures played together (<see cref="Soloist"/> null) or by one player.</summary>
public readonly record struct WaiwaiSection(int StartMeasure, int Measures, int? Soloist);

/// <summary>A section placed in chart time.</summary>
public readonly record struct WaiwaiSectionTime(TimeSpan Start, TimeSpan End, int? Soloist);

/// <summary>
/// A song's Waiwai layout (fumen/&lt;id&gt;/composition.xml): measure sections played together as
/// synchro notes (duet 1) or by one player (duet 2; assign_pattern 1 = left drum, 2 = right drum).
/// Measures outside every section are plain two-player play. Traced session11-waiwai: every cut-in
/// fell on a section boundary.
/// </summary>
public sealed class WaiwaiComposition
{
    public WaiwaiComposition(IEnumerable<WaiwaiSection> sections) =>
        Sections = [.. sections.OrderBy(static section => section.StartMeasure)];

    public ImmutableArray<WaiwaiSection> Sections { get; }

    public static WaiwaiComposition Parse(Stream xml)
    {
        var document = XDocument.Load(xml);
        int value(XElement param, string name) =>
            int.Parse(param.Element(name)?.Value ?? throw new InvalidDataException($"composition param lacks <{name}>."),
                System.Globalization.CultureInfo.InvariantCulture);
        // ponytail: a section spans size_per_unit x unit_min measures; every Green song seen has
        // unit_min == unit_max == 1, so the unit count's role is unknown.
        return new(document.Descendants("param").Select(param => new WaiwaiSection(
            value(param, "start_no"),
            value(param, "size_per_unit") * Math.Max(1, value(param, "unit_min")),
            value(param, "duet") == 1 ? null : value(param, "assign_pattern") == 2 ? 1 : 0)));
    }

    /// <summary>The sections in chart time: a measure starts at its bar line.</summary>
    public ImmutableArray<WaiwaiSectionTime> Timeline(PlayableChart chart)
    {
        TimeSpan measure(int index) => index < chart.BarLines.Length ? chart.BarLines[index].Time : chart.Duration;
        return [.. Sections.Select(section => new WaiwaiSectionTime(
            measure(section.StartMeasure), measure(section.StartMeasure + section.Measures), section.Soloist))];
    }

    /// <summary>
    /// The two players' Waiwai charts from their duet charts (traced note displays, session12-notes):
    /// in a together section both play the left chart's notes as synchro notes; in a solo section the
    /// soloist keeps their notes and the other player only each measure's first note; outside the
    /// sections each plays their own. Big and hand notes are synchro big notes everywhere (all 31 of
    /// linda hard's were).
    /// </summary>
    // ponytail: the adaptive support (waiwaiconfig.xml thinning / dondarake, which removed and changed
    // notes live in the traces, notably in the first section) and the rare note are not modelled.
    public (PlayableChart Left, PlayableChart Right) Apply(PlayableChart left, PlayableChart right)
    {
        var timeline = Timeline(left);
        WaiwaiSectionTime? at(TimeSpan time) => timeline.Cast<WaiwaiSectionTime?>()
            .FirstOrDefault(section => time >= section!.Value.Start && time < section.Value.End);
        int measure(TimeSpan time)
        {
            var index = 0;
            while (index + 1 < left.BarLines.Length && left.BarLines[index + 1].Time <= time) index++;
            return index;
        }
        PlayableChart lane(PlayableChart own, int player)
        {
            var idleMeasures = new HashSet<int>(); // measures whose one note the idle player already has
            var notes = new List<PlayableHitObject>();
            foreach (var note in own.HitObjects)
            {
                var section = at(note.StartTime);
                if (section is { Soloist: null })
                    continue; // together: from the left chart below
                if (section is { Soloist: { } soloist } && soloist != player && !idleMeasures.Add(measure(note.StartTime)))
                    continue;
                notes.Add(note with { IsSynchro = note.IsStrong });
            }
            notes.AddRange(left.HitObjects.Where(note => at(note.StartTime) is { Soloist: null })
                .Select(note => note with { IsSynchro = true }));
            var longNotes = own.LongNotes.Where(note => at(note.StartTime) is not { Soloist: null })
                .Concat(left.LongNotes.Where(note => at(note.StartTime) is { Soloist: null }))
                .OrderBy(static note => note.StartTime);
            var duration = own.Duration > left.Duration ? own.Duration : left.Duration;
            return new PlayableChart(own.Key, own.AuthoredOffset, duration, notes.OrderBy(static note => note.StartTime),
                own.TimingPoints, own.ScrollPoints, own.EffectPoints, own.BarLines, longNotes)
            { Level = own.Level, ScoreInit = own.ScoreInit, ScoreDiff = own.ScoreDiff };
        }
        return (lane(left, 0), lane(right, 1));
    }
}
