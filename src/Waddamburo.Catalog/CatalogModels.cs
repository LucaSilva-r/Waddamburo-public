using System.Collections.Immutable;

namespace Waddamburo.Catalog;

public enum TaikoCourse
{
    Easy,
    Normal,
    Hard,
    Oni,
    Ura,
}

public sealed record SongTitle
{
    public SongTitle(string primary, string? japanese = null, string? english = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primary);
        Primary = primary;
        Japanese = normalize(japanese);
        English = normalize(english);
    }

    public string Primary { get; }

    public string? Japanese { get; }

    public string? English { get; }

    private static string? normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

public sealed record SongChartDescriptor
{
    public SongChartDescriptor(
        ChartKey key,
        string difficultyName,
        CatalogAssetKey chartAsset,
        TaikoCourse? course = null,
        int? level = null,
        IEnumerable<CatalogAssetKey>? duetChartAssets = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(difficultyName);
        ArgumentNullException.ThrowIfNull(chartAsset);
        if (level < 0)
            throw new ArgumentOutOfRangeException(nameof(level));
        Key = key;
        DifficultyName = difficultyName;
        ChartAsset = chartAsset;
        Course = course;
        Level = level;
        DuetChartAssets = duetChartAssets?.ToImmutableArray() ?? [];
        if (DuetChartAssets.Length is not (0 or 2) || DuetChartAssets.Any(static asset => asset is null))
            throw new ArgumentException("Duet charts come as one per player, left then right.", nameof(duetChartAssets));
    }

    public ChartKey Key { get; }

    public string DifficultyName { get; }

    public CatalogAssetKey ChartAsset { get; }

    public TaikoCourse? Course { get; }

    public int? Level { get; }

    /// <summary>
    /// The course's two-player charts (left drum, right drum), empty when the course has none. The
    /// game's own songs author them apart from the solo chart (different parts, hand notes).
    /// </summary>
    public ImmutableArray<CatalogAssetKey> DuetChartAssets { get; }
}

public sealed record SongDescriptor
{
    public SongDescriptor(
        SongKey key,
        SongTitle title,
        string? artist,
        IEnumerable<SongChartDescriptor> charts,
        CatalogAssetKey? audioAsset = null,
        TimeSpan? previewStart = null,
        string? subtitle = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(charts);
        var materialized = charts.ToImmutableArray();
        if (materialized.IsEmpty)
            throw new ArgumentException("A song must expose at least one chart.", nameof(charts));
        if (materialized.Any(chart => chart is null || chart.Key.Song != key))
            throw new ArgumentException("Every chart key must belong to its song.", nameof(charts));
        if (materialized.Select(static chart => chart.Key).Distinct().Count() != materialized.Length)
            throw new ArgumentException("Chart keys must be unique within a song.", nameof(charts));
        if (previewStart < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(previewStart));

        Key = key;
        Title = title;
        Artist = string.IsNullOrWhiteSpace(artist) ? null : artist;
        Charts = materialized;
        AudioAsset = audioAsset;
        PreviewStart = previewStart;
        Subtitle = string.IsNullOrWhiteSpace(subtitle) ? null : subtitle;
    }

    public SongKey Key { get; }

    public SongTitle Title { get; }

    public string? Artist { get; }

    public ImmutableArray<SongChartDescriptor> Charts { get; }

    public CatalogAssetKey? AudioAsset { get; }

    public TimeSpan? PreviewStart { get; }

    public string? Subtitle { get; }
}

public sealed record SongCategoryDescriptor
{
    public SongCategoryDescriptor(
        CategoryKey key,
        string name,
        int sortOrder,
        IEnumerable<SongKey> songs)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(songs);
        var materialized = songs.ToImmutableArray();
        if (materialized.Distinct().Count() != materialized.Length)
            throw new ArgumentException("A category cannot contain the same song twice.", nameof(songs));
        Key = key;
        Name = name;
        SortOrder = sortOrder;
        Songs = materialized;
    }

    public CategoryKey Key { get; }

    public string Name { get; }

    public int SortOrder { get; }

    public ImmutableArray<SongKey> Songs { get; }
}

public enum CatalogDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record CatalogDiagnostic(
    CatalogDiagnosticSeverity Severity,
    string Code,
    string Message,
    CatalogProviderId Provider)
{
    public CatalogDiagnosticSeverity Severity { get; } = Severity;

    public string Code { get; } = string.IsNullOrWhiteSpace(Code)
        ? throw new ArgumentException("Diagnostic code is required.", nameof(Code))
        : Code;

    public string Message { get; } = string.IsNullOrWhiteSpace(Message)
        ? throw new ArgumentException("Diagnostic message is required.", nameof(Message))
        : Message;

    public CatalogProviderId Provider { get; } = Provider ?? throw new ArgumentNullException(nameof(Provider));
}
