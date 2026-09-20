using Waddamburo.Formats;
using Waddamburo.Formats.Ddp;

namespace Waddamburo.Game.Scenes;

/// <summary>Loads user-supplied DDP archives beneath one explicit asset root.</summary>
public sealed class DirectoryLumenMovieContentSource : ILumenMovieContentSource
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

    public async ValueTask<LumenMovieContent> LoadAsync(
        string archiveId,
        string movieId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveId);
        ArgumentException.ThrowIfNullOrWhiteSpace(movieId);
        var archivePath = resolveArchivePath(archiveId);
        var length = new FileInfo(archivePath).Length;
        if (length > _limits.MaxFileBytes)
            throw new InvalidDataException($"Archive '{archiveId}' exceeds the configured {_limits.MaxFileBytes}-byte limit.");
        var bytes = await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false);
        var archive = DdpArchive.Open(bytes, _limits);
        return LumenMovieContent.Load(archive.OpenMovie(movieId), _limits);
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
