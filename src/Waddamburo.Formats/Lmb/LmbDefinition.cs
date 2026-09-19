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
    public const uint DefineSprite = 0x0027;
    public const uint FrameLabel = 0x002B;
    public const uint StringPool = 0xF001;
    public const uint ColorTransformPool = 0xF002;
    public const uint MatrixPool = 0xF003;
    public const uint BoundsPool = 0xF004;
    public const uint ActionPool = 0xF005;
    public const uint TextureBoundsPool = 0xF007;
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
}

public sealed record LmbColorTransform(short Red, short Green, short Blue, short Alpha)
{
    public EvidenceStatus Evidence { get; init; } = EvidenceStatus.Candidate;
}

public sealed record LmbMatrix(float M11, float M12, float M21, float M22, float X, float Y)
{
    public EvidenceStatus Evidence { get; init; } = EvidenceStatus.Candidate;
}

public sealed record LmbTranslation(float X, float Y)
{
    public EvidenceStatus Evidence { get; init; } = EvidenceStatus.Candidate;
}

public sealed record LmbBounds(float Left, float Top, float Right, float Bottom)
{
    public EvidenceStatus Evidence { get; init; } = EvidenceStatus.Candidate;
}

public sealed record LmbTextureBounds(float Left, float Top, float Right, float Bottom)
{
    public EvidenceStatus Evidence { get; init; } = EvidenceStatus.CorpusValidatedInference;
}

public sealed record LmbVertex(float X, float Y, float U, float V);

public sealed record LmbShapeGeometry(
    ImmutableArray<LmbVertex> Vertices,
    uint TextureIndex,
    uint Flags,
    ImmutableArray<uint> UninterpretedWords,
    LmbRecord RawRecord)
{
    public EvidenceStatus Evidence { get; init; } = EvidenceStatus.Candidate;
}

public sealed record LmbShapeDefinition(
    uint CharacterId,
    uint DeclaredGeometryCount,
    ImmutableArray<uint> UninterpretedHeaderWords,
    ImmutableArray<LmbShapeGeometry> Geometry,
    LmbRecord RawRecord)
{
    public EvidenceStatus HeaderEvidence { get; init; } = EvidenceStatus.Candidate;
}

public abstract record LmbTimelineCommand(LmbRecord RawRecord, EvidenceStatus Evidence);

public sealed record LmbShowFrameCommand(
    uint Frame,
    uint DeclaredCommandCount,
    ImmutableArray<uint> UninterpretedWords,
    LmbRecord Record)
    : LmbTimelineCommand(Record, EvidenceStatus.Candidate);

public sealed record LmbFrameKeyCommand(
    uint Frame,
    uint EntryCount,
    ImmutableArray<uint> UninterpretedWords,
    LmbRecord Record)
    : LmbTimelineCommand(Record, EvidenceStatus.Candidate);

public sealed record LmbFrameLabelCommand(
    uint StringIndex,
    uint Frame,
    uint Unknown,
    ImmutableArray<uint> UninterpretedWords,
    LmbRecord Record)
    : LmbTimelineCommand(Record, EvidenceStatus.Candidate);

public sealed record LmbPlaceObjectCommand(
    uint CharacterId,
    uint PlacementId,
    uint NameStringIndex,
    ushort Mode,
    ushort BlendMode,
    ushort Depth,
    ushort FirstFrame,
    ushort PositionKind,
    ushort PositionIndex,
    uint ColorMultiplyIndex,
    uint ColorAddIndex,
    ImmutableArray<uint> UninterpretedWords,
    LmbRecord Record)
    : LmbTimelineCommand(Record, EvidenceStatus.CorpusValidatedInference);

public sealed record LmbRemoveObjectCommand(
    uint CharacterId,
    uint PackedDepth,
    ImmutableArray<uint> UninterpretedWords,
    LmbRecord Record)
    : LmbTimelineCommand(Record, EvidenceStatus.Candidate)
{
    public uint CandidateDepth => (PackedDepth >> 16) + 1;
}

public sealed record LmbDoActionCommand(
    uint ActionIndex,
    uint Unknown,
    ImmutableArray<uint> UninterpretedWords,
    LmbRecord Record)
    : LmbTimelineCommand(Record, EvidenceStatus.Candidate);

public sealed record LmbSpriteDefinition(
    uint CharacterId,
    uint DeclaredLabelCount,
    uint DeclaredFrameCount,
    uint RepeatedLabelCount,
    uint Flags,
    ImmutableArray<uint> UninterpretedHeaderWords,
    ImmutableArray<LmbTimelineCommand> Timeline,
    LmbRecord RawRecord)
{
    public EvidenceStatus HeaderEvidence { get; init; } = EvidenceStatus.Candidate;
}

public sealed record LmbMovieDefinition(
    LmbFile RawFile,
    ImmutableArray<LmbString> Strings,
    ImmutableArray<LmbColorTransform> ColorTransforms,
    ImmutableArray<LmbMatrix> Matrices,
    ImmutableArray<LmbTranslation> Translations,
    ImmutableArray<LmbBounds> Bounds,
    ImmutableArray<LmbTextureBounds> TextureBounds,
    ImmutableArray<LmbAction> Actions,
    ImmutableArray<LmbShapeDefinition> Shapes,
    ImmutableArray<LmbSpriteDefinition> Sprites,
    ImmutableArray<LmbRecord> UninterpretedRecords);

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
