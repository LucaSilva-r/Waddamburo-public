using System.Text;
using Waddamburo.Catalog;
using Waddamburo.Game.Gameplay;

namespace Waddamburo.Game.Tests;

public sealed class WaiwaiCompositionTests
{
    [Fact]
    public void SectionsGiveTogetherMeasuresAsSynchroAndSoloMeasuresToOnePlayer()
    {
        // Measures 0-1 together, 2 left solo, 3 right solo, 4 plain (outside every section).
        var composition = WaiwaiComposition.Parse(new MemoryStream(Encoding.UTF8.GetBytes("""
            <FumenCompositionData>
              <param><duet>1</duet><start_no>0</start_no><size_per_unit>2</size_per_unit><unit_min>1</unit_min><unit_max>1</unit_max><assign_pattern>0</assign_pattern></param>
              <param><duet>2</duet><start_no>2</start_no><size_per_unit>1</size_per_unit><unit_min>1</unit_min><unit_max>1</unit_max><assign_pattern>1</assign_pattern></param>
              <param><duet>2</duet><start_no>3</start_no><size_per_unit>1</size_per_unit><unit_min>1</unit_min><unit_max>1</unit_max><assign_pattern>2</assign_pattern></param>
            </FumenCompositionData>
            """)));
        // A measure lasts one second; the right chart has ka where the left has don.
        var (left, right) = composition.Apply(chart(PlayableNoteKind.Don), chart(PlayableNoteKind.Ka));

        // Measures 2 and 3 are solos: the other player keeps only that measure's first note.
        Assert.Equal([0d, 0.5, 1, 1.5, 2, 2.5, 3, 4, 4.5], left.HitObjects.Select(static note => note.StartTime.TotalSeconds));
        Assert.Equal([0d, 0.5, 1, 1.5, 2, 3, 3.5, 4, 4.5], right.HitObjects.Select(static note => note.StartTime.TotalSeconds));
        Assert.Equal([true, true, true, true, false, false, false, false, true], right.HitObjects.Select(static note => note.IsSynchro));
        Assert.Equal(PlayableNoteKind.Don, right.HitObjects[0].Kind); // together: the left chart's notes
        Assert.Equal(PlayableNoteKind.Ka, right.HitObjects[5].Kind);  // its own solo
        Assert.Equal([null, 0, 1], composition.Timeline(left).Select(static section => section.Soloist));
    }

    private static PlayableChart chart(PlayableNoteKind kind) => new(
        new ChartKey(new SongKey(SongSourceKind.Stock, "song"), "hard"),
        TimeSpan.Zero,
        TimeSpan.FromSeconds(6),
        // Two notes a measure; the one in the last measure's second half is big (synchro everywhere).
        Enumerable.Range(0, 10).Select(half => new PlayableHitObject(TimeSpan.FromSeconds(half / 2d),
            half == 9 ? (kind == PlayableNoteKind.Don ? PlayableNoteKind.BigDon : PlayableNoteKind.BigKa) : kind)),
        [new ChartTimingPoint(TimeSpan.Zero, 240, 4, 4)],
        [new ChartScrollPoint(TimeSpan.Zero, 1)],
        [new ChartEffectPoint(TimeSpan.Zero, false)],
        Enumerable.Range(0, 5).Select(measure => new ChartBarLine(TimeSpan.FromSeconds(measure), true)));
}
