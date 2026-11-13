using System.Collections.Concurrent;
using System.Diagnostics;
using Waddamburo.Formats;
using Waddamburo.Formats.Ddp;

namespace Waddamburo.Game.Scenes;

/// <summary>Loads user-supplied DDP archives beneath one explicit asset root.</summary>
public sealed class DirectoryLumenMovieContentSource : IScopedLumenMovieContentSource
{
    private readonly string _assetRoot;
    private readonly ParserLimits _limits;

    public DirectoryLumenMovieContentSource(string assetRoot, ParserLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);
        if (!Path.IsPathFullyQualified(assetRoot))
            throw new ArgumentException("Asset root must be an absolute path.", nameof(assetRoot));
        _assetRoot = Path.GetFullPath(assetRoot);
        // The folder need not exist: every movie is then a package, or reported missing by name when asked for.
        _limits = limits ?? ParserLimits.Default;
    }

    private ConcurrentDictionary<(string ArchiveId, string MovieId), Task<LumenMovieContent>> _prefetched = new();

    public ILumenMovieContentScope CreateSceneScope() => new SceneScope(this);

    /// <summary>BC1/BC3 textures stay compressed (the renderer draws them): see <see cref="LumenMovieContent.Load"/>.</summary>
    public bool KeepCompressed { get; set; }

    /// <summary>Called with every movie a scene loads (archive, movie); any thread.</summary>
    public Action<string, string>? Loaded { get; set; }

    /// <summary>
    /// Where movie packages are looked up before the archives (docs/development/free-movies-plan.md):
    /// <c>&lt;root&gt;/&lt;archive folder&gt;/&lt;movie&gt;/movie.json</c> replaces that archive's movie. Needs
    /// <see cref="DecodeImage"/>. Null (the default): archives only.
    /// </summary>
    public string? PackageRoot { get; set; }

    /// <summary>Decodes a package's PNG textures (see <see cref="LumenMovieContent.LoadPackage"/>).</summary>
    public Func<byte[], (int Width, int Height, byte[] Rgba)?>? DecodeImage { get; set; }

    /// <summary>
    /// The movie packages that come with the engine (same layout as <see cref="PackageRoot"/>). One is played when
    /// <see cref="PreferBundled"/> says so, and always when the game data has no such movie, so the engine runs
    /// on whatever part of the game data is there, down to none. The user's own packages come first.
    /// </summary>
    public string? BundledRoot { get; set; }

    /// <summary>Whether a bundled package replaces a movie the game data has (the free_movies setting, read live).</summary>
    public Func<bool>? PreferBundled { get; set; }

    /// <summary>
    /// Without any game movies (no asset root folder): a movie that has no package either is shown empty instead
    /// of failing its scene, so the game runs on the engine's own movies as far as they go. Each is named once.
    /// </summary>
    public bool EmptyWhenMissing => !Directory.Exists(_assetRoot);

    private readonly ConcurrentDictionary<string, bool> _reportedMissing = new(StringComparer.Ordinal);

    private string? packageDirectory(string archiveId, string movieId, bool archiveMissing = false)
    {
        if (DecodeImage is null)
            return null;
        string?[] roots = archiveMissing ? [BundledRoot] : [PackageRoot, PreferBundled?.Invoke() == true ? BundledRoot : null];
        foreach (var root in roots)
        {
            if (root is null)
                continue;
            // "indicator/packeddata.ddp" + "time_counter/time_counter.lm" -> <root>/indicator/time_counter.
            var directory = Path.Combine(root, Path.GetDirectoryName(archiveId) ?? "", Path.GetFileNameWithoutExtension(movieId));
            if (File.Exists(Path.Combine(directory, Formats.Package.MoviePackage.FileName)))
                return directory;
            // The second drum's and Katsu-chan's gameplay parts: a package reads its own drum from the provider, so the
            // first drum's serves ("enso_system/katsu2p" + "gage_katsu_2p_easy" -> enso_system/don1p/gage_don_1p_easy).
            var folder = Path.GetDirectoryName(archiveId) ?? "";
            var first = folder.Replace("base2p", "base1p").Replace("katsu1p", "don1p").Replace("katsu2p", "don1p").Replace("don2p", "don1p");
            if (!folder.StartsWith("enso_system", StringComparison.Ordinal))
                continue;
            directory = Path.Combine(root, first, Path.GetFileNameWithoutExtension(movieId).Replace("_katsu_", "_don_").Replace("_2p", "_1p"));
            if (File.Exists(Path.Combine(directory, Formats.Package.MoviePackage.FileName)))
                return directory;
        }
        return null;
    }

    /// <summary>Whether a movie can be loaded: from a package, or from its archive in the game data.</summary>
    public bool Has(string archiveId, string movieId) =>
        packageDirectory(archiveId, movieId) is not null || packageDirectory(archiveId, movieId, archiveMissing: true) is not null
        || File.Exists(Path.GetFullPath(archiveId, _assetRoot));

    /// <summary>Called with each movie once decoded (archive, movie), before any upload, on the decoding thread.</summary>
    public Action<string, LumenMovieContent>? Decoded { get; set; }

    /// <summary>A movie a load reuses whole instead of decoding it (a prefetch, being fresher, wins).</summary>
    public Func<string, string, LumenMovieContent?>? Reuse { get; set; }

    private LumenMovieContent decoded(string archiveId, LumenMovieContent content)
    {
        Decoded?.Invoke(archiveId, content);
        return content;
    }

    /// <summary>
    /// Decodes these movies on worker threads; the next load of each takes the result (once: a scene
    /// frees the decoded pixels after upload). Replaces any earlier prefetch not yet taken.
    /// </summary>
    public void Prefetch(IEnumerable<(string ArchiveId, string MovieId)> movies)
    {
        var fresh = new ConcurrentDictionary<(string ArchiveId, string MovieId), Task<LumenMovieContent>>();
        foreach (var archive in movies.Distinct().GroupBy(static movie => movie.ArchiveId))
        {
            var opened = Task.Run(() => DdpArchive.Open(File.ReadAllBytes(resolveArchivePath(archive.Key)), _limits));
            foreach (var movie in archive)
                fresh[movie] = opened.ContinueWith(
                    task => decoded(archive.Key, LumenMovieContent.Load(task.GetAwaiter().GetResult().OpenMovie(movie.MovieId), _limits, KeepCompressed)),
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        _prefetched = fresh;
    }

    /// <summary>Every prefetched movie not yet taken is decoded (or failed, which its load reports).</summary>
    public bool PrefetchComplete => _prefetched.Values.All(static task => task.IsCompleted);

    /// <summary>Prefetched movies already decoded and not yet taken by a load.</summary>
    public IEnumerable<LumenMovieContent> DecodedPrefetches => _prefetched.Values
        .Where(static task => task.IsCompletedSuccessfully).Select(static task => task.Result);

    /// <summary>Drops these prefetched movies (their pixels were freed after an upload since released).</summary>
    public void Forget(IReadOnlyCollection<LumenMovieContent> contents)
    {
        foreach (var (key, task) in _prefetched)
            if (task.IsCompletedSuccessfully && contents.Contains(task.Result))
                _prefetched.TryRemove(key, out _);
    }

    public async ValueTask<LumenMovieContent> LoadAsync(
        string archiveId,
        string movieId,
        CancellationToken cancellationToken)
    {
        using var scope = CreateSceneScope();
        return await scope.LoadAsync(archiveId, movieId, cancellationToken).ConfigureAwait(false);
    }

    private sealed class SceneScope(DirectoryLumenMovieContentSource owner) : ILumenMovieContentScope
    {
        private readonly Dictionary<string, DdpArchive> _archives = new(StringComparer.Ordinal);

        public async ValueTask<LumenMovieContent> LoadAsync(
            string archiveId,
            string movieId,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(archiveId);
            ArgumentException.ThrowIfNullOrWhiteSpace(movieId);
            cancellationToken.ThrowIfCancellationRequested();
            // A package is the user's or our own work: no patches, no upscaling, no archive needed.
            // ponytail: read and decoded here on every load; cache with the prefetches if a package gets heavy.
            if (owner.packageDirectory(archiveId, movieId) is { } package)
                return LumenMovieContent.LoadPackage(package, owner.DecodeImage!, owner._limits);
            owner.Loaded?.Invoke(archiveId, movieId);
            if (owner._prefetched.TryRemove((archiveId, movieId), out var ready))
            {
                try
                {
                    return await ready.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (ready.IsFaulted)
                {
                    // A failed prefetch loads normally below, reporting its own error.
                }
            }
            if (owner.Reuse?.Invoke(archiveId, movieId) is { } kept)
                return kept;
            // No such archive in the game data: the engine's own movie, when it has one.
            if (!File.Exists(Path.GetFullPath(archiveId, owner._assetRoot))
                && owner.packageDirectory(archiveId, movieId, archiveMissing: true) is { } bundled)
                return LumenMovieContent.LoadPackage(bundled, owner.DecodeImage!, owner._limits);
            if (owner.EmptyWhenMissing)
            {
                if (owner._reportedMissing.TryAdd(movieId, true))
                    Console.WriteLine($"Movie '{movieId}' ({Path.GetDirectoryName(archiveId)}): no free version yet, shown empty.");
                return LumenMovieContent.Empty(movieId);
            }
            var archivePath = owner.resolveArchivePath(archiveId, movieId);
            var length = new FileInfo(archivePath).Length;
            if (length > owner._limits.MaxFileBytes)
                throw new InvalidDataException($"Archive '{archiveId}' exceeds the configured {owner._limits.MaxFileBytes}-byte limit.");
            var profile = Environment.GetEnvironmentVariable("WADDAMBURO_PROFILE") == "1";
            var start = profile ? Stopwatch.GetTimestamp() : 0;
            var reused = _archives.TryGetValue(archivePath, out var archive);
            if (!reused)
            {
                var bytes = await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false);
                archive = DdpArchive.Open(bytes, owner._limits);
                _archives.Add(archivePath, archive);
            }
            var read = profile ? Stopwatch.GetTimestamp() : 0;
            var parsed = LumenMovieContent.Load(archive!.OpenMovie(movieId), owner._limits, owner.KeepCompressed);
            var parsedAt = profile ? Stopwatch.GetTimestamp() : 0;
            var content = owner.decoded(archiveId, parsed);
            if (profile)
            {
                var rgbaBytes = content.Textures.Sum(static texture => (long)texture.Rgba8.Length + texture.Blocks.Length);
                Console.Error.WriteLine($"Profile movie {movieId}: archive {length / 1048576d:F1} MiB{(reused ? " reused" : "")}, "
                    + $"read {Stopwatch.GetElapsedTime(start, read).TotalMilliseconds:F0} ms, "
                    + $"parse {Stopwatch.GetElapsedTime(read, parsedAt).TotalMilliseconds:F0} ms, "
                    + $"patch/upscales {Stopwatch.GetElapsedTime(parsedAt).TotalMilliseconds:F0} ms, "
                    + $"RGBA {rgbaBytes / 1048576d:F1} MiB.");
            }
            return content;
        }

        public void Dispose() => _archives.Clear();
    }

    private string resolveArchivePath(string archiveId, string? movieId = null)
    {
        if (Path.IsPathFullyQualified(archiveId))
            throw new InvalidDataException($"Archive ID must be relative: {archiveId}");
        var candidate = Path.GetFullPath(archiveId, _assetRoot);
        var relative = Path.GetRelativePath(_assetRoot, candidate);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidDataException($"Archive ID escapes the asset root: {archiveId}");
        if (!File.Exists(candidate))
            throw new FileNotFoundException(movieId is null ? $"Archive was not found: {archiveId}"
                : $"Movie '{movieId}' is missing: no archive '{archiveId}' in the game data and no movie package for it.", candidate);
        return candidate;
    }
}
