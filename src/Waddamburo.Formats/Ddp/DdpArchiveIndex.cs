using System.Collections.Frozen;
using System.Collections.Immutable;
using Waddamburo.Formats.IO;

namespace Waddamburo.Formats.Ddp;

/// <summary>Validated directory metadata for one Green-style DDP archive.</summary>
public sealed class DdpArchiveIndex
{
    private static ReadOnlySpan<byte> Magic => "LM_NUT_TYPE1"u8;

    private readonly FrozenDictionary<string, DdpMovieEntry> _moviesByName;

    private DdpArchiveIndex(
        long archiveLength,
        long movieDataOffset,
        long movieDataLength,
        long textureDataOffset,
        long textureDataLength,
        uint trailerUnknown,
        uint trailerTextureCount,
        long trailingByteCount,
        ImmutableArray<DdpMovieEntry> movies,
        ImmutableArray<DdpTextureEntry> textures)
    {
        ArchiveLength = archiveLength;
        MovieDataOffset = movieDataOffset;
        MovieDataLength = movieDataLength;
        TextureDataOffset = textureDataOffset;
        TextureDataLength = textureDataLength;
        TrailerUnknown = trailerUnknown;
        TrailerTextureCount = trailerTextureCount;
        TrailingByteCount = trailingByteCount;
        Movies = movies;
        Textures = textures;
        _moviesByName = movies.ToFrozenDictionary(movie => movie.Name, StringComparer.Ordinal);
    }

    public long ArchiveLength { get; }

    public long MovieDataOffset { get; }

    public long MovieDataLength { get; }

    public long TextureDataOffset { get; }

    public long TextureDataLength { get; }

    public uint TrailerUnknown { get; }

    public uint TrailerTextureCount { get; }

    public long TrailingByteCount { get; }

    public ImmutableArray<DdpMovieEntry> Movies { get; }

    public ImmutableArray<DdpTextureEntry> Textures { get; }

    public static DdpArchiveIndex Parse(ReadOnlyMemory<byte> data, ParserLimits? limits = null)
    {
        using var reader = new BoundedBinaryReader(data, ByteOrder.BigEndian, limits);

        var magicOffset = reader.Offset;
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
            throw malformed("Invalid DDP magic", magicOffset);

        reader.Skip(4);
        var skipLengthOffset = reader.Offset;
        var skipLength = reader.ReadUInt32();
        skipChecked(reader, checked((long)skipLength + 20), skipLengthOffset, "DDP preamble");

        var movieCountOffset = reader.Offset;
        var movieCount = checkedCount(
            reader.ReadUInt32(),
            reader.Limits.MaxRecordCount,
            nameof(ParserLimits.MaxRecordCount),
            movieCountOffset);
        if (movieCount > reader.Limits.MaxStringCount)
        {
            throw new FormatLimitException(
                nameof(ParserLimits.MaxStringCount),
                movieCount,
                reader.Limits.MaxStringCount,
                movieCountOffset);
        }

        reader.Skip(9);
        ensureMinimumTableBytes(reader, movieCount, 20, movieCountOffset, "DDP movie table");
        ensureBuilderAllocation(reader, movieCount, movieCountOffset, "DDP movie index");
        var movies = ImmutableArray.CreateBuilder<DdpMovieEntry>(movieCount);
        var movieNames = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < movieCount; index++)
        {
            var nameLengthOffset = reader.Offset;
            var nameLength = checkedCount(
                reader.ReadUInt32(),
                reader.Limits.MaxStringBytes,
                nameof(ParserLimits.MaxStringBytes),
                nameLengthOffset);
            var nameOffset = reader.Offset;
            var name = reader.ReadUtf8(nameLength);
            validateName(name, nameOffset);
            if (!movieNames.Add(name))
                throw malformed($"Duplicate DDP movie name '{name}'", nameOffset);

            if (index == 0)
                reader.Skip(5);

            var entryOffset = reader.Offset;
            var relativeOffset = reader.ReadUInt32();
            var length = reader.ReadUInt32();
            var textureBegin = reader.ReadUInt32();
            var textureEnd = reader.ReadUInt32();
            movies.Add(new DdpMovieEntry(
                index,
                name,
                relativeOffset,
                length,
                checkedInt(textureBegin, "Movie texture-begin index", entryOffset + 8),
                checkedInt(textureEnd, "Movie texture-end index", entryOffset + 12),
                0));
        }

        reader.Skip(5);
        var textureCountOffset = reader.Offset;
        var textureCount = checkedCount(
            reader.ReadUInt32(),
            reader.Limits.MaxRecordCount,
            nameof(ParserLimits.MaxRecordCount),
            textureCountOffset);
        reader.Skip(9);

        ensureMinimumTableBytes(reader, textureCount, 8, textureCountOffset, "DDP texture table");
        ensureBuilderAllocation(reader, textureCount, textureCountOffset, "DDP texture index");
        var textures = ImmutableArray.CreateBuilder<DdpTextureEntry>(textureCount);
        for (var index = 0; index < textureCount; index++)
        {
            var relativeOffset = reader.ReadUInt32();
            var length = reader.ReadUInt32();
            textures.Add(new DdpTextureEntry(index, relativeOffset, length, 0));
        }

        var movieDataLength = reader.ReadUInt32();
        var textureDataLength = reader.ReadUInt32();
        var trailerUnknown = reader.ReadUInt32();
        var trailerTextureCount = reader.ReadUInt32();
        var movieDataOffset = reader.Position;
        var textureDataOffset = checkedAdd(movieDataOffset, movieDataLength, movieDataOffset, "DDP NUT base");
        var payloadEnd = checkedAdd(textureDataOffset, textureDataLength, textureDataOffset, "DDP payload end");
        if (payloadEnd > data.Length)
        {
            throw new FormatReadException(
                "DDP payload blocks extend beyond the archive",
                movieDataOffset,
                payloadEnd - movieDataOffset,
                data.Length - movieDataOffset);
        }

        for (var index = 0; index < movies.Count; index++)
        {
            var movie = movies[index];
            validateRelativeSpan(movie.RelativeOffset, movie.Length, movieDataLength, movieDataOffset, "DDP movie");
            if (movie.TextureBegin > movie.TextureEnd || movie.TextureEnd > textureCount)
            {
                throw malformed(
                    $"DDP movie '{movie.Name}' has texture span [{movie.TextureBegin}, {movie.TextureEnd}) outside {textureCount} entries",
                    movieDataOffset);
            }

            movies[index] = movie with
            {
                Offset = checkedAdd(movieDataOffset, movie.RelativeOffset, movieDataOffset, "DDP movie offset"),
            };
        }

        for (var index = 0; index < textures.Count; index++)
        {
            var texture = textures[index];
            validateRelativeSpan(texture.RelativeOffset, texture.Length, textureDataLength, textureDataOffset, "DDP texture");
            textures[index] = texture with
            {
                Offset = checkedAdd(textureDataOffset, texture.RelativeOffset, textureDataOffset, "DDP texture offset"),
            };
        }

        return new DdpArchiveIndex(
            data.Length,
            movieDataOffset,
            movieDataLength,
            textureDataOffset,
            textureDataLength,
            trailerUnknown,
            trailerTextureCount,
            data.Length - payloadEnd,
            movies.MoveToImmutable(),
            textures.MoveToImmutable());
    }

    public bool TryGetMovie(string name, out DdpMovieEntry movie)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _moviesByName.TryGetValue(name, out movie!);
    }

    public DdpMovieEntry GetMovie(string name) =>
        TryGetMovie(name, out var movie)
            ? movie
            : throw new KeyNotFoundException($"DDP movie '{name}' was not found.");

    private static void validateName(string name, long offset)
    {
        if (name.Length == 0)
            throw malformed("DDP movie name is empty", offset);
        if (name.Contains('\0'))
            throw malformed("DDP movie name contains a NUL character", offset);
    }

    private static void validateRelativeSpan(
        uint relativeOffset,
        uint length,
        uint blockLength,
        long blockOffset,
        string label)
    {
        if (relativeOffset > blockLength || length > blockLength - relativeOffset)
        {
            throw new FormatReadException(
                $"{label} span is outside its payload block",
                blockOffset,
                checked((long)relativeOffset + length),
                blockLength);
        }
    }

    private static int checkedCount(uint value, int maximum, string limitName, long offset)
    {
        if (value > maximum)
            throw new FormatLimitException(limitName, value, maximum, offset);
        return (int)value;
    }

    private static int checkedInt(uint value, string label, long offset)
    {
        if (value > int.MaxValue)
            throw malformed($"{label} exceeds the supported index range", offset);
        return (int)value;
    }

    private static void ensureMinimumTableBytes(
        BoundedBinaryReader reader,
        int count,
        int minimumEntryBytes,
        long offset,
        string label)
    {
        var minimumBytes = checked((long)count * minimumEntryBytes);
        if (reader.Remaining is long remaining && minimumBytes > remaining)
            throw new FormatReadException($"{label} cannot fit in the archive", offset, minimumBytes, remaining);
    }

    private static void ensureBuilderAllocation(
        BoundedBinaryReader reader,
        int count,
        long offset,
        string label)
    {
        var referenceBytes = checked((long)count * IntPtr.Size);
        if (referenceBytes > reader.Limits.MaxAllocationBytes)
        {
            throw new FormatLimitException(
                $"{label} {nameof(ParserLimits.MaxAllocationBytes)}",
                referenceBytes,
                reader.Limits.MaxAllocationBytes,
                offset);
        }
    }

    private static long checkedAdd(long left, long right, long offset, string label)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException exception)
        {
            throw new FormatReadException($"{label} overflows the supported offset space", offset, innerException: exception);
        }
    }

    private static void skipChecked(BoundedBinaryReader reader, long count, long offset, string label)
    {
        try
        {
            reader.Skip(count);
        }
        catch (FormatReadException exception)
        {
            throw new FormatReadException($"{label} is truncated", offset, count, exception.AvailableLength, exception);
        }
    }

    private static FormatReadException malformed(string message, long offset) => new(message, offset);
}

public sealed record DdpMovieEntry(
    int Index,
    string Name,
    uint RelativeOffset,
    uint Length,
    int TextureBegin,
    int TextureEnd,
    long Offset);

public sealed record DdpTextureEntry(
    int Index,
    uint RelativeOffset,
    uint Length,
    long Offset);
