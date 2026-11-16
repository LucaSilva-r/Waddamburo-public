using System.Buffers.Binary;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.Stock.Tests;

public sealed class WiiChartReaderTests
{
    [Fact]
    public void WiiChartPlaysAsItsArcadeFumen()
    {
        // 120 BPM: a tick (1/12 beat) is 5000/120 ms; a measure's offset is its lane entry, 2000 ms before its bar line.
        (int Type, int Tick, int Hits, int Length)[][] measures =
        [
            [(1, 0, -1, -1), (24, 6, -1, -1), (14, 12, -1, -1)], // don, sparkling ka, giant don
            [(10, 0, 5, 500)], // balloon
        ];
        var notes = measures.SelectMany(static measure => measure).ToArray();
        var data = new byte[16 + measures.Length * 140 + notes.Length * 16];
        void put(int at, int value) => BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(at), value);
        void putF(int at, float value) => BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(at), value);
        put(0, measures.Length);
        put(4, 16 + measures.Length * 140);
        put(8, notes.Length);
        var first = 0;
        for (var index = 0; index < measures.Length; index++)
        {
            var at = 16 + index * 140;
            putF(at, index * 2000f);
            putF(at + 4, 120);
            put(at + 8, 1);
            for (var value = 0; value < 7; value++)
                put(at + 16 + 4 * value, -1);
            for (var scroll = 0; scroll < 6; scroll++)
                putF(at + 44 + 4 * scroll, 1);
            put(at + 68, first);
            put(at + 72, measures[index].Length);
            first += measures[index].Length;
        }
        for (var index = 0; index < notes.Length; index++)
        {
            var at = 16 + measures.Length * 140 + index * 16;
            put(at, notes[index].Type);
            put(at + 4, notes[index].Tick);
            put(at + 8, notes[index].Hits);
            put(at + 12, notes[index].Length);
        }

        var tuning = new WiiChartReader.CourseTuning(4, 720, 180, [0x10000, 0x10000, 0x10000], [20, 10, 0, 1, 20, 10, 1, 30, 30]);
        var key = new ChartKey(new SongKey(SongSourceKind.Wii, "wii1:test"), "easy");
        var chart = FumenChartReader.Read(WiiChartReader.ToFumen(data, tuning, 100, 100), key, 100, 100, wiiNotes: true);

        Assert.Equal([(2000d, PlayableNoteKind.Don), (2250d, PlayableNoteKind.Ka), (2500d, PlayableNoteKind.BigDon)],
            chart.HitObjects.Select(static note => (Math.Round(note.StartTime.TotalMilliseconds), note.Kind)));
        Assert.Equal([false, false, true], chart.HitObjects.Select(static note => note.IsGiant));
        Assert.Equal([false, true, false], chart.HitObjects.Select(static note => note.IsSparkle));
        var balloon = Assert.Single(chart.LongNotes);
        Assert.Equal((4000d, 4500d, PlayableLongNoteKind.Balloon, 5),
            (balloon.StartTime.TotalMilliseconds, balloon.EndTime.TotalMilliseconds, balloon.Kind, balloon.RequiredHits));
        Assert.Equal((720, 180), (chart.ScoreInit, chart.ScoreDiff));

        // The same chart in みんなでパーティ☆3代目!'s compact layout comes back as the same arcade fumen.
        var fumen = WiiChartReader.ToFumen(data, tuning, 100, 100);
        Assert.Null(WiiChartReader.CompactToFumen(fumen, 100));
        Assert.Equal(fumen, WiiChartReader.CompactToFumen(compact(fumen), 100));
    }

    // An arcade fumen (thresholds fitting int16) in the compact layout CompactToFumen reads.
    private static byte[] compact(byte[] fumen)
    {
        var output = new List<byte>(fumen[..0x178]);
        output.AddRange(fumen[0x1B0..0x1F8]);
        output.AddRange(new byte[4]);
        output.AddRange(fumen[0x200..0x204]);
        var at = 520;
        for (var measure = 0; measure < BinaryPrimitives.ReadInt32BigEndian(fumen.AsSpan(0x200)); measure++)
        {
            output.AddRange(fumen[at..(at + 10)]);
            var threshold = new byte[2];
            for (var value = 0; value < 6; value++)
            {
                BinaryPrimitives.WriteInt16BigEndian(threshold, (short)BinaryPrimitives.ReadInt32BigEndian(fumen.AsSpan(at + 12 + 4 * value)));
                output.AddRange(threshold);
            }
            output.AddRange(new byte[2]);
            at += 40;
            for (var branch = 0; branch < 3; branch++)
            {
                var notes = BinaryPrimitives.ReadUInt16BigEndian(fumen.AsSpan(at));
                var end = at + 8;
                for (var note = 0; note < notes; note++)
                    end += BinaryPrimitives.ReadInt32BigEndian(fumen.AsSpan(end)) is 6 or 9 ? 32 : 24;
                output.AddRange(fumen[at..end]);
                at = end;
            }
        }
        return [.. output];
    }
}
