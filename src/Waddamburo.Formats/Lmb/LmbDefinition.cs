using System.Collections.Immutable;
using Waddamburo.Formats.Diagnostics;

namespace Waddamburo.Formats.Lmb;

public static class LmbTags
{
    public const uint ShowFrame = 0x0001;
    public const uint PlaceObject = 0x0004;
    public const uint RemoveObject = 0x0005;
    public const uint DoAction = 0x000C;
    public const uint DefineShape = 0xF022;
    public const uint ShapeGeometry = 0xF023;
    public const uint DefineText = 0x0025;
    public const uint DefineSprite = 0x0027;
    public const uint FrameLabel = 0x002B;
    public const uint StringPool = 0xF001;
    public const uint ColorTransformPool = 0xF002;
    public const uint MatrixPool = 0xF003;
    public const uint BoundsPool = 0xF004;
    public const uint ActionPool = 0xF005;
    public const uint TextureBoundsPool = 0xF007;
    public const uint MovieProperties = 0xF00C;
    public const uint TranslationPool = 0xF103;
    public const uint FrameKey = 0xF105;
}

public sealed record LmbString(int Index, string Value, long Offset)
{
    public EvidenceStatus Evidence { get; init; } = EvidenceStatus.Confirmed;
}

public sealed record LmbAction(int Index, ImmutableArray<byte> Bytecode, long Offset)
{
    public EvidenceStatus Evidence { get; init; } = EvidenceStatus.Confirmed;

    public required Avm1CodeBlock Code { get; init; }

    /// <summary>This action with other bytecode (a patch), decoded again.</summary>
    public LmbAction WithBytecode(ImmutableArray<byte> bytecode, ParserLimits? limits = null) =>
        this with { Bytecode = bytecode, Code = Avm1BytecodeReader.Read(bytecode, Offset, limits ?? ParserLimits.Default) };
}

public readonly record struct LmbColorTransform(short Red, short Green, short Blue, short Alpha)
{
    public EvidenceStatus Evidence { get; init; } = EvidenceStatus.Candidate;
}

public readonly record struct LmbMatrix(float M11, float M12, float M21, float M22, float X, float Y)
{
    public EvidenceStatus Evidence { get; init; } = EvidenceStatus.Candidate;
}

public readonly record struct LmbTranslation(float X, float Y)
{
    public EvidenceStatus Evidence { get; init; } = EvidenceStatus.Candidate;
}

public readonly record struct LmbBounds(float Left, float Top, float Right, float Bottom)
{
    public EvidenceStatus Evidence { get; init; } = EvidenceStatus.Candidate;
}

public readonly record struct LmbTextureBounds(float Left, float Top, float Right, float Bottom)
{
    public EvidenceStatus Evidence { get; init; } = EvidenceStatus.CorpusValidatedInference;
}

public sealed record LmbMovieProperties(ImmutableArray<uint> Words, LmbRecordInfo RawRecord)
{
    public EvidenceStatus Evidence { get; init; } = EvidenceStatus.Candidate;

    public uint? RootCharacterId => Words.Length > 3 ? Words[3] : null;

    public float? FrameRate => Words.Length > 7 ? BitConverter.UInt32BitsToSingle(Words[7]) : null;
}

public readonly record struct LmbVertex(float X, float Y, float U, float V);

public sealed record LmbShapeGeometry(
    ImmutableArray<LmbVertex> Vertices,
    uint TextureIndex,
    uint Flags,
    LmbRecordInfo RawRecord)
{
    public EvidenceStatus Evidence { get; init; } = EvidenceStatus.Candidate;
}

public sealed record LmbShapeDefinition(
    uint CharacterId,
    uint DeclaredGeometryCount,
    ImmutableArray<uint> UninterpretedHeaderWords,
    ImmutableArray<LmbShapeGeometry> Geometry,
    LmbRecordInfo RawRecord)
{
    public EvidenceStatus HeaderEvidence { get; init; } = EvidenceStatus.Candidate;
}

public enum LmbCommandKind : byte { ShowFrame, FrameKey, FrameLabel, PlaceObject, RemoveObject, DoAction }

/// <summary>
/// One timeline command, by <see cref="Kind"/>; only that kind's fields are set. A value in its sprite's timeline array
/// (kept movies held ~260k command objects, mostly placements and frame ends).
/// </summary>
public readonly record struct LmbTimelineCommand(LmbCommandKind Kind, LmbRecordInfo RawRecord)
{
    public LmbRecordInfo Record => RawRecord;

    public EvidenceStatus Evidence => Kind is LmbCommandKind.PlaceObject or LmbCommandKind.RemoveObject
        ? EvidenceStatus.CorpusValidatedInference : EvidenceStatus.Candidate;

    /// <summary>ShowFrame, FrameKey, FrameLabel: the frame.</summary>
    public uint Frame { get; init; }

    /// <summary>ShowFrame: commands declared for the frame; FrameKey: its entries.</summary>
    public uint DeclaredCommandCount { get; init; }

    public uint EntryCount => DeclaredCommandCount;

    /// <summary>FrameLabel: the label's string.</summary>
    public uint StringIndex { get; init; }

    /// <summary>FrameLabel, DoAction: an unknown word.</summary>
    public uint Unknown { get; init; }

    /// <summary>PlaceObject, RemoveObject.</summary>
    public uint CharacterId { get; init; }

    public uint PlacementId { get; init; }
    public uint NameStringIndex { get; init; }
    public ushort Mode { get; init; }
    public ushort BlendMode { get; init; }

    /// <summary>PlaceObject: the depth; RemoveObject: the high half of <see cref="PackedDepth"/>.</summary>
    public ushort Depth { get; init; }

    public ushort FirstFrame { get; init; }
    public ushort PositionKind { get; init; }
    public ushort PositionIndex { get; init; }
    public uint ColorMultiplyIndex { get; init; }
    public uint ColorAddIndex { get; init; }

    /// <summary>Inclusive last sibling depth clipped by this placement; zero means no mask.</summary>
    public ushort ClipDepth { get; init; }

    /// <summary>RemoveObject.</summary>
    public uint PackedDepth { get; init; }

    /// <summary>DoAction.</summary>
    public uint ActionIndex { get; init; }

    public static LmbTimelineCommand ShowFrame(uint frame, uint declaredCommandCount, LmbRecordInfo record) =>
        new(LmbCommandKind.ShowFrame, record) { Frame = frame, DeclaredCommandCount = declaredCommandCount };

    public static LmbTimelineCommand FrameKey(uint frame, uint entryCount, LmbRecordInfo record) =>
        new(LmbCommandKind.FrameKey, record) { Frame = frame, DeclaredCommandCount = entryCount };

    public static LmbTimelineCommand FrameLabel(uint stringIndex, uint frame, uint unknown, LmbRecordInfo record) =>
        new(LmbCommandKind.FrameLabel, record) { StringIndex = stringIndex, Frame = frame, Unknown = unknown };

    public static LmbTimelineCommand PlaceObject(uint characterId, uint placementId, uint nameStringIndex, ushort mode,
        ushort blendMode, ushort depth, ushort firstFrame, ushort positionKind, ushort positionIndex, uint colorMultiplyIndex,
        uint colorAddIndex, LmbRecordInfo record) =>
        new(LmbCommandKind.PlaceObject, record)
        {
            CharacterId = characterId, PlacementId = placementId, NameStringIndex = nameStringIndex, Mode = mode,
            BlendMode = blendMode, Depth = depth, FirstFrame = firstFrame, PositionKind = positionKind,
            PositionIndex = positionIndex, ColorMultiplyIndex = colorMultiplyIndex, ColorAddIndex = colorAddIndex,
        };

    public static LmbTimelineCommand RemoveObject(uint characterId, uint packedDepth, LmbRecordInfo record) =>
        new(LmbCommandKind.RemoveObject, record) { CharacterId = characterId, PackedDepth = packedDepth, Depth = (ushort)(packedDepth >> 16) };

    public static LmbTimelineCommand DoAction(uint actionIndex, uint unknown, LmbRecordInfo record) =>
        new(LmbCommandKind.DoAction, record) { ActionIndex = actionIndex, Unknown = unknown };
}

public static class LmbTimelineCommands
{
    /// <summary>The commands of one kind (what <c>OfType</c> did when each kind was a class).</summary>
    public static IEnumerable<LmbTimelineCommand> OfKind(this IEnumerable<LmbTimelineCommand> commands, LmbCommandKind kind) =>
        commands.Where(command => command.Kind == kind);

    /// <summary>The first command of a kind, or null (not a default command: a struct's FirstOrDefault always "finds" one).</summary>
    public static LmbTimelineCommand? FirstOfKind(this IEnumerable<LmbTimelineCommand> commands, LmbCommandKind kind)
    {
        foreach (var command in commands)
            if (command.Kind == kind)
                return command;
        return null;
    }
}

public sealed record LmbSpriteDefinition(
    uint CharacterId,
    uint DeclaredLabelCount,
    uint DeclaredFrameCount,
    uint RepeatedLabelCount,
    uint Flags,
    ImmutableArray<uint> UninterpretedHeaderWords,
    ImmutableArray<LmbTimelineCommand> Timeline,
    LmbRecordInfo RawRecord)
{
    public EvidenceStatus HeaderEvidence { get; init; } = EvidenceStatus.Candidate;

    /// <summary>
    /// Candidate linkage/export name index stored in DefineSprite header word 2.
    /// Zero denotes no exported name in the observed corpus.
    /// </summary>
    public uint? ExportStringIndex =>
        UninterpretedHeaderWords.Length >= 2 && UninterpretedHeaderWords[1] != 0
            ? UninterpretedHeaderWords[1]
            : null;
}

public sealed record LmbMovieDefinition(
    /// <summary>The records it was read from (null once dropped: a played movie does not need them).</summary>
    LmbFile? RawFile,
    LmbMovieProperties? Properties,
    ImmutableArray<LmbString> Strings,
    ImmutableArray<LmbColorTransform> ColorTransforms,
    ImmutableArray<LmbMatrix> Matrices,
    ImmutableArray<LmbTranslation> Translations,
    ImmutableArray<LmbBounds> Bounds,
    ImmutableArray<LmbTextureBounds> TextureBounds,
    ImmutableArray<LmbAction> Actions,
    ImmutableArray<LmbShapeDefinition> Shapes,
    ImmutableArray<LmbSpriteDefinition> Sprites,
    ImmutableArray<LmbRecord> UninterpretedRecords)
{
    /// <summary>Dynamic text fields (tag 0x0025), which scripts fill through <c>text</c>.</summary>
    public ImmutableArray<LmbTextDefinition> Texts { get; init; } = [];
}

/// <summary>
/// A dynamic text field (tag 0x0025, 16 words; song_select's ranking names). Word 0 is the character
/// id, word 2 the string index of its initial HTML (alignment, font face and size, colour), word 5 the
/// bounds pool index of its box; word 11 is the font size as a float. The other words are unknown.
/// </summary>
public sealed record LmbTextDefinition(uint CharacterId, uint HtmlStringIndex, uint BoundsIndex, float FontSize,
    ImmutableArray<uint> Words, LmbRecordInfo RawRecord)
{
    public EvidenceStatus Evidence { get; init; } = EvidenceStatus.Candidate;
}

public sealed record LmbSemanticValidationContext
{
    public LmbSemanticValidationContext(int? textureCount = null)
    {
        if (textureCount < 0)
            throw new ArgumentOutOfRangeException(nameof(textureCount));
        TextureCount = textureCount;
    }

    public int? TextureCount { get; }
}
