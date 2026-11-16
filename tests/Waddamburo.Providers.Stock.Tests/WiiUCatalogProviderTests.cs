using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Waddamburo.Providers.Stock.Tests;

public sealed class WiiUCatalogProviderTests
{
    [Fact]
    public void MusicInfoComesOutOfTheDrpPack()
    {
        var xml = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="utf-8"?>
            <DB_DATA num="2">
              <DATA_SET index="0"><id>mikuse</id><songFileName>SONG_MIKUSE</songFileName><title>千本桜</title><order>300</order>
                <genreNo>2</genreNo><branchHard>○</branchHard><starEasy>3</starEasy><starNormal>5</starNormal><starHard>7</starHard><starMania>9</starMania></DATA_SET>
              <DATA_SET index="1"><id>ex_mikuse</id><title>千本桜（裏）</title><genreNo>2</genreNo><starMania>10</starMania></DATA_SET>
            </DB_DATA>
            """);
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(xml);
        // A DRP entry: its 64-byte name, header words, the uncompressed size at +0x60, the zlib stream at +0x64.
        var drp = new byte[0x20 + 0x64 + packed.Length];
        Encoding.ASCII.GetBytes("musicinfo_db").CopyTo(drp, 0x20);
        BinaryPrimitives.WriteInt32BigEndian(drp.AsSpan(0x20 + 0x60), xml.Length);
        packed.ToArray().CopyTo(drp, 0x20 + 0x64);

        var entries = WiiUCatalogProvider.ReadMusicInfo(WiiUCatalogProvider.DrpEntry(drp, "musicinfo_db"));

        Assert.Equal(["mikuse", "ex_mikuse"], entries.Select(static entry => entry.Id));
        var song = entries[0];
        Assert.Equal(("千本桜", "SONG_MIKUSE", 2, 300), (song.Title, song.Bank, song.Genre, song.Order));
        Assert.Equal([3, 5, 7, 9], song.Stars);
        Assert.Equal([false, false, true, false], song.Branches);
        Assert.Equal(10, entries[1].Stars[3]);
    }
}
