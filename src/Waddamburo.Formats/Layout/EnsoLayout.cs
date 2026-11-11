using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;

namespace Waddamburo.Formats.Layout;

/// <summary>
/// Reads <c>ensolayout.bin</c>: the gameplay stage offsets, a boost::serialization binary archive
/// holding an indexed list of big-endian {x, y} float pairs.
/// </summary>
public sealed class EnsoLayout
{
    private const string Signature = "serialization::archive";
    private const int CountOffset = 0x31;
    private const int FirstRecordOffset = 0x35;
    private const int RecordSize = 12;
    private const int MaxRecords = 4096;

    private EnsoLayout(ImmutableArray<Vector2> points) => Points = points;

    /// <summary>Offsets by layout index.</summary>
    public ImmutableArray<Vector2> Points { get; }

    /// <summary>
    /// The layout without the game's file: where each part of the gameplay screen is placed (by layout index),
    /// the same arrangement as the game's own so that its movies and Waddamburo's can be mixed part by part
    /// (docs/development/free-movies-plan.md). A movie of ours is authored for its part's place here.
    /// </summary>
    public static EnsoLayout Standard { get; } = new(
    [
        new(0, 0), new(-640, -360), new(0, 0), new(0, 536), new(0, 360), new(0, 360),
        new(0, 656), new(0, 0), new(0, 536), new(0, 0), new(0, 520), new(320, 20),
        new(320, 636), new(900, 50), new(248, 271), new(248, 449), new(248, 244), new(248, 422),
        new(87, 292), new(87, 428), new(88, 252), new(88, 388), new(0, 184), new(0, 360),
        new(208, 102), new(208, 616), new(386, 105), new(386, 618), new(0, 0), new(0, 512),
        new(200, 92), new(200, 630), new(23, 193), new(23, 495), new(143, 156), new(143, 531),
    ]);

    public Vector2 this[int index] => (uint)index < (uint)Points.Length
        ? Points[index]
        : throw new InvalidDataException($"ensolayout has no entry {index} ({Points.Length} entries).");

    public static EnsoLayout Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllBytes(path));
    }

    public static EnsoLayout Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < FirstRecordOffset
            || BinaryPrimitives.ReadUInt32BigEndian(data) != Signature.Length
            || !data.Slice(4, Signature.Length).SequenceEqual(Encoding.ASCII.GetBytes(Signature)))
            throw new InvalidDataException("ensolayout is not a boost binary archive.");
        var count = BinaryPrimitives.ReadUInt32BigEndian(data[CountOffset..]);
        if (count > MaxRecords || data.Length != FirstRecordOffset + (long)count * RecordSize)
            throw new InvalidDataException("ensolayout record count does not match its size.");

        var points = ImmutableArray.CreateBuilder<Vector2>((int)count);
        for (var index = 0; index < count; index++)
        {
            var record = data.Slice(FirstRecordOffset + index * RecordSize, RecordSize);
            if (BinaryPrimitives.ReadInt32BigEndian(record) != index)
                throw new InvalidDataException($"ensolayout record {index} is out of order.");
            var point = new Vector2(
                BinaryPrimitives.ReadSingleBigEndian(record[4..]),
                BinaryPrimitives.ReadSingleBigEndian(record[8..]));
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
                throw new InvalidDataException($"ensolayout record {index} is not finite.");
            points.Add(point);
        }
        return new EnsoLayout(points.MoveToImmutable());
    }
}
