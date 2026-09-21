using System.Buffers.Binary;
using System.Text;
using Waddamburo.Formats.Audio;

namespace Waddamburo.Formats.Tests;

public sealed class NuSoundBankCatalogTests
{
    [Fact]
    public void ParsesSyntheticBigEndianBankTable()
    {
        var data = createTable("VO_COM", "SE_COM", "VO_ENTRY");

        var catalog = NuSoundBankCatalog.Parse(data);

        Assert.Equal<string>(["VO_COM", "SE_COM", "VO_ENTRY"], catalog.Banks);
        Assert.True(catalog.TryGetName(2, out var entry));
        Assert.Equal("VO_ENTRY", entry);
        Assert.False(catalog.TryGetName(3, out _));
    }

    [Fact]
    public void RejectsTraversalCharactersInBankName()
    {
        var data = createTable("../VOICE");

        Assert.Throws<InvalidDataException>(() => NuSoundBankCatalog.Parse(data));
    }

    private static byte[] createTable(params string[] names)
    {
        const int headerSize = 16;
        const int firstNameOffset = 32;
        const int entrySize = 48;
        var length = names.Length == 0 ? headerSize : firstNameOffset + (names.Length - 1) * entrySize + 32;
        var data = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), checked((uint)names.Length));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), checked((uint)data.Length));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), firstNameOffset);
        for (var index = 0; index < names.Length; index++)
            Encoding.ASCII.GetBytes(names[index], data.AsSpan(firstNameOffset + index * entrySize));
        return data;
    }
}
