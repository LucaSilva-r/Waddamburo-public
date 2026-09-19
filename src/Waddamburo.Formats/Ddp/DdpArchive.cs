using System.Collections.Immutable;

namespace Waddamburo.Formats.Ddp;

/// <summary>
/// Provides zero-copy movie and texture views over one caller-owned DDP buffer.
/// Keep the source memory alive and unchanged while using returned views.
/// </summary>
public sealed class DdpArchive
{
    private readonly ReadOnlyMemory<byte> _data;

    private DdpArchive(ReadOnlyMemory<byte> data, DdpArchiveIndex index)
    {
        _data = data;
        Index = index;
    }

    public DdpArchiveIndex Index { get; }

    public static DdpArchive Open(ReadOnlyMemory<byte> data, ParserLimits? limits = null) =>
        new(data, DdpArchiveIndex.Parse(data, limits));

    public DdpMovieView OpenMovie(string name) => OpenMovie(Index.GetMovie(name));

    public DdpMovieView OpenMovie(DdpMovieEntry movie)
    {
        ArgumentNullException.ThrowIfNull(movie);
        if (movie.Index < 0 || movie.Index >= Index.Movies.Length ||
            !ReferenceEquals(Index.Movies[movie.Index], movie))
            throw new ArgumentException("The movie entry does not belong to this DDP archive.", nameof(movie));

        var textures = ImmutableArray.CreateBuilder<DdpTextureView>(movie.TextureEnd - movie.TextureBegin);
        for (var index = movie.TextureBegin; index < movie.TextureEnd; index++)
        {
            var texture = Index.Textures[index];
            textures.Add(new DdpTextureView(texture, slice(texture.Offset, texture.Length)));
        }

        return new DdpMovieView(movie, slice(movie.Offset, movie.Length), textures.MoveToImmutable());
    }

    private ReadOnlyMemory<byte> slice(long offset, uint length) =>
        _data.Slice(checked((int)offset), checked((int)length));
}

public sealed record DdpMovieView(
    DdpMovieEntry Entry,
    ReadOnlyMemory<byte> Data,
    ImmutableArray<DdpTextureView> Textures);

public sealed record DdpTextureView(
    DdpTextureEntry Entry,
    ReadOnlyMemory<byte> Data);
