using System.Buffers.Binary;
using System.Collections.Immutable;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.Stock;

/// <summary>
/// Converts one fumen <c>.bin</c> course into a playable chart (normal branch).
/// Layout as documented by the MIT-licensed tja2fumen parser: a 520-byte header whose
/// measure count is at 0x200, then per measure BPM, start offset (ms), Go-Go and barline
/// flags, and three branches of notes. Arcade files are big-endian.
/// </summary>
internal static class FumenChartReader
{
    private const int HeaderSize = 520;
    private const int MeasureCountOffset = 0x200;
    private const int MeasureSize = 40;
    private const int BranchHeaderSize = 8;
    private const int NoteSize = 24;
    private const int RollPadding = 8;

    public static PlayableChart Read(ReadOnlySpan<byte> data, ChartKey key, int maximumMeasures, int maximumNotes)
    {
        if (data.Length < HeaderSize)
            throw new InvalidDataException("The fumen is shorter than its header.");
        // The byte order is the one that gives a plausible measure count.
        var bigEndian = BinaryPrimitives.ReadInt32BigEndian(data[MeasureCountOffset..]) is > 0 and <= 100_000;
        var reader = new Reader(data, bigEndian) { Position = MeasureCountOffset };
        var measureCount = reader.Int32();
        if (measureCount <= 0 || measureCount > maximumMeasures)
            throw new InvalidDataException("The fumen measure count is invalid.");
        reader.Position = HeaderSize;

        // Audio time (ms) of every event; shifted to non-negative chart time at the end.
        var measures = new List<(double Time, double Bpm, double Speed, bool GoGo, bool Barline)>(measureCount);
        var notes = new List<(double Time, PlayableNoteKind Kind, bool Hand)>();
        var longNotes = new List<(double Start, double End, PlayableLongNoteKind Kind, int Hits)>();
        int? scoreInit = null, scoreDiff = null;
        for (var measure = 0; measure < measureCount; measure++)
        {
            reader.Require(MeasureSize);
            var bpm = reader.Single();
            var offset = reader.Single();
            var goGo = reader.Byte() != 0;
            var barline = reader.Byte() != 0;
            reader.Position += 2 + 24 + 4; // padding, branch conditions, padding
            if (!float.IsFinite(bpm) || bpm <= 0 || !float.IsFinite(offset))
                throw new InvalidDataException($"Fumen measure {measure} has an invalid BPM or offset.");
            // A measure's offset is when its start enters the lane: one 4/4 measure before it is hit.
            var time = offset + 240_000d / bpm;
            var speed = 1d;
            for (var branch = 0; branch < 3; branch++)
            {
                reader.Require(BranchHeaderSize);
                int count = reader.UInt16();
                reader.Position += 2;
                var branchSpeed = reader.Single();
                if (branch == 0 && float.IsFinite(branchSpeed))
                    speed = branchSpeed;
                for (var index = 0; index < count; index++)
                {
                    reader.Require(NoteSize);
                    var type = reader.Int32();
                    var position = reader.Single();
                    reader.Position += 8; // item, padding
                    int first = reader.UInt16(), second = reader.UInt16();
                    var duration = reader.Single();
                    var isRoll = type is 0x6 or 0x9 or 0x62;
                    if (isRoll)
                        reader.Position += RollPadding;
                    if (branch != 0 || !float.IsFinite(position))
                        continue;
                    var noteMs = time + position;
                    if (type is 0xA or 0xC)
                    {
                        longNotes.Add((noteMs, noteMs + Math.Max(0, duration), type == 0xA ? PlayableLongNoteKind.Balloon : PlayableLongNoteKind.Kusudama, Math.Max(1, first)));
                        continue;
                    }
                    if (first != 0)
                        (scoreInit, scoreDiff) = (first, second / 4);
                    if (isRoll)
                        longNotes.Add((noteMs, noteMs + Math.Max(0, duration), type == 0x9 ? PlayableLongNoteKind.BigRoll : PlayableLongNoteKind.Roll, 0));
                    else if (kind(type) is { } noteKind)
                        notes.Add((noteMs, noteKind, type is 0xB or 0xD));
                    if (notes.Count + longNotes.Count > maximumNotes)
                        throw new InvalidDataException("The fumen exceeds the note limit.");
                }
            }
            // Control streams must be ordered; a measure never starts before the previous one.
            if (measures.Count > 0 && time < measures[^1].Time)
                time = measures[^1].Time;
            measures.Add((time, bpm, speed, goGo, barline));
        }
        if (measures.Count == 0)
            throw new InvalidDataException("The fumen has no measures.");

        var last = measures[^1];
        var endMs = new[] { last.Time + 240_000d / last.Bpm }
            .Concat(notes.Select(static note => note.Time))
            .Concat(longNotes.Select(static note => note.End))
            .Max();
        // Chart time must be non-negative; a positive authored offset puts chart zero before audio zero.
        var shiftMs = Math.Max(0, -new[] { measures[0].Time }
            .Concat(notes.Select(static note => note.Time))
            .Concat(longNotes.Select(static note => note.Start))
            .Min());
        TimeSpan chartTime(double ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms + shiftMs));

        var timing = ImmutableArray.CreateBuilder<ChartTimingPoint>();
        var scroll = ImmutableArray.CreateBuilder<ChartScrollPoint>();
        var effects = ImmutableArray.CreateBuilder<ChartEffectPoint>();
        for (var index = 0; index < measures.Count; index++)
        {
            var current = measures[index];
            var time = index == 0 ? TimeSpan.Zero : chartTime(current.Time);
            if (index == 0 || current.Bpm != measures[index - 1].Bpm)
                timing.Add(new ChartTimingPoint(time, current.Bpm, 4, 4));
            if (index == 0 || current.Speed != measures[index - 1].Speed)
                scroll.Add(new ChartScrollPoint(time, current.Speed));
            if (index == 0 || current.GoGo != measures[index - 1].GoGo)
                effects.Add(new ChartEffectPoint(time, current.GoGo));
        }

        return new PlayableChart(
            key,
            TimeSpan.FromMilliseconds(shiftMs),
            chartTime(endMs),
            notes.OrderBy(static note => note.Time).Select(note => new PlayableHitObject(chartTime(note.Time), note.Kind, note.Hand)),
            timing.ToImmutable(),
            scroll.ToImmutable(),
            effects.ToImmutable(),
            measures.Select(measure => new ChartBarLine(chartTime(measure.Time), measure.Barline)),
            longNotes.OrderBy(static note => note.Start).Select(note => new PlayableLongNote(chartTime(note.Start), chartTime(note.End), note.Kind, note.Hits)))
        { ScoreInit = scoreInit, ScoreDiff = scoreDiff };
    }

    // Note types per tja2fumen: 1-3 don, 4-5 ka, 7/8 big don/ka, 0xB/0xD big don/ka (hand notes).
    private static PlayableNoteKind? kind(int type) => type switch
    {
        0x1 or 0x2 or 0x3 => PlayableNoteKind.Don,
        0x4 or 0x5 => PlayableNoteKind.Ka,
        0x7 or 0xB => PlayableNoteKind.BigDon,
        0x8 or 0xD => PlayableNoteKind.BigKa,
        _ => null,
    };

    private ref struct Reader(ReadOnlySpan<byte> data, bool bigEndian)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public int Position { get; set; }

        public readonly void Require(int count)
        {
            if (Position > _data.Length - count)
                throw new InvalidDataException("The fumen is truncated.");
        }

        public int Int32()
        {
            Require(4);
            var span = _data[Position..];
            Position += 4;
            return bigEndian ? BinaryPrimitives.ReadInt32BigEndian(span) : BinaryPrimitives.ReadInt32LittleEndian(span);
        }

        public int UInt16()
        {
            Require(2);
            var span = _data[Position..];
            Position += 2;
            return bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span);
        }

        public byte Byte()
        {
            Require(1);
            return _data[Position++];
        }

        public float Single() => BitConverter.Int32BitsToSingle(Int32());
    }
}
