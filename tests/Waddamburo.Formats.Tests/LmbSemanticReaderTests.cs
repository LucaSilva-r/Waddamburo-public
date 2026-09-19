using System.Buffers.Binary;
using Waddamburo.Formats.Diagnostics;
using Waddamburo.Formats.IO;
using Waddamburo.Formats.Lmb;

namespace Waddamburo.Formats.Tests;

public sealed class LmbSemanticReaderTests
{
    private static readonly string[] CatAndEmpty = ["cat", ""];
    private static readonly uint[] PlaceUnknownWords = [0xDEAD, 0xA, 0xB];
    private static readonly string[] DuplicateAndOrphanCodes =
        ["LMB_DUPLICATE_POOL", "LMB_ORPHAN_GEOMETRY", "LMB_ORPHAN_TIMELINE"];

    [Fact]
    public void BuildsTypedPoolsDefinitionsAndTimelineWhileRetainingUnknownRecords()
    {
        var geometry = new uint[18];
        for (var index = 0; index < 16; index++)
            geometry[index] = single(index + 0.5f);
        geometry[16] = 3;
        geometry[17] = 0x44;

        var file = createLmb(
            record(LmbTags.StringPool, stringPool("cat", "")),
            words(LmbTags.ColorTransformPool, 1, 0x0100FF00, 0x0080FFFF),
            words(LmbTags.MatrixPool, 1, single(1), single(2), single(3), single(4), single(5), single(6)),
            words(LmbTags.TranslationPool, 1, single(7), single(8)),
            words(LmbTags.BoundsPool, 1, single(9), single(10), single(11), single(12)),
            words(LmbTags.TextureBoundsPool, 1, single(13), single(14), single(15), single(16)),
            record(LmbTags.ActionPool, actionPool([0x81, 0x02, 0xFF])),
            words(LmbTags.DefineShape, 42, 0xAA, 0xBB, 1),
            words(LmbTags.ShapeGeometry, geometry),
            words(LmbTags.DefineSprite, 7, 0x11, 0x22, 1, 2, 1, 0x33),
            words(LmbTags.FrameLabel, 0, 0, 0x99),
            words(LmbTags.PlaceObject, 42, 6, 0xDEAD, 0, 0x00010002, 0x00030000, 0x00040000, 0x80000005, 0, uint.MaxValue, 0xA, 0xB),
            words(LmbTags.RemoveObject, 42, 0x00020000),
            words(LmbTags.DoAction, 0, 0x55),
            words(LmbTags.ShowFrame, 0, 4),
            words(LmbTags.FrameKey, 1, 2),
            words(0xDEADBEEF, 1, 2));

        var result = LmbSemanticReader.Read(file);
        var movie = result.Value;

        Assert.Empty(result.Diagnostics);
        Assert.Equal(CatAndEmpty, movie.Strings.Select(value => value.Value));
        Assert.Equal(new LmbColorTransform(256, -256, 128, -1), Assert.Single(movie.ColorTransforms));
        Assert.Equal(6, Assert.Single(movie.Matrices).Y);
        Assert.Equal(new LmbTranslation(7, 8), Assert.Single(movie.Translations));
        Assert.Equal(12, Assert.Single(movie.Bounds).Bottom);
        Assert.Equal(EvidenceStatus.CorpusValidatedInference, Assert.Single(movie.TextureBounds).Evidence);
        Assert.Equal(new byte[] { 0x81, 0x02, 0xFF }, Assert.Single(movie.Actions).Bytecode);

        var shape = Assert.Single(movie.Shapes);
        Assert.Equal(42U, shape.CharacterId);
        Assert.Equal(1U, shape.DeclaredGeometryCount);
        var shapeGeometry = Assert.Single(shape.Geometry);
        Assert.Equal(4, shapeGeometry.Vertices.Length);
        Assert.Equal(0.5f, shapeGeometry.Vertices[0].X);
        Assert.Equal(3U, shapeGeometry.TextureIndex);
        Assert.Equal(EvidenceStatus.Candidate, shapeGeometry.Evidence);

        var sprite = Assert.Single(movie.Sprites);
        Assert.Equal(7U, sprite.CharacterId);
        Assert.Collection(
            sprite.Timeline,
            command => Assert.IsType<LmbFrameLabelCommand>(command),
            command =>
            {
                var place = Assert.IsType<LmbPlaceObjectCommand>(command);
                Assert.Equal(42U, place.CharacterId);
                Assert.Equal(1, place.Mode);
                Assert.Equal(2, place.BlendMode);
                Assert.Equal(3, place.Depth);
                Assert.Equal(0x8000, place.PositionKind);
                Assert.Equal(5, place.PositionIndex);
                Assert.Equal(EvidenceStatus.CorpusValidatedInference, place.Evidence);
                Assert.Equal(PlaceUnknownWords, place.UninterpretedWords);
            },
            command => Assert.Equal(3U, Assert.IsType<LmbRemoveObjectCommand>(command).CandidateDepth),
            command => Assert.IsType<LmbDoActionCommand>(command),
            command => Assert.IsType<LmbShowFrameCommand>(command),
            command => Assert.IsType<LmbFrameKeyCommand>(command));
        Assert.Equal(0xDEADBEEFU, Assert.Single(movie.UninterpretedRecords).Tag);
        Assert.Same(file, movie.RawFile);
    }

    [Fact]
    public void ReportsDuplicatePoolsAndOrphanSemanticRecordsWithoutDiscardingThem()
    {
        var file = createLmb(
            record(LmbTags.StringPool, stringPool("one")),
            record(LmbTags.StringPool, stringPool("two")),
            words(LmbTags.ShapeGeometry, new uint[18]),
            words(LmbTags.ShowFrame, 0, 0));

        var result = LmbSemanticReader.Read(file);

        Assert.Equal(["one", "two"], result.Value.Strings.Select(value => value.Value));
        Assert.Equal(2, result.Value.UninterpretedRecords.Length);
        Assert.Equal(
            DuplicateAndOrphanCodes,
            result.Diagnostics.Select(diagnostic => diagnostic.Code));
    }

    [Fact]
    public void RejectsMalformedKnownRecordsAndEnforcesSemanticLimits()
    {
        var missingTerminator = createLmb(record(
            LmbTags.StringPool,
            bytes(0, 0, 0, 1, 0, 0, 0, 3, (byte)'b', (byte)'a', (byte)'d', 1)));
        Assert.Throws<FormatReadException>(() => LmbSemanticReader.Read(missingTerminator));

        var badMatrixCount = createLmb(words(LmbTags.MatrixPool, 2, 0, 0, 0, 0, 0, 0));
        Assert.Throws<FormatReadException>(() => LmbSemanticReader.Read(badMatrixCount));

        var action = createLmb(record(LmbTags.ActionPool, actionPool([1, 2, 3])));
        var exception = Assert.Throws<FormatLimitException>(() =>
            LmbSemanticReader.Read(action, new ParserLimits(maxActionBytes: 2)));
        Assert.Equal(nameof(ParserLimits.MaxActionBytes), exception.LimitName);
    }

    private static LmbFile createLmb(params (uint Tag, byte[] Payload)[] records)
    {
        using var stream = new MemoryStream();
        stream.Write("LMB\0"u8);
        stream.Write(new byte[LmbFile.HeaderLength - 4]);
        foreach (var (tag, payload) in records)
        {
            Assert.Equal(0, payload.Length % 4);
            writeUInt32(stream, tag);
            writeUInt32(stream, checked((uint)payload.Length / 4));
            stream.Write(payload);
        }
        return LmbFile.Parse(stream.ToArray());
    }

    private static (uint Tag, byte[] Payload) record(uint tag, byte[] payload) => (tag, payload);

    private static (uint Tag, byte[] Payload) words(uint tag, params uint[] values)
    {
        var payload = new byte[values.Length * 4];
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(index * 4, 4), values[index]);
        return (tag, payload);
    }

    private static byte[] stringPool(params string[] values)
    {
        using var stream = new MemoryStream();
        writeUInt32(stream, checked((uint)values.Length));
        foreach (var value in values)
        {
            var encoded = System.Text.Encoding.UTF8.GetBytes(value);
            writeUInt32(stream, checked((uint)encoded.Length));
            stream.Write(encoded);
            stream.WriteByte(0);
            while (stream.Position % 4 != 0)
                stream.WriteByte(0);
        }
        return stream.ToArray();
    }

    private static byte[] actionPool(params byte[][] values)
    {
        using var stream = new MemoryStream();
        writeUInt32(stream, checked((uint)values.Length));
        foreach (var value in values)
        {
            writeUInt32(stream, checked((uint)value.Length));
            stream.Write(value);
            while (stream.Position % 4 != 0)
                stream.WriteByte(0);
        }
        return stream.ToArray();
    }

    private static byte[] bytes(params byte[] values) => values;

    private static uint single(float value) => BitConverter.SingleToUInt32Bits(value);

    private static void writeUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }
}
