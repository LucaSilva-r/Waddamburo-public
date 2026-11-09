namespace Waddamburo.Catalog;

public enum SongSourceKind
{
    Stock,
    Nijiiro,
    Tja,
    OsuLazer,
    OsuStable,
}

public static class SongSourceKinds
{
    /// <summary>An osu!taiko chart, from lazer or stable (the same chart reader and rules).</summary>
    public static bool IsOsu(this SongSourceKind source) => source is SongSourceKind.OsuLazer or SongSourceKind.OsuStable;

    /// <summary>
    /// The source the score server knows: osu!stable's charts are osu! charts like lazer's (same id, same rules), so
    /// uploads name them OsuLazer and its rescoring and leaderboards treat both alike.
    /// </summary>
    public static SongSourceKind ServerSource(this SongSourceKind source) =>
        source == SongSourceKind.OsuStable ? SongSourceKind.OsuLazer : source;
}

public sealed record CatalogProviderId
{
    public CatalogProviderId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record SongKey
{
    public SongKey(SongSourceKind source, string stableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        Source = source;
        StableId = stableId;
    }

    public SongSourceKind Source { get; }

    public string StableId { get; }

    public override string ToString() => $"{Source}:{StableId}";
}

public sealed record CategoryKey
{
    public CategoryKey(SongSourceKind source, string stableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        Source = source;
        StableId = stableId;
    }

    public SongSourceKind Source { get; }

    public string StableId { get; }

    public override string ToString() => $"{Source}:{StableId}";
}

public sealed record ChartKey
{
    public ChartKey(SongKey song, string stableId)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        Song = song;
        StableId = stableId;
    }

    public SongKey Song { get; }

    public string StableId { get; }

    public override string ToString() => $"{Song}/{StableId}";
}

/// <summary>An opaque provider-owned reference resolved only by that provider.</summary>
public sealed record CatalogAssetKey
{
    public CatalogAssetKey(CatalogProviderId provider, string stableId)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        Provider = provider;
        StableId = stableId;
    }

    public CatalogProviderId Provider { get; }

    public string StableId { get; }
}
