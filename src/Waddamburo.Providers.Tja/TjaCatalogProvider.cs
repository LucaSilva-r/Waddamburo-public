using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Waddamburo.Catalog;

namespace Waddamburo.Providers.Tja;

/// <summary>Discovers read-only custom TJA songs below one configured library root.</summary>
public sealed class TjaCatalogProvider : ISongCatalogProvider, ICatalogAssetResolver
{
    private readonly string _root;
    private readonly TjaProviderOptions _options;

    public TjaCatalogProvider(
        string root,
        CatalogProviderId? id = null,
        TjaProviderOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Id = id ?? new CatalogProviderId("custom-tja");
        _options = options ?? new TjaProviderOptions();
        _options.Validate();
    }

    public CatalogProviderId Id { get; }

    public SongSourceKind Source => SongSourceKind.Tja;

    public async ValueTask<SongCatalogContribution> ScanAsync(
        IProgress<CatalogScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root))
            throw new DirectoryNotFoundException("The configured custom TJA library does not exist.");

        progress?.Report(new CatalogScanProgress(Id, "discover", 0, null));
        var paths = enumerateCharts(cancellationToken);
        var inspected = new List<InspectedTjaFile>(paths.Length);
        var diagnostics = new List<CatalogDiagnostic>();

        for (var index = 0; index < paths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = paths[index];
            var relativePath = normalizeRelative(Path.GetRelativePath(_root, path));
            progress?.Report(new CatalogScanProgress(Id, "parse", index, paths.Length));
            try
            {
                var file = new FileInfo(path);
                if (file.Length > _options.MaximumChartBytes)
                {
                    diagnostics.Add(warning(
                        "TJA_FILE_TOO_LARGE",
                        $"'{relativePath}' exceeds the configured chart-size limit."));
                    continue;
                }

                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                var metadata = TjaMetadataReader.Read(bytes);
                validateBpm(metadata);
                if (!metadata.Charts.Any(static chart => chart.Course is not null))
                {
                    foreach (var chart in metadata.Charts.Where(static chart => chart.Course is null))
                    {
                        diagnostics.Add(warning(
                            "TJA_COURSE_UNSUPPORTED",
                            $"'{relativePath}' contains unsupported course '{chart.DifficultyName}'."));
                    }
                    diagnostics.Add(warning(
                        "TJA_NO_SUPPORTED_CHARTS",
                        $"'{relativePath}' contains no supported Taiko course."));
                    continue;
                }

                inspected.Add(new InspectedTjaFile(
                    relativePath,
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    metadata,
                    resolveAudio(metadata.Get("WAVE"), path, relativePath, diagnostics),
                    categoryFor(relativePath, metadata.Get("GENRE"))));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or DecoderFallbackException
                or ArgumentException)
            {
                diagnostics.Add(warning(
                    "TJA_FILE_INVALID",
                    $"'{relativePath}' could not be inspected: {exception.Message}"));
            }
        }

        var songs = new List<SongDescriptor>(inspected.Count);
        var categories = new Dictionary<string, List<SongKey>>(StringComparer.Ordinal);
        foreach (var group in inspected
            .GroupBy(static file => file.Audio.GroupKey, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            var files = group.OrderBy(static file => file.RelativePath, StringComparer.Ordinal).ToArray();
            var representative = files[0];
            var songKey = new SongKey(Source, stableSongId(group.Key));
            var charts = files
                .SelectMany(file => createCharts(file, songKey, diagnostics))
                .GroupBy(static chart => chart.Key)
                .Select(chartGroup =>
                {
                    if (chartGroup.Skip(1).Any())
                    {
                        diagnostics.Add(warning(
                            "TJA_DUPLICATE_CHART",
                            $"Song '{representative.RelativePath}' contains a duplicate chart identity."));
                    }
                    return chartGroup.First();
                })
                .ToArray();

            var japaneseTitle = firstMetadata(files, "TITLEJA");
            var baseTitle = firstMetadata(files, "TITLE");
            var englishTitle = firstMetadata(files, "TITLEEN");
            var primaryTitle = japaneseTitle ?? baseTitle ?? englishTitle
                ?? Path.GetFileNameWithoutExtension(representative.RelativePath);
            var song = new SongDescriptor(
                songKey,
                new SongTitle(primaryTitle, japaneseTitle, englishTitle),
                firstMetadata(files, "ARTIST"),
                charts,
                representative.Audio.Asset,
                parsePreview(firstMetadata(files, "DEMOSTART"), representative.RelativePath, diagnostics),
                normalizeSubtitle(firstMetadata(files, "SUBTITLEJA") ?? firstMetadata(files, "SUBTITLE")));
            songs.Add(song);

            if (!categories.TryGetValue(representative.Category, out var members))
                categories.Add(representative.Category, members = []);
            members.Add(songKey);
        }

        var orderedSongs = songs
            .OrderBy(static song => song.Title.Primary, StringComparer.Ordinal)
            .ThenBy(static song => song.Key.StableId, StringComparer.Ordinal)
            .ToArray();
        var songOrder = orderedSongs
            .Select((song, index) => (song.Key, index))
            .ToDictionary(static item => item.Key, static item => item.index);
        var orderedCategories = categories
            .OrderBy(static category => category.Key, StringComparer.Ordinal)
            .Select((category, index) => new SongCategoryDescriptor(
                new CategoryKey(Source, stableCategoryId(category.Key)),
                category.Key,
                index,
                category.Value.OrderBy(key => songOrder[key])))
            .ToArray();

        progress?.Report(new CatalogScanProgress(Id, "complete", paths.Length, paths.Length));
        return new SongCatalogContribution(Id, Source, orderedSongs, orderedCategories, diagnostics);
    }

    public ValueTask<Stream> OpenReadAsync(
        CatalogAssetKey asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Provider != Id)
            throw new ArgumentException("The asset belongs to a different catalog provider.", nameof(asset));
        cancellationToken.ThrowIfCancellationRequested();
        var relativePath = TjaAssetKeyCodec.Decode(asset);
        var path = resolveContainedPath(relativePath);
        rejectReparsePoints(path);
        Stream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }

    private string[] enumerateCharts(CancellationToken cancellationToken)
    {
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        var paths = new List<string>();
        foreach (var path in Directory.EnumerateFiles(_root, "*", enumeration))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(Path.GetExtension(path), ".tja", StringComparison.OrdinalIgnoreCase))
                continue;
            paths.Add(path);
            if (paths.Count > _options.MaximumFileCount)
                throw new InvalidDataException("The custom TJA library exceeds the configured file-count limit.");
        }

        return [.. paths.OrderBy(path => normalizeRelative(Path.GetRelativePath(_root, path)), StringComparer.Ordinal)];
    }

    private List<SongChartDescriptor> createCharts(
        InspectedTjaFile file,
        SongKey song,
        List<CatalogDiagnostic> diagnostics)
    {
        var charts = new List<SongChartDescriptor>();
        foreach (var chart in file.Metadata.Charts)
        {
            if (chart.Course is not { } course)
            {
                diagnostics.Add(warning(
                    "TJA_COURSE_UNSUPPORTED",
                    $"'{file.RelativePath}' contains unsupported course '{chart.DifficultyName}'."));
                continue;
            }

            var player = string.IsNullOrWhiteSpace(chart.Player) ? "solo" : chart.Player.ToLowerInvariant();
            var stableId = $"{file.SourceHash}:{course.ToString().ToLowerInvariant()}:{player}:{chart.Occurrence.ToString(CultureInfo.InvariantCulture)}";
            charts.Add(new SongChartDescriptor(
                new ChartKey(song, stableId),
                chart.DifficultyName,
                TjaAssetKeyCodec.Encode(Id, file.RelativePath),
                course,
                chart.Level));
        }

        return charts;
    }

    private ResolvedTjaAudio resolveAudio(
        string? wave,
        string chartPath,
        string relativeChartPath,
        List<CatalogDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(wave))
        {
            diagnostics.Add(warning("TJA_AUDIO_UNSPECIFIED", $"'{relativeChartPath}' has no WAVE value."));
            return new ResolvedTjaAudio($"chart:{relativeChartPath}", null);
        }

        wave = unquote(wave);
        try
        {
            if (isPortableRooted(wave))
                throw new InvalidDataException("Absolute audio paths are not allowed.");
            var localWave = wave.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var combined = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(chartPath)!, localWave));
            var relativeAudio = normalizeRelative(Path.GetRelativePath(_root, combined));
            _ = resolveContainedPath(relativeAudio);
            if (!File.Exists(combined))
            {
                diagnostics.Add(warning(
                    "TJA_AUDIO_MISSING",
                    $"'{relativeChartPath}' refers to an audio file that does not exist."));
                return new ResolvedTjaAudio($"chart:{relativeChartPath}", null);
            }
            rejectReparsePoints(combined);
            return new ResolvedTjaAudio($"audio:{relativeAudio}", TjaAssetKeyCodec.Encode(Id, relativeAudio));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            diagnostics.Add(warning(
                "TJA_AUDIO_INVALID",
                $"'{relativeChartPath}' has an invalid WAVE value: {exception.Message}"));
            return new ResolvedTjaAudio($"chart:{relativeChartPath}", null);
        }
    }

    private TimeSpan? parsePreview(
        string? value,
        string relativePath,
        List<CatalogDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
            return TimeSpan.Zero;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds)
            && seconds <= uint.MaxValue / 1000d)
            return TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, seconds) * 1000, MidpointRounding.ToEven));

        diagnostics.Add(warning("TJA_PREVIEW_INVALID", $"'{relativePath}' has an invalid DEMOSTART value."));
        return null;
    }

    private string resolveContainedPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new ArgumentException("The asset path must be relative to its provider root.", nameof(relativePath));
        var localRelative = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(_root, localRelative));
        var relative = Path.GetRelativePath(_root, path);
        if (relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", pathComparison()))
            throw new ArgumentException("The asset path escapes its provider root.", nameof(relativePath));
        return path;
    }

    private void rejectReparsePoints(string path)
    {
        var relative = Path.GetRelativePath(_root, path);
        var current = _root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Assets reached through symbolic links are not allowed.");
        }
    }

    private CatalogDiagnostic warning(string code, string message) =>
        new(CatalogDiagnosticSeverity.Warning, code, message, Id);

    private static void validateBpm(TjaMetadata metadata)
    {
        if (!double.TryParse(metadata.Get("BPM"), NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm)
            || !double.IsFinite(bpm)
            || bpm <= 0)
            throw new InvalidDataException("TJA requires a positive BPM value.");
    }

    private string stableCategoryId(string category)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes($"{Id.Value}\0{category}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private string stableSongId(string groupKey)
    {
        var bytes = Encoding.UTF8.GetBytes($"{Id.Value}\0{groupKey}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string? firstMetadata(IEnumerable<InspectedTjaFile> files, string name) =>
        files.Select(file => file.Metadata.Get(name)).FirstOrDefault(static value => value is not null);

    private static string categoryFor(string relativePath, string? genre)
    {
        var separator = relativePath.IndexOf('/');
        return separator > 0 ? relativePath[..separator] : genre ?? "Uncategorized";
    }

    private static string? normalizeSubtitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        return value.StartsWith("--", StringComparison.Ordinal) || value.StartsWith("++", StringComparison.Ordinal)
            ? value[2..].Trim()
            : value;
    }

    private static string unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    }

    private static string normalizeRelative(string path) =>
        path.Replace('\\', '/').Replace(Path.DirectorySeparatorChar, '/');

    private static bool isPortableRooted(string path) =>
        Path.IsPathRooted(path)
        || path.StartsWith('\\')
        || path.StartsWith('/')
        || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');

    private static StringComparison pathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record InspectedTjaFile(
        string RelativePath,
        string SourceHash,
        TjaMetadata Metadata,
        ResolvedTjaAudio Audio,
        string Category);

    private sealed record ResolvedTjaAudio(string GroupKey, CatalogAssetKey? Asset);
}
