namespace Waddamburo.Formats;

/// <summary>Immutable resource ceilings applied before parser allocation or iteration.</summary>
public sealed record ParserLimits
{
    public static ParserLimits Default { get; } = new();

    public ParserLimits(
        long maxFileBytes = 2L * 1024 * 1024 * 1024,
        int maxAllocationBytes = 256 * 1024 * 1024,
        int maxRecordCount = 1_000_000,
        int maxStringBytes = 1024 * 1024,
        int maxStringCount = 250_000,
        int maxTextureDimension = 16_384,
        long maxTextureBytes = 512L * 1024 * 1024,
        int maxActionBytes = 16 * 1024 * 1024,
        int maxVertices = 10_000_000,
        int maxIndices = 30_000_000,
        int maxBones = 4_096,
        int maxFrames = 1_000_000,
        int maxMeasures = 1_000_000,
        int maxNotes = 10_000_000,
        int maxActionInstructions = 1_000_000,
        int maxActionNesting = 256)
    {
        MaxFileBytes = requirePositive(maxFileBytes, nameof(maxFileBytes));
        MaxAllocationBytes = requirePositive(maxAllocationBytes, nameof(maxAllocationBytes));
        MaxRecordCount = requirePositive(maxRecordCount, nameof(maxRecordCount));
        MaxStringBytes = requirePositive(maxStringBytes, nameof(maxStringBytes));
        MaxStringCount = requirePositive(maxStringCount, nameof(maxStringCount));
        MaxTextureDimension = requirePositive(maxTextureDimension, nameof(maxTextureDimension));
        MaxTextureBytes = requirePositive(maxTextureBytes, nameof(maxTextureBytes));
        MaxActionBytes = requirePositive(maxActionBytes, nameof(maxActionBytes));
        MaxVertices = requirePositive(maxVertices, nameof(maxVertices));
        MaxIndices = requirePositive(maxIndices, nameof(maxIndices));
        MaxBones = requirePositive(maxBones, nameof(maxBones));
        MaxFrames = requirePositive(maxFrames, nameof(maxFrames));
        MaxMeasures = requirePositive(maxMeasures, nameof(maxMeasures));
        MaxNotes = requirePositive(maxNotes, nameof(maxNotes));
        MaxActionInstructions = requirePositive(maxActionInstructions, nameof(maxActionInstructions));
        MaxActionNesting = requirePositive(maxActionNesting, nameof(maxActionNesting));
    }

    public long MaxFileBytes { get; }

    public int MaxAllocationBytes { get; }

    public int MaxRecordCount { get; }

    public int MaxStringBytes { get; }

    public int MaxStringCount { get; }

    public int MaxTextureDimension { get; }

    public long MaxTextureBytes { get; }

    public int MaxActionBytes { get; }

    public int MaxVertices { get; }

    public int MaxIndices { get; }

    public int MaxBones { get; }

    public int MaxFrames { get; }

    public int MaxMeasures { get; }

    public int MaxNotes { get; }

    public int MaxActionInstructions { get; }

    public int MaxActionNesting { get; }

    private static int requirePositive(int value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, parameterName);
        return value;
    }

    private static long requirePositive(long value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, parameterName);
        return value;
    }
}
