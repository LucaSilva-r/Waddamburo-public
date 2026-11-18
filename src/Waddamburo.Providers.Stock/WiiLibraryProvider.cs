using Waddamburo.Catalog;

namespace Waddamburo.Providers.Stock;

/// <summary>
/// The WII library or the WII U library (one platform each, as Nijiiro's installs): its releases' songs (newest first) (<see cref="WiiCatalogProvider"/>, <see cref="WiiUCatalogProvider"/>)
/// in one folder per genre. A song on several discs (same id) whose charts play the same (each shared course's notes
/// and rolls, in order; the ports' timing floats differ) is listed once, from the newest disc;
/// one charted differently is listed per disc, its title marked with the release.
/// </summary>
public sealed class WiiLibraryProvider : ISongCatalogProvider, ICatalogAssetResolver, IPlayableChartProvider
{
    // Newest first, with the title mark a song gets when the discs chart it differently.
    private readonly (ISongCatalogProvider Provider, string Release)[] _discs;

    /// <summary>The discs, newest first.</summary>
    public IEnumerable<ISongCatalogProvider> Discs => _discs.Select(static disc => disc.Provider);

    // The game's genre order.
    private static readonly string[] Genres = ["J-POP", "アニメ", "ボーカロイド", "童謡", "バラエティ", "クラシック", "ゲームミュージック", "ナムコオリジナル"];

    /// <summary>The WII library: the Wii discs' songs.</summary>
    public WiiLibraryProvider(IEnumerable<WiiCatalogProvider> wii)
        : this(SongSourceKind.Wii, "wii", wii.Select(static disc => ((ISongCatalogProvider)disc, disc.Release, disc.Order))) { }

    /// <summary>The WII U library: the Wii U titles' songs.</summary>
    public WiiLibraryProvider(IEnumerable<WiiUCatalogProvider> wiiU)
        : this(SongSourceKind.WiiU, "wii-u", wiiU.Select(static disc => ((ISongCatalogProvider)disc, disc.Release, disc.Order))) { }

    private WiiLibraryProvider(SongSourceKind source, string id, IEnumerable<(ISongCatalogProvider Disc, string Release, int Order)> discs)
    {
        (Source, Id) = (source, new CatalogProviderId(id));
        _discs = [.. discs.OrderByDescending(static disc => disc.Order).Select(static disc => (disc.Disc, disc.Release))];
    }

    public CatalogProviderId Id { get; }

    public SongSourceKind Source { get; }

    public async ValueTask<SongCatalogContribution> ScanAsync(IProgress<CatalogScanProgress>? progress, CancellationToken cancellationToken)
    {
        var diagnostics = new List<CatalogDiagnostic>();
        // Every disc's songs, with their genre label and the disc they come from.
        var found = new List<(SongDescriptor Song, string? Genre, int Disc)>();
        for (var disc = 0; disc < _discs.Length; disc++)
        {
            var contribution = await _discs[disc].Provider.ScanAsync(null, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(contribution.Diagnostics);
            var genres = contribution.Categories.SelectMany(category => category.Songs.Select(song => (song, category.Genre)))
                .ToDictionary(static entry => entry.song, static entry => entry.Genre);
            found.AddRange(contribution.Songs.Select(song => (song, genres.GetValueOrDefault(song.Key), disc)));
            progress?.Report(new CatalogScanProgress(Id, "scan", disc + 1, _discs.Length));
        }

        var songs = new List<(SongDescriptor Song, string? Genre)>();
        foreach (var group in found.GroupBy(static entry => bareId(entry.Song.Key)))
        {
            var copies = group.ToList();
            // Same charts on every disc: one copy, the newest disc's unless an older one draws giant or sparkling notes it
            // leaves out.
            var (same, keep, most) = (true, 0, 0);
            for (var index = 1; index < copies.Count && same; index++)
            {
                var (equal, first, other) = await samePlay(copies[0], copies[index], cancellationToken).ConfigureAwait(false);
                same = equal;
                most = Math.Max(most, first);
                if (other > most)
                    (keep, most) = (index, other);
            }
            foreach (var copy in same ? [copies[keep]] : copies)
                songs.Add((relabel(copy.Song, copy.Disc, same ? null : _discs[copy.Disc].Release), copy.Genre));
        }
        var categories = songs.GroupBy(static song => song.Genre ?? "")
            .OrderBy(static genre => Array.IndexOf(Genres, genre.Key) is var order and >= 0 ? order : int.MaxValue)
            .Select((genre, index) => new SongCategoryDescriptor(new CategoryKey(Source, $"wii-{index}"),
                genre.Key.Length == 0 ? "Other" : genreName(genre.Key), index, genre.Select(static song => song.Song.Key))
            {
                Genre = genre.Key.Length == 0 ? null : genre.Key,
                Colour = WiiCatalogProvider.GenreColour(genre.Key),
            });
        return new SongCatalogContribution(Id, Source, songs.Select(static song => song.Song), categories, diagnostics);
    }

    public ValueTask<Stream> OpenReadAsync(CatalogAssetKey asset, CancellationToken cancellationToken = default)
    {
        var (disc, inner) = route(asset);
        return ((ICatalogAssetResolver)disc).OpenReadAsync(inner, cancellationToken);
    }

    public ValueTask<PlayableChart> LoadChartAsync(ChartKey chart, CatalogAssetKey asset, CancellationToken cancellationToken = default)
    {
        var (disc, inner) = route(asset);
        return ((IPlayableChartProvider)disc).LoadChartAsync(chart, inner, cancellationToken);
    }

    // Assets are "<disc index>|<the disc's own asset id>".
    private (ISongCatalogProvider Disc, CatalogAssetKey Asset) route(CatalogAssetKey asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var bar = asset.StableId.IndexOf('|', StringComparison.Ordinal);
        if (asset.Provider != Id || bar < 0 || !int.TryParse(asset.StableId.AsSpan(0, bar), out var disc) || disc < 0 || disc >= _discs.Length)
            throw new ArgumentException("The asset is not a Wii or Wii U disc's.", nameof(asset));
        return (_discs[disc].Provider, new CatalogAssetKey(_discs[disc].Provider.Id, asset.StableId[(bar + 1)..]));
    }

    private CatalogAssetKey own(int disc, CatalogAssetKey asset) => new(Id, $"{disc}|{asset.StableId}");

    // The disc's song with this library's assets and, when the discs differ, its release in the title.
    private SongDescriptor relabel(SongDescriptor song, int disc, string? release)
    {
        var key = song.Key;
        var title = release is null ? song.Title
            : new SongTitle($"{song.Title.Primary} ({release})", song.Title.Japanese is { } japanese ? $"{japanese} ({release})" : null);
        var charts = song.Charts.Select(chart => new SongChartDescriptor(new ChartKey(key, chart.Key.StableId), chart.DifficultyName, own(disc, chart.ChartAsset),
            chart.Course, chart.Level, chart.DuetChartAssets.IsEmpty ? null : chart.DuetChartAssets.Select(duet => own(disc, duet)))
        {
            HasBranches = chart.HasBranches,
        });
        return new SongDescriptor(key, title, song.Artist, charts,
            song.AudioAsset is { } audio ? own(disc, audio) : null, song.PreviewStart, song.Subtitle)
        {
            ScenePresets = song.ScenePresets,
        };
    }

    // Each course both discs chart plays the same notes and rolls in the same order; with how many giant or sparkling
    // notes each copy draws (a disc can chart the same notes without them).
    private async ValueTask<(bool Same, int First, int Second)> samePlay((SongDescriptor Song, string? Genre, int Disc) first,
        (SongDescriptor Song, string? Genre, int Disc) second, CancellationToken cancellationToken)
    {
        var (firstSpecial, secondSpecial) = (0, 0);
        foreach (var chart in first.Song.Charts)
        {
            if (second.Song.Charts.FirstOrDefault(other => other.Course == chart.Course) is not { } other)
                continue;
            try
            {
                var a = await ((IPlayableChartProvider)_discs[first.Disc].Provider).LoadChartAsync(chart.Key, chart.ChartAsset, cancellationToken).ConfigureAwait(false);
                var b = await ((IPlayableChartProvider)_discs[second.Disc].Provider).LoadChartAsync(other.Key, other.ChartAsset, cancellationToken).ConfigureAwait(false);
                if (!a.HitObjects.Select(static note => note.Kind).SequenceEqual(b.HitObjects.Select(static note => note.Kind))
                    || !a.LongNotes.Select(static note => note.Kind).SequenceEqual(b.LongNotes.Select(static note => note.Kind)))
                    return default;
                firstSpecial += a.HitObjects.Count(static note => note.IsGiant || note.IsSparkle);
                secondSpecial += b.HitObjects.Count(static note => note.IsGiant || note.IsSparkle);
            }
            catch (InvalidDataException)
            {
                return default;
            }
        }
        return (true, firstSpecial, secondSpecial);
    }

    // "wii1:anp", "wii4:anp" and "wiiu:anp" are one song id.
    private static string bareId(SongKey key) => key.StableId[(key.StableId.IndexOf(':', StringComparison.Ordinal) + 1)..];

    private static string genreName(string genre) => genre switch
    {
        "アニメ" => "Anime",
        "ボーカロイド" => "Vocaloid",
        "童謡" => "Kids",
        "バラエティ" => "Variety",
        "クラシック" => "Classical",
        "ゲームミュージック" => "Game Music",
        "ナムコオリジナル" => "Namco Original",
        _ => genre,
    };
}
