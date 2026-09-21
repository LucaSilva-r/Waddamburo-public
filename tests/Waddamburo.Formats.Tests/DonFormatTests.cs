using System.Buffers.Binary;
using System.Numerics;
using Waddamburo.Formats.Don;
using Waddamburo.Formats.IO;
using Waddamburo.Formats.Nud;

namespace Waddamburo.Formats.Tests;

public sealed class DonFormatTests
{
    [Fact]
    public void ParsesBakedAnimationAndEvaluatesRowVectorHierarchy()
    {
        var values = new float[DonSkeleton.AccessoryValuesPerFrame];
        values[0] = 10;
        values[1] = 20;
        values[2] = 30;
        values[6] = 2;
        values[7] = 3;
        values[8] = 4;
        values[12] = values[13] = values[14] = 1;
        var animation = DonAnimationFile.Parse(createAnimation([values]));
        var skeleton = DonSkeleton.ForAnimation(animation);

        var world = skeleton.EvaluateWorld(animation.GetFrame(0));

        Assert.Equal(DonSkeleton.AccessoryValuesPerFrame, animation.ValuesPerFrame);
        Assert.Equal(2, world.Length);
        Assert.Equal(new Vector3(12, 23, 34), world[1].Translation);
        Assert.All(skeleton.CreateSkinningPalette(animation.GetFrame(0), animation.GetFrame(0)),
            matrix => Assert.True(Matrix4x4.Identity.Equals(matrix)));
    }

    [Fact]
    public void CharacterSkeletonExposesExpressionAndCompactPaletteMapping()
    {
        var values = new float[DonSkeleton.CharacterValuesPerFrame];
        for (var offset = 6; offset < 291;)
        {
            var channelLength = offset == 6 ? 9 : 9;
            if (offset + 8 < 291)
                values[offset + 6] = values[offset + 7] = values[offset + 8] = 1;
            offset += channelLength;
        }
        values[291] = 7.4f;
        var animation = DonAnimationFile.Parse(createAnimation([values]));
        var skeleton = DonSkeleton.ForAnimation(animation);

        Assert.Equal(39, skeleton.Channels.Length);
        Assert.Equal(7, skeleton.GetExpression(animation.GetFrame(0)));
        Assert.Equal(11, DonSkeleton.ExpandCompactPaletteIndex(8, 25));
        Assert.Equal(38, DonSkeleton.ExpandCompactPaletteIndex(26, 26));
    }

    [Fact]
    public void RejectsMalformedAndResourceExcessiveAnimations()
    {
        Assert.Throws<FormatReadException>(() => DonAnimationFile.Parse(createAnimation([], 1.5f)));
        Assert.Throws<FormatReadException>(() => DonAnimationFile.Parse(new byte[5]));
        var twoFrames = createAnimation([new float[15], new float[15]]);
        Assert.Throws<FormatLimitException>(() =>
            DonAnimationFile.Parse(twoFrames, new ParserLimits(maxFrames: 1)));
    }

    [Fact]
    public void ParsesRigidNudGeometryMaterialsAndStripWinding()
    {
        var model = NudFile.Parse(createRigidTriangle());

        Assert.Equal(0x0200, model.Version);
        Assert.Equal(1, model.BoneCount);
        var item = Assert.Single(model.Objects);
        Assert.Equal("body", item.Name);
        Assert.Equal(0, item.SingleBindBone);
        var polygon = Assert.Single(item.Polygons);
        Assert.Equal(new ushort[] { 0, 1, 2 }, polygon.TriangleIndices);
        Assert.Equal(new Vector3(1, 2, 3), polygon.Vertices[1].Position);
        Assert.Equal(new Vector2(0.5f, 0.25f), polygon.Vertices[1].TextureCoordinate);
        Assert.Equal(new Vector4(1, 0, 0, 0), polygon.Vertices[1].BoneWeights);
        var material = Assert.Single(polygon.Materials);
        Assert.Equal(1, material.ShaderKind);
        Assert.Equal(0x00010000u, Assert.Single(material.TextureIds));
    }

    [Fact]
    public void NudParserRejectsBadIndicesAndHonorsVertexLimit()
    {
        var badIndex = createRigidTriangle();
        writeU16(badIndex.AsSpan(0x64), 3);
        Assert.Throws<FormatReadException>(() => NudFile.Parse(badIndex));

        Assert.Throws<FormatLimitException>(() =>
            NudFile.Parse(createRigidTriangle(), new ParserLimits(maxVertices: 2)));
    }

    private static byte[] createAnimation(float[][] frames, float? encodedCount = null)
    {
        var stride = frames.Length == 0 ? 0 : frames[0].Length;
        var data = new byte[4 + frames.Length * stride * 4];
        writeF32(data, encodedCount ?? frames.Length);
        var offset = 4;
        foreach (var frame in frames)
        {
            Assert.Equal(stride, frame.Length);
            foreach (var value in frame)
            {
                writeF32(data.AsSpan(offset), value);
                offset += 4;
            }
        }
        return data;
    }

    private static byte[] createRigidTriangle()
    {
        const int polygonBase = 0x60;
        const int polygonTable = 0x70;
        const int material = 0xA0;
        const int vertexBase = 0xE0;
        const int nameBase = 0x128;
        var data = new byte[0x130];
        "NDP3"u8.CopyTo(data);
        writeU32(data.AsSpan(4), (uint)data.Length);
        writeU16(data.AsSpan(8), 0x0200);
        writeU16(data.AsSpan(10), 1);
        writeU16(data.AsSpan(14), 1);
        writeU32(data.AsSpan(0x10), polygonBase - 0x30);
        writeU32(data.AsSpan(0x14), vertexBase - polygonBase);
        writeU32(data.AsSpan(0x18), nameBase - vertexBase);
        writeU32(data.AsSpan(0x1C), 0);

        writeU32(data.AsSpan(0x30 + 0x20), 0);
        writeU16(data.AsSpan(0x30 + 0x28), 0);
        writeU16(data.AsSpan(0x30 + 0x2A), 1);
        writeU32(data.AsSpan(0x30 + 0x2C), polygonTable);

        writeU16(data.AsSpan(polygonBase), 0);
        writeU16(data.AsSpan(polygonBase + 2), 1);
        writeU16(data.AsSpan(polygonBase + 4), 2);
        writeU32(data.AsSpan(polygonTable), 0);
        writeU32(data.AsSpan(polygonTable + 4), 0);
        writeU32(data.AsSpan(polygonTable + 8), 0);
        writeU16(data.AsSpan(polygonTable + 12), 3);
        data[polygonTable + 14] = 0x06;
        data[polygonTable + 15] = 0x10;
        writeU32(data.AsSpan(polygonTable + 16), material);
        writeU16(data.AsSpan(polygonTable + 0x20), 3);

        writeU32(data.AsSpan(material), 1);
        writeU16(data.AsSpan(material + 8), 0);
        writeU16(data.AsSpan(material + 10), 1);
        writeU16(data.AsSpan(material + 12), 0);
        writeU16(data.AsSpan(material + 18), 0x405);
        writeU32(data.AsSpan(material + 0x20), 0x00010000);

        for (var index = 0; index < 3; index++)
        {
            var offset = vertexBase + index * 24;
            writeF32(data.AsSpan(offset), index);
            writeF32(data.AsSpan(offset + 4), index * 2);
            writeF32(data.AsSpan(offset + 8), index * 3);
            writeF16(data.AsSpan(offset + 12), 0);
            writeF16(data.AsSpan(offset + 14), 0);
            writeF16(data.AsSpan(offset + 16), 1);
            writeF16(data.AsSpan(offset + 20), index * 0.5f);
            writeF16(data.AsSpan(offset + 22), index * 0.25f);
        }
        "body\0"u8.CopyTo(data.AsSpan(nameBase));
        return data;
    }

    private static void writeU16(Span<byte> destination, int value) =>
        BinaryPrimitives.WriteUInt16BigEndian(destination, checked((ushort)value));

    private static void writeU32(Span<byte> destination, int value) => writeU32(destination, checked((uint)value));

    private static void writeU32(Span<byte> destination, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(destination, value);

    private static void writeF32(Span<byte> destination, float value) =>
        writeU32(destination, unchecked((uint)BitConverter.SingleToInt32Bits(value)));

    private static void writeF16(Span<byte> destination, float value) =>
        writeU16(destination, BitConverter.HalfToUInt16Bits((Half)value));
}
