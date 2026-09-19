using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Waddamburo.Formats.Ddp;
using Waddamburo.Formats.IO;

namespace Waddamburo.Formats.Tests;

public sealed class DdpArchiveTests
{
    [Fact]
    public void IndexesAndOpensMultipleMoviesWithoutCopyingPayloads()
    {
        var data = createArchive(
            [
                new MovieFixture("first/first.lm", 0, 3, 0, 2),
                new MovieFixture("second/second.lm", 3, 2, 2, 3),
            ],
            [
                new DataFixture(0, 2),
                new DataFixture(2, 1),
                new DataFixture(3, 2),
            ],
            [0x10, 0x11, 0x12, 0x20, 0x21],
            [0xA0, 0xA1, 0xB0, 0xC0, 0xC1],
            trailingBytes: [0xEE]);

        var archive = DdpArchive.Open(data);
        var first = archive.OpenMovie("first/first.lm");
        var second = archive.OpenMovie("second/second.lm");

        Assert.Equal(2, archive.Index.Movies.Length);
        Assert.Equal(3, archive.Index.Textures.Length);
        Assert.Equal(1, archive.Index.TrailingByteCount);
        Assert.Equal(new byte[] { 0x10, 0x11, 0x12 }, first.Data.ToArray());
        Assert.Equal(new byte[] { 0x20, 0x21 }, second.Data.ToArray());
        Assert.Equal(2, first.Textures.Length);
        Assert.Single(second.Textures);
        Assert.Equal(new byte[] { 0xC0, 0xC1 }, second.Textures[0].Data.ToArray());

        Assert.True(MemoryMarshal.TryGetArray(first.Data, out var movieSegment));
        Assert.Same(data, movieSegment.Array);
        Assert.True(MemoryMarshal.TryGetArray(second.Textures[0].Data, out var textureSegment));
        Assert.Same(data, textureSegment.Array);
    }

    [Fact]
    public void EveryTruncationOfMinimalArchiveFailsPredictably()
    {
        var data = createArchive(
            [new MovieFixture("movie.lm", 0, 1, 0, 1)],
            [new DataFixture(0, 1)],
            [0x11],
            [0x22]);

        for (var length = 0; length < data.Length; length++)
        {
            var truncated = data.AsMemory(0, length);
            Assert.ThrowsAny<FormatException>(() => DdpArchiveIndex.Parse(truncated));
        }
    }

    [Fact]
    public void RejectsInvalidMagicAndArchiveControlledNames()
    {
        var invalidMagic = createArchive();
        invalidMagic[0] ^= 0xFF;
        Assert.Throws<FormatReadException>(() => DdpArchiveIndex.Parse(invalidMagic));

        var duplicate = createArchive(
            [
                new MovieFixture("same.lm", 0, 0, 0, 0),
                new MovieFixture("same.lm", 0, 0, 0, 0),
            ]);
        var duplicateError = Assert.Throws<FormatReadException>(() => DdpArchiveIndex.Parse(duplicate));
        Assert.Contains("Duplicate", duplicateError.Message, StringComparison.Ordinal);

        var embeddedNull = createArchive([new MovieFixture("bad\0name.lm", 0, 0, 0, 0)]);
        Assert.Throws<FormatReadException>(() => DdpArchiveIndex.Parse(embeddedNull));

        var invalidUtf8 = createArchive([new MovieFixture("x", 0, 0, 0, 0)]);
        var nameOffset = findFirstNameOffset(invalidUtf8);
        invalidUtf8[nameOffset] = 0xFF;
        Assert.Throws<FormatReadException>(() => DdpArchiveIndex.Parse(invalidUtf8));
    }

    [Theory]
    [InlineData(4u, 1u, 0, 0)]
    [InlineData(0u, 5u, 0, 0)]
    [InlineData(0u, 1u, 2, 1)]
    [InlineData(0u, 1u, 0, 2)]
    public void RejectsMovieAndTextureSpansOutsideTheirBlocks(
        uint movieOffset,
        uint movieLength,
        int textureBegin,
        int textureEnd)
    {
        var data = createArchive(
            [new MovieFixture("movie.lm", movieOffset, movieLength, textureBegin, textureEnd)],
            [new DataFixture(0, 1)],
            [0x11],
            [0x22]);

        Assert.Throws<FormatReadException>(() => DdpArchiveIndex.Parse(data));
    }

    [Theory]
    [InlineData(2u, 1u)]
    [InlineData(0u, 3u)]
    public void RejectsTextureEntriesOutsideTextureBlock(uint offset, uint length)
    {
        var data = createArchive(
            [new MovieFixture("movie.lm", 0, 1, 0, 1)],
            [new DataFixture(offset, length)],
            [0x11],
            [0x22, 0x23]);

        Assert.Throws<FormatReadException>(() => DdpArchiveIndex.Parse(data));
    }

    [Fact]
    public void EnforcesRecordAndStringLimitsBeforeIterationOrAllocation()
    {
        var twoMovies = createArchive(
            [
                new MovieFixture("a", 0, 0, 0, 0),
                new MovieFixture("b", 0, 0, 0, 0),
            ]);
        var recordError = Assert.Throws<FormatLimitException>(() =>
            DdpArchiveIndex.Parse(twoMovies, new ParserLimits(maxRecordCount: 1)));
        Assert.Equal(nameof(ParserLimits.MaxRecordCount), recordError.LimitName);

        var longName = createArchive([new MovieFixture("long", 0, 0, 0, 0)]);
        var stringError = Assert.Throws<FormatLimitException>(() =>
            DdpArchiveIndex.Parse(longName, new ParserLimits(maxStringBytes: 3)));
        Assert.Equal(nameof(ParserLimits.MaxStringBytes), stringError.LimitName);
    }

    [Fact]
    public void ForgedCountsAreRejectedBeforeTableAllocation()
    {
        var movieCount = createArchive();
        writeUInt32(movieCount.AsSpan(42, 4), 100_000);
        var movieError = Assert.Throws<FormatReadException>(() => DdpArchiveIndex.Parse(movieCount));
        Assert.Contains("movie table", movieError.Message, StringComparison.Ordinal);

        var textureCount = createArchive();
        writeUInt32(textureCount.AsSpan(60, 4), 100_000);
        var textureError = Assert.Throws<FormatReadException>(() => DdpArchiveIndex.Parse(textureCount));
        Assert.Contains("texture table", textureError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesTrailerMetadataWithoutAssumingItMatchesDirectoryCount()
    {
        var data = createArchive(trailerUnknown: 0x12345678, trailerTextureCount: 99);

        var index = DdpArchiveIndex.Parse(data);

        Assert.Equal(0x12345678u, index.TrailerUnknown);
        Assert.Equal(99u, index.TrailerTextureCount);
    }

    [Fact]
    public void EntryFromAnotherArchiveCannotBeUsedForExtraction()
    {
        var first = DdpArchive.Open(createArchive([new MovieFixture("a", 0, 0, 0, 0)]));
        var second = DdpArchive.Open(createArchive([new MovieFixture("b", 0, 0, 0, 0)]));

        Assert.Throws<ArgumentException>(() => first.OpenMovie(second.Index.Movies[0]));
        Assert.Throws<KeyNotFoundException>(() => first.OpenMovie("missing"));
    }

    private static byte[] createArchive(
        MovieFixture[]? movies = null,
        DataFixture[]? textures = null,
        byte[]? movieData = null,
        byte[]? textureData = null,
        byte[]? trailingBytes = null,
        uint trailerUnknown = 0,
        uint? trailerTextureCount = null)
    {
        movies ??= [];
        textures ??= [];
        movieData ??= [];
        textureData ??= [];
        trailingBytes ??= [];

        using var stream = new MemoryStream();
        stream.Write("LM_NUT_TYPE1"u8);
        stream.Write(new byte[4]);
        writeUInt32(stream, 2);
        stream.Write(new byte[] { 0xA1, 0xA2 });
        stream.Write(new byte[20]);
        writeUInt32(stream, checked((uint)movies.Length));
        stream.Write(new byte[9]);
        for (var index = 0; index < movies.Length; index++)
        {
            var movie = movies[index];
            var name = Encoding.UTF8.GetBytes(movie.Name);
            writeUInt32(stream, checked((uint)name.Length));
            stream.Write(name);
            if (index == 0)
                stream.Write(new byte[5]);
            writeUInt32(stream, movie.Offset);
            writeUInt32(stream, movie.Length);
            writeUInt32(stream, checked((uint)movie.TextureBegin));
            writeUInt32(stream, checked((uint)movie.TextureEnd));
        }

        stream.Write(new byte[5]);
        writeUInt32(stream, checked((uint)textures.Length));
        stream.Write(new byte[9]);
        foreach (var texture in textures)
        {
            writeUInt32(stream, texture.Offset);
            writeUInt32(stream, texture.Length);
        }

        writeUInt32(stream, checked((uint)movieData.Length));
        writeUInt32(stream, checked((uint)textureData.Length));
        writeUInt32(stream, trailerUnknown);
        writeUInt32(stream, trailerTextureCount ?? checked((uint)textures.Length));
        stream.Write(movieData);
        stream.Write(textureData);
        stream.Write(trailingBytes);
        return stream.ToArray();
    }

    private static int findFirstNameOffset(byte[] archive)
    {
        const int fixedPrefixLength = 12 + 4 + 4 + 2 + 20 + 4 + 9 + 4;
        return fixedPrefixLength;
    }

    private static void writeUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void writeUInt32(Span<byte> destination, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(destination, value);

    private sealed record MovieFixture(
        string Name,
        uint Offset,
        uint Length,
        int TextureBegin,
        int TextureEnd);

    private sealed record DataFixture(uint Offset, uint Length);
}
