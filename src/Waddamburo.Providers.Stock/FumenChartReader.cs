using System.Buffers.Binary;
using System.Collections.Immutable;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.Stock;

/// <summary>
/// Converts one fumen <c>.bin</c> course into a playable chart.
/// Layout as documented by the MIT-licensed tja2fumen parser: a 520-byte header whose
/// measure count is at 0x200 and branch points per judgement at 0x1D4, then per measure BPM, start
/// offset (ms), Go-Go and barline flags, six branch thresholds, and three branches of notes (normal,
/// expert, master). Arcade files are big-endian. A chart with any threshold set has 譜面分岐: its notes
/// are kept per route; otherwise the three branches repeat each other and the normal one is read.
/// </summary>
internal static class FumenChartReader
{
    private const int HeaderSize = 520;
    private const int MeasureCountOffset = 0x200;
    private const int HasBranchesOffset = 0x1B0;
    private const int BranchPointsOffset = 0x1D4;
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
        reader.Position = BranchPointsOffset;
        var points = new ChartBranchPoints(reader.Int32(), reader.Int32(), reader.Int32(), reader.Int32(),
            reader.Int32(), reader.Int32(), reader.Int32(), reader.Int32(), reader.Int32());
        var (thresholded, differ) = branchFlags(data, bigEndian, measureCount);
        var branched = thresholded && differ;
        reader.Position = HeaderSize;

        // Audio time (ms) of every event; shifted to non-negative chart time at the end.
        var measures = new List<(double Time, double Bpm, double[] Speeds, bool GoGo, bool Barline)>(measureCount);
        var notes = new List<(double Time, PlayableNoteKind Kind, bool Hand, bool InRun, bool Ko, ChartBranch? Branch)>();
        var longNotes = new List<(double Start, double End, PlayableLongNoteKind Kind, int Hits, ChartBranch? Branch)>();
        var checks = new List<(int Measure, ChartBranchThreshold Normal, ChartBranchThreshold Expert, ChartBranchThreshold Master)>();
        int? scoreInit = null, scoreDiff = null;
        for (var measure = 0; measure < measureCount; measure++)
        {
            reader.Require(MeasureSize);
            var bpm = reader.Single();
            var offset = reader.Single();
            var goGo = reader.Byte() != 0;
            var barline = reader.Byte() != 0;
            reader.Position += 2; // padding
            var fromNormal = new ChartBranchThreshold(reader.Int32(), reader.Int32());
            var fromExpert = new ChartBranchThreshold(reader.Int32(), reader.Int32());
            var fromMaster = new ChartBranchThreshold(reader.Int32(), reader.Int32());
            reader.Position += 4; // padding
            if (thresholded && (fromNormal, fromExpert, fromMaster) != (ChartBranchThreshold.Stay, ChartBranchThreshold.Stay, ChartBranchThreshold.Stay))
                checks.Add((measure, fromNormal, fromExpert, fromMaster));
            if (!float.IsFinite(bpm) || bpm <= 0 || !float.IsFinite(offset))
                throw new InvalidDataException($"Fumen measure {measure} has an invalid BPM or offset.");
            // A measure's offset is when its start enters the lane: one 4/4 measure before it is hit.
            var time = offset + 240_000d / bpm;
            var speeds = new double[] { 1, 1, 1 };
            for (var branch = 0; branch < 3; branch++)
            {
                reader.Require(BranchHeaderSize);
                int count = reader.UInt16();
                reader.Position += 2;
                var branchSpeed = reader.Single();
                if (float.IsFinite(branchSpeed))
                    speeds[branch] = branchSpeed;
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
                    if (branch != 0 && !branched || !float.IsFinite(position))
                        continue;
                    ChartBranch? route = branched ? (ChartBranch)branch : null;
                    var noteMs = time + position;
                    if (type is 0xA or 0xC)
                    {
                        longNotes.Add((noteMs, noteMs + Math.Max(0, duration), type == 0xA ? PlayableLongNoteKind.Balloon : PlayableLongNoteKind.Kusudama, Math.Max(1, first), route));
                        continue;
                    }
                    if (first != 0 && branch == 0)
                        (scoreInit, scoreDiff) = (first, second / 4);
                    if (isRoll)
                        longNotes.Add((noteMs, noteMs + Math.Max(0, duration), type == 0x9 ? PlayableLongNoteKind.BigRoll : PlayableLongNoteKind.Roll, 0, route));
                    else if (kind(type) is { } noteKind)
                        notes.Add((noteMs, noteKind, type is 0xB or 0xD, type is 0x2 or 0x3 or 0x5, type is 0x3, route));
                    if (notes.Count + longNotes.Count > maximumNotes)
                        throw new InvalidDataException("The fumen exceeds the note limit.");
                }
            }
            // Control streams must be ordered; a measure never starts before the previous one.
            if (measures.Count > 0 && time < measures[^1].Time)
                time = measures[^1].Time;
            measures.Add((time, bpm, speeds, goGo, barline));
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
        // Tempo is one per measure; each route has its own scroll.
        ImmutableArray<ChartScrollPoint>.Builder[] scrolls = [.. Enumerable.Range(0, 3).Select(static _ => ImmutableArray.CreateBuilder<ChartScrollPoint>())];
        var effects = ImmutableArray.CreateBuilder<ChartEffectPoint>();
        for (var index = 0; index < measures.Count; index++)
        {
            var current = measures[index];
            var time = index == 0 ? TimeSpan.Zero : chartTime(current.Time);
            if (index == 0 || current.Bpm != measures[index - 1].Bpm)
                timing.Add(new ChartTimingPoint(time, current.Bpm, 4, 4));
            for (var route = 0; route < scrolls.Length; route++)
                if (index == 0 || current.Speeds[route] != measures[index - 1].Speeds[route])
                    scrolls[route].Add(new ChartScrollPoint(time, current.Speeds[route]));
            if (index == 0 || current.GoGo != measures[index - 1].GoGo)
                effects.Add(new ChartEffectPoint(time, current.GoGo));
        }

        return new PlayableChart(
            key,
            TimeSpan.FromMilliseconds(shiftMs),
            chartTime(endMs),
            notes.OrderBy(static note => note.Time).Select(note => new PlayableHitObject(chartTime(note.Time), note.Kind, note.Hand)
                { InRun = note.InRun, IsKo = note.Ko, Branch = note.Branch }),
            timing.ToImmutable(),
            scrolls[0].ToImmutable(),
            effects.ToImmutable(),
            measures.Select(measure => new ChartBarLine(chartTime(measure.Time), measure.Barline)),
            longNotes.OrderBy(static note => note.Start).Select(note => new PlayableLongNote(chartTime(note.Start), chartTime(note.End), note.Kind, note.Hits)
                { Branch = note.Branch }))
        {
            ScoreInit = scoreInit,
            ScoreDiff = scoreDiff,
            RouteControls = branched ? [.. scrolls.Select(scroll => new ChartRouteControls(timing.ToImmutable(), scroll.ToImmutable()))] : [],
            // A measure's thresholds pick its route; they are judged a measure earlier, at the previous bar line
            // (tja2fumen: "calculated one measure before #BRANCHSTART").
            Branching = thresholded ? new ChartBranching([.. checks.Select(check => new ChartBranchCheck(
                chartTime(measures[Math.Max(0, check.Measure - 1)].Time), chartTime(measures[check.Measure].Time),
                check.Normal, check.Expert, check.Master))], points) { SameNotes = !differ } : null,
        };
    }

    /// <summary>
    /// The header's has-branches byte alone: song select's 分岐 mark for Green's charts, read without parsing
    /// the file (Green sets it on its 88 branched charts; Nijiiro uses musicinfo's flags instead).
    /// </summary>
    public static bool HasBranchesFlag(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length <= HasBranchesOffset) return false;
            file.Position = HasBranchesOffset;
            return file.ReadByte() > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // 譜面分岐: a threshold set somewhere (all -1: no check there), and whether the routes' notes differ. Same notes
    // still change route on the lane (Joubutsu 2000 forces Master, Normal, Master) but play and hash as one route.
    // The header's has-branches byte is not used: Nijiiro files carry other values there.
    private static (bool Thresholds, bool Differ) branchFlags(ReadOnlySpan<byte> data, bool bigEndian, int measureCount)
    {
        var reader = new Reader(data, bigEndian) { Position = HeaderSize };
        bool thresholds = false, differ = false;
        Span<Range> notes = stackalloc Range[3];
        for (var measure = 0; measure < measureCount && !(thresholds && differ); measure++)
        {
            reader.Require(MeasureSize);
            reader.Position += 12;
            for (var value = 0; value < 6; value++)
                thresholds |= reader.Int32() != -1;
            reader.Position += 4;
            for (var branch = 0; branch < 3; branch++)
            {
                reader.Require(BranchHeaderSize);
                int count = reader.UInt16();
                reader.Position += 6;
                var from = reader.Position;
                for (var index = 0; index < count; index++)
                {
                    reader.Require(NoteSize);
                    var type = reader.Int32();
                    reader.Position += NoteSize - 4 + (type is 0x6 or 0x9 or 0x62 ? RollPadding : 0);
                }
                notes[branch] = from..reader.Position;
            }
            differ |= !data[notes[0]].SequenceEqual(data[notes[1]]) || !data[notes[0]].SequenceEqual(data[notes[2]]);
        }
        return (thresholds, differ);
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
