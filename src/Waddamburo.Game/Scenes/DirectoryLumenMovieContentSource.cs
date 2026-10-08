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
        if (!Directory.Exists(_assetRoot))
            throw new DirectoryNotFoundException($"Asset root does not exist: {_assetRoot}");
        _limits = limits ?? ParserLimits.Default;
    }

    private ConcurrentDictionary<(string ArchiveId, string MovieId), Task<LumenMovieContent>> _prefetched = new();

    public ILumenMovieContentScope CreateSceneScope() => new SceneScope(this);

    /// <summary>Called with every movie a scene loads (archive, movie); any thread.</summary>
    public Action<string, string>? Loaded { get; set; }

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
                    task => decoded(archive.Key, LumenMovieContent.Load(task.GetAwaiter().GetResult().OpenMovie(movie.MovieId), _limits)),
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
            var archivePath = owner.resolveArchivePath(archiveId);
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
            var content = owner.decoded(archiveId, LumenMovieContent.Load(archive!.OpenMovie(movieId), owner._limits));
            if (profile)
            {
                var rgbaBytes = content.Textures.Sum(static texture => (long)texture.Rgba8.Length);
                Console.Error.WriteLine($"Profile movie {movieId}: archive {length / 1048576d:F1} MiB{(reused ? " reused" : "")}, "
                    + $"read {Stopwatch.GetElapsedTime(start, read).TotalMilliseconds:F0} ms, "
                    + $"parse/decode {Stopwatch.GetElapsedTime(read).TotalMilliseconds:F0} ms, "
                    + $"RGBA {rgbaBytes / 1048576d:F1} MiB.");
            }
            return content;
        }

        public void Dispose() => _archives.Clear();
    }

    private string resolveArchivePath(string archiveId)
    {
        if (Path.IsPathFullyQualified(archiveId))
            throw new InvalidDataException($"Archive ID must be relative: {archiveId}");
        var candidate = Path.GetFullPath(archiveId, _assetRoot);
        var relative = Path.GetRelativePath(_assetRoot, candidate);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidDataException($"Archive ID escapes the asset root: {archiveId}");
        if (!File.Exists(candidate))
            throw new FileNotFoundException($"Archive was not found: {archiveId}", candidate);
        return candidate;
    }
}
