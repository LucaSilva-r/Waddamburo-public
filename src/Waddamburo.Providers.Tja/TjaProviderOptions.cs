namespace Waddamburo.Providers.Tja;

public sealed record TjaProviderOptions
{
    public const int DefaultMaximumFileCount = 100_000;
    public const long DefaultMaximumChartBytes = 16 * 1024 * 1024;

    public int MaximumFileCount { get; init; } = DefaultMaximumFileCount;

    public long MaximumChartBytes { get; init; } = DefaultMaximumChartBytes;

    internal void Validate()
    {
        if (MaximumFileCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumFileCount));
        if (MaximumChartBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumChartBytes));
    }
}
