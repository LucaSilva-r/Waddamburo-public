using System.Buffers.Binary;
using System.Text;

namespace Waddamburo.Providers.Stock;

/// <summary>
/// 太鼓の達人Wii (2008) <c>sheet/sht/bin/&lt;id&gt;1p_&lt;e|n|h|m&gt;.bin</c>, big-endian. Header: measure count,
/// note table offset (16 + 140 × measures), note count, has-branches. A 140-byte measure: start offset (ms, the
/// arcade fumen's lane-entry time), BPM, barline, Go-Go, section reset (0) or -1, six branch thresholds as in the
/// arcade fumen, six scrolls (three per note set), then two note sets × three branches of (first note, count,
/// notes left). A 16-byte note: type, tick (1/12 beat from the measure start), balloon hits, roll length (ms).
/// Set 0 is the chart; set 1 only differs in some 2P duet files. The chart is rewritten as an arcade fumen and read
/// by <see cref="FumenChartReader"/> (its wiiNotes), so branches, rolls and scroll follow the same rules.
/// </summary>
internal static class WiiChartReader
{
    private const int HeaderSize = 16, MeasureSize = 140, NoteSize = 16;

    /// <summary>A course's scoring from <c>tuning.bin</c>: stars, 配点, soul gauge ratios (16.16) and branch points.</summary>
    internal sealed record CourseTuning(int Stars, int ScoreInit, int ScoreDiff, int[] GaugeRatios, int[] BranchPoints);

    /// <summary>The chart as an arcade fumen (520-byte header, 40-byte measures, 24-byte notes), big-endian.</summary>
    public static byte[] ToFumen(ReadOnlySpan<byte> wii, CourseTuning? tuning, int maximumMeasures, int maximumNotes)
    {
        if (wii.Length < HeaderSize)
            throw new InvalidDataException("The Wii chart is shorter than its header.");
        int measures = i32(wii, 0), notesAt = i32(wii, 4), notes = i32(wii, 8);
        if (measures <= 0 || measures > maximumMeasures || notes < 0 || notes > maximumNotes
            || notesAt != HeaderSize + measures * MeasureSize || (long)notesAt + (long)notes * NoteSize > wii.Length)
            throw new InvalidDataException("The Wii chart header is invalid.");

        using var output = new MemoryStream();
        var header = new byte[520];
        header[0x1B0] = (byte)(i32(wii, 12) != 0 ? 1 : 0);
        for (var index = 0; index < 3; index++)
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x1C8 + 4 * index), tuning?.GaugeRatios[index] ?? 0x10000);
        // Points per 良/可/不可/連打 used by the branch thresholds; tja2fumen's defaults when the tuning has none.
        int[] points = tuning?.BranchPoints ?? [20, 10, 0, 1, 20, 10, 1, 20, 0];
        for (var index = 0; index < 9; index++)
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x1D4 + 4 * index), points[index]);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x200), measures);
        output.Write(header);

        var scored = false;
        Span<byte> buffer = stackalloc byte[40];
        for (var measure = 0; measure < measures; measure++)
        {
            var at = HeaderSize + measure * MeasureSize;
            var bpm = f32(wii, at + 4);
            buffer.Clear();
            BinaryPrimitives.WriteSingleBigEndian(buffer, bpm);
            BinaryPrimitives.WriteSingleBigEndian(buffer[4..], f32(wii, at)); // same lane-entry offset as the arcade's
            buffer[8] = (byte)(i32(wii, at + 12) != 0 ? 1 : 0); // Go-Go
            buffer[9] = (byte)(i32(wii, at + 8) != 0 ? 1 : 0); // barline
            // ponytail: the section reset (at + 16) is dropped, as FumenChartReader has no #SECTION; add it there if a
            // Wii branched chart picks the wrong route.
            for (var value = 0; value < 6; value++)
                BinaryPrimitives.WriteInt32BigEndian(buffer[(12 + 4 * value)..], i32(wii, at + 20 + 4 * value));
            output.Write(buffer[..40]);
            for (var branch = 0; branch < 3; branch++)
            {
                int first = i32(wii, at + 68 + 12 * branch), count = i32(wii, at + 72 + 12 * branch);
                if (first < 0 || count < 0 || first > notes - count)
                    throw new InvalidDataException($"Wii chart measure {measure} points outside its notes.");
                buffer.Clear();
                BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)count);
                BinaryPrimitives.WriteSingleBigEndian(buffer[4..], f32(wii, at + 44 + 4 * branch));
                output.Write(buffer[..8]);
                for (var index = first; index < first + count; index++)
                {
                    var note = notesAt + index * NoteSize;
                    int wiiType = i32(wii, note), type = arcadeType(wiiType);
                    buffer.Clear();
                    BinaryPrimitives.WriteInt32BigEndian(buffer, type);
                    BinaryPrimitives.WriteSingleBigEndian(buffer[4..], i32(wii, note + 4) * 5000f / bpm);
                    if (type is 0xA or 0xC)
                        BinaryPrimitives.WriteUInt16BigEndian(buffer[16..], (ushort)Math.Clamp(i32(wii, note + 8), 1, ushort.MaxValue));
                    else if (!scored && branch == 0 && tuning is { ScoreInit: > 0 })
                    {
                        // The arcade fumen keeps 配点 on its first note.
                        BinaryPrimitives.WriteUInt16BigEndian(buffer[16..], (ushort)Math.Min(tuning.ScoreInit, ushort.MaxValue));
                        BinaryPrimitives.WriteUInt16BigEndian(buffer[18..], (ushort)Math.Min(tuning.ScoreDiff * 4, ushort.MaxValue));
                        scored = true;
                    }
                    if (type is 0x6 or 0x9 or 0xA or 0xC)
                        BinaryPrimitives.WriteSingleBigEndian(buffer[20..], Math.Max(0, i32(wii, note + 12)));
                    output.Write(buffer[..(type is 0x6 or 0x9 ? 32 : 24)]);
                }
            }
        }
        return output.ToArray();
    }

    // The arcade's numbering, 14-25 the Wii notes (read with FumenChartReader's wiiNotes).
    private static int arcadeType(int type) => type is >= 1 and <= 25 ? type
        : throw new InvalidDataException($"Unknown Wii note type {type}.");

    /// <summary>
    /// <c>sheet/tuning/bin/tuning.bin</c>: a record count, 2172-byte records (music id, bank and an unknown string
    /// offset, then 18 course entries of 120 bytes), then a string pool. A course entry: name offset
    /// (<c>&lt;id&gt;1p_&lt;c&gt;</c>), stars, ?, score init, score diff, …, gauge ratios at +68, branch points at +80.
    /// </summary>
    public static Dictionary<string, CourseTuning> ReadTuning(ReadOnlySpan<byte> data)
    {
        const int RecordSize = 2172, Courses = 18, CourseSize = 120;
        var result = new Dictionary<string, CourseTuning>(StringComparer.Ordinal);
        if (data.Length < 4 || i32(data, 0) is var count && (count < 0 || count > (data.Length - 4) / RecordSize))
            return result;
        var pool = data[(4 + count * RecordSize)..];
        for (var record = 0; record < count; record++)
            for (var course = 0; course < Courses; course++)
            {
                var at = 4 + record * RecordSize + 12 + course * CourseSize;
                if (text(pool, i32(data, at), Encoding.ASCII) is not { Length: > 0 } name)
                    continue;
                result[name] = new CourseTuning(i32(data, at + 4), i32(data, at + 12), i32(data, at + 16),
                    [i32(data, at + 68), i32(data, at + 72), i32(data, at + 76)],
                    [i32(data, at + 80), i32(data, at + 84), i32(data, at + 88), i32(data, at + 92), i32(data, at + 96),
                     i32(data, at + 100), i32(data, at + 104), i32(data, at + 108), i32(data, at + 112)]);
            }
        return result;
    }

    internal sealed record Song(string Id, string Title, int Genre, TimeSpan? Preview);

    /// <summary>
    /// <c>sheet/musicinfo/bin/musicinfo.bin</c>: song count, record offset, string pool offset, then 108-byte
    /// records: id and title (Shift-JIS) offsets, genre (0 J-POP, 1 アニメ, 2 バラエティ, 3 クラシック, 4 ナムコオリジナル,
    /// 5 童謡, 6 ゲームミュージック), …, preview start in ms at +48.
    /// </summary>
    public static List<Song> ReadMusicInfo(ReadOnlySpan<byte> data)
    {
        const int RecordSize = 108;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var shiftJis = Encoding.GetEncoding(932);
        var songs = new List<Song>();
        if (data.Length < 12)
            return songs;
        int count = i32(data, 0), start = i32(data, 4), poolAt = i32(data, 8);
        if (count < 0 || start < 12 || poolAt > data.Length || (long)start + (long)count * RecordSize > poolAt)
            return songs;
        var pool = data[poolAt..];
        for (var index = 0; index < count; index++)
        {
            var at = start + index * RecordSize;
            if (text(pool, i32(data, at), Encoding.ASCII) is not { Length: > 0 } id)
                continue;
            var preview = i32(data, at + 48);
            songs.Add(new Song(id, text(pool, i32(data, at + 4), shiftJis) is { Length: > 0 } title ? title.Trim() : id,
                i32(data, at + 8), preview > 0 ? TimeSpan.FromMilliseconds(preview) : null));
        }
        return songs;
    }

    private static string? text(ReadOnlySpan<byte> pool, int offset, Encoding encoding)
    {
        if (offset < 0 || offset >= pool.Length)
            return null;
        var end = pool[offset..].IndexOf((byte)0);
        return end < 0 ? null : encoding.GetString(pool.Slice(offset, end));
    }

    private static int i32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadInt32BigEndian(data[at..]);

    private static float f32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadSingleBigEndian(data[at..]);
}
