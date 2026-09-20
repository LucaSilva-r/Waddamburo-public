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
            words(LmbTags.MovieProperties, 0, 0, 0, 7, 0, 0, 0, single(60)),
            record(LmbTags.StringPool, stringPool("cat", "")),
            words(LmbTags.ColorTransformPool, 1, 0x0100FF00, 0x0080FFFF),
            words(LmbTags.MatrixPool, 1, single(1), single(2), single(3), single(4), single(5), single(6)),
            words(LmbTags.TranslationPool, 1, single(7), single(8)),
            words(LmbTags.BoundsPool, 1, single(9), single(10), single(11), single(12)),
            words(LmbTags.TextureBoundsPool, 1, single(13), single(14), single(15), single(16)),
            record(LmbTags.ActionPool, actionPool([0x81, 0x02, 0x00, 0xFF, 0x00])),
            words(LmbTags.DefineShape, 42, 0xAA, 0xBB, 1),
            words(LmbTags.ShapeGeometry, geometry),
            words(LmbTags.DefineSprite, 7, 0x11, 0x22, 1, 2, 1, 0x33),
            words(LmbTags.FrameLabel, 0, 0, 0x99),
            words(LmbTags.ShowFrame, 0, 3),
            words(LmbTags.PlaceObject, 42, 6, 0xDEAD, 0, 0x00010002, 0x00030000, 0x00010000, 0x80000000, 0, uint.MaxValue, 0xA, 0xB),
            words(LmbTags.RemoveObject, 42, 0x00020000),
            words(LmbTags.DoAction, 0, 0x55),
            words(LmbTags.ShowFrame, 1, 0),
            words(LmbTags.FrameKey, 1, 0),
            words(0xDEADBEEF, 1, 2));

        var result = LmbSemanticReader.Read(file);
        var movie = result.Value;

        Assert.Empty(result.Diagnostics);
        Assert.Equal(7U, movie.Properties!.RootCharacterId);
        Assert.Equal(60f, movie.Properties.FrameRate);
        Assert.Equal(CatAndEmpty, movie.Strings.Select(value => value.Value));
        Assert.Equal(new LmbColorTransform(256, -256, 128, -1), Assert.Single(movie.ColorTransforms));
        Assert.Equal(6, Assert.Single(movie.Matrices).Y);
        Assert.Equal(new LmbTranslation(7, 8), Assert.Single(movie.Translations));
        Assert.Equal(12, Assert.Single(movie.Bounds).Bottom);
        Assert.Equal(EvidenceStatus.CorpusValidatedInference, Assert.Single(movie.TextureBounds).Evidence);
        var action = Assert.Single(movie.Actions);
        Assert.Equal(new byte[] { 0x81, 0x02, 0x00, 0xFF, 0x00 }, action.Bytecode);
        Assert.Equal(5, Assert.Single(action.Code.Instructions).Length);

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
            command => Assert.IsType<LmbShowFrameCommand>(command),
            command =>
            {
                var place = Assert.IsType<LmbPlaceObjectCommand>(command);
                Assert.Equal(42U, place.CharacterId);
                Assert.Equal(1, place.Mode);
                Assert.Equal(2, place.BlendMode);
                Assert.Equal(3, place.Depth);
                Assert.Equal(0x8000, place.PositionKind);
                Assert.Equal(0, place.PositionIndex);
                Assert.Equal(EvidenceStatus.CorpusValidatedInference, place.Evidence);
                Assert.Equal(PlaceUnknownWords, place.UninterpretedWords);
            },
            command => Assert.Equal((ushort)2, Assert.IsType<LmbRemoveObjectCommand>(command).Depth),
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

    [Fact]
    public void DecodesActionBoundariesBranchesAndLexicalBodies()
    {
        var branch = readSingleAction([0x06, 0x99, 0x02, 0x00, 0x01, 0x00, 0x07, 0x00]);
        Assert.Equal([0, 1, 6, 7], branch.Code.Instructions.Select(instruction => instruction.Offset));
        Assert.Equal(7, branch.Code.Instructions[1].BranchTarget);
        Assert.Equal(new byte[] { 0x01, 0x00 }, branch.Code.Instructions[1].OperandBytes);
        Assert.Equal(new Avm1BranchOperand(1, 7), branch.Code.Instructions[1].Operand);

        var lexical = readSingleAction([
            0x9B, 0x06, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x07, 0x00,
            0x94, 0x02, 0x00, 0x02, 0x00, 0x06, 0x00,
            0x00,
        ]);
        Assert.Equal((9, 2), (lexical.Code.Instructions[0].Body!.Offset, lexical.Code.Instructions[0].Body!.Length));
        Assert.Equal((16, 2), (lexical.Code.Instructions[1].Body!.Offset, lexical.Code.Instructions[1].Body!.Length));
        Assert.Equal(0x07, lexical.Code.Instructions[0].Body!.Instructions[0].Opcode);
        Assert.Equal(0x06, lexical.Code.Instructions[1].Body!.Instructions[0].Opcode);
    }

    [Fact]
    public void DecodesTypedPushFunctionAndUrlOperands()
    {
        var push = Assert.IsType<Avm1PushOperand>(readSingleAction([
            0x96, 0x21, 0x00,
            0x00, 0x34, 0x12,
            0x01, 0x00, 0x00, 0xC0, 0x3F,
            0x02,
            0x03,
            0x04, 0x07,
            0x05, 0x01,
            0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x40,
            0x07, 0xFE, 0xFF, 0xFF, 0xFF,
            0x08, 0xAA,
            0x09, 0x78, 0x56,
            0x00,
        ]).Code.Instructions[0].Operand);
        Assert.Collection(
            push.Values,
            value => Assert.Equal(new Avm1PushStringValue(0, 0x1234), value),
            value => Assert.Equal(new Avm1PushFloatValue(1.5f), value),
            value => Assert.IsType<Avm1PushNullValue>(value),
            value => Assert.IsType<Avm1PushUndefinedValue>(value),
            value => Assert.Equal(new Avm1PushRegisterValue(7), value),
            value => Assert.Equal(new Avm1PushBooleanValue(true), value),
            value => Assert.Equal(new Avm1PushDoubleValue(2.5), value),
            value => Assert.Equal(new Avm1PushIntegerValue(-2), value),
            value => Assert.Equal(new Avm1PushStringValue(8, 0xAA), value),
            value => Assert.Equal(new Avm1PushStringValue(9, 0x5678), value));

        var functionInstruction = readSingleAction([
            0x8E, 0x0F, 0x00,
            0x02, 0x00, 0x02, 0x00, 0x05, 0x34, 0x12,
            0x01, 0x03, 0x00, 0x00, 0x04, 0x00,
            0x02, 0x00,
            0x06, 0x00,
        ]).Code.Instructions[0];
        var function = Assert.IsType<Avm1FunctionOperand>(functionInstruction.Operand);
        Assert.Equal((2, 5, 0x1234, 2), (function.NameStringIndex, function.RegisterCount, function.Flags, function.BodyLength));
        Assert.Collection(
            function.Parameters,
            parameter => Assert.Equal(new Avm1FunctionParameter(1, 3), parameter),
            parameter => Assert.Equal(new Avm1FunctionParameter(0, 4), parameter));
        Assert.Equal(2, functionInstruction.Body!.Length);

        var url = Assert.IsType<Avm1GetUrlOperand>(readSingleAction([
            0x83, 0x0A, 0x00,
            (byte)'e', (byte)'v', (byte)'e', (byte)'n', (byte)'t', 0x00,
            (byte)'_', (byte)'r', (byte)'o', 0x00,
            0x00,
        ]).Code.Instructions[0].Operand);
        Assert.Equal(("event", "_ro"), (url.Url, url.Target));
    }

    [Fact]
    public void DecodesTypedTimelineRegisterAndStringControlOperands()
    {
        var action = readSingleAction([
            0x81, 0x02, 0x00, 0x34, 0x12,
            0x87, 0x01, 0x00, 0x05,
            0x8C, 0x02, 0x00, 0x07, 0x00,
            0x8A, 0x03, 0x00, 0x09, 0x00, 0x02,
            0x8D, 0x01, 0x00, 0x03,
            0x9A, 0x01, 0x00, 0xC1,
            0x9F, 0x03, 0x00, 0x02, 0x78, 0x56,
            0x00,
        ]);

        Assert.Collection(
            action.Code.Instructions,
            instruction => Assert.Equal(new Avm1FrameOperand(0x1234), instruction.Operand),
            instruction => Assert.Equal(new Avm1RegisterOperand(5), instruction.Operand),
            instruction => Assert.Equal(new Avm1StringIndexOperand(7), instruction.Operand),
            instruction => Assert.Equal(new Avm1WaitForFrameOperand(9, 2), instruction.Operand),
            instruction => Assert.Equal(new Avm1WaitForFrame2Operand(3), instruction.Operand),
            instruction => Assert.Equal(new Avm1FlagsOperand(0xC1), instruction.Operand),
            instruction => Assert.Equal(new Avm1GotoFrame2Operand(2, 0x5678), instruction.Operand),
            instruction => Assert.Null(instruction.Operand));
    }

    [Fact]
    public void RejectsTruncatedActionsAndBranchesIntoInstructionPayloads()
    {
        Assert.Throws<FormatReadException>(() => readSingleAction([0x81, 0x02, 0x00, 0xFF]));
        Assert.Throws<FormatReadException>(() => readSingleAction([0x99, 0x02, 0x00, 0xFC, 0xFF, 0x00]));
        Assert.Throws<FormatReadException>(() => readSingleAction([0x96, 0x01, 0x00, 0xFF, 0x00]));
        Assert.Throws<FormatReadException>(() => readSingleAction([0x96, 0x02, 0x00, 0x07, 0x01, 0x00]));
        Assert.Throws<FormatReadException>(() => readSingleAction([0x83, 0x03, 0x00, (byte)'x', 0x00, (byte)'y', 0x00]));
        Assert.Throws<FormatReadException>(() => readSingleAction([0x87, 0x02, 0x00, 0x01, 0x02, 0x00]));
        Assert.Throws<FormatReadException>(() => readSingleAction([0x9F, 0x02, 0x00, 0x01, 0x02, 0x00]));
    }

    [Fact]
    public void EnforcesActionInstructionAndLexicalNestingLimits()
    {
        var twoInstructions = createLmb(record(LmbTags.ActionPool, actionPool([0x06, 0x00])));
        var instructionException = Assert.Throws<FormatLimitException>(() =>
            LmbSemanticReader.Read(twoInstructions, new ParserLimits(maxActionInstructions: 1)));
        Assert.Equal(nameof(ParserLimits.MaxActionInstructions), instructionException.LimitName);

        var nestedWith = createLmb(record(LmbTags.ActionPool, actionPool([
            0x94, 0x02, 0x00, 0x07, 0x00,
            0x94, 0x02, 0x00, 0x02, 0x00, 0x00, 0x00,
        ])));
        var nestingException = Assert.Throws<FormatLimitException>(() =>
            LmbSemanticReader.Read(nestedWith, new ParserLimits(maxActionNesting: 1)));
        Assert.Equal(nameof(ParserLimits.MaxActionNesting), nestingException.LimitName);
    }

    [Fact]
    public void ReportsTypedActionStringReferencesOutsideTheMoviePool()
    {
        var file = createLmb(
            record(LmbTags.StringPool, stringPool("only")),
            record(LmbTags.ActionPool, actionPool([
                0x96, 0x03, 0x00, 0x00, 0x02, 0x00,
                0x8C, 0x02, 0x00, 0x03, 0x00,
                0x00,
            ])));

        var result = LmbSemanticReader.Read(file);

        Assert.Equal(
            ["LMB_AVM_STRING_INDEX_OUT_OF_RANGE", "LMB_AVM_STRING_INDEX_OUT_OF_RANGE"],
            result.Diagnostics.Select(diagnostic => diagnostic.Code));
        Assert.All(result.Diagnostics, diagnostic => Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity));
    }

    [Fact]
    public void ReportsInvalidSemanticReferencesWithoutDiscardingTypedCommands()
    {
        var geometry = new uint[18];
        geometry[16] = 4;
        var file = createLmb(
            record(LmbTags.StringPool, stringPool("name")),
            words(LmbTags.ColorTransformPool, 1, 0, 0),
            words(LmbTags.MatrixPool, 1, 0, 0, 0, 0, 0, 0),
            words(LmbTags.TranslationPool, 1, 0, 0),
            record(LmbTags.ActionPool, actionPool([0x00])),
            words(LmbTags.DefineShape, 42, 0, 0, 2),
            words(LmbTags.ShapeGeometry, geometry),
            words(LmbTags.DefineSprite, 7, 0, 0, 2, 1, 2, 0),
            words(LmbTags.ShowFrame, 4, 3),
            words(LmbTags.FrameLabel, 3, 2, 0),
            words(LmbTags.PlaceObject, 99, 0, 0, 3, 0x00010000, 0, 0x00020000, 0x80000004, 5, uint.MaxValue, 0, 0),
            words(LmbTags.DoAction, 2, 0),
            words(LmbTags.FrameKey, 3, 0),
            words(LmbTags.DefineSprite, 42, 0, 0, 0, 1, 0, 0));

        var result = LmbSemanticReader.Read(
            file,
            validationContext: new LmbSemanticValidationContext(textureCount: 1));
        var codes = result.Diagnostics.Select(diagnostic => diagnostic.Code).ToArray();

        Assert.Contains("LMB_GEOMETRY_COUNT_MISMATCH", codes);
        Assert.Contains("LMB_TEXTURE_INDEX_OUT_OF_RANGE", codes);
        Assert.Contains("LMB_DUPLICATE_CHARACTER_ID", codes);
        Assert.Contains("LMB_LABEL_COUNT_MISMATCH", codes);
        Assert.Contains("LMB_STRING_INDEX_OUT_OF_RANGE", codes);
        Assert.Contains("LMB_CHARACTER_ID_NOT_FOUND", codes);
        Assert.Equal(
            DiagnosticSeverity.Warning,
            result.Diagnostics.Single(diagnostic => diagnostic.Code == "LMB_CHARACTER_ID_NOT_FOUND").Severity);
        Assert.Contains("LMB_FRAME_OUT_OF_RANGE", codes);
        Assert.Contains("LMB_POSITION_INDEX_OUT_OF_RANGE", codes);
        Assert.Contains("LMB_COLOR_INDEX_OUT_OF_RANGE", codes);
        Assert.Contains("LMB_ACTION_INDEX_OUT_OF_RANGE", codes);
        Assert.All(
            result.Diagnostics.Where(diagnostic => diagnostic.Code.EndsWith("OUT_OF_RANGE", StringComparison.Ordinal)),
            diagnostic => Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity));
        Assert.Equal(5, result.Value.Sprites[0].Timeline.Length);
        Assert.Same(file.Records[8], result.Value.Sprites[0].Timeline[0].RawRecord);
    }

    [Fact]
    public void AcceptsPlacementSentinelsAndRetainsUnknownPositionKindsAsDiagnostics()
    {
        var file = createLmb(
            record(LmbTags.StringPool, stringPool("name")),
            words(LmbTags.DefineSprite, 1, 0, 0, 0, 1, 0, 0),
            words(LmbTags.ShowFrame, 0, 2),
            words(LmbTags.PlaceObject, uint.MaxValue, 0, 0, 0, 0x00020000, 0, 0, 0xFFFF0000, uint.MaxValue, uint.MaxValue, 0, 0),
            words(LmbTags.PlaceObject, 0, 0, 0, 0, 0x00020000, 0, 0, 0x12340000, uint.MaxValue, uint.MaxValue, 0, 0));

        var result = LmbSemanticReader.Read(file);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("LMB_UNKNOWN_POSITION_KIND", diagnostic.Code);
        Assert.Equal(EvidenceStatus.Unknown, diagnostic.Evidence);
        Assert.Equal(3, result.Value.Sprites[0].Timeline.Length);
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

    private static LmbAction readSingleAction(byte[] bytecode) =>
        Assert.Single(LmbSemanticReader.Read(createLmb(record(LmbTags.ActionPool, actionPool(bytecode)))).Value.Actions);

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
