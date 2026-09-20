namespace Waddamburo.Catalog;

public enum SongSourceKind
{
    Stock,
    Nijiiro,
    Tja,
    OsuLazer,
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
