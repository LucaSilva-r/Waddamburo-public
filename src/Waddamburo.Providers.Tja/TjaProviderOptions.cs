namespace Waddamburo.Providers.Tja;

public sealed record TjaProviderOptions
{
    public const int DefaultMaximumFileCount = 100_000;
    public const long DefaultMaximumChartBytes = 16 * 1024 * 1024;
    public const int DefaultMaximumMeasures = 100_000;
    public const int DefaultMaximumNotes = 1_000_000;

    public int MaximumFileCount { get; init; } = DefaultMaximumFileCount;

    public long MaximumChartBytes { get; init; } = DefaultMaximumChartBytes;

    public int MaximumMeasures { get; init; } = DefaultMaximumMeasures;

    public int MaximumNotes { get; init; } = DefaultMaximumNotes;

    internal void Validate()
    {
        if (MaximumFileCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumFileCount));
        if (MaximumChartBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumChartBytes));
        if (MaximumMeasures <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumMeasures));
        if (MaximumNotes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumNotes));
    }
}
