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

    public ILumenMovieContentScope CreateSceneScope() => new SceneScope(this);

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
            var content = LumenMovieContent.Load(archive!.OpenMovie(movieId), owner._limits);
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
