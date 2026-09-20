using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Waddamburo.Formats.Diagnostics;
using Waddamburo.Formats.IO;

namespace Waddamburo.Formats.Lmb;

/// <summary>Builds immutable, evidence-labelled Lumen definitions without replacing the lossless record stream.</summary>
public static class LmbSemanticReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ParseResult<LmbMovieDefinition> Read(
        LmbFile file,
        ParserLimits? limits = null,
        LmbSemanticValidationContext? validationContext = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        var parserLimits = limits ?? ParserLimits.Default;
        var diagnostics = ImmutableArray.CreateBuilder<ParseDiagnostic>();
        var strings = ImmutableArray.CreateBuilder<LmbString>();
        var colors = ImmutableArray.CreateBuilder<LmbColorTransform>();
        var matrices = ImmutableArray.CreateBuilder<LmbMatrix>();
        var translations = ImmutableArray.CreateBuilder<LmbTranslation>();
        var bounds = ImmutableArray.CreateBuilder<LmbBounds>();
        var textureBounds = ImmutableArray.CreateBuilder<LmbTextureBounds>();
        var actions = ImmutableArray.CreateBuilder<LmbAction>();
        var shapes = new List<ShapeBuilder>();
        var sprites = new List<SpriteBuilder>();
        var uninterpreted = ImmutableArray.CreateBuilder<LmbRecord>();
        LmbMovieProperties? properties = null;
        ShapeBuilder? currentShape = null;
        SpriteBuilder? currentSprite = null;

        foreach (var record in file.Records)
        {
            if (record.Tag != LmbTags.ShapeGeometry)
                currentShape = null;

            switch (record.Tag)
            {
                case LmbTags.MovieProperties:
                    if (properties is not null)
                    {
                        diagnostics.Add(new ParseDiagnostic(
                            DiagnosticSeverity.Warning,
                            "LMB_DUPLICATE_PROPERTIES",
                            record.HeaderOffset,
                            record.Index,
                            EvidenceStatus.Candidate,
                            "Movie properties appear more than once; the last record is retained."));
                    }
                    properties = new LmbMovieProperties(readWords(record, 0), record);
                    break;
                case LmbTags.StringPool:
                    reportDuplicatePool(record, diagnostics);
                    readStrings(record, parserLimits, strings);
                    break;
                case LmbTags.ColorTransformPool:
                    reportDuplicatePool(record, diagnostics);
                    readColorTransforms(record, parserLimits, colors);
                    break;
                case LmbTags.MatrixPool:
                    reportDuplicatePool(record, diagnostics);
                    readMatrices(record, parserLimits, matrices);
                    break;
                case LmbTags.TranslationPool:
                    reportDuplicatePool(record, diagnostics);
                    readTranslations(record, parserLimits, translations);
                    break;
                case LmbTags.BoundsPool:
                    reportDuplicatePool(record, diagnostics);
                    readBounds(record, parserLimits, bounds);
                    break;
                case LmbTags.TextureBoundsPool:
                    reportDuplicatePool(record, diagnostics);
                    readTextureBounds(record, parserLimits, textureBounds);
                    break;
                case LmbTags.ActionPool:
                    reportDuplicatePool(record, diagnostics);
                    readActions(record, parserLimits, actions);
                    break;
                case LmbTags.DefineShape:
                    {
                        currentSprite = null;
                        var words = readWords(record, 4);
                        currentShape = new ShapeBuilder(
                            words[0],
                            words[3],
                            words[1..3],
                            record);
                        shapes.Add(currentShape);
                        break;
                    }
                case LmbTags.ShapeGeometry:
                    {
                        var words = readWords(record, 18);
                        if (currentShape is null)
                        {
                            reportOrphan(record, "LMB_ORPHAN_GEOMETRY", "Shape geometry does not follow a shape definition.", diagnostics);
                            uninterpreted.Add(record);
                            break;
                        }

                        var vertices = ImmutableArray.CreateBuilder<LmbVertex>(4);
                        for (var index = 0; index < 16; index += 4)
                        {
                            vertices.Add(new LmbVertex(
                                toSingle(words[index]),
                                toSingle(words[index + 1]),
                                toSingle(words[index + 2]),
                                toSingle(words[index + 3])));
                        }
                        currentShape.Geometry.Add(new LmbShapeGeometry(
                            vertices.MoveToImmutable(),
                            words[16],
                            words[17],
                            words[18..],
                            record));
                        break;
                    }
                case LmbTags.DefineSprite:
                    {
                        var words = readWords(record, 7);
                        currentSprite = new SpriteBuilder(
                            words[0],
                            words[3],
                            words[4],
                            words[5],
                            words[6],
                            words[1..3],
                            record);
                        sprites.Add(currentSprite);
                        break;
                    }
                case LmbTags.ShowFrame:
                    {
                        var words = readWords(record, 2);
                        addTimelineCommand(
                            currentSprite,
                            new LmbShowFrameCommand(words[0], words[1], words[2..], record),
                            record,
                            diagnostics,
                            uninterpreted);
                        break;
                    }
                case LmbTags.FrameKey:
                    {
                        var words = readWords(record, 2);
                        addTimelineCommand(
                            currentSprite,
                            new LmbFrameKeyCommand(words[0], words[1], words[2..], record),
                            record,
                            diagnostics,
                            uninterpreted);
                        break;
                    }
                case LmbTags.FrameLabel:
                    {
                        var words = readWords(record, 3);
                        addTimelineCommand(
                            currentSprite,
                            new LmbFrameLabelCommand(words[0], words[1], words[2], words[3..], record),
                            record,
                            diagnostics,
                            uninterpreted);
                        break;
                    }
                case LmbTags.PlaceObject:
                    {
                        var words = readWords(record, 12);
                        addTimelineCommand(
                            currentSprite,
                            new LmbPlaceObjectCommand(
                                words[0],
                                words[1],
                                words[3],
                                high(words[4]),
                                low(words[4]),
                                high(words[5]),
                                high(words[6]),
                                high(words[7]),
                                low(words[7]),
                                words[8],
                                words[9],
                                ImmutableArray.Create(words[2], words[10], words[11]).AddRange(words[12..]),
                                record),
                            record,
                            diagnostics,
                            uninterpreted);
                        break;
                    }
                case LmbTags.RemoveObject:
                    {
                        var words = readWords(record, 2);
                        addTimelineCommand(
                            currentSprite,
                            new LmbRemoveObjectCommand(words[0], words[1], words[2..], record),
                            record,
                            diagnostics,
                            uninterpreted);
                        break;
                    }
                case LmbTags.DoAction:
                    {
                        var words = readWords(record, 2);
                        addTimelineCommand(
                            currentSprite,
                            new LmbDoActionCommand(words[0], words[1], words[2..], record),
                            record,
                            diagnostics,
                            uninterpreted);
                        break;
                    }
                default:
                    uninterpreted.Add(record);
                    break;
            }
        }

        var definition = new LmbMovieDefinition(
            file,
            properties,
            strings.ToImmutable(),
            colors.ToImmutable(),
            matrices.ToImmutable(),
            translations.ToImmutable(),
            bounds.ToImmutable(),
            textureBounds.ToImmutable(),
            actions.ToImmutable(),
            shapes.Select(shape => shape.Build()).ToImmutableArray(),
            sprites.Select(sprite => sprite.Build()).ToImmutableArray(),
            uninterpreted.ToImmutable());
        LmbReferenceValidator.Validate(definition, validationContext, diagnostics);
        return new ParseResult<LmbMovieDefinition>(definition, diagnostics.ToImmutable());
    }

    private static void readStrings(
        LmbRecord record,
        ParserLimits limits,
        ImmutableArray<LmbString>.Builder destination)
    {
        var span = record.Payload.Span;
        var count = readCount(record, limits.MaxStringCount);
        var cursor = 4;
        long totalBytes = 0;
        for (var index = 0; index < count; index++)
        {
            requireBytes(record, cursor, 4, "string length");
            var byteLength = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(cursor, 4));
            cursor += 4;
            if (byteLength > limits.MaxStringBytes)
                throw new FormatLimitException(nameof(ParserLimits.MaxStringBytes), byteLength, limits.MaxStringBytes, record.PayloadOffset + cursor);
            totalBytes = checked(totalBytes + byteLength);
            if (totalBytes > limits.MaxAllocationBytes)
                throw new FormatLimitException(nameof(ParserLimits.MaxAllocationBytes), totalBytes, limits.MaxAllocationBytes, record.PayloadOffset + cursor);

            var storageLength = align4(checked((int)byteLength + 1));
            requireBytes(record, cursor, storageLength, "string data");
            if (span[cursor + (int)byteLength] != 0)
                throw new FormatReadException("LMB string is not NUL terminated", record.PayloadOffset + cursor + byteLength, 1, 1);
            string value;
            try
            {
                value = StrictUtf8.GetString(span.Slice(cursor, (int)byteLength));
            }
            catch (DecoderFallbackException exception)
            {
                throw new FormatReadException("LMB string is not valid UTF-8", record.PayloadOffset + cursor, byteLength, byteLength, exception);
            }
            destination.Add(new LmbString(destination.Count, value, record.PayloadOffset + cursor));
            cursor += storageLength;
        }
        requireExactEnd(record, cursor);
    }

    private static void readActions(
        LmbRecord record,
        ParserLimits limits,
        ImmutableArray<LmbAction>.Builder destination)
    {
        var span = record.Payload.Span;
        var count = readCount(record, limits.MaxRecordCount);
        var cursor = 4;
        long totalBytes = 0;
        for (var index = 0; index < count; index++)
        {
            requireBytes(record, cursor, 4, "action length");
            var byteLength = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(cursor, 4));
            cursor += 4;
            if (byteLength > limits.MaxActionBytes)
                throw new FormatLimitException(nameof(ParserLimits.MaxActionBytes), byteLength, limits.MaxActionBytes, record.PayloadOffset + cursor);
            totalBytes = checked(totalBytes + byteLength);
            if (totalBytes > limits.MaxAllocationBytes)
                throw new FormatLimitException(nameof(ParserLimits.MaxAllocationBytes), totalBytes, limits.MaxAllocationBytes, record.PayloadOffset + cursor);

            var storageLength = align4(checked((int)byteLength));
            requireBytes(record, cursor, storageLength, "action bytecode");
            var bytecode = ImmutableArray.CreateRange(span.Slice(cursor, (int)byteLength).ToArray());
            var actionOffset = record.PayloadOffset + cursor;
            destination.Add(new LmbAction(
                destination.Count,
                bytecode,
                actionOffset)
            {
                Code = Avm1BytecodeReader.Read(bytecode, actionOffset, limits),
            });
            cursor += storageLength;
        }
        requireExactEnd(record, cursor);
    }

    private static void readColorTransforms(
        LmbRecord record,
        ParserLimits limits,
        ImmutableArray<LmbColorTransform>.Builder destination)
    {
        var words = readCountedWords(record, 2, limits);
        for (var index = 0; index < words.Length; index += 2)
        {
            destination.Add(new LmbColorTransform(
                unchecked((short)high(words[index])),
                unchecked((short)low(words[index])),
                unchecked((short)high(words[index + 1])),
                unchecked((short)low(words[index + 1]))));
        }
    }

    private static void readMatrices(LmbRecord record, ParserLimits limits, ImmutableArray<LmbMatrix>.Builder destination)
    {
        var words = readCountedWords(record, 6, limits);
        for (var index = 0; index < words.Length; index += 6)
        {
            destination.Add(new LmbMatrix(
                toSingle(words[index]),
                toSingle(words[index + 1]),
                toSingle(words[index + 2]),
                toSingle(words[index + 3]),
                toSingle(words[index + 4]),
                toSingle(words[index + 5])));
        }
    }

    private static void readTranslations(LmbRecord record, ParserLimits limits, ImmutableArray<LmbTranslation>.Builder destination)
    {
        var words = readCountedWords(record, 2, limits);
        for (var index = 0; index < words.Length; index += 2)
            destination.Add(new LmbTranslation(toSingle(words[index]), toSingle(words[index + 1])));
    }

    private static void readBounds(LmbRecord record, ParserLimits limits, ImmutableArray<LmbBounds>.Builder destination)
    {
        var words = readCountedWords(record, 4, limits);
        for (var index = 0; index < words.Length; index += 4)
            destination.Add(new LmbBounds(toSingle(words[index]), toSingle(words[index + 1]), toSingle(words[index + 2]), toSingle(words[index + 3])));
    }

    private static void readTextureBounds(LmbRecord record, ParserLimits limits, ImmutableArray<LmbTextureBounds>.Builder destination)
    {
        var words = readCountedWords(record, 4, limits);
        for (var index = 0; index < words.Length; index += 4)
            destination.Add(new LmbTextureBounds(toSingle(words[index]), toSingle(words[index + 1]), toSingle(words[index + 2]), toSingle(words[index + 3])));
    }

    private static ImmutableArray<uint> readCountedWords(LmbRecord record, int stride, ParserLimits limits)
    {
        var count = readCount(record, limits.MaxRecordCount);
        var requiredWords = checked(1L + ((long)count * stride));
        if (requiredWords > limits.MaxAllocationBytes / sizeof(uint))
            throw new FormatLimitException(nameof(ParserLimits.MaxAllocationBytes), requiredWords * sizeof(uint), limits.MaxAllocationBytes, record.PayloadOffset);
        if (record.WordCount != requiredWords)
            throw new FormatReadException("LMB pool size does not match its count", record.PayloadOffset, requiredWords * 4, record.PayloadLength);
        return readWords(record, 1)[1..];
    }

    private static int readCount(LmbRecord record, int maximum)
    {
        requireBytes(record, 0, 4, "pool count");
        var count = BinaryPrimitives.ReadUInt32BigEndian(record.Payload.Span[..4]);
        if (count > maximum)
            throw new FormatLimitException("LmbPoolCount", count, maximum, record.PayloadOffset);
        return checked((int)count);
    }

    private static ImmutableArray<uint> readWords(LmbRecord record, int minimumCount)
    {
        if (record.WordCount < minimumCount)
            throw new FormatReadException("LMB semantic record is too short", record.PayloadOffset, minimumCount * 4L, record.PayloadLength);
        var words = ImmutableArray.CreateBuilder<uint>(checked((int)record.WordCount));
        for (var offset = 0; offset < record.Payload.Length; offset += 4)
            words.Add(BinaryPrimitives.ReadUInt32BigEndian(record.Payload.Span.Slice(offset, 4)));
        return words.MoveToImmutable();
    }

    private static void addTimelineCommand(
        SpriteBuilder? sprite,
        LmbTimelineCommand command,
        LmbRecord record,
        ImmutableArray<ParseDiagnostic>.Builder diagnostics,
        ImmutableArray<LmbRecord>.Builder uninterpreted)
    {
        if (sprite is not null)
        {
            sprite.Timeline.Add(command);
            return;
        }
        reportOrphan(record, "LMB_ORPHAN_TIMELINE", "Timeline command appears outside a sprite definition.", diagnostics);
        uninterpreted.Add(record);
    }

    private static void reportDuplicatePool(LmbRecord record, ImmutableArray<ParseDiagnostic>.Builder diagnostics)
    {
        if (record.TagOccurrenceIndex == 0)
            return;
        diagnostics.Add(new ParseDiagnostic(
            DiagnosticSeverity.Warning,
            "LMB_DUPLICATE_POOL",
            record.HeaderOffset,
            record.Index,
            EvidenceStatus.Confirmed,
            $"Pool tag 0x{record.Tag:X8} occurs more than once; entries are retained in record order."));
    }

    private static void reportOrphan(
        LmbRecord record,
        string code,
        string message,
        ImmutableArray<ParseDiagnostic>.Builder diagnostics) =>
        diagnostics.Add(new ParseDiagnostic(
            DiagnosticSeverity.Warning,
            code,
            record.HeaderOffset,
            record.Index,
            EvidenceStatus.Candidate,
            message));

    private static void requireBytes(LmbRecord record, int offset, int length, string label)
    {
        if (offset < 0 || length < 0 || (long)offset + length > record.Payload.Length)
        {
            var available = offset >= 0 && offset <= record.Payload.Length ? record.Payload.Length - offset : 0;
            throw new FormatReadException($"LMB {label} is truncated", record.PayloadOffset + Math.Max(0, offset), length, available);
        }
    }

    private static void requireExactEnd(LmbRecord record, int cursor)
    {
        if (cursor != record.Payload.Length)
            throw new FormatReadException("LMB pool has trailing or missing data", record.PayloadOffset + cursor, 0, record.Payload.Length - cursor);
    }

    private static int align4(int value) => checked((value + 3) & ~3);

    private static float toSingle(uint value) => BitConverter.UInt32BitsToSingle(value);

    private static ushort high(uint value) => checked((ushort)(value >> 16));

    private static ushort low(uint value) => checked((ushort)(value & ushort.MaxValue));

    private sealed class ShapeBuilder(
        uint characterId,
        uint declaredGeometryCount,
        ImmutableArray<uint> uninterpretedHeaderWords,
        LmbRecord rawRecord)
    {
        public List<LmbShapeGeometry> Geometry { get; } = [];

        public LmbShapeDefinition Build() => new(
            characterId,
            declaredGeometryCount,
            uninterpretedHeaderWords,
            Geometry.ToImmutableArray(),
            rawRecord);
    }

    private sealed class SpriteBuilder(
        uint characterId,
        uint declaredLabelCount,
        uint declaredFrameCount,
        uint repeatedLabelCount,
        uint flags,
        ImmutableArray<uint> uninterpretedHeaderWords,
        LmbRecord rawRecord)
    {
        public List<LmbTimelineCommand> Timeline { get; } = [];

        public LmbSpriteDefinition Build() => new(
            characterId,
            declaredLabelCount,
            declaredFrameCount,
            repeatedLabelCount,
            flags,
            uninterpretedHeaderWords,
            Timeline.ToImmutableArray(),
            rawRecord);
    }
}
