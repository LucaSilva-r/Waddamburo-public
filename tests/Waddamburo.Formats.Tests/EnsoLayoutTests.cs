using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Waddamburo.Formats.Layout;

namespace Waddamburo.Formats.Tests;

public sealed class EnsoLayoutTests
{
    [Fact]
    public void ParsesSyntheticArchive()
    {
        var layout = EnsoLayout.Parse(createArchive(new(0, 0), new(-640, -360), new(0, 184)));

        Assert.Equal(3, layout.Points.Length);
        Assert.Equal(new Vector2(-640, -360), layout[1]);
        Assert.Equal(new Vector2(0, 184), layout[2]);
        Assert.Throws<InvalidDataException>(() => layout[3]);
    }

    [Fact]
    public void RejectsSizeMismatch()
    {
        var data = createArchive(new Vector2(0, 0));

        Assert.Throws<InvalidDataException>(() => EnsoLayout.Parse(data.AsSpan(0, data.Length - 1)));
    }

    [Fact]
    public void RejectsOutOfOrderRecords()
    {
        var data = createArchive(new(0, 0), new(1, 1));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0x35 + 12), 5);

        Assert.Throws<InvalidDataException>(() => EnsoLayout.Parse(data));
    }

    private static byte[] createArchive(params Vector2[] points)
    {
        var data = new byte[0x35 + points.Length * 12];
        BinaryPrimitives.WriteUInt32BigEndian(data, 22);
        Encoding.ASCII.GetBytes("serialization::archive", data.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x31), (uint)points.Length);
        for (var index = 0; index < points.Length; index++)
        {
            var record = data.AsSpan(0x35 + index * 12);
            BinaryPrimitives.WriteInt32BigEndian(record, index);
            BinaryPrimitives.WriteSingleBigEndian(record[4..], points[index].X);
            BinaryPrimitives.WriteSingleBigEndian(record[8..], points[index].Y);
        }
        return data;
    }
}
